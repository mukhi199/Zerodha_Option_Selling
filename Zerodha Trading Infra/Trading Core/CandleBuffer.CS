using System.Collections.Concurrent;
using Trading.Core.Models;

namespace Trading.Core;

/// <summary>
/// Thread-safe in-memory buffer for closed OHLC candles across all timeframes.
/// Accumulated throughout the trading day and drained at EOD (15:30 IST).
/// </summary>
public class CandleBuffer
{
    private readonly ConcurrentBag<Candle> _candles = new();

    public void Add(Candle candle) => _candles.Add(candle);

    public int Count => _candles.Count;

    /// <summary>
    /// Returns a snapshot of all closed candles and clears the buffer atomically.
    /// </summary>
    public List<Candle> DrainAll()
    {
        var snapshot = _candles.ToList();
        _candles.Clear();
        return snapshot;
    }
}
