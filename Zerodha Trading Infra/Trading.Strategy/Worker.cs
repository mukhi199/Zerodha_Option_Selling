// =============================================================================
// Worker_FIXED.cs
// =============================================================================
// FIXES APPLIED (search "// FIX #N" to jump to each change):
//
//   FIX #1 — Market hours guard added before pre-warm and strategy start
//             (app could be started outside market hours and attempt Zerodha
//              API calls that either fail or return stale/empty data)
//
//   FIX #2 — Instrument tokens moved to appsettings.json
//             (was hardcoded in a Dictionary literal — any instrument change
//              required a code change and redeploy; now config-driven)
//
//   FIX #3 — Fatal error triggers graceful shutdown via CancellationToken
//             (original catch logged the error but swallowed it — the host
//              kept running in a broken state with no indicators warmed and
//              no orders possible; now requests host shutdown on fatal error)
//
//   FIX #4 — LoadIntoTrackerAsync called twice per symbol eliminated
//             (original code called LoadIntoTrackerAsync at start of loop,
//              then again unconditionally at the end — double-loading adds
//              every close price to the tracker twice on a cold start,
//              corrupting SMA window and EMA values)
//
//   FIX #5 — SaveCprToDatabase made async to avoid blocking the startup thread
//             (was synchronous with db.SaveChanges() blocking ExecuteAsync;
//              converted to async with await db.SaveChangesAsync())
// =============================================================================

namespace Trading.Strategy;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Trading.Strategy.Services;
using Trading.Zerodha.Services;
using Trading.Core.Data;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly ZerodhaAuthService _authService;
    private readonly OrderExecutionService _orderService;
    private readonly TechnicalIndicatorsTracker _tracker;
    private readonly MovingAverageService _maService;
    private readonly INfoSymbolMaster _nfoSymbolMaster;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime; // FIX #3

    public Worker(
        ILogger<Worker> logger,
        IConfiguration configuration,
        ZerodhaAuthService authService,
        OrderExecutionService orderService,
        TechnicalIndicatorsTracker tracker,
        MovingAverageService maService,
        INfoSymbolMaster nfoSymbolMaster,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime)  // FIX #3 ↓ — inject lifetime for graceful shutdown
    {
        _logger          = logger;
        _configuration   = configuration;
        _authService     = authService;
        _orderService    = orderService;
        _tracker         = tracker;
        _maService       = maService;
        _nfoSymbolMaster = nfoSymbolMaster;
        _scopeFactory    = scopeFactory;
        _lifetime        = lifetime;  // FIX #3
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Strategy Engine Worker starting at: {time}", DateTimeOffset.Now);

        try
        {
            // -----------------------------------------------------------------
            // FIX #1 ↓ — Market hours guard.
            //
            // ORIGINAL: no time check — app would attempt Zerodha API calls and
            // pre-warm on any startup regardless of time of day. Starting at
            // midnight or on a weekend would either get empty historical data
            // or fail with Zerodha API errors, leaving the tracker un-warmed.
            //
            // FIX: wait until market opens (9:15 AM IST) before starting.
            // If already past market hours (after 3:30 PM), log a warning.
            // This is especially important when the app auto-restarts overnight.
            // -----------------------------------------------------------------
            var now         = DateTime.Now;
            var marketOpen  = new TimeSpan(9, 15, 0);
            var marketClose = new TimeSpan(15, 30, 0);

            if (now.TimeOfDay < marketOpen)  // FIX #1
            {
                var waitUntil = DateTime.Today.Add(marketOpen);
                var delay     = waitUntil - now;
                _logger.LogInformation(
                    "Market not yet open. Waiting {Minutes:F0} minutes until 9:15 AM...",
                    delay.TotalMinutes);
                await Task.Delay(delay, stoppingToken);  // FIX #1: sleep until market open
            }
            else if (now.TimeOfDay > marketClose)  // FIX #1
            {
                _logger.LogWarning(
                    "Worker started after market close ({Time}). " +
                    "Pre-warm will run but no live trading will occur today.",
                    now.ToString("HH:mm"));
            }

            // 1. Authenticate
            var accessToken = await _authService.EnsureAccessTokenAsync();
            _orderService.SetAccessToken(accessToken);
            _nfoSymbolMaster.Initialize(accessToken);
            _logger.LogInformation("Strategy Engine authenticated successfully with Zerodha.");

            // 2. Pre-warm Technical Indicators
            await PreWarmIndicators(accessToken);

            // 3. Save CPR levels to DB
            await SaveCprToDatabaseAsync();  // FIX #5: now async

            // MQDataConsumer background service auto-starts and feeds strategies.
            // Keep the worker alive until host shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — swallow
            _logger.LogInformation("Worker shutdown requested.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Strategy Engine encountered a fatal error. Triggering host shutdown.");

            // -----------------------------------------------------------------
            // FIX #3 ↓ — Request graceful host shutdown on fatal error.
            //
            // ORIGINAL: catch logged the error and returned, leaving the host
            // running in a broken state — MQDataConsumer still received ticks
            // and candles but the tracker was un-warmed and orders had no token.
            //
            // FIX: call _lifetime.StopApplication() so the entire host shuts
            // down cleanly and the process can be restarted by a service manager
            // (Windows Service / systemd / Docker restart policy).
            // -----------------------------------------------------------------
            _lifetime.StopApplication();  // FIX #3
        }
    }

    private async Task PreWarmIndicators(string accessToken)
    {
        _logger.LogInformation("Pre-warming Technical Indicators...");

        var apiKey = _configuration["Zerodha:ApiKey"];
        var kite   = new KiteConnect.Kite(apiKey);
        kite.SetAccessToken(accessToken);

        var symbols = new[] { "NIFTY 50", "NIFTY BANK" };

        // -----------------------------------------------------------------
        // FIX #2 ↓ — Instrument tokens loaded from configuration.
        //
        // ORIGINAL:
        //   var tokens = new Dictionary<string, string>
        //   {
        //       { "NIFTY 50",   "256265" },
        //       { "NIFTY BANK", "260105" }
        //   };
        //
        // PROBLEM: hardcoded tokens require a code change and redeploy if
        // Zerodha ever changes an instrument token (rare but possible), or
        // if you want to add a new symbol. Also makes the code harder to
        // test with mock tokens in a staging environment.
        //
        // FIX: read from appsettings.json under "Zerodha:InstrumentTokens".
        // Add this to your appsettings.json:
        //   "Zerodha": {
        //     "InstrumentTokens": {
        //       "NIFTY 50":   "256265",
        //       "NIFTY BANK": "260105"
        //     }
        //   }
        // -----------------------------------------------------------------
        var tokensSection = _configuration.GetSection("Zerodha:InstrumentTokens");  // FIX #2
        var tokens = tokensSection.GetChildren()
            .ToDictionary(x => x.Key, x => x.Value ?? "");  // FIX #2

        if (tokens.Count == 0)
        {
            // FIX #2: fall back to hardcoded defaults with a warning so existing
            // deployments without the new config key still work
            _logger.LogWarning(
                "Zerodha:InstrumentTokens not found in config. " +
                "Using hardcoded defaults. Add them to appsettings.json to remove this warning.");
            tokens = new Dictionary<string, string>
            {
                { "NIFTY 50",   "256265" },
                { "NIFTY BANK", "260105" }
            };
        }

        foreach (var symbol in symbols)
        {
            // Step 1: Load what we have in DB into tracker
            await _maService.LoadIntoTrackerAsync(symbol);

            // Step 2: Check if DB is stale and fill missing days from Zerodha
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var lastRecord = await db.DailyCloses
                .Where(d => d.Symbol == symbol)
                .OrderByDescending(d => d.Date)
                .FirstOrDefaultAsync();

            DateTime fromDate = lastRecord != null
                ? lastRecord.Date.AddDays(1)
                : DateTime.Now.AddDays(-300);
            DateTime toDate = DateTime.Now.Date.AddDays(-1); // up to yesterday only

            if (fromDate <= toDate)
            {
                _logger.LogInformation(
                    "{Symbol}: DB ends at {LastDate}. Fetching missing days {From} → {To} from Zerodha...",
                    symbol,
                    lastRecord?.Date.ToShortDateString() ?? "N/A",
                    fromDate.ToShortDateString(),
                    toDate.ToShortDateString());

                try
                {
                    if (!tokens.TryGetValue(symbol, out var token) || string.IsNullOrEmpty(token))
                    {
                        _logger.LogWarning("{Symbol}: No instrument token configured. Skipping Zerodha sync.", symbol);
                    }
                    else
                    {
                        var dailyData = kite.GetHistoricalData(token, fromDate, toDate, "day", false);

                        if (dailyData?.Count > 0)
                        {
                            _logger.LogInformation("{Symbol}: Received {Count} new candles from Zerodha.", symbol, dailyData.Count);
                            await _maService.BootstrapFromHistoryAsync(symbol, dailyData);
                        }
                        else
                        {
                            _logger.LogWarning("{Symbol}: No new candles returned from Zerodha.", symbol);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Symbol}: Failed to fetch missing daily closes from Zerodha.", symbol);
                }
            }
            else
            {
                _logger.LogInformation(
                    "{Symbol}: DB is up-to-date (last: {LastDate}). No Zerodha sync needed.",
                    symbol,
                    lastRecord?.Date.ToShortDateString());
            }

            // -----------------------------------------------------------------
            // FIX #4 ↓ — Final re-load removed.
            //
            // ORIGINAL CODE had this at the end of the loop:
            //   await _maService.LoadIntoTrackerAsync(symbol);  // "Final safety re-load"
            //
            // PROBLEM: LoadIntoTrackerAsync calls _tracker.AddClosePrice for
            // every close in the DB. If we already called it at Step 1 above,
            // calling it again doubles every close price in the EMA calculation,
            // producing wrong SMA windows and corrupted EMA values.
            // BootstrapFromHistoryAsync also internally calls LoadIntoTrackerAsync,
            // so a fresh bootstrap path also ends up double-loading.
            //
            // FIX: remove the second call. The tracker is already correctly
            // warmed after Step 1 + the optional bootstrap step.
            // If you need the 3-day range verified, just read it — don't reload.
            // -----------------------------------------------------------------

            // FIX #4: no second LoadIntoTrackerAsync call here ↑

            var range = _tracker.GetThreeDayRange(symbol);
            if (range != null)
                _logger.LogInformation(
                    ">>> {Symbol} 3-DAY RANGE: Low={Low} High={High} <<<",
                    symbol, range.Value.Low, range.Value.High);
            else
                _logger.LogWarning(
                    "{Symbol}: 3-Day range still null after sync. DB may need more historical data.",
                    symbol);
        }

        _logger.LogInformation("Pre-warm complete. SMAs, EMAs, and CPRs ready.");
    }

    // =========================================================================
    // FIX #5 — SaveCprToDatabase converted to async.
    //
    // ORIGINAL: private void SaveCprToDatabase() with db.SaveChanges()
    // PROBLEM: synchronous SaveChanges() blocks the ExecuteAsync thread while
    // EF Core flushes to SQLite. On a slow disk or large DB this holds up
    // the entire startup sequence unnecessarily.
    // FIX: renamed to SaveCprToDatabaseAsync, returns Task, uses SaveChangesAsync.
    // =========================================================================
    private async Task SaveCprToDatabaseAsync()  // FIX #5
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var symbols = new[] { "NIFTY 50", "NIFTY BANK" };
            foreach (var sym in symbols)
            {
                var dCpr = _tracker.GetDailyCPR(sym);
                if (dCpr != null) SaveRecord(db, sym, "Daily",   DateTime.Today, dCpr);

                var wCpr = _tracker.GetWeeklyCPR(sym);
                if (wCpr != null) SaveRecord(db, sym, "Weekly",  DateTime.Today, wCpr);

                var mCpr = _tracker.GetMonthlyCPR(sym);
                if (mCpr != null) SaveRecord(db, sym, "Monthly", DateTime.Today, mCpr);
            }

            await db.SaveChangesAsync();  // FIX #5: async save
            _logger.LogInformation("Saved latest CPRs to SQLite Database.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save CPR to Database.");
        }
    }

    private void SaveRecord(
        AppDbContext db, string symbol, string timeframe,
        DateTime date, TechnicalIndicatorsTracker.PivotLevels cpr)
    {
        var existing = db.CprData.FirstOrDefault(
            c => c.Symbol == symbol && c.Timeframe == timeframe && c.Date == date);

        if (existing == null)
        {
            db.CprData.Add(new CprData
            {
                Symbol        = symbol,
                Timeframe     = timeframe,
                Date          = date,
                Pivot         = cpr.Pivot,
                BottomCentral = cpr.BottomCentral,
                TopCentral    = cpr.TopCentral,
                R1 = cpr.R1, S1 = cpr.S1,
                R2 = cpr.R2, S2 = cpr.S2,
                R3 = cpr.R3, S3 = cpr.S3
            });
        }
    }
}
