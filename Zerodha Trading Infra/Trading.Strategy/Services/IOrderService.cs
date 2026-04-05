namespace Trading.Strategy.Services;

public interface IOrderService
{
    void PlaceMarketOrder(string symbol, string exchange, int quantity, string transactionType);
    void PlaceStopLossOrder(string symbol, string exchange, int quantity, decimal stopPrice, string transactionType);
}
