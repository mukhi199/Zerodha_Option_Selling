namespace Trading.Strategy.Services;

using Microsoft.Extensions.Logging;
using Trading.Core.Models;
using Trading.Core.Utils;

/// <summary>
/// CPR Bounce Strategy for NIFTY 50 and NIFTY BANK.
///
/// Logic overview:
///  1. On the first 5-minute candle of the day, record the Day Open and determine
///     whether the open is ABOVE or BELOW the Daily CPR.
///  2. On each subsequent 5-minute candle, check if price has entered the CPR zone.
///  3. When price touches the CPR zone, detect the candlestick pattern (Hammer,
///     Engulfing, Harami, Doji, etc.) and classify it as a bullish or bearish trigger.
///  4. Also factor in the DAY-OPEN context:
///       - Open was ABOVE CPR → Price is coming DOWN to CPR → potential bullish bounce or bearish breakdown.
///       - Open was BELOW CPR → Price came UP to CPR → potential bear rejection or bullish breakout.
///  5. Log a TRIGGER message with all details. No live orders are placed.
/// </summary>
public class CprBounceStrategy : IStrategy
{
    private readonly TechnicalIndicatorsTracker _tracker;
    private readonly IStrategicStateStore        _stateStore;
    private readonly ILogger<CprBounceStrategy> _logger;

    // Track day-open and previous candle per symbol
    private readonly Dictionary<string, decimal>  _dayOpen       = new();
    private readonly Dictionary<string, DateTime> _dayOpenDate   = new();
    private readonly Dictionary<string, Candle>   _prevCandle    = new();

    // Avoid re-triggering multiple times at the same CPR touch
    private readonly Dictionary<string, DateTime> _lastTriggerAt = new();

    // Symbols this strategy acts on
    private static readonly HashSet<string> _symbols = new()
    {
        "NIFTY 50", "NIFTY BANK"
    };

    public CprBounceStrategy(TechnicalIndicatorsTracker tracker, IStrategicStateStore stateStore, ILogger<CprBounceStrategy> logger)
    {
        _tracker    = tracker;
        _stateStore = stateStore;
        _logger     = logger;
    }

    // ── IStrategy.OnTick ───────────────────────────────────────────────────

    public void OnTick(NormalizedTick tick)
    {
        // Not used in this strategy
    }

    // ── IStrategy.OnCandle ────────────────────────────────────────────────

    public void OnCandle(Candle candle)
    {
        if (candle.IntervalMinutes != 5)           return;
        if (!_symbols.Contains(candle.Symbol))     return;

        var sym = candle.Symbol;

        // ── 1. Track Day Open ─────────────────────────────────────────
        if (!_dayOpenDate.TryGetValue(sym, out var openDate) || openDate.Date != candle.StartTime.Date)
        {
            _dayOpen[sym]     = candle.Open;
            _dayOpenDate[sym] = candle.StartTime;

            _logger.LogInformation(
                "[CprBounceStrategy] [{Symbol}] New Day Open recorded: {Open} at {Time}",
                sym, candle.Open, candle.StartTime);
        }

        // ── 2. Fetch CPR levels ───────────────────────────────────────
        var dailyCpr   = _tracker.GetDailyCPR(sym);
        var weeklyCpr  = _tracker.GetWeeklyCPR(sym);
        var monthlyCpr = _tracker.GetMonthlyCPR(sym);

        if (dailyCpr == null)
        {
            _logger.LogWarning("[CprBounceStrategy] [{Symbol}] Daily CPR not available — skipping candle.", sym);
            _prevCandle[sym] = candle;
            return;
        }

        PrintCprTable(sym, dailyCpr, weeklyCpr, monthlyCpr, candle.StartTime);

        // ── 3. Determine Day-Open context ─────────────────────────────
        var dayOpen             = _dayOpen[sym];
        bool openAboveCpr       = dayOpen > dailyCpr.TopCentral;
        bool openBelowCpr       = dayOpen < dailyCpr.BottomCentral;

        string openContext = openAboveCpr ? "OPEN ABOVE CPR" :
                             openBelowCpr ? "OPEN BELOW CPR" : "OPEN INSIDE CPR";

        // ── 4. Check if current candle touches CPR zone ───────────────
        bool touchesCpr = CandleTouchesCpr(candle, dailyCpr);
        
        // Update State Store
        _stateStore.UpdateSymbolState(sym, s => {
            s.Pivot = dailyCpr.Pivot;
            s.Bc = dailyCpr.BottomCentral;
            s.Tc = dailyCpr.TopCentral;
            if (touchesCpr) dailyCpr.TouchedToday = true;
            s.IsVirginCpr = !dailyCpr.TouchedToday;
        });

        if (!touchesCpr)
        {
            _prevCandle[sym] = candle;
            return;
        }

        // Avoid spamming triggers: only fire once per 15-minute window
        if (_lastTriggerAt.TryGetValue(sym, out var last) &&
            (candle.StartTime - last).TotalMinutes < 15)
        {
            _prevCandle[sym] = candle;
            return;
        }

        _lastTriggerAt[sym] = candle.StartTime;

        // ── 5. Identify Candlestick Pattern ───────────────────────────
        var singlePattern = CandlePatternDetector.DetectSingleCandle(candle);
        var twoPattern    = _prevCandle.TryGetValue(sym, out var prev)
                            ? CandlePatternDetector.DetectTwoCandle(prev, candle)
                            : CandlePatternDetector.CandlePattern.None;

        // Prefer two-candle patterns over single-candle
        var finalPattern  = twoPattern != CandlePatternDetector.CandlePattern.None
                            ? twoPattern
                            : singlePattern;

        bool isBullish = CandlePatternDetector.IsBullish(finalPattern);
        bool isBearish = CandlePatternDetector.IsBearish(finalPattern);

        string direction = finalPattern == CandlePatternDetector.CandlePattern.Doji
                           ? "INDECISION (Doji)"
                           : isBullish ? "BULLISH" : isBearish ? "BEARISH" : "NEUTRAL";

        // ── 6. Scenario narrative ─────────────────────────────────────
        string scenario = BuildScenario(openAboveCpr, openBelowCpr, isBullish, isBearish, dailyCpr, candle);

        // ── 7. Log the TRIGGER ────────────────────────────────────────
        _logger.LogWarning(
            "=== TRIGGER ===\n" +
            "  Symbol   : {Symbol}\n" +
            "  Time     : {Time}\n" +
            "  Day Open : {DayOpen} ({OpenContext})\n" +
            "  CPR Zone : BC={BC} | Pivot={Pivot} | TC={TC}\n" +
            "  Candle   : O={Open} H={High} L={Low} C={Close}\n" +
            "  Pattern  : {Pattern}\n" +
            "  Direction: {Direction}\n" +
            "  Scenario : {Scenario}\n" +
            "================",
            sym, candle.StartTime, dayOpen, openContext,
            dailyCpr.BottomCentral, dailyCpr.Pivot, dailyCpr.TopCentral,
            candle.Open, candle.High, candle.Low, candle.Close,
            CandlePatternDetector.Describe(finalPattern),
            direction,
            scenario);

        _prevCandle[sym] = candle;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any part of the candle overlaps the CPR zone
    /// (BottomCentral → TopCentral).
    /// </summary>
    private static bool CandleTouchesCpr(Candle c, TechnicalIndicatorsTracker.PivotLevels cpr)
    {
        // Candle body or wicks must intersect [BottomCentral, TopCentral]
        return c.High >= cpr.BottomCentral && c.Low <= cpr.TopCentral;
    }

    /// <summary>
    /// Builds a human-readable scenario description.
    /// </summary>
    private static string BuildScenario(
        bool openAboveCpr, bool openBelowCpr,
        bool isBullish,    bool isBearish,
        TechnicalIndicatorsTracker.PivotLevels cpr, Candle candle)
    {
        if (openAboveCpr)
        {
            if (isBullish)
                return "Price dropped to Daily CPR from ABOVE and is showing a bullish reversal. " +
                       "CPR may act as strong support. Watch for long entry above TC.";
            if (isBearish)
                return "Price dropped to Daily CPR from ABOVE and is showing a bearish continuation. " +
                       "If it breaks below BC, a downside move towards S1 is likely.";
            return "Price dropped to Daily CPR from ABOVE. Candle is indecisive at CPR — wait for next candle confirmation.";
        }

        if (openBelowCpr)
        {
            if (isBullish)
                return "Price has rallied from BELOW Daily CPR and is testing TC from below. " +
                       "A bullish breakout above TC could lead to R1. Stay long on close above TC.";
            if (isBearish)
                return "Price has rallied to Daily CPR from BELOW and is getting rejected. " +
                       "CPR is acting as resistance. Watch for short entry below BC targeting S1.";
            return "Price is at Daily CPR from BELOW. Indecision — wait for breakout to either side.";
        }

        // Open was inside CPR
        if (isBullish)
            return "Day opened inside CPR. Candle is bullish — watch for upward break above TC to target R1.";
        if (isBearish)
            return "Day opened inside CPR. Candle is bearish — watch for downward break below BC to target S1.";

        return "Day opened inside CPR. Doji / Neutral candle — highly uncertain, wait for direction.";
    }

    /// <summary>
    /// Logs a clean CPR table for the symbol (only once per candle but rate-limited).
    /// </summary>
    private void PrintCprTable(string symbol,
        TechnicalIndicatorsTracker.PivotLevels daily,
        TechnicalIndicatorsTracker.PivotLevels? weekly,
        TechnicalIndicatorsTracker.PivotLevels? monthly,
        DateTime asOf)
    {
        // Only print once per hour per symbol to avoid log flooding
        const string logKey = "tableprint";
        if (_lastTriggerAt.TryGetValue(symbol + logKey, out var lastPrint) &&
            (asOf - lastPrint).TotalHours < 1)
            return;

        _lastTriggerAt[symbol + logKey] = asOf;

        _logger.LogInformation(
            "[CprBounceStrategy] [{Symbol}] CPR Table ({AsOf})\n" +
            "┌─────────────┬───────────┬───────────┬───────────┐\n" +
            "│ Timeframe   │  Bot.Ctrl │   Pivot   │  Top.Ctrl │\n" +
            "├─────────────┼───────────┼───────────┼───────────┤\n" +
            "│ Daily       │ {DayBC,9:F2} │ {DayP,9:F2} │ {DayTC,9:F2} │\n" +
            "│ Weekly      │ {WkBC,9} │ {WkP,9} │ {WkTC,9} │\n" +
            "│ Monthly     │ {MoBC,9} │ {MoP,9} │ {MoTC,9} │\n" +
            "│             │     R1    │     S1    │     R2    │\n" +
            "│ (Daily)     │ {DR1,9:F2} │ {DS1,9:F2} │ {DR2,9:F2} │\n" +
            "└─────────────┴───────────┴───────────┴───────────┘",
            symbol, asOf,
            daily.BottomCentral, daily.Pivot, daily.TopCentral,
            weekly  != null ? weekly.BottomCentral.ToString("F2")  : "N/A      ",
            weekly  != null ? weekly.Pivot.ToString("F2")          : "N/A      ",
            weekly  != null ? weekly.TopCentral.ToString("F2")     : "N/A      ",
            monthly != null ? monthly.BottomCentral.ToString("F2") : "N/A      ",
            monthly != null ? monthly.Pivot.ToString("F2")         : "N/A      ",
            monthly != null ? monthly.TopCentral.ToString("F2")    : "N/A      ",
            daily.R1, daily.S1, daily.R2);
    }
}
