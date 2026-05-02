using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Trading.Core.Models;
using Trading.Strategy.Services;
using Trading.Zerodha.Services;

namespace Trading.Strategy.Services
{
    public class LevelStrangleStrategy : IStrategy
    {
        private readonly OrderExecutionService _orderService;
        private readonly INfoSymbolMaster _symbolMaster;
        private readonly IStrategicStateStore _stateStore;
        private readonly TechnicalIndicatorsTracker _tracker;
        private readonly ILogger<LevelStrangleStrategy> _logger;

        private class LevelState
        {
            public string Name { get; set; } = string.Empty;
            public DateTime ExecutedDate { get; set; } = DateTime.MinValue;
            public DateTime SquaredOffDate { get; set; } = DateTime.MinValue;
            
            public decimal LastSpotPrice { get; set; } = 0;
            public DateTime LastTickTime { get; set; } = DateTime.MinValue;
            public decimal PrevDayHigh { get; set; } = 0;

            // Entry data for PnL tracking
            public decimal CeEntryLtp { get; set; } = 0;
            public decimal PeEntryLtp { get; set; } = 0;
            public string CeTradingSymbol { get; set; } = string.Empty;
            public string PeTradingSymbol { get; set; } = string.Empty;
            public int CeLotSize { get; set; } = 0;
            public int PeLotSize { get; set; } = 0;
        }

        private readonly ConcurrentDictionary<string, LevelState> _states = new();

        public LevelStrangleStrategy(
            OrderExecutionService orderService, 
            INfoSymbolMaster symbolMaster, 
            IStrategicStateStore stateStore,
            TechnicalIndicatorsTracker tracker,
            ILogger<LevelStrangleStrategy> logger)
        {
            _orderService = orderService;
            _symbolMaster = symbolMaster;
            _stateStore = stateStore;
            _tracker = tracker;
            _logger = logger;

            _states["NIFTY 50"] = new LevelState { Name = "NIFTY 50" };
            _states["NIFTY BANK"] = new LevelState { Name = "NIFTY BANK" };
        }

        public void OnTick(NormalizedTick tick)
        {
            if (_states.TryGetValue(tick.Symbol, out var state))
            {
                state.LastSpotPrice = tick.Price;
                state.LastTickTime = tick.ExchangeTime.ToLocalTime();
                
                // Fetch PDH if not set for today
                if (state.PrevDayHigh <= 0)
                {
                    // Note: In this system, PDH is typically fetched from state store or tracker
                    // For now we attempt to get it from the state store specifically
                    var masterState = _stateStore.GetAllStates().FirstOrDefault(s => s.Symbol == tick.Symbol);
                    if (masterState != null && masterState.Pdh > 0)
                        state.PrevDayHigh = masterState.Pdh;
                }

                CheckAndExecute(state);
            }
            else if (tick.Symbol.StartsWith("NIFTY") || tick.Symbol.StartsWith("BANKNIFTY"))
            {
                _orderService.CachePaperLtp("NFO", tick.Symbol, tick.Price);
            }
        }

        public void OnCandle(Candle candle)
        {
            // Triggers are tick-based for immediate execution
        }

        private void CheckAndExecute(LevelState state)
        {
            var now = state.LastTickTime;
            var today = now.Date;

            // ── Guard 1: Entry ────────────────────────────────────────────────
            if (state.ExecutedDate != today
                && now.TimeOfDay >= new TimeSpan(9, 15, 0)
                && now.TimeOfDay < new TimeSpan(15, 0, 0))
            {
                bool triggerPdh = state.PrevDayHigh > 0 && state.LastSpotPrice >= state.PrevDayHigh;
                bool triggerCpr = IsPriceNearCpr(state);

                if (triggerPdh || triggerCpr)
                {
                    string reason = triggerPdh ? "PDH" : "CPR";
                    _logger.LogInformation(">>> LEVEL STRANGLE TRIGGERED ({Reason}) for {Symbol} at {Time} with Spot: {Spot} <<<", 
                                           reason, state.Name, now, state.LastSpotPrice);
                    
                    bool success = ExecuteStrangle(state);
                    if (success)
                        state.ExecutedDate = today;
                }
            }

            // ── Guard 2: EOD Square-off ───────────────────────────────────────
            if (state.ExecutedDate == today
                && state.SquaredOffDate != today
                && now.TimeOfDay >= new TimeSpan(15, 15, 0))
            {
                SquareOff(state);
                state.SquaredOffDate = today;
            }
        }

        private bool IsPriceNearCpr(LevelState state)
        {
            var cpr = _tracker.GetDailyCPR(state.Name);
            if (cpr == null) return false;

            // "Around CPR" defined as being between Top and Bottom central levels
            return state.LastSpotPrice >= cpr.BottomCentral && state.LastSpotPrice <= cpr.TopCentral;
        }

        private bool ExecuteStrangle(LevelState state)
        {
            try
            {
                decimal spot = state.LastSpotPrice;
                decimal otmOffset = state.Name == "NIFTY BANK" ? 500 : 200;
                decimal hedgeOffset = state.Name == "NIFTY BANK" ? 1200 : 500;

                decimal ceStrike = Math.Round((spot + otmOffset) / 100.0m) * 100.0m; 
                decimal peStrike = Math.Round((spot - otmOffset) / 100.0m) * 100.0m;
                decimal hCeStrike = Math.Round((spot + hedgeOffset) / 100.0m) * 100.0m;
                decimal hPeStrike = Math.Round((spot - hedgeOffset) / 100.0m) * 100.0m;

                var ce  = _symbolMaster.GetActiveStrikeOption(state.Name, ceStrike, false);
                var pe  = _symbolMaster.GetActiveStrikeOption(state.Name, peStrike, true);
                var hCe = _symbolMaster.GetActiveStrikeOption(state.Name, hCeStrike, false);
                var hPe = _symbolMaster.GetActiveStrikeOption(state.Name, hPeStrike, true);

                if (string.IsNullOrEmpty(ce.TradingSymbol) || string.IsNullOrEmpty(pe.TradingSymbol)) return false;

                // 1. Hedging
                _orderService.PlaceMarketOrder(hCe.TradingSymbol, "NFO", hCe.LotSize, "BUY");
                _orderService.PlaceMarketOrder(hPe.TradingSymbol, "NFO", hPe.LotSize, "BUY");

                // 2. Main Shorts
                _orderService.PlaceMarketOrder(ce.TradingSymbol, "NFO", ce.LotSize, "SELL");
                _orderService.PlaceMarketOrder(pe.TradingSymbol, "NFO", pe.LotSize, "SELL");

                // 3. SL Orders (2x price)
                decimal ceLtp = 0, peLtp = 0;
                for (int i = 0; i < 10; i++)
                {
                    ceLtp = _orderService.GetLtp("NFO", ce.TradingSymbol);
                    peLtp = _orderService.GetLtp("NFO", pe.TradingSymbol);
                    if (ceLtp > 0 && peLtp > 0) break;
                    System.Threading.Thread.Sleep(500);
                }

                state.CeEntryLtp = ceLtp; state.PeEntryLtp = peLtp;
                state.CeTradingSymbol = ce.TradingSymbol; state.PeTradingSymbol = pe.TradingSymbol;
                state.CeLotSize = ce.LotSize; state.PeLotSize = pe.LotSize;

                if (ceLtp > 0) _orderService.PlaceStopLossOrder(ce.TradingSymbol, "NFO", ce.LotSize, Math.Round(ceLtp * 2, 1), "BUY");
                if (peLtp > 0) _orderService.PlaceStopLossOrder(pe.TradingSymbol, "NFO", pe.LotSize, Math.Round(peLtp * 2, 1), "BUY");

                _logger.LogInformation("[SAFETY] Level-Based Strangle deployed for {Symbol}.", state.Name);
                _stateStore.UpdateSymbolState(state.Name, s => s.StrangleStatus += " | LevelStrangle Active");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Symbol}: Level Strangle deployment failed.", state.Name);
                return false;
            }
        }

        private void SquareOff(LevelState state)
        {
            if (state.CeEntryLtp <= 0 || state.PeEntryLtp <= 0) return;

            decimal ceExit = _orderService.GetLtp("NFO", state.CeTradingSymbol);
            decimal peExit = _orderService.GetLtp("NFO", state.PeTradingSymbol);

            if (ceExit > 0 && peExit > 0)
            {
                decimal totalPnl = ((state.CeEntryLtp - ceExit) * state.CeLotSize) + ((state.PeEntryLtp - peExit) * state.PeLotSize);
                _logger.LogInformation("📊 [PnL Level Strangle] {Symbol}: ₹{Total:+0.##;-0.##}", state.Name, totalPnl);
            }
        }
    }
}
