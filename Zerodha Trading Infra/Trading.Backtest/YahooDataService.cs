using Microsoft.Extensions.Logging;
using Trading.Core.Models;

namespace Trading.Backtest;

public class YahooDataService
{
    private readonly ILogger<YahooDataService> _logger;

    public YahooDataService(ILogger<YahooDataService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses Yahoo Chart JSON into Candles.
    /// Works for both Daily and Intraday (5m) data.
    /// </summary>
    public List<Trading.Core.Models.Candle> ParseYahooJson(string symbol, string json, int intervalMinutes)
    {
        var candles = new List<Trading.Core.Models.Candle>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var chart = doc.RootElement.GetProperty("chart");
            var result = chart.GetProperty("result")[0];
            
            var timestamps = result.GetProperty("timestamp");
            var indicators = result.GetProperty("indicators").GetProperty("quote")[0];
            
            var opens = indicators.GetProperty("open");
            var highs = indicators.GetProperty("high");
            var lows = indicators.GetProperty("low");
            var closes = indicators.GetProperty("close");
            var volumes = indicators.GetProperty("volume");

            for (int i = 0; i < timestamps.GetArrayLength(); i++)
            {
                if (opens[i].ValueKind == System.Text.Json.JsonValueKind.Null ||
                    closes[i].ValueKind == System.Text.Json.JsonValueKind.Null)
                    continue;

                var unixTime = timestamps[i].GetInt64();
                var startTime = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;

                candles.Add(new Trading.Core.Models.Candle
                {
                    Symbol = symbol,
                    StartTime = startTime,
                    Open = (decimal)opens[i].GetDouble(),
                    High = (decimal)highs[i].GetDouble(),
                    Low = (decimal)lows[i].GetDouble(),
                    Close = (decimal)closes[i].GetDouble(),
                    Volume = (long)volumes[i].GetInt64(),
                    IntervalMinutes = intervalMinutes
                });
            }

            _logger.LogInformation("Yahoo: Parsed {Count} candles for {Symbol}", candles.Count, symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Yahoo: Error parsing JSON for {Symbol}", symbol);
        }
        return candles;
    }
}
