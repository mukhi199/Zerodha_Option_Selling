namespace Trading.Core.Models;

/// <summary>
/// Normalized tick received from Zerodha WebSocket.
/// </summary>
public class NormalizedTick
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Zerodha instrument token (uint, matches Kite SDK).</summary>
    public uint InstrumentToken { get; set; }

    public decimal Price { get; set; }

    public long Volume { get; set; }

    public decimal OpenInterest { get; set; }

    public decimal BidPrice { get; set; }

    public decimal AskPrice { get; set; }

    /// <summary>Exchange timestamp from Zerodha feed.</summary>
    public DateTime ExchangeTime { get; set; }

    /// <summary>Local receipt time.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}