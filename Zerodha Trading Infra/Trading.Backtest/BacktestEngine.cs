namespace Trading.Backtest;

using Trading.Core.Models;
using Trading.Strategy.Services;
using Microsoft.Extensions.Logging;

public class BacktestEngine
{
    private readonly List<IStrategy> _strategies;
    private readonly TechnicalIndicatorsTracker _tracker;
    private readonly ILogger<BacktestEngine> _logger;

    public BacktestEngine(List<IStrategy> strategies, TechnicalIndicatorsTracker tracker, ILogger<BacktestEngine> logger)
    {
        _strategies = strategies;
        _tracker = tracker;
        _logger = logger;
    }

    public void Run(List<Candle> historicalData)
    {
        _logger.LogInformation("Starting Backtest with {Count} candles...", historicalData.Count);

        foreach (var candle in historicalData)
        {
            // Feed the candle to all strategies
            foreach (var strategy in _strategies)
            {
                strategy.OnCandle(candle);
            }

            // Simulating a tick from the candle (at Close) for all strategies
            var tick = new NormalizedTick
            {
                Symbol = candle.Symbol,
                Price = candle.Close,
                ExchangeTime = candle.StartTime.AddMinutes(candle.IntervalMinutes)
            };

            foreach (var strategy in _strategies)
            {
                strategy.OnTick(tick);
            }
        }

        _logger.LogInformation("Backtest completed.");
    }
}
