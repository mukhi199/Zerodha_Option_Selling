using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Trading.Core.Models;
using Trading.Strategy.Services;
using Trading.Zerodha.Services;

namespace Trading.Strategy.Services
{
    public class Strangle920Strategy : IStrategy
    {
        private readonly OrderExecutionService _orderService;
        private readonly INfoSymbolMaster _symbolMaster;
        private readonly IStrategicStateStore _stateStore;
        private readonly ILogger<Strangle920Strategy> _logger;

        private class SymbolState
        {
            public string Name { get; set; } = string.Empty;
            public DateTime ExecutedDate { get; set; } = DateTime.MinValue;
            public DateTime SquaredOffDate { get; set; } = DateTime.MinValue;
            
            public decimal LastSpotPrice { get; set; } = 0;
            public DateTime LastTickTime { get; set; } = DateTime.MinValue;

            // Entry data for PnL tracking
            public decimal CeEntryLtp { get; set; } = 0;
            public decimal PeEntryLtp { get; set; } = 0;
            public string CeTradingSymbol { get; set; } = string.Empty;
            public string PeTradingSymbol { get; set; } = string.Empty;
            public int CeLotSize { get; set; } = 0;
            public int PeLotSize { get; set; } = 0;
        }

        private readonly ConcurrentDictionary<string, SymbolState> _states = new();

        public Strangle920Strategy(
            OrderExecutionService orderService, 
            INfoSymbolMaster symbolMaster, 
            IStrategicStateStore stateStore,
            ILogger<Strangle920Strategy> logger)
        {
            _orderService = orderService;
            _symbolMaster = symbolMaster;
            _stateStore = stateStore;
            _logger = logger;

            _states["NIFTY 50"] = new SymbolState { Name = "NIFTY 50" };
            _states["NIFTY BANK"] = new SymbolState { Name = "NIFTY BANK" };
        }

        public void OnTick(NormalizedTick tick)
        {
            if (_states.TryGetValue(tick.Symbol, out var state))
            {
                state.LastSpotPrice = tick.Price;
                state.LastTickTime = tick.ExchangeTime.ToLocalTime();
                CheckAndExecute(state);
            }
            else if (tick.Symbol.StartsWith("NIFTY") || tick.Symbol.StartsWith("BANKNIFTY"))
            {
                // Cache all NFO option tick prices for paper LTP lookups
                _orderService.CachePaperLtp("NFO", tick.Symbol, tick.Price);
            }
        }

        public void OnCandle(Candle candle)
        {
            // Not used for this specific time-based strategy
        }

        private void CheckAndExecute(SymbolState state)
        {
            var now = state.LastTickTime;
            var today = now.Date;

            // ── Guard 1: Entry — fire EXACTLY ONCE per calendar day ────────────
            if (state.ExecutedDate != today
                && now.TimeOfDay >= new TimeSpan(9, 20, 0)
                && now.TimeOfDay < new TimeSpan(15, 15, 0))
            {
                _logger.LogInformation(">>> 9:20 AM STRANGLE TRIGGERED for {Symbol} at {Time} with Spot: {Spot} <<<", state.Name, now, state.LastSpotPrice);
                bool success = ExecuteStrangle(state);
                if (success)
                    state.ExecutedDate = today;  // lock out for rest of day
            }

            // ── Guard 2: EOD Square-off — fire EXACTLY ONCE per calendar day ───
            if (state.ExecutedDate == today                        // only if we entered today
                && state.SquaredOffDate != today                   // only if not already closed
                && now.TimeOfDay >= new TimeSpan(15, 15, 0))
            {
                _logger.LogInformation(">>> 15:15 PM STRANGLE SQUARE-OFF for {Symbol} at {Time} <<<", state.Name, now);
                SquareOff(state);
                state.SquaredOffDate = today;  // lock out EOD for rest of day

                _stateStore.UpdateSymbolState(state.Name, s => s.StrangleStatus = "Squared Off (EOD)");
            }
        }

        private void SquareOff(SymbolState state)
        {
            _logger.LogInformation("Executing EOD Square-off for 9:20 Strangle legs for {Symbol}.", state.Name);

            // Calculate PnL if we tracked entry
            if (state.CeEntryLtp > 0 && state.PeEntryLtp > 0
                && !string.IsNullOrEmpty(state.CeTradingSymbol)
                && !string.IsNullOrEmpty(state.PeTradingSymbol))
            {
                decimal ceExitLtp = _orderService.GetLtp("NFO", state.CeTradingSymbol);
                decimal peExitLtp = _orderService.GetLtp("NFO", state.PeTradingSymbol);

                if (ceExitLtp > 0 && peExitLtp > 0)
                {
                    // We SOLD the strangle, so profit = entry - exit (per unit)
                    decimal cePnl = (state.CeEntryLtp - ceExitLtp) * state.CeLotSize;
                    decimal pePnl = (state.PeEntryLtp - peExitLtp) * state.PeLotSize;
                    decimal totalPnl = cePnl + pePnl;

                    string pnlTag = totalPnl >= 0 ? "PROFIT" : "LOSS";
                    _logger.LogInformation(
                        "📊 [PnL Summary] {Symbol} CE: Entry={CEEntry} Exit={CEExit} PnL={CEPnl:+0.##;-0.##} | " +
                        "PE: Entry={PEEntry} Exit={PEExit} PnL={PEPnl:+0.##;-0.##} | " +
                        "TOTAL {Tag}: ₹{Total:+0.##;-0.##}",
                        state.Name, state.CeEntryLtp, ceExitLtp, cePnl,
                        state.PeEntryLtp, peExitLtp, pePnl,
                        pnlTag, totalPnl);

                    _stateStore.UpdateSymbolState(state.Name, s =>
                        s.StrangleStatus = $"Squared Off EOD | {pnlTag}: ₹{totalPnl:+0.##;-0.##}");
                }
                else
                {
                    _logger.LogWarning("{Symbol}: Could not fetch exit LTPs for PnL calculation at close.", state.Name);
                }
            }
        }

        private bool ExecuteStrangle(SymbolState state)
        {
            try
            {
                decimal spot = state.LastSpotPrice;
                
                // Offsets differ by symbol
                decimal otmOffset = state.Name == "NIFTY BANK" ? 500 : 200;
                decimal hedgeOffset = state.Name == "NIFTY BANK" ? 1200 : 500;

                // All rounded to nearest 100 as per user requirement
                decimal ceStrike = Math.Round((spot + otmOffset) / 100.0m) * 100.0m; 
                decimal peStrike = Math.Round((spot - otmOffset) / 100.0m) * 100.0m;
                
                decimal hedgeCeStrike = Math.Round((spot + hedgeOffset) / 100.0m) * 100.0m;
                decimal hedgePeStrike = Math.Round((spot - hedgeOffset) / 100.0m) * 100.0m;

                var ce   = _symbolMaster.GetActiveStrikeOption(state.Name, ceStrike, false);
                var pe   = _symbolMaster.GetActiveStrikeOption(state.Name, peStrike, true);
                var hCe  = _symbolMaster.GetActiveStrikeOption(state.Name, hedgeCeStrike, false);
                var hPe  = _symbolMaster.GetActiveStrikeOption(state.Name, hedgePeStrike, true);

                if (string.IsNullOrEmpty(ce.TradingSymbol) || string.IsNullOrEmpty(pe.TradingSymbol))
                {
                    _logger.LogError("{Symbol}: Failed to resolve option symbols for 9:20 Strangle.", state.Name);
                    return false;
                }

                _logger.LogInformation("{Symbol} Selected strikes: CE {CE} ({CEStrike}), PE {PE} ({PEStrike})",
                    state.Name, ce.TradingSymbol, ceStrike, pe.TradingSymbol, peStrike);

                // 1. EXECUTE HEDGES FIRST (Margin Benefit)
                _logger.LogInformation("{Symbol}: Buying Margin Hedges...", state.Name);
                _orderService.PlaceMarketOrder(hCe.TradingSymbol, "NFO", hCe.LotSize, "BUY");
                _orderService.PlaceMarketOrder(hPe.TradingSymbol, "NFO", hPe.LotSize, "BUY");

                // 2. EXECUTE SHORT STRANGLE
                _logger.LogInformation("{Symbol}: Selling Main Strangle...", state.Name);
                _orderService.PlaceMarketOrder(ce.TradingSymbol, "NFO", ce.LotSize, "SELL");
                _orderService.PlaceMarketOrder(pe.TradingSymbol, "NFO", pe.LotSize, "SELL");

                // 3. CAPTURE ENTRY LTP FOR PnL TRACKING
                _logger.LogInformation("{Symbol}: Fetching LTPs for SL orders...", state.Name);
                
                decimal ceLtp = 0;
                decimal peLtp = 0;

                // Retry loop for paper mode tick cache warming
                for (int i = 0; i < 10; i++)
                {
                    ceLtp = _orderService.GetLtp("NFO", ce.TradingSymbol);
                    peLtp = _orderService.GetLtp("NFO", pe.TradingSymbol);
                    
                    if (ceLtp > 0 && peLtp > 0) break;
                    
                    _logger.LogWarning("{Symbol}: Waiting for option LTPs in cache (Attempt {Attempt}/10)...", state.Name, i + 1);
                    System.Threading.Thread.Sleep(500);
                }

                // Store state for tracking
                state.CeEntryLtp      = ceLtp;
                state.PeEntryLtp      = peLtp;
                state.CeTradingSymbol = ce.TradingSymbol;
                state.PeTradingSymbol = pe.TradingSymbol;
                state.CeLotSize       = ce.LotSize;
                state.PeLotSize       = pe.LotSize;

                if (ceLtp > 0)
                {
                    decimal ceSl = Math.Round(ceLtp * 2.0m, 1);
                    _orderService.PlaceStopLossOrder(ce.TradingSymbol, "NFO", ce.LotSize, ceSl, "BUY");
                }
                
                if (peLtp > 0)
                {
                    decimal peSl = Math.Round(peLtp * 2.0m, 1);
                    _orderService.PlaceStopLossOrder(pe.TradingSymbol, "NFO", pe.LotSize, peSl, "BUY");
                }
                
                _logger.LogInformation("[SAFETY] {Symbol} 9:20 Strangle deployed. CE Entry: {CELtp} | PE Entry: {PELtp}", state.Name, ceLtp, peLtp);

                _stateStore.UpdateSymbolState(state.Name, s => {
                    s.StrangleStatus = "Deployed @ 9:20 AM";
                    s.StrangleLegs = $"CE: {ce.TradingSymbol} @ {ceLtp:N1} (SL: {ceLtp*2:N1}) | PE: {pe.TradingSymbol} @ {peLtp:N1} (SL: {peLtp*2:N1})";
                });
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Symbol}: Strangle execution failed.", state.Name);
                return false;
            }
        }
    }
}
