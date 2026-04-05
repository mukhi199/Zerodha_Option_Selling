namespace Trading.Strategy.Services;

using Microsoft.Extensions.Logging;
using Trading.Core.Models;

public class SampleStrategy : IStrategy
{
    private readonly OrderExecutionService _orderService;
    private readonly ILogger<SampleStrategy> _logger;
    private bool _hasPlacedOrder = false;

    private readonly TechnicalIndicatorsTracker _tracker;

    public SampleStrategy(OrderExecutionService orderService, TechnicalIndicatorsTracker tracker, ILogger<SampleStrategy> logger)
    {
        _orderService = orderService;
        _tracker = tracker;
        _logger = logger;
    }

    public void OnTick(NormalizedTick tick)
    {
        // Add to historical ticks if needed
    }

    public void OnCandle(Candle candle)
    {
        // Track the close price for SMA calculation
        // Typically MAs are calculated on specific intervals, e.g., Daily or 5m. 
        // Assuming we are calculating on 5m candles:
        if (candle.IntervalMinutes == 5 && (candle.Symbol == "NIFTY 50" || candle.Symbol == "NIFTY BANK"))
        {
            _tracker.AddClosePrice(candle.Symbol, candle.Close);

            var sma50 = _tracker.GetSMA(candle.Symbol, 50);
            var sma200 = _tracker.GetSMA(candle.Symbol, 200);
            
            var ema50 = _tracker.GetEMA(candle.Symbol, 50);
            var ema200 = _tracker.GetEMA(candle.Symbol, 200);

            var dailyCpr = _tracker.GetDailyCPR(candle.Symbol);

            _logger.LogInformation("[{Symbol} 5m] SMA50: {SMA50}, SMA200: {SMA200} | EMA50: {EMA50}, EMA200: {EMA200}", 
                candle.Symbol, sma50, sma200, ema50, ema200);
            
            if (dailyCpr != null)
            {
                _logger.LogInformation("[{Symbol} daily CPR] Pivot: {Pivot}, BC: {BC}, TC: {TC}, R1: {R1}, S1: {S1}", 
                    candle.Symbol, dailyCpr.Pivot, dailyCpr.BottomCentral, dailyCpr.TopCentral, dailyCpr.R1, dailyCpr.S1);
            }

            // Example check for a 5-minute candle closing below 500
            if (candle.Close < 500 && !_hasPlacedOrder)
            {
                _logger.LogInformation("[SampleStrategy] Trading condition met on 5m CANDLE for {Symbol} at {Close}", candle.Symbol, candle.Close);
                
                // _orderService.PlaceStopLossOrder(candle.Symbol, "NSE", 1, candle.Close - 5, "SELL");
                
                _hasPlacedOrder = true;
            }
        }
    }
}
