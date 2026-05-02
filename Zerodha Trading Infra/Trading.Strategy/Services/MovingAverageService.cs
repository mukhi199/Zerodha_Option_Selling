// =============================================================================
// MovingAverageService_FIXED.cs
// =============================================================================
// FIXES APPLIED (search "// FIX #N" to jump to each change):
//
//   FIX #1 — N+1 query eliminated in BootstrapFromHistoryAsync
//             (was calling AnyAsync inside a foreach loop = 300 DB round-trips;
//              now fetches all existing dates in one query, checks in-memory)
//
//   FIX #2 — Double AddClosePrice on bootstrap eliminated
//             (BootstrapFromHistoryAsync called LoadIntoTrackerAsync which adds
//              all closes, then called UpdateDailyAsync which added the last
//              close again — corrupting EMA values for the final data point)
//
//   FIX #3 — EMA values seeded from snapshot on LoadIntoTrackerAsync
//             (the TODO comment "seeding from snapshot is more accurate" is now
//              implemented — if a snapshot exists its EMA values are pushed into
//              the tracker so EMA state survives restarts accurately)
//
//   FIX #4 — Null MA values guarded before snapshot assignment
//             (GetSMA/GetEMA return nullable decimal; assigning null directly to
//              snapshot fields without a guard causes silent data loss in the DB)
// =============================================================================

namespace Trading.Strategy.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trading.Core.Data;
using Trading.Core.Models;

public class MovingAverageService
{
    private readonly AppDbContext _db;
    private readonly TechnicalIndicatorsTracker _tracker;
    private readonly ILogger<MovingAverageService> _logger;

    public MovingAverageService(
        AppDbContext db,
        TechnicalIndicatorsTracker tracker,
        ILogger<MovingAverageService> logger)
    {
        _db      = db;
        _tracker = tracker;
        _logger  = logger;
    }

    // =========================================================================
    /// <summary>
    /// Loads the last 200 daily closes from the database into the in-memory tracker.
    /// Returns true if enough data was found to fully warm up (200 periods).
    /// </summary>
    // =========================================================================
    public async Task<bool> LoadIntoTrackerAsync(string symbol)
    {
        var closes = await _db.DailyCloses
            .Where(d => d.Symbol == symbol)
            .OrderByDescending(d => d.Date)
            .Take(200)
            .OrderBy(d => d.Date)
            .ToListAsync();

        if (closes.Count == 0) return false;

        foreach (var c in closes)
        {
            _tracker.AddClosePrice(symbol, c.Close);

            // Only add completed days (yesterday or older) to 3-Day range history
            if (c.Date.Date < DateTime.Today)
                _tracker.AddDailyRange(symbol, c.High, c.Low);
        }

        // ── Seed CPR Levels Dynamically ──
        var lastDay = closes.Last();
        _tracker.SetDailyCPR(symbol, lastDay.High, lastDay.Low, lastDay.Close);

        var today = DateTime.Today;

        // Previous Calendar Week
        int diff               = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
        var startOfCurrentWeek = today.AddDays(-diff).Date;
        var startOfLastWeek    = startOfCurrentWeek.AddDays(-7);
        var endOfLastWeek      = startOfCurrentWeek.AddDays(-1);

        var weekCandles = closes.Where(c => c.Date >= startOfLastWeek && c.Date <= endOfLastWeek).ToList();
        if (weekCandles.Any())
        {
            _tracker.SetWeeklyCPR(symbol,
                weekCandles.Max(c => c.High),
                weekCandles.Min(c => c.Low),
                weekCandles.Last().Close);
        }

        // Previous Calendar Month
        var startOfCurrentMonth = new DateTime(today.Year, today.Month, 1);
        var startOfLastMonth    = startOfCurrentMonth.AddMonths(-1);
        var endOfLastMonth      = startOfCurrentMonth.AddDays(-1);

        var monthCandles = closes.Where(c => c.Date >= startOfLastMonth && c.Date <= endOfLastMonth).ToList();
        if (monthCandles.Any())
        {
            _tracker.SetMonthlyCPR(symbol,
                monthCandles.Max(c => c.High),
                monthCandles.Min(c => c.Low),
                monthCandles.Last().Close);
        }

        // ── Restore EMA values from latest snapshot ──
        var latestSnapshot = await _db.MovingAverageSnapshots
            .Where(s => s.Symbol == symbol)
            .OrderByDescending(s => s.Date)
            .FirstOrDefaultAsync();

        if (latestSnapshot != null)
        {
            // -----------------------------------------------------------------
            // FIX #3 — Seed EMA values from the saved snapshot.
            //
            // ORIGINAL CODE:
            //   _logger.LogInformation("Restored {Symbol} state from DB...");
            //   // Note: TechnicalIndicatorsTracker might need a method to set
            //   // existing EMA values. For now, adding close prices sequentially
            //   // will rebuild EMAs, but seeding from snapshot is more accurate.
            //
            // PROBLEM: The TODO was never implemented. On every restart the tracker
            // rebuilt EMAs from scratch using sequential close prices, which gives
            // approximate (not exact) EMA values — especially for EMA200 which needs
            // 200+ candles to fully converge. The snapshot already stores the exact
            // last-known EMA values; we should use them.
            //
            // FIX: Call SetEMA on the tracker for each period if the snapshot has
            // a value, overwriting the sequentially-rebuilt approximate value with
            // the historically-accurate one.
            // -----------------------------------------------------------------

            // FIX #3 ↓ — seed tracker EMAs from snapshot (requires SetEMA on tracker)
            if (latestSnapshot.EMA50.HasValue)
                _tracker.SetEMA(symbol, 50,  latestSnapshot.EMA50.Value);   // FIX #3
            if (latestSnapshot.EMA100.HasValue)
                _tracker.SetEMA(symbol, 100, latestSnapshot.EMA100.Value);  // FIX #3
            if (latestSnapshot.EMA200.HasValue)
                _tracker.SetEMA(symbol, 200, latestSnapshot.EMA200.Value);  // FIX #3

            _logger.LogInformation(
                "Restored {Symbol} EMA state from DB snapshot dated {Date}. EMA50={EMA50}, EMA200={EMA200}",
                symbol,
                latestSnapshot.Date.ToShortDateString(),
                latestSnapshot.EMA50,
                latestSnapshot.EMA200);
        }
        else
        {
            _logger.LogInformation(
                "No snapshot found for {Symbol}. EMAs rebuilt sequentially from {Count} closes.",
                symbol, closes.Count);
        }

        return closes.Count >= 200;
    }

    // =========================================================================
    /// <summary>
    /// Saves today's close and updates the MA snapshot in the database.
    /// </summary>
    // =========================================================================
    public async Task UpdateDailyAsync(
        string symbol, DateTime date,
        decimal open, decimal high, decimal low, decimal close)
    {
        // Upsert daily close
        var existingClose = await _db.DailyCloses
            .FirstOrDefaultAsync(d => d.Symbol == symbol && d.Date.Date == date.Date);

        if (existingClose == null)
        {
            _db.DailyCloses.Add(new DailyClose
            {
                Symbol = symbol, Date = date.Date,
                Open = open, High = high, Low = low, Close = close
            });
        }
        else
        {
            existingClose.Open  = open;
            existingClose.High  = high;
            existingClose.Low   = low;
            existingClose.Close = close;
        }

        await _db.SaveChangesAsync();

        _tracker.AddClosePrice(symbol, close);

        // -----------------------------------------------------------------
        // FIX #4 — Null MA values guarded before snapshot assignment.
        //
        // ORIGINAL CODE:
        //   var snapshot = new MovingAverageSnapshot
        //   {
        //       SMA50  = _tracker.GetSMA(symbol, 50),   // returns decimal?
        //       EMA200 = _tracker.GetEMA(symbol, 200),  // returns decimal?
        //       ...
        //   };
        //
        // PROBLEM: GetSMA/GetEMA return nullable decimal (decimal?). If the
        // tracker doesn't have enough data yet (e.g. only 30 closes loaded),
        // GetSMA(50) returns null. Assigning null to the snapshot field without
        // a guard writes NULL into the DB column, silently losing previously
        // stored values on the next upsert.
        //
        // FIX: only overwrite a snapshot field if the tracker returned a value.
        // -----------------------------------------------------------------

        // FIX #4 ↓ — read nullable values first, then guard before assigning
        var sma50  = _tracker.GetSMA(symbol, 50);   // FIX #4
        var sma100 = _tracker.GetSMA(symbol, 100);  // FIX #4
        var sma200 = _tracker.GetSMA(symbol, 200);  // FIX #4
        var ema50  = _tracker.GetEMA(symbol, 50);   // FIX #4
        var ema100 = _tracker.GetEMA(symbol, 100);  // FIX #4
        var ema200 = _tracker.GetEMA(symbol, 200);  // FIX #4

        var existingSnapshot = await _db.MovingAverageSnapshots
            .FirstOrDefaultAsync(s => s.Symbol == symbol && s.Date.Date == date.Date);

        if (existingSnapshot == null)
        {
            _db.MovingAverageSnapshots.Add(new MovingAverageSnapshot
            {
                Symbol = symbol,
                Date   = date.Date,
                SMA50  = sma50,   // null is acceptable on first insert (not enough history yet)
                SMA100 = sma100,
                SMA200 = sma200,
                EMA50  = ema50,
                EMA100 = ema100,
                EMA200 = ema200
            });
        }
        else
        {
            // FIX #4 ↓ — only overwrite if tracker returned a real value;
            //            preserve existing DB value if tracker has insufficient data
            if (sma50.HasValue)  existingSnapshot.SMA50  = sma50;   // FIX #4
            if (sma100.HasValue) existingSnapshot.SMA100 = sma100;  // FIX #4
            if (sma200.HasValue) existingSnapshot.SMA200 = sma200;  // FIX #4
            if (ema50.HasValue)  existingSnapshot.EMA50  = ema50;   // FIX #4
            if (ema100.HasValue) existingSnapshot.EMA100 = ema100;  // FIX #4
            if (ema200.HasValue) existingSnapshot.EMA200 = ema200;  // FIX #4
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated {Symbol} MA Snapshot for {Date}", symbol, date.ToShortDateString());
    }

    // =========================================================================
    /// <summary>
    /// Seeds the database with historical data from Zerodha.
    /// </summary>
    // =========================================================================
    public async Task BootstrapFromHistoryAsync(string symbol, List<KiteConnect.Historical> data)
    {
        _logger.LogInformation("Bootstrapping {Symbol} from {Count} historical candles.", symbol, data.Count);

        // -----------------------------------------------------------------
        // FIX #1 — Eliminate N+1 query pattern in bootstrap loop.
        //
        // ORIGINAL CODE:
        //   foreach (var h in data)
        //   {
        //       var existing = await _db.DailyCloses.AnyAsync(d => d.Symbol == symbol && d.Date == date);
        //       if (!existing) { _db.DailyCloses.Add(...); }
        //   }
        //
        // PROBLEM: AnyAsync inside a foreach = one DB round-trip per candle.
        // With 300 candles that's 300 sequential DB calls before a single insert.
        // On SQLite this can take several seconds; on a cold start it blocks the
        // entire pre-warm pipeline.
        //
        // FIX: fetch all existing dates for this symbol in one query into a
        // HashSet, then check membership in-memory — O(1) per candle, 1 DB call.
        // -----------------------------------------------------------------

        // FIX #1 ↓ — single query to get all existing dates
        var existingDates = await _db.DailyCloses
            .Where(d => d.Symbol == symbol)
            .Select(d => d.Date)
            .ToHashSetAsync();  // FIX #1: one DB call instead of 300

        foreach (var h in data)
        {
            var date = h.TimeStamp.Date;
            if (!existingDates.Contains(date))  // FIX #1: in-memory check
            {
                _db.DailyCloses.Add(new DailyClose
                {
                    Symbol = symbol,
                    Date   = date,
                    Open   = h.Open,
                    High   = h.High,
                    Low    = h.Low,
                    Close  = h.Close
                });
            }
        }

        await _db.SaveChangesAsync();  // FIX #1: single SaveChanges for all inserts

        // -----------------------------------------------------------------
        // FIX #2 — Eliminate double AddClosePrice on the last candle.
        //
        // ORIGINAL CODE:
        //   await LoadIntoTrackerAsync(symbol);      // adds all closes incl. last
        //   var last = data.Last();
        //   await UpdateDailyAsync(...last...);      // adds last close AGAIN
        //
        // PROBLEM: LoadIntoTrackerAsync calls _tracker.AddClosePrice for every
        // close including the last one. Then UpdateDailyAsync immediately calls
        // _tracker.AddClosePrice(symbol, close) for the same last candle again.
        // This means the EMA calculation receives the final data point twice,
        // biasing it toward the last close price.
        //
        // FIX: After bootstrap, call LoadIntoTrackerAsync to warm up the tracker,
        // then call UpdateDailyAsync with skipTrackerUpdate: true (new overload)
        // so the DB snapshot is saved without double-counting the close price.
        // -----------------------------------------------------------------

        // FIX #2 ↓ — load tracker first (adds all closes including last)
        await LoadIntoTrackerAsync(symbol);

        // FIX #2 ↓ — save the snapshot only, skip re-adding the close to tracker
        var last = data.Last();
        await SaveSnapshotOnlyAsync(symbol, last.TimeStamp.Date);  // FIX #2
    }

    // =========================================================================
    // FIX #2 — New private helper: saves the MA snapshot without calling
    // AddClosePrice again. Used by BootstrapFromHistoryAsync to avoid the
    // double-count that occurred when UpdateDailyAsync was called after
    // LoadIntoTrackerAsync had already added the last close.
    // =========================================================================
    private async Task SaveSnapshotOnlyAsync(string symbol, DateTime date)  // FIX #2
    {
        var sma50  = _tracker.GetSMA(symbol, 50);
        var sma100 = _tracker.GetSMA(symbol, 100);
        var sma200 = _tracker.GetSMA(symbol, 200);
        var ema50  = _tracker.GetEMA(symbol, 50);
        var ema100 = _tracker.GetEMA(symbol, 100);
        var ema200 = _tracker.GetEMA(symbol, 200);

        var existingSnapshot = await _db.MovingAverageSnapshots
            .FirstOrDefaultAsync(s => s.Symbol == symbol && s.Date.Date == date.Date);

        if (existingSnapshot == null)
        {
            _db.MovingAverageSnapshots.Add(new MovingAverageSnapshot
            {
                Symbol = symbol, Date = date.Date,
                SMA50 = sma50, SMA100 = sma100, SMA200 = sma200,
                EMA50 = ema50, EMA100 = ema100, EMA200 = ema200
            });
        }
        else
        {
            if (sma50.HasValue)  existingSnapshot.SMA50  = sma50;
            if (sma100.HasValue) existingSnapshot.SMA100 = sma100;
            if (sma200.HasValue) existingSnapshot.SMA200 = sma200;
            if (ema50.HasValue)  existingSnapshot.EMA50  = ema50;
            if (ema100.HasValue) existingSnapshot.EMA100 = ema100;
            if (ema200.HasValue) existingSnapshot.EMA200 = ema200;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Bootstrap complete. Saved MA snapshot for {Symbol} dated {Date}.", symbol, date.ToShortDateString());
    }
}
