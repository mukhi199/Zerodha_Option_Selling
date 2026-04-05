namespace Trading.Strategy;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopeFactory;

    public Worker(
        ILogger<Worker> logger, 
        IConfiguration configuration,
        ZerodhaAuthService authService,
        OrderExecutionService orderService,
        TechnicalIndicatorsTracker tracker,
        MovingAverageService maService,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _authService = authService;
        _orderService = orderService;
        _tracker = tracker;
        _maService = maService;
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
            // Try to load from DB first
            bool warmed = await _maService.LoadIntoTrackerAsync(symbol);
            if (!warmed)
            {
                _logger.LogInformation("{Symbol} not found in DB or insufficient data. Bootstrapping...", symbol);
                var token = tokens[symbol];
                var toDate = DateTime.Now;
                var fromDate = toDate.AddDays(-300);
                var dailyData = kite.GetHistoricalData(token, fromDate, toDate, "day", false);
                
                if (dailyData.Count > 0)
                {
                    await _maService.BootstrapFromHistoryAsync(symbol, dailyData);
                }
            }
            else
            {
                _logger.LogInformation("{Symbol} indicators loaded from local database.", symbol);
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
