namespace Trading.Strategy.Services;

using System.Collections.Concurrent;
using Trading.Core.Models;

public class TechnicalIndicatorsTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<decimal>> _closePriceHistory = new();
    private readonly ConcurrentDictionary<string, Dictionary<int, decimal>> _emaValues = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(decimal High, decimal Low)>> _dailyRanges = new();
    
    // RSI & RMA Tracking
    private readonly ConcurrentDictionary<string, decimal> _prevClose = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiAvgGain = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiAvgLoss = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiLastValue = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiRmaValue = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiHistory = new(); // to store prev RSI value for cross checks
    private readonly ConcurrentDictionary<string, decimal> _rsiRmaHistory = new();

    private readonly int _maxPeriods = 200;

    // A class to hold CPR and Support/Resistance levels
    public class PivotLevels
    {
        public decimal Pivot { get; set; }
        public decimal BottomCentral { get; set; }
        public decimal TopCentral { get; set; }
        public decimal R1 { get; set; }
        public decimal S1 { get; set; }
        public decimal R2 { get; set; }
        public decimal S2 { get; set; }
        public decimal R3 { get; set; }
        public decimal S3 { get; set; }
        public bool TouchedToday { get; set; } = false;
    }

    private readonly ConcurrentDictionary<string, PivotLevels> _dailyCPR = new();
    private readonly ConcurrentDictionary<string, PivotLevels> _weeklyCPR = new();
    private readonly ConcurrentDictionary<string, PivotLevels> _monthlyCPR = new();

    // ── SMA & EMA Tracking ─────────────────────────────────

    public void AddClosePrice(string symbol, decimal closePrice)
    {
        var history = _closePriceHistory.GetOrAdd(symbol, _ => new ConcurrentQueue<decimal>());
        
        // --- RSI & RMA Calculation (14, 9) ---
        if (_prevClose.TryGetValue(symbol, out var prev))
        {
            decimal change = closePrice - prev;
            decimal gain = Math.Max(change, 0);
            decimal loss = Math.Max(-change, 0);

            // Wilder's Smoothing / RMA for Average Gain & Loss (Period = 14)
            int rsiPeriod = 14;
            decimal avgGain = _rsiAvgGain.ContainsKey(symbol) ? ((_rsiAvgGain[symbol] * (rsiPeriod - 1)) + gain) / rsiPeriod : gain;
            decimal avgLoss = _rsiAvgLoss.ContainsKey(symbol) ? ((_rsiAvgLoss[symbol] * (rsiPeriod - 1)) + loss) / rsiPeriod : loss;

            _rsiAvgGain[symbol] = avgGain;
            _rsiAvgLoss[symbol] = avgLoss;

            decimal rs = avgLoss == 0 ? 100 : avgGain / avgLoss;
            decimal currentRsi = avgLoss == 0 ? 100 : 100.0m - (100.0m / (1.0m + rs));

            if (_rsiLastValue.TryGetValue(symbol, out var lastRsi))
                _rsiHistory[symbol] = lastRsi; // Store prev RSI
            
            _rsiLastValue[symbol] = currentRsi;

            // Smoothed RSI (RMA of RSI, Period = 9)
            int rmaPeriod = 9;
            decimal smoothedRsi = _rsiRmaValue.ContainsKey(symbol) ? ((_rsiRmaValue[symbol] * (rmaPeriod - 1)) + currentRsi) / rmaPeriod : currentRsi;
            
            if (_rsiRmaValue.TryGetValue(symbol, out var lastRma))
                _rsiRmaHistory[symbol] = lastRma; // Store prev RMA

            _rsiRmaValue[symbol] = smoothedRsi;
        }
        else
        {
            // Initializing with zero gain/loss on first candle
            _rsiAvgGain[symbol] = 0;
            _rsiAvgLoss[symbol] = 0;
            _rsiLastValue[symbol] = 50; // Neutral start
            _rsiRmaValue[symbol] = 50;
        }

        _prevClose[symbol] = closePrice;
        // -------------------------------------

        history.Enqueue(closePrice);

        while (history.Count > _maxPeriods)
            history.TryDequeue(out _);

        // Update EMAs dynamically
        var emas = _emaValues.GetOrAdd(symbol, _ => new Dictionary<int, decimal>());
        UpdateEma(emas, 9, closePrice);
        UpdateEma(emas, 50, closePrice);
        UpdateEma(emas, 100, closePrice);
        UpdateEma(emas, 200, closePrice);
    }

    private void UpdateEma(Dictionary<int, decimal> emas, int period, decimal price)
    {
        if (!emas.ContainsKey(period))
            emas[period] = price; // Initialize first EMA with the price itself
        else
        {
            decimal k = 2.0m / (period + 1);
            emas[period] = (price * k) + (emas[period] * (1 - k));
        }
    }

    public decimal? GetSMA(string symbol, int periods)
    {
        if (!_closePriceHistory.TryGetValue(symbol, out var history) || history.Count < periods)
            return null;
        
        return history.Skip(history.Count - periods).Average();
    }

    public decimal? GetEMA(string symbol, int periods)
    {
        if (_emaValues.TryGetValue(symbol, out var emas) && emas.TryGetValue(periods, out var val))
            return val;
        return null;
    }

    // Returns: (CurrentRSI, PrevRSI, CurrentRMA, PrevRMA)
    public (decimal Rsi, decimal PrevRsi, decimal Rma, decimal PrevRma)? GetRsiAndRma(string symbol)
    {
        if (_rsiLastValue.TryGetValue(symbol, out var rsi) &&
            _rsiHistory.TryGetValue(symbol, out var prevRsi) &&
            _rsiRmaValue.TryGetValue(symbol, out var rma) &&
            _rsiRmaHistory.TryGetValue(symbol, out var prevRma))
        {
            return (rsi, prevRsi, rma, prevRma);
        }
        return null;
    }

    // ── Major Trend Tracking ───────────────────────────────

    public enum MarketTrend
    {
        Up,
        Down,
        Neutral
    }

    /// <summary>
    /// Evaluates the major trend by comparing current price to the 200-period Simple Moving Average (DMA).
    /// </summary>
    public MarketTrend GetMajorTrend(string symbol, decimal currentPrice)
    {
        var dma200 = GetSMA(symbol, 200);
        
        if (!dma200.HasValue)
            return MarketTrend.Neutral;

        return currentPrice > dma200.Value ? MarketTrend.Up : MarketTrend.Down;
    }

    // ── 3-Day Range Tracking ───────────────────────────────
    
    public void AddDailyRange(string symbol, decimal high, decimal low)
    {
        var history = _dailyRanges.GetOrAdd(symbol, _ => new ConcurrentQueue<(decimal, decimal)>());
        history.Enqueue((high, low));

        while (history.Count > 3)
            history.TryDequeue(out _);
    }

    public (decimal High, decimal Low)? GetThreeDayRange(string symbol)
    {
        if (!_dailyRanges.TryGetValue(symbol, out var history) || history.Count < 3)
            return null;

        decimal maxHigh = history.Max(x => x.High);
        decimal minLow = history.Min(x => x.Low);
        return (maxHigh, minLow);
    }

    // ── CPR Tracking ───────────────────────────────────────

    public void SetDailyCPR(string symbol, decimal prevHigh, decimal prevLow, decimal prevClose)
        => _dailyCPR[symbol] = CalculateCPR(prevHigh, prevLow, prevClose);

    public void SetWeeklyCPR(string symbol, decimal prevHigh, decimal prevLow, decimal prevClose)
        => _weeklyCPR[symbol] = CalculateCPR(prevHigh, prevLow, prevClose);

    public void SetMonthlyCPR(string symbol, decimal prevHigh, decimal prevLow, decimal prevClose)
        => _monthlyCPR[symbol] = CalculateCPR(prevHigh, prevLow, prevClose);

    public PivotLevels? GetDailyCPR(string symbol) => _dailyCPR.TryGetValue(symbol, out var levels) ? levels : null;
    public PivotLevels? GetWeeklyCPR(string symbol) => _weeklyCPR.TryGetValue(symbol, out var levels) ? levels : null;
    public PivotLevels? GetMonthlyCPR(string symbol) => _monthlyCPR.TryGetValue(symbol, out var levels) ? levels : null;

    private static PivotLevels CalculateCPR(decimal high, decimal low, decimal close)
    {
        decimal pivot = (high + low + close) / 3;
        decimal bottomCentral = (high + low) / 2;
        decimal topCentral = (pivot - bottomCentral) + pivot;

        // Ensure TopCentral is mathematically higher than BottomCentral for logical consistency
        if (bottomCentral > topCentral)
        {
            (bottomCentral, topCentral) = (topCentral, bottomCentral);
        }

        return new PivotLevels
        {
            Pivot = pivot,
            BottomCentral = bottomCentral,
            TopCentral = topCentral,
            R1 = (2 * pivot) - low,
            S1 = (2 * pivot) - high,
            R2 = pivot + (high - low),
            S2 = pivot - (high - low),
            R3 = high + 2 * (pivot - low),
            S3 = low - 2 * (high - pivot)
        };
    }
}
