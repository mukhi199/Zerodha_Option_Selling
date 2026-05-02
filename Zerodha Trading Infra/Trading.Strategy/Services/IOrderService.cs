namespace Trading.Strategy.Services;

using KiteConnect;

public interface IOrderService
{
    string? PlaceMarketOrder(string symbol, string exchange, int quantity, string transactionType);
    string? PlaceStopLossOrder(string symbol, string exchange, int quantity, decimal stopPrice, string transactionType);
    
    // Hedging
    string? PlaceHedgedBasket(string futureSymbol, string optionSymbol, int quantity, bool isLongFuture, decimal slPrice);
    void CloseHedgedBasket(string futureSymbol, string optionSymbol, int quantity, bool isLongFuture);

    List<Order> GetPendingOrders();
    void CancelOrder(string orderId, string variety = "regular");
}
