using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Trading.Core;
using Trading.Core.Models;

namespace Trading.MQ.Services;

/// <summary>
/// Aggregates raw ticks into multi-timeframe OHLC candles.
/// Supported intervals: 1m, 3m, 5m, 30m.
/// Thread-safe via ConcurrentDictionary.
/// When a candle interval closes (new bar starts), it is:
///   1. Published to RabbitMQ candles-exchange via MQCandlePublisher
///   2. Added to the CandleBuffer for EOD KDB+ flush
/// </summary>
public class CandleAggregator
{
    private static readonly int[] Intervals = { 1, 3, 5, 30 };

    // Key: "{Symbol}_{IntervalMinutes}"
    private readonly ConcurrentDictionary<string, Candle> _openCandles = new();

    private readonly IMQCandlePublisher _mqPublisher;
    private readonly CandleBuffer      _candleBuffer;
    private readonly ILogger<CandleAggregator> _logger;

    public CandleAggregator(
        IMQCandlePublisher mqPublisher,
        CandleBuffer candleBuffer,
        ILogger<CandleAggregator> logger)
    {
        _mqPublisher = mqPublisher;
        _candleBuffer    = candleBuffer;
        _logger          = logger;
    }

    /// <summary>
    /// Feed a tick into the aggregator for all configured timeframes.
    /// </summary>
    public void ProcessTick(NormalizedTick tick)
    {
        foreach (var interval in Intervals)
        {
            ProcessTickForInterval(tick, interval);
        }
    }

    // ─────────────── per-interval logic ───────────────

    private void ProcessTickForInterval(NormalizedTick tick, int intervalMinutes)
    {
        var key       = $"{tick.Symbol}_{intervalMinutes}";
        var barStart  = AlignToInterval(tick.ExchangeTime, intervalMinutes);

        _openCandles.AddOrUpdate(
            key,
            _ => CreateCandle(tick, barStart, intervalMinutes),
            (_, existing) =>
            {
                if (existing.StartTime == barStart)
                {
                    UpdateCandle(existing, tick);
                    return existing;
                }
                else
                {
                    existing.IsClosed = true;
                    EmitClosedCandle(existing);
                    return CreateCandle(tick, barStart, intervalMinutes);
                }
            });
    }

    private static Candle CreateCandle(NormalizedTick tick, DateTime barStart, int interval)
        => new()
        {
            Symbol          = tick.Symbol,
            IntervalMinutes = interval,
            StartTime       = barStart,
            Open            = tick.Price,
            High            = tick.Price,
            Low             = tick.Price,
            Close           = tick.Price,
            Volume          = tick.Volume,
            IsClosed        = false
        };

    private static void UpdateCandle(Candle candle, NormalizedTick tick)
    {
        candle.High   = Math.Max(candle.High, tick.Price);
        candle.Low    = Math.Min(candle.Low,  tick.Price);
        candle.Close  = tick.Price;
        candle.Volume += tick.Volume;
    }

    private void EmitClosedCandle(Candle candle)
    {
        try
        {
            _logger.LogDebug("Closed {Interval}m candle for {Symbol} @ {Start}: O={O} H={H} L={L} C={C} V={V}",
                candle.IntervalMinutes, candle.Symbol, candle.StartTime,
                candle.Open, candle.High, candle.Low, candle.Close, candle.Volume);

            _mqPublisher.PublishCandle(candle);
            _candleBuffer.Add(candle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit closed candle for {Symbol}", candle.Symbol);
        }
    }

    // ─────────────── time alignment ───────────────

    /// <summary>
    /// Aligns a timestamp to the floor of the given minute interval.
    /// e.g., 09:47:23 with 5m interval → 09:45:00
    /// </summary>
    private static DateTime AlignToInterval(DateTime time, int intervalMinutes)
    {
        var minuteOfDay  = time.Hour * 60 + time.Minute;
        var alignedMin   = (minuteOfDay / intervalMinutes) * intervalMinutes;
        var alignedHour  = alignedMin / 60;
        var alignedMinute = alignedMin % 60;

        return new DateTime(time.Year, time.Month, time.Day,
            alignedHour, alignedMinute, 0, DateTimeKind.Local);
    }

    /// <summary>
    /// Force-close all open candles (called at market close / process shutdown).
    /// </summary>
    public void FlushOpenCandles()
    {
        foreach (var kvp in _openCandles)
        {
            var candle = kvp.Value;
            if (!candle.IsClosed)
            {
                candle.IsClosed = true;
                EmitClosedCandle(candle);
            }
        }

        _openCandles.Clear();
    }
}
