namespace Trading.Strategy.Services;

using KiteConnect;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

public class OrderExecutionService : IOrderService
{
    private readonly Kite _kite;
    private readonly ILogger<OrderExecutionService> _logger;
    
    // Paper-mode LTP cache: keyed by "EXCHANGE:SYMBOL"
    private readonly ConcurrentDictionary<string, decimal> _paperPriceCache = new();

    public OrderExecutionService(string apiKey, ILogger<OrderExecutionService> logger)
    {
        _kite = new Kite(apiKey);
        _logger = logger;
    }

    public void SetAccessToken(string accessToken)
    {
        _kite.SetAccessToken(accessToken);
    }

    /// <summary>
    /// Called by strategies to feed live tick prices into the paper-mode LTP cache.
    /// Key format: "EXCHANGE:SYMBOL" e.g. "NFO:NIFTY2642124800CE"
    /// </summary>
    public void CachePaperLtp(string exchange, string symbol, decimal price)
    {
        _paperPriceCache[$"{exchange}:{symbol}"] = price;
    }

    public string? PlaceMarketOrder(string symbol, string exchange, int quantity, string transactionType)
    {
        try
        {
            bool isPaper = Environment.GetEnvironmentVariable("PAPER_TRADING") == "true";
            _logger.LogInformation("{Mode} Placing Market {TransactionType} order for {Quantity} {Symbol}", isPaper ? "[PAPER]" : "[LIVE]", transactionType, quantity, symbol);
            
            if (isPaper) 
            {
                var mockId = Guid.NewGuid().ToString().Substring(0, 8);
                _logger.LogInformation("Paper trade executed virtually. Order ID: {OrderId}", mockId);
                return mockId;
            }
            
            var response = _kite.PlaceOrder(
                Exchange: exchange,
                TradingSymbol: symbol,
                TransactionType: transactionType,
                Quantity: quantity,
                Price: 0,
                Product: "MIS",
                OrderType: "MARKET",
                Validity: "DAY",
                DisclosedQuantity: 0,
                TriggerPrice: 0,
                SquareOffValue: 0,
                StoplossValue: 0,
                TrailingStoploss: 0,
                Variety: "regular"
            );

            var orderId = (string)response.GetValueOrDefault("data")?.GetValueOrDefault("order_id")!;
            _logger.LogInformation("Market order placed successfully. Order ID: {OrderId}", orderId);
            return orderId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place market order for {Symbol}", symbol);
            return null;
        }
    }

    public string? PlaceStopLossOrder(string symbol, string exchange, int quantity, decimal stopPrice, string transactionType)
    {
        try
        {
            bool isPaper = Environment.GetEnvironmentVariable("PAPER_TRADING") == "true";
            _logger.LogInformation("{Mode} Placing SL {TransactionType} order for {Quantity} {Symbol} @ {StopPrice}", isPaper ? "[PAPER]" : "[LIVE]", transactionType, quantity, symbol, stopPrice);
            
            if (isPaper) 
            {
                var mockId = Guid.NewGuid().ToString().Substring(0, 8);
                _logger.LogInformation("Paper SL trade executed virtually. Order ID: {OrderId}", mockId);
                return mockId;
            }
            
            var response = _kite.PlaceOrder(
                Exchange: exchange,
                TradingSymbol: symbol,
                TransactionType: transactionType,
                Quantity: quantity,
                Price: 0, 
                Product: "MIS",
                OrderType: "SL-M",
                Validity: "DAY",
                DisclosedQuantity: 0,
                TriggerPrice: stopPrice,
                SquareOffValue: 0,
                StoplossValue: 0,
                TrailingStoploss: 0,
                Variety: "regular"
            );

            var orderId = (string)response.GetValueOrDefault("data")?.GetValueOrDefault("order_id")!;
            _logger.LogInformation("Stop Loss order placed successfully. Order ID: {OrderId}", orderId);
            return orderId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place SL order for {Symbol}", symbol);
            return null;
        }
    }

    public string? PlaceHedgedBasket(string futureSymbol, string optionSymbol, int quantity, bool isLongFuture, decimal slPrice)
    {
        _logger.LogInformation(">>> EXECUTING HEDGED BASKET & REALITY CHECK SL-M <<<");
        // To get margin benefits instantly, buy the OTM option first
        PlaceMarketOrder(optionSymbol, "NFO", quantity, "BUY");

        // Then execute the futures leg
        string? futOrderId = PlaceMarketOrder(futureSymbol, "NFO", quantity, isLongFuture ? "BUY" : "SELL");
        
        // INSTANTLY inject the stop loss market order attached to the active futures direction
        string? slOrderId = PlaceStopLossOrder(futureSymbol, "NFO", quantity, slPrice, isLongFuture ? "SELL" : "BUY");
        return slOrderId;
    }

    public void CloseHedgedBasket(string futureSymbol, string optionSymbol, int quantity, bool isLongFuture)
    {
        _logger.LogInformation(">>> SQUARING OFF HEDGED BASKET <<<");
        // To avoid margin penalty, MUST square off futures first
        PlaceMarketOrder(futureSymbol, "NFO", quantity, isLongFuture ? "SELL" : "BUY");

        // Then sell the option
        PlaceMarketOrder(optionSymbol, "NFO", quantity, "SELL");
    }

    public List<Order> GetPendingOrders()
    {
        try
        {
            var orders = _kite.GetOrders();
            return orders.Where(o => o.Status == "OPEN" || o.Status == "TRIGGER PENDING").ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch pending orders.");
            return new List<Order>();
        }
    }

    public void CancelOrder(string orderId, string variety = "regular")
    {
        try
        {
            _logger.LogInformation("Cancelling Order ID: {OrderId}", orderId);
            _kite.CancelOrder(orderId, variety);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order {OrderId}", orderId);
        }
    }

    public decimal GetLtp(string exchange, string symbol)
    {
        bool isPaper = Environment.GetEnvironmentVariable("PAPER_TRADING") == "true";
        if (isPaper)
        {
            // Use the cached tick price fed by the live streamer
            var key = $"{exchange}:{symbol}";
            if (_paperPriceCache.TryGetValue(key, out var cached) && cached > 0)
            {
                _logger.LogInformation("[PAPER] LTP for {Symbol}: {Price} (from tick cache)", symbol, cached);
                return cached;
            }
            _logger.LogWarning("[PAPER] No cached LTP for {Symbol}. Falling back to live Kite API.", symbol);
        }
        
        try
        {
            var ltp = _kite.GetLTP(new string[] { $"{exchange}:{symbol}" });
            if (ltp.ContainsKey($"{exchange}:{symbol}"))
            {
                return ltp[$"{exchange}:{symbol}"].LastPrice;
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch LTP for {Symbol}", symbol);
            return 0;
        }
    }
}
