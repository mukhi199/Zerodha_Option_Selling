using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Trading.Core.Models;
using Trading.Zerodha.Services;

namespace Trading.Strategy.Services
{
    public class StandardOrbStrategy : IStrategy
    {
        private class State
        {
            public string Symbol { get; set; } = string.Empty;
            public DateTime CurrentDate { get; set; } = DateTime.MinValue;
            public int TradesToday { get; set; } = 0;
            
            public decimal OrbHigh { get; set; } = 0;
            public decimal OrbLow { get; set; } = 0;
            public bool OrbSet { get; set; } = false;

            public bool IsActive { get; set; } = false;
            public bool IsLong { get; set; } = false;
            public decimal EntryPrice { get; set; } = 0;
            public decimal StopLossLevel { get; set; } = 0;
            public decimal TargetLevel { get; set; } = 0;
            
            public string OptionSymbol { get; set; } = string.Empty;
            public string PendingSlOrderId { get; set; } = string.Empty;
            public decimal DayPnl { get; set; } = 0;
        }

        private readonly ConcurrentDictionary<string, State> _states = new();
        private readonly OrderExecutionService _orderService;
        private readonly INfoSymbolMaster _symbolMaster;
        private readonly TechnicalIndicatorsTracker _tracker;
        private readonly ILogger<StandardOrbStrategy> _logger;
        
        private readonly TimeSpan _orbEnd = new TimeSpan(9, 30, 0); // 15-min ORB (9:15 to 9:30)
        private readonly TimeSpan _windowEnd = new TimeSpan(14, 30, 0);
        private readonly TimeSpan _squareOffTime = new TimeSpan(15, 15, 0);

        private readonly decimal _niftyMinWidth;
        private readonly decimal _bankNiftyMinWidth;

        public StandardOrbStrategy(
            OrderExecutionService orderService, 
            INfoSymbolMaster symbolMaster, 
            TechnicalIndicatorsTracker tracker,
            ILogger<StandardOrbStrategy> logger,
            IConfiguration config)
        {
            _orderService = orderService;
            _symbolMaster = symbolMaster;
            _tracker = tracker;
            _logger = logger;

            _niftyMinWidth = config.GetValue<decimal>("OrbStrategy:NiftyMinWidth", 30m);
            _bankNiftyMinWidth = config.GetValue<decimal>("OrbStrategy:BankNiftyMinWidth", 80m);

            _states["NIFTY 50"] = new State { Symbol = "NIFTY 50" };
            _states["NIFTY BANK"] = new State { Symbol = "NIFTY BANK" };
        }

        public void OnTick(NormalizedTick tick)
        {
            if (!_states.TryGetValue(tick.Symbol, out var state) || !state.IsActive) return;

            var now = DateTime.Now.TimeOfDay;
            
            if (now >= _squareOffTime)
            {
                _logger.LogInformation("[{Symbol}] Standard ORB: Exiting at EOD (3:15 PM).", state.Symbol);
                SquareOff(state, tick.Price, "EOD Base");
                return;
            }

            bool hitSl = state.IsLong ? (tick.Price <= state.StopLossLevel) : (tick.Price >= state.StopLossLevel);
            bool hitTp = state.IsLong ? (tick.Price >= state.TargetLevel) : (tick.Price <= state.TargetLevel);

            if (hitTp)
            {
                SquareOff(state, tick.Price, "Take Profit (1.5x ORB Width)");
            }
            else if (hitSl)
            {
                SquareOff(state, tick.Price, "Stop Loss");
            }
        }

        public void OnCandle(Candle candle)
        {
            if (!_states.TryGetValue(candle.Symbol, out var state)) return;

            var localTime = candle.StartTime.ToLocalTime();
            var tod = localTime.TimeOfDay;
            var date = localTime.Date;

            // Rollover reset
            if (date > state.CurrentDate)
            {
                state.CurrentDate = date;
                state.TradesToday = 0;
                state.OrbHigh = 0;
                state.OrbLow = 0;
                state.OrbSet = false;
                state.DayPnl = 0;

                if (state.IsActive)
                {
                    state.IsActive = false;
                }
            }

            // Build ORB
            if (tod < _orbEnd)
            {
                if (state.OrbHigh == 0 && state.OrbLow == 0)
                {
                    state.OrbHigh = candle.High;
                    state.OrbLow = candle.Low;
                }
                else
                {
                    state.OrbHigh = Math.Max(state.OrbHigh, candle.High);
                    state.OrbLow = Math.Min(state.OrbLow, candle.Low);
                }
            }
            else if (tod == _orbEnd && !state.OrbSet)
            {
                // Note: The candle starting at 9:25 ends at 9:30, so its tod (StartTime) is 9:25.
                // Wait, if tod == 9:30, this is the first candle outside the ORB.
                state.OrbSet = true;
            }

            // At 9:25 candle, it's the last candle of the ORB. Next candle is 9:30.
            // So if tod >= 9:30, OrbSet should be true.
            if (tod >= _orbEnd && !state.OrbSet)
            {
                state.OrbSet = true;
                if (state.OrbHigh > 0 && state.OrbLow > 0)
                {
                    decimal minW = candle.Symbol.Contains("BANK") ? _bankNiftyMinWidth : _niftyMinWidth;
                    decimal w = state.OrbHigh - state.OrbLow;
                    if (w < minW)
                    {
                        _logger.LogWarning("[{Symbol}] ORB Narrow ({W:F1} < {MinW}). Signal might be weak today.", candle.Symbol, w, minW);
                    }
                    else
                    {
                        _logger.LogInformation("[{Symbol}] ORB Set: {H} to {L} (Width: {W:F1})", candle.Symbol, state.OrbHigh, state.OrbLow, w);
                    }
                }
            }

            if (!state.OrbSet || state.IsActive || state.TradesToday >= 1) return;
            if (tod < _orbEnd || tod >= _windowEnd) return;
            if (state.OrbHigh <= 0 || state.OrbLow <= 0) return; // ORB data not populated

            decimal minWidth = candle.Symbol.Contains("BANK") ? _bankNiftyMinWidth : _niftyMinWidth;
            decimal width = state.OrbHigh - state.OrbLow;
            
            if (width < minWidth) return;

            var rsiData = _tracker.GetRsiAndRma(candle.Symbol);
            var ema20 = _tracker.GetEMA(candle.Symbol, 20);
            var vwap = _tracker.GetVwap(candle.Symbol);

            if (rsiData == null || !ema20.HasValue || !vwap.HasValue) return;

            bool isAboveOrb = candle.Close > state.OrbHigh;
            bool isBelowOrb = candle.Close < state.OrbLow;

            bool trendUp = candle.Close > ema20.Value;
            bool trendDown = candle.Close < ema20.Value;

            bool rsiBull = rsiData.Value.Rsi > 50m;
            bool rsiBear = rsiData.Value.Rsi < 50m;

            bool vwapBull = candle.Close > vwap.Value;
            bool vwapBear = candle.Close < vwap.Value;

            bool condLong = isAboveOrb && trendUp && rsiBull && vwapBull;
            bool condShort = isBelowOrb && trendDown && rsiBear && vwapBear;

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
                    state.StopLossLevel = state.OrbLow;
                    state.TargetLevel = state.EntryPrice + (width * 1.5m);

                    var optInfo = _symbolMaster.GetActiveOtmOption(candle.Symbol, state.EntryPrice, true);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] Standard ORB LONG! Entry={En:N1} SL={Sl:N1} Target={Tp:N1}", candle.Symbol, state.EntryPrice, state.StopLossLevel, state.TargetLevel);
                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, qty, true, state.StopLossLevel);
                }
                else
                {
                    state.StopLossLevel = state.OrbHigh;
                    state.TargetLevel = state.EntryPrice - (width * 1.5m);

                    var optInfo = _symbolMaster.GetActiveOtmOption(candle.Symbol, state.EntryPrice, false);
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] Standard ORB SHORT! Entry={En:N1} SL={Sl:N1} Target={Tp:N1}", candle.Symbol, state.EntryPrice, state.StopLossLevel, state.TargetLevel);
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
            _logger.LogInformation("[{Symbol}] Squared off ORB Strategy ({Reason}). Est PnL Points: {Pnl:N2}", state.Symbol, reason, pnl);

            state.IsActive = false;
        }

        public string GetStatusDigest()
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("🟠 *ORB Strategy*");
            foreach (var kv in _states)
            {
                var s = kv.Value;
                decimal w = s.OrbHigh != decimal.MinValue && s.OrbLow != decimal.MaxValue ? s.OrbHigh - s.OrbLow : 0;
                string orbStatus = !s.OrbSet ? "Building ORB..." : $"ORB H:{s.OrbHigh:N0} L:{s.OrbLow:N0} W:{w:N0}";
                string statusLine = s.IsActive
                    ? $"  ▶ {s.Symbol}: *ACTIVE* {(s.IsLong ? "LONG🔼" : "SHORT🔻")} | Entry:{s.EntryPrice:N1} SL:{s.StopLossLevel:N1} TP:{s.TargetLevel:N1}"
                    : $"  ⏳ {s.Symbol}: {orbStatus} | Trades:{s.TradesToday}/1";
                string pnlStr = s.DayPnl == 0 ? "₹0" : (s.DayPnl > 0 ? $"🟢 +{s.DayPnl:N0}pts" : $"🔴 {s.DayPnl:N0}pts");
                lines.AppendLine($"{statusLine} | P&L: {pnlStr}");
            }
            return lines.ToString();
        }
    }
}
