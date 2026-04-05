using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Trading.Backtest;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. Setup Logging
        using var loggerFactory = LoggerFactory.Create(builder => 
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("==========================================================");
        logger.LogInformation("  ZERODHA STRATEGY BACKTESTER (REAL DATA)");
        logger.LogInformation("==========================================================");

        // 2. Load Configuration
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        // 3. Get Access Token (assuming it exists or asking user)
        // Hardcoded path to common token file for now
        var tokenFile = "Trading.Strategy/access_token.json";
        var accessToken = "";

        if (File.Exists(tokenFile))
        {
            var raw = File.ReadAllText(tokenFile).Split('|');
            if (raw.Length == 2) accessToken = raw[1];
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            logger.LogWarning("Valid Zerodha access token not found. Zerodha backtest will be skipped, but Yahoo backtest will proceed.");
        }

        // 4. Initialize Runner
        var runner = new BacktestRunner(config, accessToken, loggerFactory);

        // 5. Execute Backtests
        
        try 
        {
            // Try Yahoo Backtest first (Using local JSON files)
            await runner.RunYahooBacktestAsync("NIFTY 50", days: 5);
            await runner.RunYahooBacktestAsync("NIFTY BANK", days: 5);

            // Optional: Zerodha Backtest (Needs token)
            // if (!string.IsNullOrEmpty(accessToken)) {
            //    await runner.RunAsync("NIFTY 50", 256265, days: 5);
            // }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backtest session failed.");
        }

        logger.LogInformation("==========================================================");
        logger.LogInformation("  BACKTEST SESSION COMPLETE");
        logger.LogInformation("==========================================================");
    }
}
