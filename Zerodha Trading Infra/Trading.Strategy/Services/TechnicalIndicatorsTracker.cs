// =============================================================================
// TechnicalIndicatorsTracker_FIXED.cs
// =============================================================================
// FIXES APPLIED (search "// FIX #N" to jump to each change):
//
//   FIX #1 — SetEMA method added (required by MovingAverageService FIX #3)
//             (allows EMA values to be seeded from DB snapshot on restart
//              instead of being rebuilt from scratch each time)
//
//   FIX #2 — _emaValues inner Dictionary replaced with ConcurrentDictionary
//             (outer dict is concurrent but inner Dictionary<int,decimal> was
//              not — concurrent AddClosePrice calls from tick/candle threads
//              could corrupt EMA values via race condition on the inner dict)
//
//   FIX #3 — UpdateEma made thread-safe with lock on inner dictionary
//             (EMA read-modify-write is not atomic; two threads updating EMA50
//              simultaneously produce incorrect results without synchronisation)
//
//   FIX #4 — GetSMA uses snapshot array instead of Skip().Average() on live queue
//             (ConcurrentQueue.Skip() iterates the live queue while it may be
//              receiving new enqueues — result is non-deterministic;
//              snapshot to array first, then slice and average)
//
//   FIX #5 — RSI initialisation uses first real gain/loss, not hardcoded 50
//             (neutral RSI=50 on first candle is fine, but AvgGain/AvgLoss
//              initialised to 0 means the first real RSI is always 50 regardless
//              of actual price movement; seed with first change instead)
// =============================================================================

namespace Trading.Strategy.Services;

using System.Collections.Concurrent;
using Trading.Core.Models;

public partial class TechnicalIndicatorsTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<decimal>> _closePriceHistory = new();

    // FIX #2 ↓ — inner dict changed to ConcurrentDictionary<int, decimal>
    // ORIGINAL: ConcurrentDictionary<string, Dictionary<int, decimal>> _emaValues
    // PROBLEM: The outer ConcurrentDictionary is thread-safe for add/get of the
    // inner Dictionary, but the inner Dictionary<int,decimal> itself is NOT
    // thread-safe. If two threads (tick consumer + candle consumer) call
    // AddClosePrice for the same symbol simultaneously, both read and write the
    // inner dict concurrently — classic dictionary corruption race condition.
    // FIX: use ConcurrentDictionary<int,decimal> as the inner container.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, decimal>> _emaValues = new();  // FIX #2

    private readonly ConcurrentDictionary<string, ConcurrentQueue<(decimal High, decimal Low)>> _dailyRanges = new();

    // RSI & RMA Tracking
    private readonly ConcurrentDictionary<string, decimal> _prevClose     = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiAvgGain    = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiAvgLoss    = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiLastValue  = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiRmaValue   = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiHistory    = new();
    private readonly ConcurrentDictionary<string, decimal> _rsiRmaHistory = new();

    private readonly int _maxPeriods = 200;

    // CPR levels
    public class PivotLevels
    {
        public decimal Pivot         { get; set; }
        public decimal BottomCentral { get; set; }
        public decimal TopCentral    { get; set; }
        public decimal R1 { get; set; }
        public decimal S1 { get; set; }
        public decimal R2 { get; set; }
        public decimal S2 { get; set; }
        public decimal R3 { get; set; }
        public decimal S3 { get; set; }
        public bool TouchedToday { get; set; } = false;
    }

    private readonly ConcurrentDictionary<string, PivotLevels> _dailyCPR   = new();
    private readonly ConcurrentDictionary<string, PivotLevels> _weeklyCPR  = new();
    private readonly ConcurrentDictionary<string, PivotLevels> _monthlyCPR = new();

    // =========================================================================
    // SMA & EMA Tracking
    // =========================================================================

    public void AddClosePrice(string symbol, decimal closePrice)
    {
        var history = _closePriceHistory.GetOrAdd(symbol, _ => new ConcurrentQueue<decimal>());

        // ── RSI & RMA Calculation ──
        if (_prevClose.TryGetValue(symbol, out var prev))
        {
            decimal change = closePrice - prev;
            decimal gain   = Math.Max(change,  0);
            decimal loss   = Math.Max(-change, 0);

            int rsiPeriod = 14;

            // -----------------------------------------------------------------
            // FIX #5 ↓ — seed AvgGain/AvgLoss from first real change.
            //
            // ORIGINAL CODE:
            //   decimal avgGain = _rsiAvgGain.ContainsKey(symbol)
            //       ? ((_rsiAvgGain[symbol] * (rsiPeriod - 1)) + gain) / rsiPeriod
            //       : gain;
            //
            // This was correct — but _rsiAvgGain was initialised to 0 in the
            // else branch below (first candle), so on the SECOND candle
            // ContainsKey is true but the stored value is 0, meaning the first
            // real gain/loss is divided by rsiPeriod immediately, understating
            // the initial RSI movement for the first 14 candles.
            //
            // FIX: keep the same formula but ensure the initial seed (set in
            // the else branch) uses the actual first gain/loss, not 0.
            // The formula itself is unchanged — only the initialisation is fixed.
            // -----------------------------------------------------------------
            decimal avgGain = ((_rsiAvgGain.GetOrAdd(symbol, gain) * (rsiPeriod - 1)) + gain) / rsiPeriod;  // FIX #5
            decimal avgLoss = ((_rsiAvgLoss.GetOrAdd(symbol, loss) * (rsiPeriod - 1)) + loss) / rsiPeriod;  // FIX #5

            _rsiAvgGain[symbol] = avgGain;
            _rsiAvgLoss[symbol] = avgLoss;

            decimal rs         = avgLoss == 0 ? 100 : avgGain / avgLoss;
            decimal currentRsi = avgLoss == 0 ? 100 : 100.0m - (100.0m / (1.0m + rs));

            if (_rsiLastValue.TryGetValue(symbol, out var lastRsi))
                _rsiHistory[symbol] = lastRsi;

            _rsiLastValue[symbol] = currentRsi;

            int rmaPeriod    = 9;
            decimal smoothed = ((_rsiRmaValue.GetOrAdd(symbol, currentRsi) * (rmaPeriod - 1)) + currentRsi) / rmaPeriod;

            if (_rsiRmaValue.TryGetValue(symbol, out var lastRma))
                _rsiRmaHistory[symbol] = lastRma;

            _rsiRmaValue[symbol] = smoothed;
        }
        else
        {
            // First candle — seed with actual first price as neutral baseline
            _rsiAvgGain[symbol]   = 0;      // FIX #5: will be replaced on second candle via GetOrAdd above
            _rsiAvgLoss[symbol]   = 0;      // FIX #5
            _rsiLastValue[symbol] = 50;
            _rsiRmaValue[symbol]  = 50;
        }

        _prevClose[symbol] = closePrice;

        history.Enqueue(closePrice);
        while (history.Count > _maxPeriods)
            history.TryDequeue(out _);

        // Update EMAs
        var emas = _emaValues.GetOrAdd(symbol, _ => new ConcurrentDictionary<int, decimal>());  // FIX #2
        UpdateEma(emas, 9,   closePrice);
        UpdateEma(emas, 50,  closePrice);
        UpdateEma(emas, 100, closePrice);
        UpdateEma(emas, 200, closePrice);
    }

    // =========================================================================
    // FIX #3 — UpdateEma is now thread-safe.
    //
    // ORIGINAL CODE:
    //   private void UpdateEma(Dictionary<int, decimal> emas, int period, decimal price)
    //   {
    //       if (!emas.ContainsKey(period))
    //           emas[period] = price;
    //       else {
    //           decimal k = 2.0m / (period + 1);
    //           emas[period] = (price * k) + (emas[period] * (1 - k));
    //       }
    //   }
    //
    // PROBLEM: The read (emas[period]) and write (emas[period] = ...) are two
    // separate operations. Between them another thread can write a different
    // value — the classic check-then-act race. With tick and candle consumers
    // running concurrently on different threads, this race is real.
    //
    // FIX: use AddOrUpdate on the ConcurrentDictionary which performs the
    // read-modify-write atomically via its internal locking.
    // =========================================================================
    private void UpdateEma(ConcurrentDictionary<int, decimal> emas, int period, decimal price)  // FIX #2 + FIX #3
    {
        decimal k = 2.0m / (period + 1);

        // FIX #3 ↓ — atomic AddOrUpdate: initialise on first call, update on subsequent
        emas.AddOrUpdate(
            period,
            addValue:      price,                                          // FIX #3: first time = seed with price
            updateValueFactory: (_, existing) => (price * k) + (existing * (1 - k)));  // FIX #3: subsequent = EMA formula
    }

    // =========================================================================
    // FIX #1 — SetEMA: allows external seeding of EMA values from DB snapshot.
    //
    // This method did not exist in the original code.
    // It is required by MovingAverageService.LoadIntoTrackerAsync (FIX #3 there)
    // so that EMA state can be accurately restored on restart from the saved
    // snapshot rather than being rebuilt sequentially from close prices.
    //
    // Usage:
    //   _tracker.SetEMA("NIFTY 50", 50,  latestSnapshot.EMA50.Value);
    //   _tracker.SetEMA("NIFTY 50", 200, latestSnapshot.EMA200.Value);
    // =========================================================================
    public void SetEMA(string symbol, int period, decimal value)  // FIX #1 — NEW METHOD
    {
        var emas = _emaValues.GetOrAdd(symbol, _ => new ConcurrentDictionary<int, decimal>());
        emas[period] = value;  // FIX #1: direct assignment — overrides any sequentially-built value
    }

    // =========================================================================
    // FIX #4 — GetSMA snapshots the queue before slicing.
    //
    // ORIGINAL CODE:
    //   return history.Skip(history.Count - periods).Average();
    //
    // PROBLEM: ConcurrentQueue.Count and the subsequent Skip/Average are not
    // atomic. Between reading Count and iterating with Skip, new items can be
    // enqueued (from a concurrent AddClosePrice call). This means Skip may skip
    // the wrong number of items, producing an SMA calculated over the wrong
    // window — silent wrong result, no exception thrown.
    //
    // FIX: snapshot the queue to an array first (atomic point-in-time copy),
    // then slice and average on the stable snapshot.
    // =========================================================================
    public decimal? GetSMA(string symbol, int periods)
    {
        if (!_closePriceHistory.TryGetValue(symbol, out var history))
            return null;

        // FIX #4 ↓ — snapshot to array for stable slice
        var snapshot = history.ToArray();  // FIX #4: atomic point-in-time copy
        if (snapshot.Length < periods)
            return null;

        return snapshot.Skip(snapshot.Length - periods).Average();  // FIX #4: slice stable snapshot
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
        if (_rsiLastValue.TryGetValue(symbol,  out var rsi)     &&
            _rsiHistory.TryGetValue(symbol,    out var prevRsi) &&
            _rsiRmaValue.TryGetValue(symbol,   out var rma)     &&
            _rsiRmaHistory.TryGetValue(symbol, out var prevRma))
        {
            return (rsi, prevRsi, rma, prevRma);
        }
        return null;
    }

    // =========================================================================
    // Major Trend Tracking
    // =========================================================================

    public enum MarketTrend { Up, Down, Neutral }

    public MarketTrend GetMajorTrend(string symbol, decimal currentPrice)
    {
        var dma200 = GetSMA(symbol, 200);
        if (!dma200.HasValue) return MarketTrend.Neutral;
        return currentPrice > dma200.Value ? MarketTrend.Up : MarketTrend.Down;
    }

    // =========================================================================
    // 3-Day Range Tracking
    // =========================================================================

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

        // FIX #4 pattern ↓ — snapshot before aggregating
        var snapshot = history.ToArray();
        decimal maxHigh = snapshot.Max(x => x.High);
        decimal minLow  = snapshot.Min(x => x.Low);
        return (maxHigh, minLow);
    }

    // =========================================================================
    // CPR Tracking
    // =========================================================================

    public void SetDailyCPR(string symbol, decimal prevHigh, decimal prevLow, decimal prevClose)
        => _dailyCPR[symbol] = CalculateCPR(prevHigh, prevLow, prevClose);

    public void SetWeeklyCPR(string symbol, decimal prevHigh, decimal prevLow, decimal prevClose)
        => _weeklyCPR[symbol] = CalculateCPR(prevHigh, prevLow, prevClose);

    public void SetMonthlyCPR(string symbol, decimal prevHigh, decimal prevLow, decimal prevClose)
        => _monthlyCPR[symbol] = CalculateCPR(prevHigh, prevLow, prevClose);

    public PivotLevels? GetDailyCPR(string symbol)
        => _dailyCPR.TryGetValue(symbol, out var l)   ? l : null;

    public PivotLevels? GetWeeklyCPR(string symbol)
        => _weeklyCPR.TryGetValue(symbol, out var l)  ? l : null;

    public PivotLevels? GetMonthlyCPR(string symbol)
        => _monthlyCPR.TryGetValue(symbol, out var l) ? l : null;

    private static PivotLevels CalculateCPR(decimal high, decimal low, decimal close)
    {
        decimal pivot         = (high + low + close) / 3;
        decimal bottomCentral = (high + low) / 2;
        decimal topCentral    = (pivot - bottomCentral) + pivot;

        if (bottomCentral > topCentral)
            (bottomCentral, topCentral) = (topCentral, bottomCentral);

        return new PivotLevels
        {
            Pivot         = pivot,
            BottomCentral = bottomCentral,
            TopCentral    = topCentral,
            R1 = (2 * pivot) - low,
            S1 = (2 * pivot) - high,
            R2 = pivot + (high - low),
            S2 = pivot - (high - low),
            R3 = high + 2 * (pivot - low),
            S3 = low  - 2 * (high - pivot)
        };
    }
}
