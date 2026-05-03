using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Trading.Core.Models;
using Trading.Strategy.Services;
using Trading.Zerodha.Services;

namespace Trading.Strategy.Services
{
    public class IntradayBigMoveStrategy : IStrategy
    {
        private class State
        {
            public string Symbol { get; set; } = string.Empty;
            public decimal MaxBodySizeToday { get; set; } = 0m;
            public DateTime CurrentDate { get; set; } = DateTime.MinValue;
            public int TradesToday { get; set; } = 0;
            
            public bool IsActive { get; set; } = false;
            public bool IsLong { get; set; } = false;
            public decimal EntryPrice { get; set; } = 0;
            public decimal StopLossLevel { get; set; } = 0;
            public string OptionSymbol { get; set; } = string.Empty;
            public string PendingSlOrderId { get; set; } = string.Empty;
            
            public bool IsTrailing9EMA { get; set; } = false;
            public decimal TargetLockPoints { get; set; } = 100m;
        }

        private readonly ConcurrentDictionary<string, State> _states = new();
        private readonly OrderExecutionService _orderService;
        private readonly INfoSymbolMaster _symbolMaster;
        private readonly TechnicalIndicatorsTracker _tracker;
        private readonly IStrategicStateStore _stateStore;
        private readonly ILogger<IntradayBigMoveStrategy> _logger;
        
        private readonly TimeSpan _windowStart = new TimeSpan(10, 30, 0); // Optimized: Time Filter (10:30-12:30 yields highest WinRate)
        private readonly TimeSpan _windowEnd = new TimeSpan(12, 30, 0);
        private readonly TimeSpan _squareOffTime = new TimeSpan(15, 15, 0);

        public IntradayBigMoveStrategy(
            OrderExecutionService orderService, 
            INfoSymbolMaster symbolMaster, 
            TechnicalIndicatorsTracker tracker,
            IStrategicStateStore stateStore,
            ILogger<IntradayBigMoveStrategy> logger)
        {
            _orderService = orderService;
            _symbolMaster = symbolMaster;
            _tracker = tracker;
            _stateStore = stateStore;
            _logger = logger;

            _states["NIFTY 50"] = new State { Symbol = "NIFTY 50", TargetLockPoints = 100m };
            _states["NIFTY BANK"] = new State { Symbol = "NIFTY BANK", TargetLockPoints = 200m };
        }

        public void OnTick(NormalizedTick tick)
        {
            if (!_states.TryGetValue(tick.Symbol, out var state)) return;

            if (state.IsActive)
            {
                var now = DateTime.Now.TimeOfDay;
                
                // 1. Time-based Square Off
                if (now >= _squareOffTime)
                {
                    _logger.LogInformation("[{Symbol}] Exiting Big Move Position at EOD (3:15 PM).", state.Symbol);
                    SquareOff(state, tick.Price);
                    return;
                }

                // 2. Trailing Lock Check
                if (!state.IsTrailing9EMA)
                {
                    decimal currentPnlPoints = state.IsLong 
                        ? tick.Price - state.EntryPrice 
                        : state.EntryPrice - tick.Price;

                    if (currentPnlPoints >= state.TargetLockPoints)
                    {
                        _logger.LogInformation("[{Symbol}] Target buffer (+{Points} pts) reached! Locking target and switching to 9-EMA Trail.", state.Symbol, state.TargetLockPoints);
                        state.IsTrailing9EMA = true;
                        
                        // Cancel fixed SL order since we are moving to close-basis manual trailing
                        if (!string.IsNullOrEmpty(state.PendingSlOrderId))
                        {
                            _orderService.CancelOrder(state.PendingSlOrderId);
                            state.PendingSlOrderId = string.Empty;
                        }
                    }
                }
            }
        }

        public void OnCandle(Candle candle)
        {
            if (!_states.TryGetValue(candle.Symbol, out var state)) return;
            var localTime = candle.StartTime.ToLocalTime();
            var tod = localTime.TimeOfDay;
            var date = localTime.Date;

            // Daily Rollover
            if (date > state.CurrentDate)
            {
                state.CurrentDate = date;
                state.MaxBodySizeToday = 0m;
                state.TradesToday = 0;
                if (state.IsActive)
                {
                    // Failsafe reset if position held overnight mistakenly
                    state.IsActive = false;
                    state.IsTrailing9EMA = false;
                }
            }

            decimal body = Math.Abs(candle.Close - candle.Open);

            // Update Max Body Size (Only tracking candles after 9:45 to filter out opening gap noise)
            if (tod >= _windowStart)
            {
                state.MaxBodySizeToday = Math.Max(state.MaxBodySizeToday, body);
            }

            // ── Active Trade EMA Trailing Check ──
            if (state.IsActive && state.IsTrailing9EMA)
            {
                decimal ema9 = _tracker.GetEMA(candle.Symbol, 9) ?? 0m;
                if (ema9 > 0)
                {
                    bool crossBelow = state.IsLong && candle.Close < ema9;
                    bool crossAbove = !state.IsLong && candle.Close > ema9;

                    if (crossBelow || crossAbove)
                    {
                        _logger.LogInformation("[{Symbol}] 9-EMA Trailing Exit Triggered! (Close: {Close}, 9EMA: {Ema9})", state.Symbol, candle.Close, ema9);
                        SquareOff(state, candle.Close);
                    }
                }
                return;
            }

            // ── Entry Gate ──
            if (state.IsActive || state.TradesToday >= 1) return; // Max 1 trade per day
            if (tod < _windowStart || tod >= _windowEnd) return; // Between 9:45 and 14:30

            // If we don't have a valid history to compare to, skip.
            if (state.MaxBodySizeToday == 0) return;

            // ── Anomaly Body Logic ──
            if (body > state.MaxBodySizeToday * 1.2m)
            {
                bool isBullish = candle.Close > candle.Open;
                _logger.LogInformation(">>> [BIG MOVE ANOMALY] {Symbol}: Massive 5m candle detected at {Time}! Body={Body:N2} (MaxPrev={Max:N2}). Direction: {Dir} <<<",
                    candle.Symbol, localTime, body, state.MaxBodySizeToday, isBullish ? "BULLISH" : "BEARISH");

                state.TradesToday++;
                state.IsActive = true;
                state.IsLong = isBullish;
                state.EntryPrice = candle.Close;
                state.IsTrailing9EMA = false;
                
                var futInfo = _symbolMaster.GetActiveFuture(candle.Symbol);
                int qty = futInfo.LotSize;

                if (isBullish)
                {
                    state.StopLossLevel = candle.Low;
                    var optInfo = _symbolMaster.GetActiveOtmOption(candle.Symbol, state.EntryPrice, true); // PE for hedge
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] Executing LONG on Big Move Setup at {Price}. SL placed at candle low: {SL}", candle.Symbol, state.EntryPrice, state.StopLossLevel);
                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, qty, true, state.StopLossLevel);
                }
                else
                {
                    state.StopLossLevel = candle.High;
                    var optInfo = _symbolMaster.GetActiveOtmOption(candle.Symbol, state.EntryPrice, false); // CE for hedge
                    state.OptionSymbol = optInfo.TradingSymbol;

                    _logger.LogInformation("[{Symbol}] Executing SHORT on Big Move Setup at {Price}. SL placed at candle high: {SL}", candle.Symbol, state.EntryPrice, state.StopLossLevel);
                    state.PendingSlOrderId = _orderService.PlaceHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, qty, false, state.StopLossLevel);
                }
            }
        }

        private void SquareOff(State state, decimal exitPrice)
        {
            if (!string.IsNullOrEmpty(state.PendingSlOrderId))
            {
                _orderService.CancelOrder(state.PendingSlOrderId);
                state.PendingSlOrderId = string.Empty;
            }

            var futInfo = _symbolMaster.GetActiveFuture(state.Symbol);
            _orderService.CloseHedgedBasket(futInfo.TradingSymbol, state.OptionSymbol, futInfo.LotSize, state.IsLong);
            
            decimal pnl = state.IsLong ? exitPrice - state.EntryPrice : state.EntryPrice - exitPrice;
            _logger.LogInformation("[{Symbol}] Squared off Big Move Strategy. Est PnL Points: {Pnl:N2}", state.Symbol, pnl);

            state.IsActive = false;
            state.IsTrailing9EMA = false;
        }
    }
}
