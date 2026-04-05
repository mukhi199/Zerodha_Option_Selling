using System.Collections.Concurrent;
using Trading.Core.Models;

namespace Trading.Core;

/// <summary>
/// Thread-safe in-memory buffer for raw ticks.
/// Accumulated throughout the trading day and drained at EOD (15:30 IST).
/// </summary>
public class TickBuffer
{
    private readonly ConcurrentBag<NormalizedTick> _ticks = new();

    public void Add(NormalizedTick tick) => _ticks.Add(tick);

    public int Count => _ticks.Count;

    /// <summary>
    /// Returns a snapshot of all accumulated ticks and clears the buffer atomically.
    /// </summary>
    public List<NormalizedTick> DrainAll()
    {
        var snapshot = _ticks.ToList();
        _ticks.Clear();
        return snapshot;
    }
}
