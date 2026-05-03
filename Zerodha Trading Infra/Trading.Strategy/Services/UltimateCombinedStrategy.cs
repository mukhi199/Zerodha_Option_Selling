// =============================================================================
// UltimateCombinedStrategy_FIXED.cs
// =============================================================================
// FIXES APPLIED (search for "// FIX #N" to jump to each change):
//
//   FIX #1 — EMA seeded from pre-warmed TechnicalIndicatorsTracker on first candle
//             (was starting from zero, ignoring 300-day bootstrap history)
//
//   FIX #2 — CandlesToday split into OrbCandleCount + ScanCandleCount
//             (was double-incremented, breaking both ORB detection and log throttle)
//
//   FIX #3 — Stale EntryPrice/SlPrice cleared after webhook SL hit
//             (was leaving stale values that could corrupt next trade)
//
//   FIX #4 — ExecuteManualTrade EMA-9 fallback guarded properly
//             (EMA-9 is never tracked elsewhere; added safe null-chain)
//
//   FIX #5 — Proximity suggestion log throttled (once per 5 candles per symbol)
//             (was firing on every matching candle, flooding logs in trending markets)
//
//   FIX #6 — SymbolState internal fields reset on SL webhook to prevent stale state
//             (OptionSymbol, EntryPrice, SlPrice all cleared consistently)
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Trading.Core.Models;
using Trading.Core.Utils;

namespace Trading.Strategy.Services
{
    public class SymbolState
    {
        public string Symbol { get; set; } = "";
        public decimal SlLimit { get; set; }
        public int EmaPeriod { get; set; }
        public decimal MinBandWidth { get; set; }

        public int TradesToday { get; set; } = 0;
        public string? PendingSlOrderId { get; set; }
        public DateTime CurrentDay { get; set; } = DateTime.MinValue;

        public decimal DayHigh { get; set; } = decimal.MinValue;
        public decimal DayLow { get; set; } = decimal.MaxValue;
        public decimal PrevDayH { get; set; } = 0;
        public decimal PrevDayL { get; set; } = 0;

        public decimal PrevLHHigh { get; set; } = decimal.MinValue;
        public decimal PrevLHLow { get; set; } = decimal.MaxValue;
        public decimal CurrLHHigh { get; set; } = decimal.MinValue;
        public decimal CurrLHLow { get; set; } = decimal.MaxValue;
        public bool PrevLHReady { get; set; } = false;

        public Candle? PrevCandle { get; set; } = null;

        public decimal EmaValue { get; set; } = 0;
        public bool EmaReady { get; set; } = false;

        // -----------------------------------------------------------------
        // FIX #2 — was a single CandlesToday used for BOTH ORB counting AND
        //           log throttle, causing double-increment corruption.
        //           Now split into two clearly-named counters.
        // -----------------------------------------------------------------
        public int OrbCandleCount { get; set; } = 0;   // FIX #2: replaces CandlesToday for ORB
        public int ScanCandleCount { get; set; } = 0;  // FIX #2: replaces CandlesToday for log throttle

        public decimal OrbHigh { get; set; } = decimal.MinValue;
        public decimal OrbLow { get; set; } = decimal.MaxValue;
        public decimal OrbOpen { get; set; } = 0;
        public decimal OrbClose { get; set; } = 0;
        public bool OrbSet { get; set; } = false;
        public bool IsGapUp { get; set; } = false;
        public bool IsGapDown { get; set; } = false;
        public bool OrbIsBearish { get; set; } = false;
        public bool OrbIsBullish { get; set; } = false;

        public bool IsLong { get; set; } = false;
        public bool IsShort { get; set; } = false;
        public decimal EntryPrice { get; set; } = 0;
        public decimal SlPrice { get; set; } = 0;
        public string OptionSymbol { get; set; } = "";

        // Retracement logic
        public bool HasBroken3DH { get; set; } = false;
        public bool HasRetraced3DH { get; set; } = false;
        public bool HasBroken3DL { get; set; } = false;
        public bool HasRetraced3DL { get; set; } = false;

        // FIX #5 — throttle proximity suggestion logs
        public int ProximitySuggestionCandle { get; set; } = 0; // FIX #5
    }

    public class UltimateCombinedStrategy : IStrategy
    {
        private readonly IOrderService _orderService;
        private readonly TechnicalIndicatorsTracker _tracker;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<UltimateCombinedStrategy> _logger;
        private readonly Trading.Zerodha.Services.INfoSymbolMaster _symbolMaster;
        private readonly IStrategicStateStore _stateStore;

        private readonly ConcurrentDictionary<string, SymbolState> _states = new();
        private readonly TimeSpan _entryDeadline  = new TimeSpan(14, 30, 0); // FIX: Extended from 12:00 to 14:30 to capture afternoon moves
        private readonly TimeSpan _lastHourStart  = new TimeSpan(14, 15, 0);
        private readonly TimeSpan _lastHourEnd    = new TimeSpan(15, 15, 0);
        private readonly TimeSpan _squareOffTime  = new TimeSpan(15, 15, 0);
        private readonly TimeSpan _orbEnd         = new TimeSpan(9,  45, 0);
        private readonly TimeSpan _marketOpen     = new TimeSpan(9,  15, 0);

        private static readonly HashSet<CandlePatternDetector.CandlePattern> AllowedPatterns = new()
        {
            CandlePatternDetector.CandlePattern.BullishMarubozu,
            CandlePatternDetector.CandlePattern.BearishMarubozu,
            CandlePatternDetector.CandlePattern.BullishEngulfing,
            CandlePatternDetector.CandlePattern.BearishEngulfing,
        };

        public UltimateCombinedStrategy(
            IOrderService orderService,
            TechnicalIndicatorsTracker tracker,
            Trading.Zerodha.Services.INfoSymbolMaster symbolMaster,
            IStrategicStateStore stateStore,
            ILoggerFactory loggerFactory)
        {
            _orderService  = orderService;
            _tracker       = tracker;
            _loggerFactory = loggerFactory;
            _logger        = _loggerFactory.CreateLogger<UltimateCombinedStrategy>();
            _symbolMaster  = symbolMaster;
            _stateStore    = stateStore;

            _states["NIFTY 50"]   = new SymbolState { Symbol = "NIFTY 50",   SlLimit = 60m,  EmaPeriod = 20, MinBandWidth = 40m  };
            _states["NIFTY BANK"] = new SymbolState { Symbol = "NIFTY BANK", SlLimit = 100m, EmaPeriod = 50, MinBandWidth = 80m  };
        }

        // =====================================================================
        // OnTick — EOD square-off only
        // =====================================================================
        public void OnTick(NormalizedTick tick)
        {
            if (!_states.TryGetValue(tick.Symbol, out var state)) return;

            if (state.IsLong || state.IsShort)
            {
                bool eod = DateTime.Now.TimeOfDay >= _squareOffTime;
                if (eod)
                {
                    _logger.LogInformation("[{Symbol}] Exiting {Position} at {Price}. Reason: EOD Square Off",
                        tick.Symbol, state.IsLong ? "Long" : "Short", tick.Price);

                    if (!string.IsNullOrEmpty(state.PendingSlOrderId))
                    {
                        _orderService.CancelOrder(state.PendingSlOrderId);
                        state.PendingSlOrderId = null;
                    }

                    var futInfo = _symbolMaster.GetActiveFuture(tick.Symbol);
                    _orderService.CloseHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, futInfo.LotSize, state.IsLong);

                    state.IsLong       = false;
                    state.IsShort      = false;
                    state.EntryPrice   = 0;
                    state.SlPrice      = 0;
                    state.OptionSymbol = "";
                }
            }
        }

        // =====================================================================
        // OnCandle — main engine
        // =====================================================================
        public void OnCandle(Candle candle)
        {
            candle.Symbol ??= string.Empty;

            // ── Futures Volume Routing ──
            // Spot indices (NIFTY 50 / NIFTY BANK) have zero traded volume on NSE.
            // When a futures candle arrives we store its volume so UpdateVwap can
            // use it for accurate VWAP calculation.
            if (candle.Symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase))
            {
                string spotSymbol = candle.Symbol.StartsWith("NIFTY BANK", StringComparison.OrdinalIgnoreCase) || candle.Symbol.StartsWith("BANKNIFTY", StringComparison.OrdinalIgnoreCase)
                    ? "NIFTY BANK"
                    : "NIFTY 50";
                _tracker.SetFuturesVolume(spotSymbol, candle.Volume);
                return; // futures candles don't drive strategy logic directly
            }

            if (!_states.TryGetValue(candle.Symbol, out var state)) return;

            var localTime = candle.StartTime.ToLocalTime();
            var tod       = localTime.TimeOfDay;

            // -----------------------------------------------------------------
            // FIX #1 — EMA seeding from pre-warmed TechnicalIndicatorsTracker.
            //
            // ORIGINAL CODE:
            //   if (!state.EmaReady) { state.EmaValue = candle.Close; state.EmaReady = true; }
            //
            // PROBLEM: This seeds the EMA from the very first live candle close,
            // completely discarding the 300-day bootstrap history that Worker.cs
            // pre-warmed into _tracker. The EMA starts near the current price but
            // takes hundreds of candles to converge — during that time all EMA-based
            // entry signals (emaBullish / emaBearish) are unreliable.
            //
            // FIX: On first candle, read the pre-warmed EMA value from _tracker.
            // Only fall back to candle.Close if the tracker has no value yet.
            // -----------------------------------------------------------------
            decimal emaAlpha = 2m / (state.EmaPeriod + 1);
            if (!state.EmaReady)
            {
                // FIX #1 ↓
                var seededEma = _tracker.GetEMA(candle.Symbol, state.EmaPeriod);
                state.EmaValue = seededEma ?? candle.Close;  // FIX #1: use pre-warmed value
                state.EmaReady = true;
                _logger.LogInformation("[{Symbol}] EMA{Period} seeded at {Value} (from {Source})",
                    candle.Symbol, state.EmaPeriod, state.EmaValue,
                    seededEma.HasValue ? "tracker pre-warm" : "first candle fallback"); // FIX #1
            }
            else
            {
                state.EmaValue = emaAlpha * candle.Close + (1 - emaAlpha) * state.EmaValue;
            }

            // ── Update Indicators ──
            _tracker.UpdateVwap(candle.Symbol, candle.High, candle.Low, candle.Close, candle.Volume);

            // ── Day Rollover ──
            if (state.CurrentDay == DateTime.MinValue || localTime.Date > state.CurrentDay.Date)
            {
                if (state.CurrentDay != DateTime.MinValue)
                {
                    _tracker.AddDailyRange(candle.Symbol, state.DayHigh, state.DayLow);
                    state.PrevLHHigh  = state.CurrLHHigh;
                    state.PrevLHLow   = state.CurrLHLow;
                    state.PrevLHReady = state.PrevLHHigh != decimal.MinValue;
                    state.PrevDayH    = state.DayHigh;
                    state.PrevDayL    = state.DayLow;

                    // FIX: Health check log so we can see if PrevLH data is populating correctly
                    _logger.LogInformation("[{Symbol}] Day Rollover: PrevLHReady={Ready} | H={H} L={L} | Band={Band:F1}pts (Min={Min})",
                        candle.Symbol, state.PrevLHReady, state.PrevLHHigh, state.PrevLHLow,
                        state.PrevLHHigh - state.PrevLHLow, state.MinBandWidth);
                }

                state.CurrentDay  = localTime.Date;
                state.TradesToday = 0;
                state.DayHigh     = decimal.MinValue;
                state.DayLow      = decimal.MaxValue;
                state.CurrLHHigh  = decimal.MinValue;
                state.CurrLHLow   = decimal.MaxValue;
                state.PrevCandle  = null;
                
                _tracker.ResetVwap(candle.Symbol);

                // FIX #2 ↓ — reset both counters on day rollover (was resetting single CandlesToday)
                state.OrbCandleCount  = 0;  // FIX #2
                state.ScanCandleCount = 0;  // FIX #2
                state.ProximitySuggestionCandle = 0; // FIX #5

                state.OrbHigh = decimal.MinValue;
                state.OrbLow  = decimal.MaxValue;
                state.OrbSet  = false;

                state.IsGapUp   = state.PrevDayH > 0 && candle.Open > state.PrevDayH;
                state.IsGapDown = state.PrevDayL > 0 && candle.Open < state.PrevDayL;
                state.OrbOpen   = candle.Open;

                state.IsLong  = false;
                state.IsShort = false;

                state.HasBroken3DH  = false;
                state.HasRetraced3DH = false;
                state.HasBroken3DL  = false;
                state.HasRetraced3DL = false;
            }

            state.DayHigh = Math.Max(state.DayHigh, candle.High);
            state.DayLow  = Math.Min(state.DayLow,  candle.Low);

            if (tod >= _lastHourStart && tod < _lastHourEnd)
            {
                state.CurrLHHigh = Math.Max(state.CurrLHHigh, candle.High);
                state.CurrLHLow  = Math.Min(state.CurrLHLow,  candle.Low);
            }

            // ── Build ORB ──
            if (tod <= _orbEnd)
            {
                // FIX #2 ↓ — use OrbCandleCount (was CandlesToday which was shared)
                state.OrbCandleCount++;  // FIX #2
                state.OrbHigh = Math.Max(state.OrbHigh, candle.High);
                state.OrbLow  = Math.Min(state.OrbLow,  candle.Low);
                if (state.OrbCandleCount == 3)  // FIX #2: was state.CandlesToday
                {
                    state.OrbClose    = candle.Close;
                    state.OrbSet      = true;
                    state.OrbIsBearish = state.OrbClose < state.OrbOpen;
                    state.OrbIsBullish = state.OrbClose > state.OrbOpen;
                }
            }

            EvaluateEntry(candle, state);

            // Update state store
            var threeDay = _tracker.GetThreeDayRange(candle.Symbol);
            _stateStore.UpdateSymbolState(candle.Symbol, s => {
                s.Ltp              = candle.Close;
                s.Ema50            = state.EmaValue;
                s.Ema200           = _tracker.GetEMA(candle.Symbol, 200) ?? 0;
                s.ThreeDayHigh     = threeDay?.High ?? 0;
                s.ThreeDayLow      = threeDay?.Low  ?? 0;
                s.Pdh              = state.PrevDayH;
                s.Pdl              = state.PrevDayL;
                s.OrbHigh          = state.OrbHigh;
                s.OrbLow           = state.OrbLow;
                s.ConsolidationHigh = state.PrevLHHigh;
                s.ConsolidationLow  = state.PrevLHLow;
                s.Trend            = s.Ltp > s.Ema200 ? "Bullish" : "Bearish";
            });

            state.PrevCandle = candle;
        }

        // =====================================================================
        // EvaluateEntry
        // =====================================================================
        private void EvaluateEntry(Candle candle, SymbolState state)
        {
            if (state.IsLong || state.IsShort) return;

            var strategicState = _stateStore.GetAllStates().FirstOrDefault(s => s.Symbol == candle.Symbol);
            bool isManualBuy  = strategicState?.ManualOverrideSignal == "BUY";
            bool isManualSell = strategicState?.ManualOverrideSignal == "SELL";

            if (isManualBuy || isManualSell)
            {
                _logger.LogWarning("[{Symbol}] MANUAL OVERRIDE (IMMEDIATE) TRIGGERED: {Signal}",
                    candle.Symbol, strategicState?.ManualOverrideSignal);
                _stateStore.UpdateSymbolState(candle.Symbol, s => s.ManualOverrideSignal = "None");
                ExecuteManualTrade(candle.Symbol, isManualBuy, strategicState?.ManualStopLoss ?? 0);
                return;
            }

            if (strategicState != null && strategicState.ManualTriggerSide != "None")
            {
                bool condBuyTrigger  = strategicState.ManualTriggerSide == "BUY"  && candle.Close >= strategicState.ManualTriggerLevel;
                bool condSellTrigger = strategicState.ManualTriggerSide == "SELL" && candle.Close <= strategicState.ManualTriggerLevel;

                if (condBuyTrigger || condSellTrigger)
                {
                    _logger.LogWarning("[{Symbol}] MANUAL CONDITIONAL TRIGGERED: {Side} @ {Level} (Closed: {Close})",
                        candle.Symbol, strategicState.ManualTriggerSide, strategicState.ManualTriggerLevel, candle.Close);

                    _stateStore.UpdateSymbolState(candle.Symbol, s => {
                        s.ManualTriggerSide  = "None";
                        s.ManualTriggerLevel = 0;
                    });

                    ExecuteManualTrade(candle.Symbol, condBuyTrigger, strategicState.ManualStopLoss);
                    return;
                }
            }

            if (state.TradesToday >= 2 || !state.EmaReady)
            {
                if (state.TradesToday >= 2)
                    _logger.LogInformation("[{Symbol}] Trade skipped: max trades today ({Count}/2).", candle.Symbol, state.TradesToday);
                return;
            }

            var localTime = candle.StartTime.ToLocalTime();
            var tod       = localTime.TimeOfDay;
            bool inWindow = tod > _marketOpen && tod < _entryDeadline;
            if (!inWindow) return;

            bool isBullish  = candle.Close > candle.Open;
            bool isBearish  = candle.Close < candle.Open;
            bool emaBullish = candle.Close > state.EmaValue;
            bool emaBearish = candle.Close < state.EmaValue;
            bool bandOk     = state.PrevLHReady && (state.PrevLHHigh - state.PrevLHLow) >= state.MinBandWidth;

            // ── RSI Filter ──────────────────────────────────────────────────────
            var rsiData  = _tracker.GetRsiAndRma(candle.Symbol);
            bool rsiReady = rsiData.HasValue;
            decimal rsi   = rsiReady ? rsiData!.Value.Rsi : 50m;

            bool rsiLong     = !rsiReady || rsi > 55m;
            bool rsiShort    = !rsiReady || rsi < 45m;
            bool rsiGapLong  = !rsiReady || rsi > 52m;  // Softer threshold for gap days
            bool rsiGapShort = !rsiReady || rsi < 48m;
            // ────────────────────────────────────────────────────────────────────

            bool pdlhLong  = bandOk && candle.High >= state.PrevLHHigh && isBullish && emaBullish && rsiLong;
            bool pdlhShort = bandOk && candle.Low  <= state.PrevLHLow  && isBearish && emaBearish && rsiShort;

            var threeDayRange = _tracker.GetThreeDayRange(state.Symbol);
            if (threeDayRange != null)
            {
                if (state.DayHigh >= threeDayRange.Value.High) state.HasBroken3DH = true;
                if (state.DayLow  <= threeDayRange.Value.Low)  state.HasBroken3DL = true;

                decimal bufferH = threeDayRange.Value.High * 0.0015m;
                decimal bufferL = threeDayRange.Value.Low  * 0.0015m;

                if (state.HasBroken3DH && candle.Low  <= threeDayRange.Value.High + bufferH) state.HasRetraced3DH = true;
                if (state.HasBroken3DL && candle.High >= threeDayRange.Value.Low  - bufferL) state.HasRetraced3DL = true;
            }

            // RSI gates applied: >55 for longs, <45 for shorts (3-day momentum)
            bool tdLongInitial     = threeDayRange != null && candle.High >= threeDayRange.Value.High && isBullish && rsiLong;
            bool tdLongRetracement = threeDayRange != null && state.HasRetraced3DH && candle.Close > threeDayRange.Value.High && isBullish && rsiLong;
            bool tdLong            = tdLongInitial || tdLongRetracement;

            bool tdShortInitial     = threeDayRange != null && candle.Low <= threeDayRange.Value.Low && isBearish && rsiShort;
            bool tdShortRetracement = threeDayRange != null && state.HasRetraced3DL && candle.Close < threeDayRange.Value.Low && isBearish && rsiShort;
            bool tdShort            = tdShortInitial || tdShortRetracement;

            bool gapOrbLong  = state.OrbSet && state.IsGapDown && state.OrbIsBullish && candle.High >= state.OrbHigh && isBullish && rsiGapLong;
            bool gapOrbShort = state.OrbSet && state.IsGapUp   && state.OrbIsBearish && candle.Low  <= state.OrbLow  && isBearish && rsiGapShort;

            bool goLong  = pdlhLong  || tdLong  || gapOrbLong;
            bool goShort = pdlhShort || tdShort || gapOrbShort;

            // ── Status Pulse Log (every 5 candles) ──────────────────────────────
            // Shows all key levels, distances, and live trigger states so the
            // user can monitor what the engine is watching in real-time.
            // ────────────────────────────────────────────────────────────────────
            state.ScanCandleCount++;
            bool shouldLogSuggestion = (state.ScanCandleCount % 5 == 0);

            if (shouldLogSuggestion)
            {
                decimal price = candle.Close;
                string tdHighStr = threeDayRange != null
                    ? $"{threeDayRange.Value.High:N1} (dist={price - threeDayRange.Value.High:+0.#;-0.#;0})"
                    : "N/A";
                string tdLowStr  = threeDayRange != null
                    ? $"{threeDayRange.Value.Low:N1}  (dist={price - threeDayRange.Value.Low:+0.#;-0.#;0})"
                    : "N/A";
                string pdlhHighStr = state.PrevLHReady
                    ? $"{state.PrevLHHigh:N1} (dist={price - state.PrevLHHigh:+0.#;-0.#;0})"
                    : "NOT READY";
                string pdlhLowStr  = state.PrevLHReady
                    ? $"{state.PrevLHLow:N1}  (dist={price - state.PrevLHLow:+0.#;-0.#;0})"
                    : "NOT READY";
                string orbStr = state.OrbSet
                    ? $"H={state.OrbHigh:N1} L={state.OrbLow:N1}"
                    : "NOT SET";

                _logger.LogInformation(
                    "\n╔══════════════ STATUS PULSE [{Symbol}] @ {Time} ══════════════╗\n" +
                    "║  Price : {Price:N2}   EMA{Ep}: {Ema:N2}   [{EmaDir}]  RSI: {Rsi:N1}\n" +
                    "║  3-Day High : {TdH}   Broken={BrH}  Retraced={RtH}  → tdLong={TdL}\n" +
                    "║  3-Day Low  : {TdLo}  Broken={BrL}  Retraced={RtL}  → tdShort={TdS}\n" +
                    "║  PDLH High  : {Ph}   pdlhLong={PdL}  BandOk={Bo}\n" +
                    "║  PDLH Low   : {Pl}   pdlhShort={PdS}\n" +
                    "║  ORB        : {Orb}   GapUp={Gu}  GapDown={Gd}  orbLong={OL}  orbShort={OS}\n" +
                    "║  Trades Today: {Tt}/2   InWindow: {Iw}   GoLong={GL}  GoShort={GS}\n" +
                    "╚══════════════════════════════════════════════════════╝",
                    candle.Symbol, localTime.ToString("HH:mm"),
                    price, state.EmaPeriod, state.EmaValue,
                    emaBullish ? "BULL↑" : (emaBearish ? "BEAR↓" : "FLAT"), rsi,
                    tdHighStr, state.HasBroken3DH, state.HasRetraced3DH, tdLong,
                    tdLowStr,  state.HasBroken3DL, state.HasRetraced3DL, tdShort,
                    pdlhHighStr, pdlhLong, bandOk,
                    pdlhLowStr,  pdlhShort,
                    orbStr, state.IsGapUp, state.IsGapDown, gapOrbLong, gapOrbShort,
                    state.TradesToday, inWindow, goLong, goShort
                );
            }

            // RSI Rejection Log — visible whenever a breakout is structurally valid but RSI blocked it
            if (!goLong && !goShort && rsiReady && shouldLogSuggestion)
            {
                bool structLong  = (bandOk && candle.High >= state.PrevLHHigh && isBullish && emaBullish)
                                 || (threeDayRange != null && candle.High >= threeDayRange.Value.High && isBullish);
                bool structShort = (bandOk && candle.Low <= state.PrevLHLow && isBearish && emaBearish)
                                 || (threeDayRange != null && candle.Low <= threeDayRange.Value.Low && isBearish);

                if (structLong && !rsiLong)
                    _logger.LogWarning("[{Sym}] RSI BLOCKED LONG @ {P:N1} | RSI={R:N1} (need >55)", candle.Symbol, candle.Close, rsi);
                else if (structShort && !rsiShort)
                    _logger.LogWarning("[{Sym}] RSI BLOCKED SHORT @ {P:N1} | RSI={R:N1} (need <45)", candle.Symbol, candle.Close, rsi);
            }

            if (bandOk && state.PrevLHHigh > 0 && candle.Close > 0 && shouldLogSuggestion)
            {
                decimal onePct     = candle.Close * 0.002m;
                bool nearPdlhHigh  = Math.Abs(candle.Close - state.PrevLHHigh) <= onePct;
                bool nearPdlhLow   = Math.Abs(candle.Close - state.PrevLHLow)  <= onePct;

                if (nearPdlhHigh && emaBullish)
                    _logger.LogWarning("[{Symbol}] SUGGESTION: Price {Price:N1} approaching Prev Last-Hour High {High:N1}. Bullish candle = LONG.",
                        candle.Symbol, candle.Close, state.PrevLHHigh);
                else if (nearPdlhLow && emaBearish)
                    _logger.LogWarning("[{Symbol}] SUGGESTION: Price {Price:N1} approaching Prev Last-Hour Low {Low:N1}. Bearish candle = SHORT.",
                        candle.Symbol, candle.Close, state.PrevLHLow);
            }

            if (goLong || goShort)
            {
                var p1     = CandlePatternDetector.DetectSingleCandle(candle);
                var p2     = state.PrevCandle != null
                                ? CandlePatternDetector.DetectTwoCandle(state.PrevCandle, candle)
                                : CandlePatternDetector.CandlePattern.None;
                var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;

                if (!AllowedPatterns.Contains(finalP))
                {
                    string direction = goLong ? "LONG" : "SHORT";
                    string strategy  = goLong
                        ? (gapOrbLong  ? "Gap ORB" : pdlhLong  ? "PDLH" : "3-Day")
                        : (gapOrbShort ? "Gap ORB" : pdlhShort ? "PDLH" : "3-Day");
                    _logger.LogWarning("[{Symbol}] REJECTED {Direction} ({Strategy}): Pattern '{Pattern}' not allowed. Need Marubozu/Engulfing. O={Open:N1} H={High:N1} L={Low:N1} C={Close:N1}",
                        candle.Symbol, direction, strategy, CandlePatternDetector.Describe(finalP),
                        candle.Open, candle.High, candle.Low, candle.Close);
                    return;
                }

                string patternUsed  = CandlePatternDetector.Describe(finalP);
                var    futInfo      = _symbolMaster.GetActiveFuture(state.Symbol);
                string futureSymbol = futInfo.TradingSymbol;
                int    qty          = futInfo.LotSize;

                if (goLong)
                {
                    state.EntryPrice = gapOrbLong ? Math.Max(candle.Open, state.OrbHigh)
                                     : pdlhLong   ? Math.Max(candle.Open, state.PrevLHHigh)
                                     :              Math.Max(candle.Open, threeDayRange!.Value.High);

                    state.SlPrice  = gapOrbLong ? state.OrbLow : state.EntryPrice - state.SlLimit;
                    state.IsLong   = true;
                    state.TradesToday++;

                    var optInfo       = _symbolMaster.GetActiveOtmOption(state.Symbol, state.EntryPrice, true);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] LONGS Executed | Strategy: {Strategy} | Pattern: {Pattern} | Entry: {Entry} | Future: {Future}",
                        state.Symbol, gapOrbLong ? "Gap ORB" : "PDLH/3Day", patternUsed, state.EntryPrice, futureSymbol);

                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, true, state.SlPrice);
                    MarginCalculator.PrintMarginAndCharges(state.Symbol, state.EntryPrice, qty, true);
                }
                else
                {
                    state.EntryPrice = gapOrbShort ? Math.Min(candle.Open, state.OrbLow)
                                     : pdlhShort   ? Math.Min(candle.Open, state.PrevLHLow)
                                     :              Math.Min(candle.Open, threeDayRange!.Value.Low);

                    state.SlPrice  = gapOrbShort ? state.OrbHigh : state.EntryPrice + state.SlLimit;
                    state.IsShort  = true;
                    state.TradesToday++;

                    var optInfo        = _symbolMaster.GetActiveOtmOption(state.Symbol, state.EntryPrice, false);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] SHORTS Executed | Strategy: {Strategy} | Pattern: {Pattern} | Entry: {Entry} | Future: {Future}",
                        state.Symbol, gapOrbShort ? "Gap ORB" : "PDLH/3Day", patternUsed, state.EntryPrice, futureSymbol);

                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, false, state.SlPrice);
                    MarginCalculator.PrintMarginAndCharges(state.Symbol, state.EntryPrice, qty, false);
                }
            }
            else if (inWindow && !state.IsLong && !state.IsShort)
            {
                // FIX #2 ↓ — gate-failure summary now uses ScanCandleCount (not CandlesToday)
                if (state.ScanCandleCount % 10 == 0)  // FIX #2
                {
                    var reasons = new System.Text.StringBuilder();
                    if (threeDayRange == null)           reasons.Append("| 3DayRange=null ");
                    else if (!isBullish && !isBearish)   reasons.Append("| Candle=Doji ");
                    if (!emaBullish && !emaBearish)      reasons.Append("| EMA=flat ");
                    if (!bandOk)                         reasons.Append("| PDLH=band<min ");
                    if (!state.OrbSet)                   reasons.Append("| ORB=not set ");

                    if (threeDayRange != null)
                    {
                        decimal pctToHigh = (threeDayRange.Value.High - candle.Close) / candle.Close * 100;
                        decimal pctToLow  = (candle.Close - threeDayRange.Value.Low)  / candle.Close * 100;
                        reasons.Append($"| 3DH={threeDayRange.Value.High:N1}({pctToHigh:+0.0;-0.0}%) 3DL={threeDayRange.Value.Low:N1}({pctToLow:+0.0;-0.0}%) ");
                    }

                    _logger.LogInformation("[{Symbol}] SCAN: No setup found yet. LTP={LTP:N1} EMA={EMA:N1} {Reasons}",
                        candle.Symbol, candle.Close, state.EmaValue, reasons.ToString());
                }
            }
        }

        // =====================================================================
        // ExecuteManualTrade
        // =====================================================================
        private void ExecuteManualTrade(string symbol, bool isLong, decimal manualSl = 0)
        {
            if (!_states.TryGetValue(symbol, out var state)) return;

            var    futInfo      = _symbolMaster.GetActiveFuture(symbol);
            string futureSymbol = futInfo.TradingSymbol;
            int    qty          = futInfo.LotSize;

            // -----------------------------------------------------------------
            // FIX #4 — EMA-9 fallback guarded with proper null coalescing chain.
            //
            // ORIGINAL CODE:
            //   state.EntryPrice = _tracker.GetEMA(symbol, 9) ?? 0;
            //   if (state.EntryPrice == 0) state.EntryPrice = state.EmaValue;
            //
            // PROBLEM: EMA-9 is never tracked by TechnicalIndicatorsTracker
            // (only 50/100/200 are bootstrapped). GetEMA(symbol, 9) always
            // returns null, so this always fell through to state.EmaValue anyway,
            // but with an unnecessary log-misleading intermediate zero assignment.
            //
            // FIX: collapse to a single null-coalescing expression; add comment.
            // -----------------------------------------------------------------
            // FIX #4 ↓
            state.EntryPrice = _tracker.GetEMA(symbol, 9) ?? state.EmaValue;  // FIX #4: EMA-9 rarely available; falls back to strategy EMA
            if (state.EntryPrice == 0)
            {
                _logger.LogWarning("[{Symbol}] Manual trade: EntryPrice could not be determined from EMA. Using 0.", symbol);
            }

            if (isLong)
            {
                state.SlPrice  = manualSl > 0 ? manualSl : state.EntryPrice - state.SlLimit;
                state.IsLong   = true;
                state.TradesToday++;

                var optInfo        = _symbolMaster.GetActiveOtmOption(symbol, state.EntryPrice, true);
                state.OptionSymbol = optInfo.TradingSymbol;

                _logger.LogWarning("[{Symbol}] MANUAL {TradeType} EXECUTION | Future: {Future} | SL: {SL}",
                    symbol, manualSl > 0 ? "CONDITIONAL" : "IMMEDIATE", futureSymbol, state.SlPrice);

                state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, true, state.SlPrice);
            }
            else
            {
                state.SlPrice  = manualSl > 0 ? manualSl : state.EntryPrice + state.SlLimit;
                state.IsShort  = true;
                state.TradesToday++;

                var optInfo        = _symbolMaster.GetActiveOtmOption(symbol, state.EntryPrice, false);
                state.OptionSymbol = optInfo.TradingSymbol;

                _logger.LogWarning("[{Symbol}] MANUAL {TradeType} EXECUTION | Future: {Future} | SL: {SL}",
                    symbol, manualSl > 0 ? "CONDITIONAL" : "IMMEDIATE", futureSymbol, state.SlPrice);

                state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, false, state.SlPrice);
            }
        }

        // =====================================================================
        // ProcessWebhook — SL native trigger callback
        // =====================================================================
        public void ProcessWebhook(System.Text.Json.JsonElement payload)
        {
            try
            {
                if (payload.TryGetProperty("order_id", out var oid) &&
                    payload.TryGetProperty("status",   out var status))
                {
                    string orderId     = oid.GetString()    ?? "";
                    string orderStatus = status.GetString() ?? "";

                    if (orderStatus == "COMPLETE")
                    {
                        foreach (var kvp in _states)
                        {
                            var state = kvp.Value;
                            if (state.PendingSlOrderId == orderId)
                            {
                                _logger.LogInformation(
                                    "NATIVE SL TRIGGERED! [{Symbol}] Webhook confirmed SL execution. Squaring off option hedge.",
                                    state.Symbol);

                                var futInfo = _symbolMaster.GetActiveFuture(state.Symbol);
                                _orderService.PlaceMarketOrder(state.OptionSymbol, "NFO", futInfo.LotSize, "SELL");

                                // -----------------------------------------------------------------
                                // FIX #3 — Clear ALL position-related fields after SL webhook hit.
                                //
                                // ORIGINAL CODE only cleared IsLong/IsShort and PendingSlOrderId.
                                // EntryPrice, SlPrice, and OptionSymbol were left with stale values.
                                //
                                // PROBLEM: If TradesToday < 2, the strategy could take a second
                                // trade with corrupted EntryPrice/SlPrice/OptionSymbol from the
                                // previous trade, leading to wrong SL placement on the new order.
                                //
                                // FIX: Reset all position fields to clean state after SL trigger.
                                // -----------------------------------------------------------------
                                state.IsLong           = false;   // same as before
                                state.IsShort          = false;   // same as before
                                state.PendingSlOrderId = null;    // same as before
                                state.EntryPrice       = 0;       // FIX #3 ↓
                                state.SlPrice          = 0;       // FIX #3
                                state.OptionSymbol     = "";      // FIX #3
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Webhook POSTback payload.");
            }
        }
    }
}
