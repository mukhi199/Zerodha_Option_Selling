namespace Trading.Core.Utils;

using Trading.Core.Models;

/// <summary>
/// Identifies common single and two-candle reversal/continuation patterns
/// from a 5-minute (or any) Candle.
/// </summary>
public static class CandlePatternDetector
{
    // Threshold percentages of the total range (High - Low) used for ratios
    private const decimal DojiBodyRatio        = 0.10m;  // body ≤ 10 % of range → Doji
    private const decimal HammerBodyRatio      = 0.33m;  // body ≤ 33 % of range
    private const decimal HammerWickMultiplier = 2.0m;   // lower wick ≥ 2× body
    private const decimal LongWickRatio        = 0.65m;  // wick ≥ 65 % of range

    // ─── Pattern flags ─────────────────────────────────────────────────────

    public enum CandlePattern
    {
        None,
        BullishEngulfing,
        BearishEngulfing,
        BullishHarami,
        BearishHarami,
        Hammer,           // Potential bullish reversal at support
        ShootingStar,     // Potential bearish reversal at resistance
        Doji,             // Indecision
        BullishLongWick,  // Long lower wick (buyers rejected lower price)
        BearishLongWick,  // Long upper wick (sellers rejected higher price)
        BullishCandle,    // Plain bullish (Close > Open)
        BearishCandle,    // Plain bearish (Close < Open)
        BullishMarubozu,  // Strong bullish momentum, almost no wicks
        BearishMarubozu   // Strong bearish momentum, almost no wicks
    }

    // ─── Single-candle analysis ─────────────────────────────────────────────

    public static CandlePattern DetectSingleCandle(Candle c)
    {
        decimal range = c.High - c.Low;
        if (range == 0) return CandlePattern.Doji;

        decimal body      = Math.Abs(c.Close - c.Open);
        decimal upperWick = c.High - Math.Max(c.Open, c.Close);
        decimal lowerWick = Math.Min(c.Open, c.Close) - c.Low;

        // Doji – almost no body
        if (body <= DojiBodyRatio * range)
            return CandlePattern.Doji;

        // Marubozu - Massive body taking >90% of the entire candle span
        if (body >= 0.90m * range && range > 0)
        {
            if (c.Close > c.Open) return CandlePattern.BullishMarubozu;
            if (c.Close < c.Open) return CandlePattern.BearishMarubozu;
        }

        // Hammer – small body at top, long lower wick (at least 2× the body)
        if (body <= HammerBodyRatio * range && lowerWick >= HammerWickMultiplier * body && upperWick < body)
            return CandlePattern.Hammer;

        // Shooting Star – small body at bottom, long upper wick
        if (body <= HammerBodyRatio * range && upperWick >= HammerWickMultiplier * body && lowerWick < body)
            return CandlePattern.ShootingStar;

        // Long Wicks
        if (lowerWick >= LongWickRatio * range)
            return CandlePattern.BullishLongWick;

        if (upperWick >= LongWickRatio * range)
            return CandlePattern.BearishLongWick;

        // Plain directional candles
        return c.Close > c.Open ? CandlePattern.BullishCandle : CandlePattern.BearishCandle;
    }

    // ─── Two-candle analysis ────────────────────────────────────────────────

    /// <summary>
    /// Detects two-candle patterns using the previous and current candle.
    /// Returns None if no recognisable two-candle pattern is found.
    /// </summary>
    public static CandlePattern DetectTwoCandle(Candle prev, Candle curr)
    {
        // Bullish Engulfing: prev is bearish, curr is bullish and fully engulfs prev
        if (prev.Close < prev.Open &&
            curr.Close > curr.Open &&
            curr.Open  < prev.Close &&
            curr.Close > prev.Open)
            return CandlePattern.BullishEngulfing;

        // Bearish Engulfing: prev is bullish, curr is bearish and fully engulfs prev
        if (prev.Close > prev.Open &&
            curr.Close < curr.Open &&
            curr.Open  > prev.Close &&
            curr.Close < prev.Open)
            return CandlePattern.BearishEngulfing;

        // Bullish Harami: prev is big bearish, curr is small bullish inside prev
        if (prev.Close < prev.Open &&
            curr.Close > curr.Open &&
            curr.Open  > prev.Close &&
            curr.Close < prev.Open)
            return CandlePattern.BullishHarami;

        // Bearish Harami: prev is big bullish, curr is small bearish inside prev
        if (prev.Close > prev.Open &&
            curr.Close < curr.Open &&
            curr.Open  < prev.Close &&
            curr.Close > prev.Open)
            return CandlePattern.BearishHarami;

        return CandlePattern.None;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable description of the pattern.
    /// </summary>
    public static string Describe(CandlePattern pattern) => pattern switch
    {
        CandlePattern.BullishEngulfing => "Bullish Engulfing",
        CandlePattern.BearishEngulfing => "Bearish Engulfing",
        CandlePattern.BullishHarami   => "Bullish Harami",
        CandlePattern.BearishHarami   => "Bearish Harami",
        CandlePattern.Hammer          => "Hammer",
        CandlePattern.ShootingStar    => "Shooting Star",
        CandlePattern.Doji            => "Doji",
        CandlePattern.BullishLongWick => "Bullish Long-Wick",
        CandlePattern.BearishLongWick => "Bearish Long-Wick",
        CandlePattern.BullishCandle   => "Bullish Candle",
        CandlePattern.BearishCandle   => "Bearish Candle",
        CandlePattern.BullishMarubozu => "Bullish Marubozu",
        CandlePattern.BearishMarubozu => "Bearish Marubozu",
        _                             => "No Pattern"
    };

    /// <summary>
    /// Returns true when the pattern suggests bullish momentum.
    /// </summary>
    public static bool IsBullish(CandlePattern p) =>
        p is CandlePattern.BullishEngulfing or
             CandlePattern.BullishHarami    or
             CandlePattern.Hammer           or
             CandlePattern.BullishLongWick  or
             CandlePattern.BullishMarubozu  or
             CandlePattern.BullishCandle;

    /// <summary>
    /// Returns true when the pattern suggests bearish momentum.
    /// </summary>
    public static bool IsBearish(CandlePattern p) =>
        p is CandlePattern.BearishEngulfing or
             CandlePattern.BearishHarami    or
             CandlePattern.ShootingStar     or
             CandlePattern.BearishLongWick  or
             CandlePattern.BearishMarubozu  or
             CandlePattern.BearishCandle;
}
