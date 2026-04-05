namespace Trading.Backtest;

using Trading.Strategy.Services;
using Microsoft.Extensions.Logging;

public class BacktestOrderService : IOrderService
{
    private readonly ILogger<BacktestOrderService> _logger;
    public List<TradeRecord> Trades { get; } = new();

    public BacktestOrderService(ILogger<BacktestOrderService> logger)
    {
        _logger = logger;
    }

    public void PlaceMarketOrder(string symbol, string exchange, int quantity, string transactionType)
    {
        _logger.LogInformation("[BACKTEST] Market Order: {TransactionType} {Quantity} {Symbol}", transactionType, quantity, symbol);
        Trades.Add(new TradeRecord 
        { 
            Symbol = symbol, 
            Type = transactionType, 
            Quantity = quantity, 
            OrderType = "MARKET",
            ExchangeTime = DateTime.Now // In a real backtest, this should be the candle timestamp
        });
    }

    public void PlaceStopLossOrder(string symbol, string exchange, int quantity, decimal stopPrice, string transactionType)
    {
        _logger.LogInformation("[BACKTEST] SL Order: {TransactionType} {Quantity} {Symbol} @ {StopPrice}", transactionType, quantity, symbol, stopPrice);
        Trades.Add(new TradeRecord 
        { 
            Symbol = symbol, 
            Type = transactionType, 
            Quantity = quantity, 
            OrderType = "SL", 
            Price = stopPrice,
            ExchangeTime = DateTime.Now
        });
    }
}

public class TradeRecord
{
    public string Symbol { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string OrderType { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime ExchangeTime { get; set; }
}
