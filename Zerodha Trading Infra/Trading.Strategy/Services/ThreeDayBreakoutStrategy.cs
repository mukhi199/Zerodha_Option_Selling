namespace Trading.Strategy.Services;

using Microsoft.Extensions.Logging;
using Trading.Core.Models;
using KiteConnect;

public class ThreeDayBreakoutStrategy : IStrategy
{
    private readonly IOrderService _orderService;
    private readonly TechnicalIndicatorsTracker _tracker;
    private readonly ILogger<ThreeDayBreakoutStrategy> _logger;
    
    // Track if an order has been placed today for a symbol to avoid spamming
    private readonly HashSet<string> _tradedToday = new();

    public ThreeDayBreakoutStrategy(IOrderService orderService, TechnicalIndicatorsTracker tracker, ILogger<ThreeDayBreakoutStrategy> logger)
    {
        _orderService = orderService;
        _tracker = tracker;
        _logger = logger;
    }

    public void OnTick(NormalizedTick tick)
    {
        if (tick.Symbol != "NIFTY 50" && tick.Symbol != "NIFTY BANK")
            return;

        if (_tradedToday.Contains(tick.Symbol))
            return;

        var range = _tracker.GetThreeDayRange(tick.Symbol);
        if (range == null)
            return;

        // Dynamically compute the active front-month future symbol
        var futureSymbol = GetActiveFutureSymbol(tick.Symbol);

        if (tick.Price > range.Value.High)
        {
            _logger.LogInformation("[{Symbol}] 3-Day High ({High}) BROKEN UPWARDS by current price {Price}. Buying Future {Future}!", 
                tick.Symbol, range.Value.High, tick.Price, futureSymbol);

            // Placing trade for backtest/live
            _orderService.PlaceMarketOrder(futureSymbol, "NFO", 1, "BUY");
            
            _tradedToday.Add(tick.Symbol);
        }
        else if (tick.Price < range.Value.Low)
        {
            _logger.LogInformation("[{Symbol}] 3-Day Low ({Low}) BROKEN DOWNWARDS by current price {Price}. Selling Future {Future}!", 
                tick.Symbol, range.Value.Low, tick.Price, futureSymbol);

            // Placing trade for backtest/live
            _orderService.PlaceMarketOrder(futureSymbol, "NFO", 1, "SELL");
            
            _tradedToday.Add(tick.Symbol);
        }
    }

    public void OnCandle(Candle candle)
    {
        // Not used for instantaneous tick breakout
    }

    private string GetActiveFutureSymbol(string spotSymbol)
    {
        var baseSymbol = spotSymbol == "NIFTY 50" ? "NIFTY" : "BANKNIFTY";
        var now = DateTime.Now;

        // Find the last Thursday of the current month
        var lastDayOfMonth = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month));
        while (lastDayOfMonth.DayOfWeek != DayOfWeek.Thursday)
        {
            lastDayOfMonth = lastDayOfMonth.AddDays(-1);
        }

        // If today is past the expiry date (last Thursday), rollover to the next month's future
        var expiryMonthDate = now;
        if (now.Date > lastDayOfMonth.Date)
        {
            expiryMonthDate = now.AddMonths(1);
        }

        var yy = expiryMonthDate.ToString("yy");
        var mmm = expiryMonthDate.ToString("MMM").ToUpper();

        return $"{baseSymbol}{yy}{mmm}FUT";
    }
}
