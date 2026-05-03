using System;
using System.Collections.Concurrent;

namespace Trading.Strategy.Services;

public partial class TechnicalIndicatorsTracker
{
    private readonly ConcurrentDictionary<string, decimal> _vwapCumulativePriceVolume = new();
    private readonly ConcurrentDictionary<string, decimal> _vwapCumulativeVolume = new();
    private readonly ConcurrentDictionary<string, decimal> _vwapCumulativeSquaredPriceVolume = new();

    public void ResetVwap(string symbol)
    {
        _vwapCumulativePriceVolume[symbol] = 0;
        _vwapCumulativeVolume[symbol] = 0;
        _vwapCumulativeSquaredPriceVolume[symbol] = 0;
    }

    public void UpdateVwap(string symbol, decimal high, decimal low, decimal close, decimal volume)
    {
        decimal typicalPrice = (high + low + close) / 3m;
        
        decimal pV = typicalPrice * volume;
        decimal pPsqV = typicalPrice * typicalPrice * volume;

        _vwapCumulativePriceVolume.AddOrUpdate(symbol, pV, (_, existing) => existing + pV);
        _vwapCumulativeVolume.AddOrUpdate(symbol, volume, (_, existing) => existing + volume);
        _vwapCumulativeSquaredPriceVolume.AddOrUpdate(symbol, pPsqV, (_, existing) => existing + pPsqV);
    }

    public decimal? GetVwap(string symbol)
    {
        if (_vwapCumulativeVolume.TryGetValue(symbol, out var cumulativeVolume) && cumulativeVolume > 0)
        {
            if (_vwapCumulativePriceVolume.TryGetValue(symbol, out var cumulativePriceVolume))
            {
                return cumulativePriceVolume / cumulativeVolume;
            }
        }
        return null;
    }

    // Returns (Upper1, Lower1, Upper2, Lower2)
    public (decimal U1, decimal L1, decimal U2, decimal L2)? GetVwapBands(string symbol)
    {
        var vwap = GetVwap(symbol);
        if (vwap.HasValue && 
            _vwapCumulativeVolume.TryGetValue(symbol, out var cumVol) && cumVol > 0 &&
            _vwapCumulativePriceVolume.TryGetValue(symbol, out var cumPV) && 
            _vwapCumulativeSquaredPriceVolume.TryGetValue(symbol, out var cumPsqV))
        {
            // Variance = [Sum(P_i^2 * V_i) / Sum(V_i)] - VWAP^2
            decimal term1 = cumPsqV / cumVol;
            decimal term2 = vwap.Value * vwap.Value;
            decimal variance = term1 - term2;
            
            // To avoid precision negative issues near 0
            if (variance < 0) variance = 0;
            
            decimal stdDev = (decimal)Math.Sqrt((double)variance);

            return (
                U1: vwap.Value + stdDev,
                L1: vwap.Value - stdDev,
                U2: vwap.Value + 2 * stdDev,
                L2: vwap.Value - 2 * stdDev
            );
        }
        return null;
    }

    public bool IsAboveVwap(string symbol, decimal price)
    {
        var vwap = GetVwap(symbol);
        return vwap.HasValue && price > vwap.Value;
    }

    public bool IsBelowVwap(string symbol, decimal price)
    {
        var vwap = GetVwap(symbol);
        return vwap.HasValue && price < vwap.Value;
    }
}
