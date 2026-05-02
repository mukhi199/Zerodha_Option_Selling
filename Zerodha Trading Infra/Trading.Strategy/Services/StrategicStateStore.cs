using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Trading.Core.Models;

namespace Trading.Strategy.Services;

public class StrategicSymbolState
{
    public string Symbol { get; set; } = "";
    public decimal Ltp { get; set; }
    public decimal Ema50 { get; set; }
    public decimal Ema200 { get; set; }
    
    // 3-Day Breakout
    public decimal ThreeDayHigh { get; set; }
    public decimal ThreeDayLow { get; set; }
    
    // ORB (15 min)
    public decimal OrbHigh { get; set; }
    public decimal OrbLow { get; set; }
    
    // PDLH - Last hour consolidation
    public decimal ConsolidationHigh { get; set; }
    public decimal ConsolidationLow { get; set; }
    
    public string Trend { get; set; } = "Neutral";
    
    // CPR levels
    public decimal Pivot { get; set; }
    public decimal Bc { get; set; }
    public decimal Tc { get; set; }
    public bool IsVirginCpr { get; set; } = true;

    // Previous Day High/Low
    public decimal Pdh { get; set; }
    public decimal Pdl { get; set; }

    // Strangle Status
    public string StrangleStatus { get; set; } = "Not Started";
    public string StrangleLegs { get; set; } = "";
    
    // Manual Overrides
    public string ManualOverrideSignal { get; set; } = "None"; // e.g. "BUY", "SELL" (Immediate)
    public decimal ManualTriggerLevel { get; set; }
    public string ManualTriggerSide { get; set; } = "None"; // "BUY", "SELL", "None" (Conditional)
    public decimal ManualStopLoss { get; set; }

    public DateTime LastUpdate { get; set; }
}

public class SystemMetrics
{
    public bool WebSocketConnected { get; set; }
    public long TicksProcessed { get; set; }
    public DateTime LastUpdate { get; set; }
}

public interface IStrategicStateStore
{
    void UpdateSymbolState(string symbol, Action<StrategicSymbolState> updateAction);
    StrategicSymbolState? GetSymbolState(string symbol);
    IEnumerable<StrategicSymbolState> GetAllStates();
    
    void UpdateSystemMetrics(Action<SystemMetrics> updateAction);
    SystemMetrics GetSystemMetrics();
}

public class StrategicStateStore : IStrategicStateStore
{
    private readonly ConcurrentDictionary<string, StrategicSymbolState> _states = new();
    private readonly SystemMetrics _metrics = new() {  WebSocketConnected = true, LastUpdate = DateTime.Now };

    public StrategicStateStore()
    {
    }

    public void UpdateSymbolState(string symbol, Action<StrategicSymbolState> updateAction)
    {
        var state = _states.GetOrAdd(symbol, s => new StrategicSymbolState { Symbol = s });
        lock (state)
        {
            updateAction(state);
            state.LastUpdate = DateTime.Now;
        }
    }

    public StrategicSymbolState? GetSymbolState(string symbol)
    {
        return _states.TryGetValue(symbol, out var state) ? state : null;
    }

    public IEnumerable<StrategicSymbolState> GetAllStates()
    {
        return _states.Values;
    }

    public void UpdateSystemMetrics(Action<SystemMetrics> updateAction)
    {
        lock (_metrics)
        {
            updateAction(_metrics);
            _metrics.LastUpdate = DateTime.Now;
        }
    }

    public SystemMetrics GetSystemMetrics() => _metrics;
}
