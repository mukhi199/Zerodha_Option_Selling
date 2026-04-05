namespace Trading.Core.Models;

/// <summary>
/// OHLCV candle bar for a given symbol and timeframe interval.
/// </summary>
public class Candle
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Candle duration in minutes (1, 3, 5, 30).</summary>
    public int IntervalMinutes { get; set; }

    /// <summary>Start of the candle period (aligned to interval boundary).</summary>
    public DateTime StartTime { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public long Volume { get; set; }

    /// <summary>True once the interval has elapsed and the candle is finalized.</summary>
    public bool IsClosed { get; set; }
}
