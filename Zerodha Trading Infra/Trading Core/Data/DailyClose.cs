namespace Trading.Core.Data;

/// <summary>
/// Stores one daily OHLC + close price per symbol per trading date.
/// Used as the source of truth for recalculating SMA without Zerodha API calls.
/// </summary>
public class DailyClose
{
    public int Id { get; set; }

    /// <summary>e.g. "NIFTY 50", "NIFTY BANK"</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The trading date (date component only, time always 00:00:00).</summary>
    public DateTime Date { get; set; }

    public decimal Open  { get; set; }
    public decimal High  { get; set; }
    public decimal Low   { get; set; }
    public decimal Close { get; set; }
}
