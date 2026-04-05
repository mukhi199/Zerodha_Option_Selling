using KiteConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Trading.Core.Models;
using Trading.Strategy.Services;

namespace Trading.Backtest;

public class BacktestRunner
{
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _accessToken;
    private readonly ILogger<BacktestRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public BacktestRunner(IConfiguration config, string accessToken, ILoggerFactory loggerFactory)
    {
        _apiKey = config["Zerodha:ApiKey"] ?? "";
        _apiSecret = config["Zerodha:ApiSecret"] ?? "";
        _accessToken = accessToken;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BacktestRunner>();
    }

    public async Task RunAsync(string symbol, uint instrumentToken, int days)
    {
        _logger.LogInformation("--- BACKTEST START: {Symbol} ({Days} days) ---", symbol, days);

        var kite = new Kite(_apiKey, _accessToken);

        // 1. Fetch Daily candles (needed for CPR calculation and 3-Day Range)
        // We need 'days + 30' to ensure we have enough history for the 200 EMA and 3-day range pre-warm
        var toDate = DateTime.Today;
        var fromDate = toDate.AddDays(-(days + 30));
        
        _logger.LogInformation("Fetching daily candles from {From} to {To}...", fromDate.ToShortDateString(), toDate.ToShortDateString());
        var dailyHistory = await Task.Run(() => kite.GetHistoricalData(instrumentToken.ToString(), fromDate, toDate, "day"));
        
        // 2. Fetch 5-Minute candles for the actual backtest period
        var testFromDate = toDate.AddDays(-days);
        _logger.LogInformation("Fetching 5-minute candles from {From} to {To}...", testFromDate.ToShortDateString(), toDate.ToShortDateString());
        var intradayHistory = await Task.Run(() => kite.GetHistoricalData(instrumentToken.ToString(), testFromDate, toDate, "5minute"));

        // 3. Initialize Indicators & Strategies
        var tracker = new TechnicalIndicatorsTracker();
        var orderService = new BacktestOrderService(_loggerFactory.CreateLogger<BacktestOrderService>());
        
        var strategies = new List<IStrategy>
        {
            new ThreeDayBreakoutStrategy(orderService, tracker, _loggerFactory.CreateLogger<ThreeDayBreakoutStrategy>()),
            new CprBounceStrategy(tracker, _loggerFactory.CreateLogger<CprBounceStrategy>())
        };

        var engine = new BacktestEngine(strategies, tracker, _loggerFactory.CreateLogger<BacktestEngine>());

        // 4. Group data by day to simulate daily reset/warmup
        var intradayByDay = intradayHistory
            .GroupBy(h => h.TimeStamp.Date)
            .OrderBy(g => g.Key)
            .ToList();

        var dailyByDay = dailyHistory.ToDictionary(h => h.TimeStamp.Date);

        foreach (var dayGroup in intradayByDay)
        {
            var today = dayGroup.Key;
            _logger.LogInformation("Simulating Day: {Date}", today.ToShortDateString());

            // --- WARM UP FOR THE DAY ---
            // A) Set 3-Day Range (previous 3 trading days)
            var prevDays = dailyHistory
                .Where(h => h.TimeStamp.Date < today)
                .OrderByDescending(h => h.TimeStamp.Date)
                .Take(3)
                .Reverse()
                .ToList();

            if (prevDays.Count == 3)
            {
                foreach (var pd in prevDays)
                {
                    tracker.AddDailyRange(symbol, pd.High, pd.Low);
                }
            }

            // B) Set Daily CPR (previous trading day)
            var prevDay = dailyHistory
                .Where(h => h.TimeStamp.Date < today)
                .OrderByDescending(h => h.TimeStamp.Date)
                .FirstOrDefault();

            if (prevDay.TimeStamp != default)
            {
                tracker.SetDailyCPR(symbol, (decimal)prevDay.High, (decimal)prevDay.Low, (decimal)prevDay.Close);
            }

            // --- RUN INTRADAY SIMULATION ---
            var candles = dayGroup.Select(h => new Candle
            {
                Symbol = symbol,
                StartTime = h.TimeStamp,
                Open = (decimal)h.Open,
                High = (decimal)h.High,
                Low = (decimal)h.Low,
                Close = (decimal)h.Close,
                Volume = (long)h.Volume,
                IntervalMinutes = 5
            }).ToList();

            engine.Run(candles);
        }

        _logger.LogInformation("--- BACKTEST COMPLETE for {Symbol} ---", symbol);
        _logger.LogInformation("Total Trades: {Count}", orderService.Trades.Count);
    }
    public async Task RunYahooBacktestAsync(string symbol, int days)
    {
        _logger.LogInformation("--- YAHOO BACKTEST START: {Symbol} ({Days} days) ---", symbol, days);

        var yahoo = new YahooDataService(_loggerFactory.CreateLogger<YahooDataService>());
        var baseDir = Path.Combine(Directory.GetCurrentDirectory(), "Trading.Backtest");

        // 1. Fetch Daily candles from Local JSON (previously fetched via curl)
        string dailyJsonPath = Path.Combine(baseDir, symbol.Contains("BANK") ? "banknifty_daily.json" : "nifty_daily.json");
        if (!File.Exists(dailyJsonPath))
        {
            _logger.LogError("Yahoo: Daily JSON file {Path} not found. Run curl command first.", dailyJsonPath);
            return;
        }
        var dailyJson = File.ReadAllText(dailyJsonPath);
        var dailyHistory = yahoo.ParseYahooJson(symbol, dailyJson, 1440);

        // 2. Load 5-Minute candles from Local JSON
        string intradayJsonPath = Path.Combine(baseDir, symbol.Contains("BANK") ? "banknifty_5m.json" : "nifty_5m.json");
        if (!File.Exists(intradayJsonPath))
        {
            _logger.LogError("Yahoo: Intraday JSON file {Path} not found. Run curl command first.", intradayJsonPath);
            return;
        }
        var intradayJson = File.ReadAllText(intradayJsonPath);
        var intradayHistory = yahoo.ParseYahooJson(symbol, intradayJson, 5);

        if (intradayHistory.Count == 0)
        {
            _logger.LogError("Yahoo: No intraday data parsed for {Symbol}", symbol);
            return;
        }

        // 3. Initialize Indicators & Strategies
        var tracker = new TechnicalIndicatorsTracker();
        var orderService = new BacktestOrderService(_loggerFactory.CreateLogger<BacktestOrderService>());
        
        var strategies = new List<IStrategy>
        {
            new ThreeDayBreakoutStrategy(orderService, tracker, _loggerFactory.CreateLogger<ThreeDayBreakoutStrategy>()),
            new CprBounceStrategy(tracker, _loggerFactory.CreateLogger<CprBounceStrategy>())
        };

        var engine = new BacktestEngine(strategies, tracker, _loggerFactory.CreateLogger<BacktestEngine>());

        // 4. Group data by day
        var intradayByDay = intradayHistory
            .GroupBy(h => h.StartTime.Date)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var dayGroup in intradayByDay)
        {
            var today = dayGroup.Key;
            _logger.LogInformation("Simulating Day: {Date}", today.ToShortDateString());

            // --- WARM UP FOR THE DAY ---
            var prevDaysDaily = dailyHistory
                .Where(h => h.StartTime.Date < today)
                .OrderByDescending(h => h.StartTime.Date)
                .Take(3)
                .Reverse()
                .ToList();

            if (prevDaysDaily.Count == 3)
            {
                foreach (var pd in prevDaysDaily)
                {
                    tracker.AddDailyRange(symbol, pd.High, pd.Low);
                }
            }

            var prevDay = dailyHistory
                .Where(h => h.StartTime.Date < today)
                .OrderByDescending(h => h.StartTime.Date)
                .FirstOrDefault();

            if (prevDay != null && prevDay.StartTime != default)
            {
                tracker.SetDailyCPR(symbol, prevDay.High, prevDay.Low, prevDay.Close);
            }

            // --- RUN INTRADAY SIMULATION ---
            engine.Run(dayGroup.ToList());
        }

        _logger.LogInformation("--- YAHOO BACKTEST COMPLETE for {Symbol} ---", symbol);
        _logger.LogInformation("Total Trades Found: {Count}", orderService.Trades.Count);
        
        foreach(var t in orderService.Trades) {
            _logger.LogWarning("RESULT-TRADE: {Time} {Type} {Qty} {Sym} @ {Price} ({OrderType})", 
                t.ExchangeTime, t.Type, t.Quantity, t.Symbol, t.Price, t.OrderType);
        }
    }
}
