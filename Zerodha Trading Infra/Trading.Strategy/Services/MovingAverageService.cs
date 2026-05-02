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

    public MovingAverageService(AppDbContext db, TechnicalIndicatorsTracker tracker, ILogger<MovingAverageService> logger)
    {
        _db = db;
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>
    /// Loads the last 200 daily closes from the database into the in-memory tracker.
    /// Returns true if enough data was found to fully warm up (200 periods).
    /// </summary>
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
            
            // Only add to the 3-Day Daily Range history if it's a COMPLETED day (Yesterday or older)
            if (c.Date.Date < DateTime.Today)
            {
                _tracker.AddDailyRange(symbol, c.High, c.Low);
            }
        }

        // ── Seed CPR Levels Dynamically ──
        var lastDay = closes.Last();
        _tracker.SetDailyCPR(symbol, lastDay.High, lastDay.Low, lastDay.Close);

        var today = DateTime.Today;
        // Previous Calendar Week
        int diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
        var startOfCurrentWeek = today.AddDays(-1 * diff).Date;
        var startOfLastWeek = startOfCurrentWeek.AddDays(-7);
        var endOfLastWeek = startOfCurrentWeek.AddDays(-1);
        
        var weekCandles = closes.Where(c => c.Date >= startOfLastWeek && c.Date <= endOfLastWeek).ToList();
        if (weekCandles.Any())
        {
            var wHigh = weekCandles.Max(c => c.High);
            var wLow = weekCandles.Min(c => c.Low);
            var wClose = weekCandles.Last().Close;
            _tracker.SetWeeklyCPR(symbol, wHigh, wLow, wClose);
        }

        // Previous Calendar Month
        var startOfCurrentMonth = new DateTime(today.Year, today.Month, 1);
        var startOfLastMonth = startOfCurrentMonth.AddMonths(-1);
        var endOfLastMonth = startOfCurrentMonth.AddDays(-1);
        
        var monthCandles = closes.Where(c => c.Date >= startOfLastMonth && c.Date <= endOfLastMonth).ToList();
        if (monthCandles.Any())
        {
            var mHigh = monthCandles.Max(c => c.High);
            var mLow = monthCandles.Min(c => c.Low);
            var mClose = monthCandles.Last().Close;
            _tracker.SetMonthlyCPR(symbol, mHigh, mLow, mClose);
        }

        // Also try to load the latest snapshot to restore EMA values correctly
        var latestSnapshot = await _db.MovingAverageSnapshots
            .Where(s => s.Symbol == symbol)
            .OrderByDescending(s => s.Date)
            .FirstOrDefaultAsync();

        if (latestSnapshot != null)
        {
            // Note: TechnicalIndicatorsTracker might need a method to set existing EMA values
            // For now, adding close prices sequentially will rebuild EMAs, but seeding from snapshot is more accurate.
            _logger.LogInformation("Restored {Symbol} state from DB. Latest Date: {Date}", symbol, latestSnapshot.Date.ToShortDateString());
        }

        return closes.Count >= 200;
    }

    /// <summary>
    /// Saves today's close and updates the MA snapshot in the database.
    /// </summary>
    public async Task UpdateDailyAsync(string symbol, DateTime date, decimal open, decimal high, decimal low, decimal close)
    {
        var existingClose = await _db.DailyCloses.FirstOrDefaultAsync(d => d.Symbol == symbol && d.Date.Date == date.Date);
        if (existingClose == null)
        {
            _db.DailyCloses.Add(new DailyClose { Symbol = symbol, Date = date.Date, Open = open, High = high, Low = low, Close = close });
        }
        else
        {
            existingClose.Open = open; existingClose.High = high; existingClose.Low = low; existingClose.Close = close;
        }

        await _db.SaveChangesAsync();

        // Update technical tracker with the latest price if it's new
        _tracker.AddClosePrice(symbol, close);

        // Save snapshot
        var snapshot = new MovingAverageSnapshot
        {
            Symbol = symbol,
            Date = date.Date,
            SMA50 = _tracker.GetSMA(symbol, 50),
            SMA100 = _tracker.GetSMA(symbol, 100),
            SMA200 = _tracker.GetSMA(symbol, 200),
            EMA50 = _tracker.GetEMA(symbol, 50),
            EMA100 = _tracker.GetEMA(symbol, 100),
            EMA200 = _tracker.GetEMA(symbol, 200)
        };

        var existingSnapshot = await _db.MovingAverageSnapshots.FirstOrDefaultAsync(s => s.Symbol == symbol && s.Date.Date == date.Date);
        if (existingSnapshot == null)
        {
            _db.MovingAverageSnapshots.Add(snapshot);
        }
        else
        {
            // Update existing
            existingSnapshot.SMA50 = snapshot.SMA50; existingSnapshot.SMA100 = snapshot.SMA100; existingSnapshot.SMA200 = snapshot.SMA200;
            existingSnapshot.EMA50 = snapshot.EMA50; existingSnapshot.EMA100 = snapshot.EMA100; existingSnapshot.EMA200 = snapshot.EMA200;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated {Symbol} MA Snapshot for {Date}", symbol, date.ToShortDateString());
    }

    /// <summary>
    /// Seeds the database with historical data from Zerodha.
    /// </summary>
    public async Task BootstrapFromHistoryAsync(string symbol, List<KiteConnect.Historical> data)
    {
        _logger.LogInformation("Bootstrapping {Symbol} from historical data ({Count} candles).", symbol, data.Count);
        
        foreach (var h in data)
        {
            var date = h.TimeStamp.Date;
            var existing = await _db.DailyCloses.AnyAsync(d => d.Symbol == symbol && d.Date == date);
            if (!existing)
            {
                _db.DailyCloses.Add(new DailyClose
                {
                    Symbol = symbol,
                    Date = date,
                    Open = h.Open,
                    High = h.High,
                    Low = h.Low,
                    Close = h.Close
                });
            }
        }

        await _db.SaveChangesAsync();
        
        // After seeding closes, rebuild the tracker and save a snapshot
        await LoadIntoTrackerAsync(symbol);
        
        var last = data.Last();
        await UpdateDailyAsync(symbol, last.TimeStamp.Date, last.Open, last.High, last.Low, last.Close);
    }
}
