namespace Trading.Core.Data;

/// <summary>
/// Stores a snapshot of computed SMA and EMA values for a symbol on a given date.
/// Updated once per trading day — the EMA values here are required to efficiently
/// roll forward EMA the next day without fetching full history again.
/// </summary>
public class MovingAverageSnapshot
{
    public int Id { get; set; }

    /// <summary>e.g. "NIFTY 50", "NIFTY BANK"</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The trading date this snapshot was computed for.</summary>
    public DateTime Date { get; set; }

    // ── Simple Moving Averages ──────────────────────────
    public decimal? SMA50  { get; set; }
    public decimal? SMA100 { get; set; }
    public decimal? SMA200 { get; set; }

    // ── Exponential Moving Averages ─────────────────────
    // These are CRITICAL: tomorrow's EMA only needs these + the new close price.
    public decimal? EMA50  { get; set; }
    public decimal? EMA100 { get; set; }
    public decimal? EMA200 { get; set; }
}
