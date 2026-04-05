using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trading.Core;
using Trading.MQ.Services;

namespace Trading.KX.Services;

/// <summary>
/// Hosted background service that fires the EOD KDB+ flush at exactly 15:30 IST
/// (UTC+5:30, so 10:00 UTC).
/// Steps at 15:30:
///  1. Force-close all open OHLC candles (flush partial bars to MQ + buffer)
///  2. Drain TickBuffer → batch insert into KDB+ ticks table
///  3. Drain CandleBuffer → batch insert into KDB+ candles table
///  4. Log completion
/// The service keeps running thereafter (does NOT shut down the app).
/// </summary>
public class EodFlushService : BackgroundService
{
    private readonly KdbWriter        _kdbWriter;
    private readonly TickBuffer       _tickBuffer;
    private readonly CandleBuffer     _candleBuffer;
    private readonly CandleAggregator _candleAggregator;
    private readonly ILogger<EodFlushService> _logger;

    // 15:30 IST = 10:00 UTC
    private static readonly TimeSpan EodTimeUtc = new(10, 0, 0);
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(5);

    public EodFlushService(
        KdbWriter kdbWriter,
        TickBuffer tickBuffer,
        CandleBuffer candleBuffer,
        CandleAggregator candleAggregator,
        ILogger<EodFlushService> logger)
    {
        _kdbWriter        = kdbWriter;
        _tickBuffer       = tickBuffer;
        _candleBuffer     = candleBuffer;
        _candleAggregator = candleAggregator;
        _logger           = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EodFlushService started. Will flush to KDB+ at 15:30 IST (10:00 UTC) each day.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delayToEod = CalculateDelayToEod();
            var nextPeriodic = DateTime.UtcNow.Add(PeriodicInterval);

            // Wait for EITHER the periodic interval OR the EOD time
            // Simplified: Wait for PeriodicInterval and check if we hit EOD along the way
            try
            {
                await Task.Delay(PeriodicInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            // Perform periodic flush
            await RunFlushAsync("PERIODIC");

            // Check if we just crossed or are very close to EOD
            if (CalculateDelayToEod().TotalMinutes < 1)
            {
                await RunFlushAsync("EOD");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); // Prevent double-fire
            }
        }
    }

    private async Task RunFlushAsync(string reason)
    {
        _logger.LogInformation("=== {Reason} FLUSH STARTED at {Time} IST ===", reason, DateTime.Now);

        try
        {
            // 1. Force-close all open candle bars
            _logger.LogInformation("Flushing open candle bars...");
            _candleAggregator.FlushOpenCandles();

            // Small delay to let the flush propagate to CandleBuffer
            await Task.Delay(500);

            // 2. Drain + insert ticks
            var ticks = _tickBuffer.DrainAll();
            _logger.LogInformation("Draining {Count} ticks to KDB+...", ticks.Count);
            _kdbWriter.InsertTickBatch(ticks);

            // 3. Drain + insert candles
            var candles = _candleBuffer.DrainAll();
            _logger.LogInformation("Draining {Count} candles to KDB+...", candles.Count);
            _kdbWriter.InsertCandleBatch(candles);

            _logger.LogInformation("=== EOD FLUSH COMPLETE: {Ticks} ticks, {Candles} candles written to KDB+ ===",
                ticks.Count, candles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EOD flush failed.");
        }
    }

    /// <summary>
    /// Calculates the delay from now (UTC) to the next 15:30 IST / 10:00 UTC mark.
    /// </summary>
    private static TimeSpan CalculateDelayToEod()
    {
        var nowUtc    = DateTime.UtcNow;
        var todayEod  = nowUtc.Date.Add(EodTimeUtc);

        if (nowUtc >= todayEod)
        {
            // Already past today's EOD → target tomorrow's EOD
            todayEod = todayEod.AddDays(1);
        }

        return todayEod - nowUtc;
    }
}
