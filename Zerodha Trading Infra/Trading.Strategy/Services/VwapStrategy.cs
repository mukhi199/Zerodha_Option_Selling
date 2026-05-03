using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Trading.Core.Models;
using Trading.Zerodha.Services;

namespace Trading.Strategy.Services
{
    public class VwapStrategy : IStrategy
    {
        private class State
        {
            public string Symbol { get; set; } = string.Empty;
            public DateTime CurrentDate { get; set; } = DateTime.MinValue;
            public int TradesToday { get; set; } = 0;
            public int ScanCandleCount { get; set; } = 0;
            
            public bool IsActive { get; set; } = false;
            public bool IsLong { get; set; } = false;
            public decimal EntryPrice { get; set; } = 0;
            public decimal StopLossLevel { get; set; } = 0;
            public decimal TargetLevel { get; set; } = 0;
            
            public string OptionSymbol { get; set; } = string.Empty;
            public string PendingSlOrderId { get; set; } = string.Empty;
            public Candle? PrevCandle { get; set; }
            public decimal DayPnl { get; set; } = 0;
        }

        private readonly ConcurrentDictionary<string, State> _states = new();
        private readonly OrderExecutionService _orderService;
        private readonly INfoSymbolMaster _symbolMaster;
        private readonly TechnicalIndicatorsTracker _tracker;
        private readonly ILogger<VwapStrategy> _logger;
        
        private readonly TimeSpan _windowStart = new TimeSpan(9, 15, 0);
        private readonly TimeSpan _windowEnd = new TimeSpan(14, 30, 0);
        private readonly TimeSpan _squareOffTime = new TimeSpan(15, 15, 0);

        public VwapStrategy(
            OrderExecutionService orderService, 
            INfoSymbolMaster symbolMaster, 
            TechnicalIndicatorsTracker tracker,
            ILogger<VwapStrategy> logger)
        {
            _orderService = orderService;
            _symbolMaster = symbolMaster;
            _tracker = tracker;
            _logger = logger;

            _states["NIFTY 50"] = new State { Symbol = "NIFTY 50" };
            _states["NIFTY BANK"] = new State { Symbol = "NIFTY BANK" };
        }

        public void OnTick(NormalizedTick tick)
        {
            if (!_states.TryGetValue(tick.Symbol, out var state) || !state.IsActive) return;

            var now = DateTime.Now.TimeOfDay;
            
            if (now >= _squareOffTime)
            {
                _logger.LogInformation("[{Symbol}] VWAP Strategy: Exiting at EOD (3:15 PM).", state.Symbol);
                SquareOff(state, tick.Price, "EOD Base");
                return;
            }

            bool hitSl = state.IsLong ? (tick.Price <= state.StopLossLevel) : (tick.Price >= state.StopLossLevel);
            bool hitTp = state.IsLong ? (tick.Price >= state.TargetLevel) : (tick.Price <= state.TargetLevel);

            if (hitTp)
            {
                SquareOff(state, tick.Price, "Take Profit (1.5x RR)");
            }
            // Optional manual immediate SL execution here if tick surpasses SL heavily (depends on system).
            // Usually the pending basket handles SL automatically. If we need to close basket, we must check if pending order is hit via broker updates. We'll do a simple fallback square off.
            else if (hitSl)
            {
                SquareOff(state, tick.Price, "Stop Loss");
            }
        }

        public void OnCandle(Candle candle)
        {
            // Route futures volume to VWAP tracker (spot symbols have no volume)
            if (candle.Symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase))
            {
                string spotSym = candle.Symbol.StartsWith("BANKNIFTY", StringComparison.OrdinalIgnoreCase)
                    ? "NIFTY BANK" : "NIFTY 50";
                _tracker.SetFuturesVolume(spotSym, candle.Volume);
                return;
            }

            if (!_states.TryGetValue(candle.Symbol, out var state)) return;


            var localTime = candle.StartTime.ToLocalTime();
            var tod = localTime.TimeOfDay;
            var date = localTime.Date;

            // Rollover reset
            if (date > state.CurrentDate)
            {
                state.CurrentDate = date;
                state.TradesToday = 0;
                state.ScanCandleCount = 0;
                state.PrevCandle = null;
                state.DayPnl = 0;
                _tracker.ResetVwap(candle.Symbol);
                if (state.IsActive)
                {
                    state.IsActive = false;
                }
            }

            _tracker.UpdateVwap(candle.Symbol, candle.High, candle.Low, candle.Close, candle.Volume);

            // Throttle logs (every 3 candles = 15 minutes for 5-min timeframe)
            state.ScanCandleCount++;
            bool shouldLog = (state.ScanCandleCount % 3 == 0);

            var vwap = _tracker.GetVwap(candle.Symbol);
            var prevCandle = state.PrevCandle;
            state.PrevCandle = candle; // store for next time

            if (state.IsActive || state.TradesToday >= 2 || !vwap.HasValue || prevCandle == null) return;
            if (tod < _windowStart || tod >= _windowEnd) return;

            var rsiData = _tracker.GetRsiAndRma(candle.Symbol);
            var ema20 = _tracker.GetEMA(candle.Symbol, 20);
            var cpr = _tracker.GetDailyCPR(candle.Symbol);

            if (rsiData == null || !ema20.HasValue || cpr == null) return;

            bool isBullish = candle.Close > candle.Open;
            bool isBearish = candle.Close < candle.Open;

            bool isVwapLongCross = prevCandle.Close <= vwap.Value && candle.Close > vwap.Value;
            bool isVwapShortCross = prevCandle.Close >= vwap.Value && candle.Close < vwap.Value;

            bool aboveCpr = candle.Close > cpr.TopCentral;
            bool belowCpr = candle.Close < cpr.BottomCentral;

            bool trendUp = candle.Close > ema20.Value;
            bool trendDown = candle.Close < ema20.Value;

            bool rsiBull = rsiData.Value.Rsi > 55m;
            bool rsiBear = rsiData.Value.Rsi < 45m;

            bool condLong = isVwapLongCross && aboveCpr && trendUp && rsiBull && isBullish;
            bool condShort = isVwapShortCross && belowCpr && trendDown && rsiBear && isBearish;

            if (shouldLog)
            {
                _logger.LogInformation("\n[VWAP PULSE] {Symbol} @ {Tod} | P:{P:N1} V:{Vw:N1} | CPR T:{Ct:N1} B:{Cb:N1} | EMA20:{E:N1} | RSI:{R:N1} || L={Cl} S={Cs}",
                    candle.Symbol, localTime.ToString("HH:mm"), candle.Close, vwap.Value, cpr.TopCentral, cpr.BottomCentral, ema20.Value, rsiData.Value.Rsi, condLong, condShort);
            }

            if (condLong || condShort)
            {
                state.TradesToday++;
                state.IsActive = true;
                state.IsLong = condLong;
                state.EntryPrice = candle.Close;

                var futInfo = _symbolMaster.GetActiveFuture(candle.Symbol);
                int qty = futInfo.LotSize;

                if (condLong)
                {
                    state.StopLossLevel = prevCandle.Low;
                    decimal risk = state.EntryPrice - state.StopLossLevel;
                    state.TargetLevel = state.EntryPrice + (risk * 1.5m);

                    var optInfo = _symbolMaster.GetActiveOtmOption(candle.Symbol, state.EntryPrice, true);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] VWAP LONG Triggered! Entry={En:N1} SL={Sl:N1} Target={Tp:N1}", candle.Symbol, state.EntryPrice, state.StopLossLevel, state.TargetLevel);
                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, qty, true, state.StopLossLevel);
                }
                else
                {
                    state.StopLossLevel = prevCandle.High;
                    decimal risk = state.StopLossLevel - state.EntryPrice;
                    state.TargetLevel = state.EntryPrice - (risk * 1.5m);

                    var optInfo = _symbolMaster.GetActiveOtmOption(candle.Symbol, state.EntryPrice, false);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] VWAP SHORT Triggered! Entry={En:N1} SL={Sl:N1} Target={Tp:N1}", candle.Symbol, state.EntryPrice, state.StopLossLevel, state.TargetLevel);
                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, qty, false, state.StopLossLevel);
                }
            }
        }

        private void SquareOff(State state, decimal exitPrice, string reason)
        {
            if (!string.IsNullOrEmpty(state.PendingSlOrderId))
            {
                _orderService.CancelOrder(state.PendingSlOrderId);
                state.PendingSlOrderId = string.Empty;
            }

            var futInfo = _symbolMaster.GetActiveFuture(state.Symbol);
            _orderService.CloseHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, futInfo.LotSize, state.IsLong);
            
            decimal pnl = state.IsLong ? exitPrice - state.EntryPrice : state.EntryPrice - exitPrice;
            state.DayPnl += pnl;
            _logger.LogInformation("[{Symbol}] Squared off VWAP Strategy ({Reason}). Est PnL Points: {Pnl:N2}", state.Symbol, reason, pnl);

            state.IsActive = false;
        }

        public string GetStatusDigest()
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("🔷 *VWAP Strategy*");
            foreach (var kv in _states)
            {
                var s = kv.Value;
                var vwap = _tracker.GetVwap(s.Symbol);
                string vwapStr = vwap.HasValue ? $"{vwap.Value:N1}" : "N/A";
                string statusLine = s.IsActive
                    ? $"  ▶ {s.Symbol}: *ACTIVE* {(s.IsLong ? "LONG🔼" : "SHORT🔻")} | Entry:{s.EntryPrice:N1} SL:{s.StopLossLevel:N1} TP:{s.TargetLevel:N1}"
                    : $"  ⏳ {s.Symbol}: Waiting for VWAP cross | VWAP≈{vwapStr} | Trades:{s.TradesToday}/2";
                string pnlStr = s.DayPnl == 0 ? "₹0" : (s.DayPnl > 0 ? $"🟢 +{s.DayPnl:N0}pts" : $"🔴 {s.DayPnl:N0}pts");
                lines.AppendLine($"{statusLine} | P&L: {pnlStr}");
            }
            return lines.ToString();
        }
    }
}

