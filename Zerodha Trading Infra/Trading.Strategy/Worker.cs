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
    
    // We reuse the Auth service to fetch the daily cached access token.
    private readonly ZerodhaAuthService _authService;
    private readonly OrderExecutionService _orderService;
    private readonly TechnicalIndicatorsTracker _tracker;
    private readonly MovingAverageService _maService;
    private readonly INfoSymbolMaster _nfoSymbolMaster;
    private readonly IServiceScopeFactory _scopeFactory;

    public Worker(
        ILogger<Worker> logger, 
        IConfiguration configuration,
        ZerodhaAuthService authService,
        OrderExecutionService orderService,
        TechnicalIndicatorsTracker tracker,
        MovingAverageService maService,
        INfoSymbolMaster nfoSymbolMaster,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _authService = authService;
        _orderService = orderService;
        _tracker = tracker;
        _maService = maService;
        _nfoSymbolMaster = nfoSymbolMaster;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Strategy Engine Worker starting at: {time}", DateTimeOffset.Now);

        try
        {
            // 1. Ensure we have a valid access token for today
            var accessToken = await _authService.EnsureAccessTokenAsync();
            _orderService.SetAccessToken(accessToken);
            _nfoSymbolMaster.Initialize(accessToken);
            _logger.LogInformation("Strategy Engine authenticated successfully with Zerodha.");

            // 2. Pre-warm Technical Indicators
            await PreWarmIndicators(accessToken);

            // Wait until data is pre-warmed, then we optionally save to DB
            SaveCprToDatabase();

            // MQDataConsumer is a hosted background service, it automatically starts listening to MQ.
            // SampleStrategy receives data from the MQDataConsumer.
            
            // Keep the worker alive
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Strategy Engine encountered a fatal error.");
        }
    }

    private async Task PreWarmIndicators(string accessToken)
    {
        _logger.LogInformation("Pre-warming Technical Indicators...");
        
        var apiKey = _configuration["Zerodha:ApiKey"];
        var kite = new KiteConnect.Kite(apiKey);
        kite.SetAccessToken(accessToken);

        var symbols = new[] { "NIFTY 50", "NIFTY BANK" };
        var tokens = new Dictionary<string, string>
        {
            { "NIFTY 50", "256265" },
            { "NIFTY BANK", "260105" }
        };

        foreach (var symbol in symbols)
        {
            // Step 1: Load what we have in DB into tracker
            bool warmed = await _maService.LoadIntoTrackerAsync(symbol);
            
            // Step 2: Always check if DB is stale and fill missing days from Zerodha
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Trading.Core.Data.AppDbContext>();
            
            var lastRecord = await db.DailyCloses
                .Where(d => d.Symbol == symbol)
                .OrderByDescending(d => d.Date)
                .FirstOrDefaultAsync();

            // Fetch from the day after last record up to Yesterday (No partial today)
            DateTime fromDate = lastRecord != null
                ? lastRecord.Date.AddDays(1)
                : DateTime.Now.AddDays(-300);
            DateTime toDate = DateTime.Now.Date.AddDays(-1);

            if (fromDate <= toDate)
            {
                _logger.LogInformation("{Symbol}: DB data ends at {LastDate}. Fetching missing days {From} → {To} from Zerodha...",
                    symbol, lastRecord?.Date.ToShortDateString() ?? "N/A", fromDate.ToShortDateString(), toDate.ToShortDateString());
                
                try
                {
                    var token = tokens[symbol];
                    var dailyData = kite.GetHistoricalData(token, fromDate, toDate, "day", false);
                    
                    if (dailyData?.Count > 0)
                    {
                        _logger.LogInformation("{Symbol}: Received {Count} new daily candles from Zerodha.", symbol, dailyData.Count);
                        await _maService.BootstrapFromHistoryAsync(symbol, dailyData);
                    }
                    else
                    {
                        _logger.LogWarning("{Symbol}: No new daily candles returned from Zerodha.", symbol);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Symbol}: Failed to fetch missing daily closes from Zerodha.", symbol);
                }
            }
            else
            {
                _logger.LogInformation("{Symbol}: DB is up-to-date (last: {LastDate}). No sync needed.", symbol, lastRecord?.Date.ToShortDateString());
            }

            // Final safety re-load to ensure 3-day range is strictly previous days
            await _maService.LoadIntoTrackerAsync(symbol);

            var range = _tracker.GetThreeDayRange(symbol);
            if (range != null)
            {
                _logger.LogInformation(">>> {Symbol} 3-DAY RANGE (PREV): Low {Low} - High {High} <<<", symbol, range.Value.Low, range.Value.High);
            }
            else
            {
                _logger.LogWarning("{Symbol}: 3-Day range still null after sync. DB may need more historical data.", symbol);
            }
        }
        
        _logger.LogInformation("Finished tracking all historical data for SMAs, EMAs, and CPRs.");
    }

    private void SaveCprToDatabase()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Save Daily CPR
            var symbols = new[] { "NIFTY 50", "NIFTY BANK" };
            foreach (var sym in symbols)
            {
                var dCpr = _tracker.GetDailyCPR(sym);
                if (dCpr != null)
                {
                    SaveRecord(db, sym, "Daily", DateTime.Today, dCpr);
                }
                var wCpr = _tracker.GetWeeklyCPR(sym);
                if (wCpr != null)
                {
                    SaveRecord(db, sym, "Weekly", DateTime.Today, wCpr);
                }
                var mCpr = _tracker.GetMonthlyCPR(sym);
                if (mCpr != null)
                {
                    SaveRecord(db, sym, "Monthly", DateTime.Today, mCpr);
                }
            }
            db.SaveChanges();
            _logger.LogInformation("Saved latest CPRs to SQLite Database.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save CPR to Database.");
        }
    }

    private void SaveRecord(AppDbContext db, string symbol, string timeframe, DateTime date, TechnicalIndicatorsTracker.PivotLevels cpr)
    {
        var existing = db.CprData.FirstOrDefault(c => c.Symbol == symbol && c.Timeframe == timeframe && c.Date == date);
        if (existing == null)
        {
            db.CprData.Add(new CprData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                Date = date,
                Pivot = cpr.Pivot,
                BottomCentral = cpr.BottomCentral,
                TopCentral = cpr.TopCentral,
                R1 = cpr.R1,
                S1 = cpr.S1,
                R2 = cpr.R2,
                S2 = cpr.S2,
                R3 = cpr.R3,
                S3 = cpr.S3
            });
        }
    }
}
