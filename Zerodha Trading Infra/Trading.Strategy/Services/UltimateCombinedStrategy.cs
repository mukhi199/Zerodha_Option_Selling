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

        public int CandlesToday { get; set; } = 0;
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
        private readonly TimeSpan _entryDeadline = new TimeSpan(12, 0, 0);
        private readonly TimeSpan _lastHourStart = new TimeSpan(14, 15, 0);
        private readonly TimeSpan _lastHourEnd = new TimeSpan(15, 15, 0);
        private readonly TimeSpan _squareOffTime = new TimeSpan(15, 15, 0);
        private readonly TimeSpan _orbEnd = new TimeSpan(9, 45, 0);
        private readonly TimeSpan _marketOpen = new TimeSpan(9, 15, 0);

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
            _orderService = orderService;
            _tracker = tracker;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<UltimateCombinedStrategy>();
            _symbolMaster = symbolMaster;
            _stateStore = stateStore;
            
            // Pre-initialize states based on optimized grid scan parameters
            _states["NIFTY 50"] = new SymbolState { Symbol = "NIFTY 50", SlLimit = 60m, EmaPeriod = 20, MinBandWidth = 40m };
            _states["NIFTY BANK"] = new SymbolState { Symbol = "NIFTY BANK", SlLimit = 100m, EmaPeriod = 50, MinBandWidth = 80m };
        }

        public void OnTick(NormalizedTick tick)
        {
            if (!_states.TryGetValue(tick.Symbol, out var state)) return;

            if (state.IsLong || state.IsShort)
            {
                DateTime now = DateTime.Now;
                bool eod = now.TimeOfDay >= _squareOffTime;

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
                    
                    state.IsLong = false;
                    state.IsShort = false;
                    state.EntryPrice = 0;
                    state.SlPrice = 0;
                    state.OptionSymbol = "";
                }
            }
        }

        public void OnCandle(Candle candle)
        {
            candle.Symbol ??= string.Empty;
            if (!_states.TryGetValue(candle.Symbol, out var state)) 
            {
                return;
            }

            var localTime = candle.StartTime.ToLocalTime();
            var tod = localTime.TimeOfDay;

            // ── EMA Update ──
            decimal emaAlpha = 2m / (state.EmaPeriod + 1);
            if (!state.EmaReady) { state.EmaValue = candle.Close; state.EmaReady = true; }
            else { state.EmaValue = emaAlpha * candle.Close + (1 - emaAlpha) * state.EmaValue; }

            // ── Day Rollover ──
            if (state.CurrentDay == DateTime.MinValue || localTime.Date > state.CurrentDay.Date)
            {
                if (state.CurrentDay != DateTime.MinValue)
                {
                    _tracker.AddDailyRange(candle.Symbol, state.DayHigh, state.DayLow);
                    state.PrevLHHigh = state.CurrLHHigh;
                    state.PrevLHLow = state.CurrLHLow;
                    state.PrevLHReady = state.PrevLHHigh != decimal.MinValue;
                    state.PrevDayH = state.DayHigh;
                    state.PrevDayL = state.DayLow;
                }
                
                state.CurrentDay = localTime.Date;
                state.TradesToday = 0;
                state.DayHigh = decimal.MinValue;
                state.DayLow = decimal.MaxValue;
                state.CurrLHHigh = decimal.MinValue;
                state.CurrLHLow = decimal.MaxValue;
                state.PrevCandle = null;

                state.CandlesToday = 0;
                state.OrbHigh = decimal.MinValue;
                state.OrbLow = decimal.MaxValue;
                state.OrbSet = false;
                
                state.IsGapUp = state.PrevDayH > 0 && candle.Open > state.PrevDayH;
                state.IsGapDown = state.PrevDayL > 0 && candle.Open < state.PrevDayL;
                state.OrbOpen = candle.Open;

                // Also reset positions just in case of stale state
                state.IsLong = state.IsShort = false;
                
                // Reset Retracement logic
                state.HasBroken3DH = false;
                state.HasRetraced3DH = false;
                state.HasBroken3DL = false;
                state.HasRetraced3DL = false;
            }

            state.DayHigh = Math.Max(state.DayHigh, candle.High);
            state.DayLow = Math.Min(state.DayLow, candle.Low);

            if (tod >= _lastHourStart && tod < _lastHourEnd)
            {
                state.CurrLHHigh = Math.Max(state.CurrLHHigh, candle.High);
                state.CurrLHLow = Math.Min(state.CurrLHLow, candle.Low);
            }

            // ── Build ORB ──
            if (tod <= _orbEnd)
            {
                state.CandlesToday++;
                state.OrbHigh = Math.Max(state.OrbHigh, candle.High);
                state.OrbLow = Math.Min(state.OrbLow, candle.Low);
                if (state.CandlesToday == 3) // 9:25 candle finishes ORB
                {
                    state.OrbClose = candle.Close;
                    state.OrbSet = true;
                    state.OrbIsBearish = state.OrbClose < state.OrbOpen;
                    state.OrbIsBullish = state.OrbClose > state.OrbOpen;
                }
            }

            // Entry Logging happens ONLY if we are flat, before deadlines, under max trades, and EMA is ready
            EvaluateEntry(candle, state);

            // Update Strategic State Store for automated reporting
            var threeDay = _tracker.GetThreeDayRange(candle.Symbol);
            _stateStore.UpdateSymbolState(candle.Symbol, s => {
                s.Ltp = candle.Close;
                s.Ema50 = state.EmaValue;
                s.Ema200 = _tracker.GetEMA(candle.Symbol, 200) ?? 0;
                s.ThreeDayHigh = threeDay?.High ?? 0;
                s.ThreeDayLow = threeDay?.Low ?? 0;
                s.Pdh = state.PrevDayH;
                s.Pdl = state.PrevDayL;
                s.OrbHigh = state.OrbHigh;
                s.OrbLow = state.OrbLow;
                s.ConsolidationHigh = state.PrevLHHigh;
                s.ConsolidationLow = state.PrevLHLow;
                s.Trend = s.Ltp > s.Ema200 ? "Bullish" : "Bearish";
            });

            state.PrevCandle = candle;
        }

        private void EvaluateEntry(Candle candle, SymbolState state)
        {
            if (state.IsLong || state.IsShort)
                return;

            var strategicState = _stateStore.GetAllStates().FirstOrDefault(s => s.Symbol == candle.Symbol);
            bool isManualBuy = strategicState?.ManualOverrideSignal == "BUY";
            bool isManualSell = strategicState?.ManualOverrideSignal == "SELL";

            if (isManualBuy || isManualSell)
            {
                _logger.LogWarning("[{Symbol}] MANUAL OVERRIDE (IMMEDIATE) TRIGGERED: {Signal}", candle.Symbol, strategicState?.ManualOverrideSignal);
                _stateStore.UpdateSymbolState(candle.Symbol, s => s.ManualOverrideSignal = "None");
                
                ExecuteManualTrade(candle.Symbol, isManualBuy, strategicState?.ManualStopLoss ?? 0);
                return;
            }

            // --- Conditional Manual Trigger ---
            if (strategicState != null && strategicState.ManualTriggerSide != "None")
            {
                bool condBuyTrigger = strategicState.ManualTriggerSide == "BUY" && candle.Close >= strategicState.ManualTriggerLevel;
                bool condSellTrigger = strategicState.ManualTriggerSide == "SELL" && candle.Close <= strategicState.ManualTriggerLevel;

                if (condBuyTrigger || condSellTrigger)
                {
                    _logger.LogWarning("[{Symbol}] MANUAL CONDITIONAL TRIGGERED: {Side} @ {Level} (Closed: {Close})", 
                                       candle.Symbol, strategicState.ManualTriggerSide, strategicState.ManualTriggerLevel, candle.Close);
                    
                    _stateStore.UpdateSymbolState(candle.Symbol, s => {
                        s.ManualTriggerSide = "None";
                        s.ManualTriggerLevel = 0;
                    });

                    ExecuteManualTrade(candle.Symbol, condBuyTrigger, strategicState.ManualStopLoss);
                    return;
                }
            }

            if (state.TradesToday >= 2 || !state.EmaReady)
            {
                if (state.TradesToday >= 2)
                    _logger.LogInformation("[{Symbol}] Trade skipped: max trades today reached ({Count}/2).", candle.Symbol, state.TradesToday);
                return;
            }

            var localTime = candle.StartTime.ToLocalTime();
            var tod = localTime.TimeOfDay;
            bool inWindow = tod > _marketOpen && tod < _entryDeadline;
            if (!inWindow) return;

            bool isBullish = candle.Close > candle.Open;
            bool isBearish = candle.Close < candle.Open;
            
            bool emaBullish = candle.Close > state.EmaValue;
            bool emaBearish = candle.Close < state.EmaValue;

            bool bandOk = state.PrevLHReady && (state.PrevLHHigh - state.PrevLHLow) >= state.MinBandWidth;

            bool pdlhLong = bandOk && candle.High >= state.PrevLHHigh && isBullish && emaBullish;
            bool pdlhShort = bandOk && candle.Low <= state.PrevLHLow && isBearish && emaBearish;

            var threeDayRange = _tracker.GetThreeDayRange(state.Symbol);
            if (threeDayRange != null)
            {
                // Has it broken out today?
                if (state.DayHigh >= threeDayRange.Value.High) state.HasBroken3DH = true;
                if (state.DayLow <= threeDayRange.Value.Low) state.HasBroken3DL = true;

                // Did it retrace? (Pullback to within 0.15% of the 3-day line or below/above it)
                decimal bufferH = threeDayRange.Value.High * 0.0015m;
                decimal bufferL = threeDayRange.Value.Low * 0.0015m;
                
                if (state.HasBroken3DH && candle.Low <= threeDayRange.Value.High + bufferH)
                    state.HasRetraced3DH = true;
                
                if (state.HasBroken3DL && candle.High >= threeDayRange.Value.Low - bufferL)
                    state.HasRetraced3DL = true;
            }

            bool tdLongInitial = threeDayRange != null && candle.High >= threeDayRange.Value.High && isBullish && emaBullish;
            bool tdLongRetracement = threeDayRange != null && state.HasRetraced3DH && candle.Close > threeDayRange.Value.High && isBullish && emaBullish;
            bool tdLong = tdLongInitial || tdLongRetracement;

            bool tdShortInitial = threeDayRange != null && candle.Low <= threeDayRange.Value.Low && isBearish && emaBearish;
            bool tdShortRetracement = threeDayRange != null && state.HasRetraced3DL && candle.Close < threeDayRange.Value.Low && isBearish && emaBearish;
            bool tdShort = tdShortInitial || tdShortRetracement;

            bool gapOrbLong = state.OrbSet && state.IsGapDown && state.OrbIsBullish && candle.High >= state.OrbHigh && isBullish;
            bool gapOrbShort = state.OrbSet && state.IsGapUp && state.OrbIsBearish && candle.Low <= state.OrbLow && isBearish;

            bool goLong = pdlhLong || tdLong || gapOrbLong;
            bool goShort = pdlhShort || tdShort || gapOrbShort;

            // ── PROXIMITY SUGGESTIONS (fires when price is within 0.2% of a key level) ──
            if (threeDayRange != null && candle.Close > 0)
            {
                decimal onePct = candle.Close * 0.002m;
                bool nearTdHigh = Math.Abs(candle.Close - threeDayRange.Value.High) <= onePct;
                bool nearTdLow  = Math.Abs(candle.Close - threeDayRange.Value.Low)  <= onePct;

                if (nearTdHigh && emaBullish)
                    _logger.LogWarning("[{Symbol}] 💡 SUGGESTION: Price {Price:N1} is approaching 3-Day High {High:N1}. A bullish Marubozu/Engulfing candle here would trigger a LONG.", candle.Symbol, candle.Close, threeDayRange.Value.High);
                else if (nearTdLow && emaBearish)
                    _logger.LogWarning("[{Symbol}] 💡 SUGGESTION: Price {Price:N1} is approaching 3-Day Low {Low:N1}. A bearish Marubozu/Engulfing candle here would trigger a SHORT.", candle.Symbol, candle.Close, threeDayRange.Value.Low);
            }

            if (bandOk && state.PrevLHHigh > 0 && candle.Close > 0)
            {
                decimal onePct = candle.Close * 0.002m;
                bool nearPdlhHigh = Math.Abs(candle.Close - state.PrevLHHigh) <= onePct;
                bool nearPdlhLow  = Math.Abs(candle.Close - state.PrevLHLow)  <= onePct;

                if (nearPdlhHigh && emaBullish)
                    _logger.LogWarning("[{Symbol}] 💡 SUGGESTION: Price {Price:N1} approaching Prev Last-Hour High {High:N1}. Bullish candle here = LONG.", candle.Symbol, candle.Close, state.PrevLHHigh);
                else if (nearPdlhLow && emaBearish)
                    _logger.LogWarning("[{Symbol}] 💡 SUGGESTION: Price {Price:N1} approaching Prev Last-Hour Low {Low:N1}. Bearish candle here = SHORT.", candle.Symbol, candle.Close, state.PrevLHLow);
            }

            if (goLong || goShort)
            {
                // Gate 3 — Candlestick Polarity validation (Marubozu/Engulfing strictly enforced)
                var p1 = CandlePatternDetector.DetectSingleCandle(candle);
                var p2 = state.PrevCandle != null 
                            ? CandlePatternDetector.DetectTwoCandle(state.PrevCandle, candle)
                            : CandlePatternDetector.CandlePattern.None;
                
                var finalP = p2 != CandlePatternDetector.CandlePattern.None ? p2 : p1;

                if (!AllowedPatterns.Contains(finalP))
                {
                    string direction = goLong ? "LONG" : "SHORT";
                    string strategy = goLong 
                        ? (gapOrbLong ? "Gap ORB" : pdlhLong ? "PDLH" : "3-Day")
                        : (gapOrbShort ? "Gap ORB" : pdlhShort ? "PDLH" : "3-Day");
                    _logger.LogWarning("[{Symbol}] ❌ REJECTED {Direction} ({Strategy}): Candle pattern '{Pattern}' not allowed. Need Marubozu or Engulfing. O={Open:N1} H={High:N1} L={Low:N1} C={Close:N1}",
                        candle.Symbol, direction, strategy, CandlePatternDetector.Describe(finalP),
                        candle.Open, candle.High, candle.Low, candle.Close);
                    return;
                }

                string patternUsed = CandlePatternDetector.Describe(finalP);
                var futInfo = _symbolMaster.GetActiveFuture(state.Symbol);
                string futureSymbol = futInfo.TradingSymbol;
                int qty = futInfo.LotSize;

                if (goLong)
                {
                    state.EntryPrice = gapOrbLong ? Math.Max(candle.Open, state.OrbHigh) :
                                       pdlhLong ? Math.Max(candle.Open, state.PrevLHHigh) :
                                       Math.Max(candle.Open, threeDayRange!.Value.High);
                    
                    state.SlPrice = gapOrbLong ? state.OrbLow : state.EntryPrice - state.SlLimit;
                    state.IsLong = true;
                    state.TradesToday++;
                    
                    var optInfo = _symbolMaster.GetActiveOtmOption(state.Symbol, state.EntryPrice, true);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] ✅ LONGS Executed | Strategy: {Strategy} | Pattern: {Pattern} | Entry: {Entry} | Future: {Future}", 
                                           state.Symbol, gapOrbLong ? "Gap ORB" : "PDLH/3Day", patternUsed, state.EntryPrice, futureSymbol);
                    
                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, true, state.SlPrice);
                    MarginCalculator.PrintMarginAndCharges(state.Symbol, state.EntryPrice, qty, true);
                }
                else
                {
                    state.EntryPrice = gapOrbShort ? Math.Min(candle.Open, state.OrbLow) :
                                       pdlhShort ? Math.Min(candle.Open, state.PrevLHLow) :
                                       Math.Min(candle.Open, threeDayRange!.Value.Low);
                                       
                    state.SlPrice = gapOrbShort ? state.OrbHigh : state.EntryPrice + state.SlLimit;
                    state.IsShort = true;
                    state.TradesToday++;
                    
                    var optInfo = _symbolMaster.GetActiveOtmOption(state.Symbol, state.EntryPrice, false);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] ✅ SHORTS Executed | Strategy: {Strategy} | Pattern: {Pattern} | Entry: {Entry} | Future: {Future}", 
                                           state.Symbol, gapOrbShort ? "Gap ORB" : "PDLH/3Day", patternUsed, state.EntryPrice, futureSymbol);
                    
                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, false, state.SlPrice);
                    MarginCalculator.PrintMarginAndCharges(state.Symbol, state.EntryPrice, qty, false);
                }
            }
            else if (inWindow && !state.IsLong && !state.IsShort)
            {
                // ── GATE FAILURE SUMMARY every 10 candles to avoid log flooding ──
                state.CandlesToday++;
                if (state.CandlesToday % 10 == 0)
                {
                    var reasons = new System.Text.StringBuilder();
                    if (threeDayRange == null)             reasons.Append("| 3DayRange=null ");
                    else if (!isBullish && !isBearish)     reasons.Append("| Candle=Doji ");
                    if (!emaBullish && !emaBearish)        reasons.Append("| EMA=flat ");
                    if (!bandOk)                           reasons.Append("| PDLH=band<min ");
                    if (!state.OrbSet)                     reasons.Append("| ORB=not set ");

                    if (threeDayRange != null)
                    {
                        decimal pctToHigh = (threeDayRange.Value.High - candle.Close) / candle.Close * 100;
                        decimal pctToLow  = (candle.Close - threeDayRange.Value.Low) / candle.Close * 100;
                        reasons.Append($"| 3DH={threeDayRange.Value.High:N1}({pctToHigh:+0.0;-0.0}%) 3DL={threeDayRange.Value.Low:N1}({pctToLow:+0.0;-0.0}%) ");
                    }

                    _logger.LogInformation("[{Symbol}] 📊 SCAN: No setup found yet. LTP={LTP:N1} EMA={EMA:N1} {Reasons}",
                        candle.Symbol, candle.Close, state.EmaValue, reasons.ToString());
                }
            }
        }


        private void ExecuteManualTrade(string symbol, bool isLong, decimal manualSl = 0)
        {
            if (!_states.TryGetValue(symbol, out var state)) return;

            var futInfo = _symbolMaster.GetActiveFuture(symbol);
            string futureSymbol = futInfo.TradingSymbol;
            int qty = futInfo.LotSize;

            state.EntryPrice = _tracker.GetEMA(symbol, 9) ?? 0; // fallback to EMA for entry price log
            if (state.EntryPrice == 0) state.EntryPrice = state.EmaValue;

            if (isLong)
            {
                state.SlPrice = manualSl > 0 ? manualSl : state.EntryPrice - state.SlLimit;
                state.IsLong = true;
                state.TradesToday++;
                
                var optInfo = _symbolMaster.GetActiveOtmOption(symbol, state.EntryPrice, true);
                state.OptionSymbol = optInfo.TradingSymbol;

                _logger.LogWarning("[{Symbol}] !!! MANUAL {TradeType} EXECUTION !!! | Future: {Future} | SL: {SL}", 
                                   symbol, manualSl > 0 ? "CONDITIONAL" : "IMMEDIATE", futureSymbol, state.SlPrice);
                
                state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, true, state.SlPrice);
            }
            else
            {
                state.SlPrice = manualSl > 0 ? manualSl : state.EntryPrice + state.SlLimit;
                state.IsShort = true;
                state.TradesToday++;
                
                var optInfo = _symbolMaster.GetActiveOtmOption(symbol, state.EntryPrice, false);
                state.OptionSymbol = optInfo.TradingSymbol;

                _logger.LogWarning("[{Symbol}] !!! MANUAL {TradeType} EXECUTION !!! | Future: {Future} | SL: {SL}", 
                                   symbol, manualSl > 0 ? "CONDITIONAL" : "IMMEDIATE", futureSymbol, state.SlPrice);
                
                state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futureSymbol, state.OptionSymbol, qty, false, state.SlPrice);
            }
        }

        public void ProcessWebhook(System.Text.Json.JsonElement payload)
        {
            try
            {
                if (payload.TryGetProperty("order_id", out var oid) && payload.TryGetProperty("status", out var status))
                {
                    string orderId = oid.GetString() ?? "";
                    string orderStatus = status.GetString() ?? "";

                    if (orderStatus == "COMPLETE")
                    {
                        foreach (var kvp in _states)
                        {
                            var state = kvp.Value;
                            if (state.PendingSlOrderId == orderId)
                            {
                                _logger.LogInformation("\ud83d\udea8 NATIVE SL TRIGGERED! [{Symbol}] Webhook confirmed Stop Loss execution. Squaring off Option hedge.", state.Symbol);
                                
                                var futInfo = _symbolMaster.GetActiveFuture(state.Symbol);
                                
                                // Future is already squared off by SL. We only need to dump the active Option hedge.
                                _orderService.PlaceMarketOrder(state.OptionSymbol, "NFO", futInfo.LotSize, "SELL");
                                
                                state.IsLong = false;
                                state.IsShort = false;
                                state.PendingSlOrderId = null;
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
