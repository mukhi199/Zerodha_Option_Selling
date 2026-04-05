namespace Trading.Strategy.Services;

using KiteConnect;
using Microsoft.Extensions.Logging;

public class OrderExecutionService : IOrderService
{
    private readonly Kite _kite;
    private readonly ILogger<OrderExecutionService> _logger;

    public OrderExecutionService(string apiKey, ILogger<OrderExecutionService> logger)
    {
        _kite = new Kite(apiKey);
        _logger = logger;
    }

    public void SetAccessToken(string accessToken)
    {
        _kite.SetAccessToken(accessToken);
    }

    public void PlaceMarketOrder(string symbol, string exchange, int quantity, string transactionType)
    {
        try
        {
            _logger.LogInformation("Placing Market {TransactionType} order for {Quantity} {Symbol}", transactionType, quantity, symbol);
            
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

            _logger.LogInformation("Market order placed successfully. Order ID: {OrderId}", (string)response.GetValueOrDefault("data")?.GetValueOrDefault("order_id")!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place market order for {Symbol}", symbol);
        }
    }

    public void PlaceStopLossOrder(string symbol, string exchange, int quantity, decimal stopPrice, string transactionType)
    {
        try
        {
            _logger.LogInformation("Placing SL {TransactionType} order for {Quantity} {Symbol} @ {StopPrice}", transactionType, quantity, symbol, stopPrice);
            
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

            _logger.LogInformation("Stop Loss order placed successfully. Order ID: {OrderId}", (string)response.GetValueOrDefault("data")?.GetValueOrDefault("order_id")!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place SL order for {Symbol}", symbol);
        }
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
}
