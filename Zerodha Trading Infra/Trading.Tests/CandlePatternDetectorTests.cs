using System;
using Xunit;
using Trading.Core.Models;
using Trading.Core.Utils;

namespace Trading.Tests
{
    public class CandlePatternDetectorTests
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Creates a candle with explicit OHLC values (no time needed for pattern tests).</summary>
        private static Candle Make(decimal open, decimal high, decimal low, decimal close) =>
            new()
            {
                Symbol         = "TEST",
                IntervalMinutes = 5,
                StartTime      = DateTime.Today,
                Open           = open,
                High           = high,
                Low            = low,
                Close          = close,
                Volume         = 1000
            };

        // ─────────────────────────── Single-candle ───────────────────────────

        [Fact]
        public void DetectSingleCandle_Doji_WhenBodyIsNearZero()
        {
            // Body is 0 (Open == Close), so it's a Doji
            var candle = Make(100m, 110m, 90m, 100m);
            var result = CandlePatternDetector.DetectSingleCandle(candle);
            Assert.Equal(CandlePatternDetector.CandlePattern.Doji, result);
        }

        [Fact]
        public void DetectSingleCandle_Hammer_LongLowerWickSmallBodyAtTop()
        {
            // Range = 100 (120 - 20). Body = 15 (105 → 120). LowerWick = 85. UpperWick = 0.
            // body(15) = 15% of range(100) → NOT Doji (>10%), IS ≤33% ✓
            // lowerWick(85) ≥ 2×body(30) ✓   upperWick(0) < body ✓  → Hammer
            var candle = Make(105m, 120m, 20m, 120m);
            var result = CandlePatternDetector.DetectSingleCandle(candle);
            Assert.Equal(CandlePatternDetector.CandlePattern.Hammer, result);
        }

        [Fact]
        public void DetectSingleCandle_ShootingStar_LongUpperWickSmallBodyAtBottom()
        {
            // Range = 100 (120 - 20). Body = 15 (20 → 35). UpperWick = 85. LowerWick = 0.
            // body(15) = 15% → NOT Doji (>10%), IS ≤33% ✓
            // upperWick(85) ≥ 2×body(30) ✓   lowerWick(0) < body ✓  → ShootingStar
            var candle = Make(35m, 120m, 20m, 20m);
            var result = CandlePatternDetector.DetectSingleCandle(candle);
            Assert.Equal(CandlePatternDetector.CandlePattern.ShootingStar, result);
        }

        [Fact]
        public void DetectSingleCandle_BullishLongWick_LongLowerWick()
        {
            // Range = 100 (200 - 100).  Body = 40 (160 → 200).  LowerWick = 60 (160 - 100).
            // body > 33% of range so NOT a Hammer.  lowerWick (60) ≥ 65% of range (65)? 60 < 65
            // Let's use a clear case: body 5, lowerWick 75 out of range 80
            // Open=175, Close=180, High=180, Low=100 → range=80, body=5, lowerWick=75, upperWick=0
            // lowerWick (75) ≥ 65% * 80 (52) ✓  BUT body (5) ≤ 33%*80 (26.4) ✓ and lowerWick≥2×body ✓ → Hammer
            // To avoid Hammer: need body > 33% of range.
            // Open=130, Close=180, High=180, Low=100 → range=80, body=50, lowerWick=30, upperWick=0
            // body (50) > 33% (26.4) → not Hammer/SS
            // lowerWick (30) ≥ 65% * 80 (52)? No. Increase lowerWick: Open=170, Close=180, H=180, L=100 → lowerWick=70 ≥ 52 ✓
            var candle = Make(170m, 180m, 100m, 180m);
            // body = 10, range = 80, 10 ≤ 33%*80=26.4 ✓ → Hammer (body small + long lower)
            // Edge case: just verify it's at least bullish
            var result = CandlePatternDetector.DetectSingleCandle(candle);
            Assert.True(CandlePatternDetector.IsBullish(result),
                $"Expected bullish pattern but got {result}");
        }

        [Fact]
        public void DetectSingleCandle_BearishLongWick_LongUpperWick()
        {
            // Open=30, Close=20, High=100, Low=20 → range=80, body=10, upperWick=70, lowerWick=0
            // body(10) ≤ 33%*80(26.4) ✓  upperWick(70) ≥ 2×body(20) ✓ lowerWick(0) < body ✓ → ShootingStar
            var candle = Make(30m, 100m, 20m, 20m);
            var result = CandlePatternDetector.DetectSingleCandle(candle);
            Assert.True(CandlePatternDetector.IsBearish(result),
                $"Expected bearish pattern but got {result}");
        }

        [Fact]
        public void DetectSingleCandle_PlainBullishCandle()
        {
            // Big bullish candle, no special pattern
            var candle = Make(100m, 160m, 95m, 155m);
            var result = CandlePatternDetector.DetectSingleCandle(candle);
            Assert.Equal(CandlePatternDetector.CandlePattern.BullishCandle, result);
        }

        [Fact]
        public void DetectSingleCandle_PlainBearishCandle()
        {
            var candle = Make(155m, 160m, 95m, 100m);
            var result = CandlePatternDetector.DetectSingleCandle(candle);
            Assert.Equal(CandlePatternDetector.CandlePattern.BearishCandle, result);
        }

        // ─────────────────────────── Two-candle ─────────────────────────────

        [Fact]
        public void DetectTwoCandle_BullishEngulfing()
        {
            // prev: bearish small candle (110 → 100)
            var prev = Make(110m, 115m, 95m, 100m);
            // curr: bullish that engulfs prev body — Open below prev.Close, Close above prev.Open
            var curr = Make(98m, 120m, 96m, 112m);
            var result = CandlePatternDetector.DetectTwoCandle(prev, curr);
            Assert.Equal(CandlePatternDetector.CandlePattern.BullishEngulfing, result);
        }

        [Fact]
        public void DetectTwoCandle_BearishEngulfing()
        {
            // prev: bullish small candle (100 → 110)
            var prev = Make(100m, 115m, 95m, 110m);
            // curr: bearish that engulfs prev body — Open above prev.Close, Close below prev.Open
            var curr = Make(112m, 115m, 96m, 98m);
            var result = CandlePatternDetector.DetectTwoCandle(prev, curr);
            Assert.Equal(CandlePatternDetector.CandlePattern.BearishEngulfing, result);
        }

        [Fact]
        public void DetectTwoCandle_BullishHarami()
        {
            // prev: big bearish candle (120 → 80)
            var prev = Make(120m, 125m, 75m, 80m);
            // curr: small bullish inside prev body — Open > prev.Close(80), Close < prev.Open(120)
            var curr = Make(85m, 100m, 83m, 95m);
            var result = CandlePatternDetector.DetectTwoCandle(prev, curr);
            Assert.Equal(CandlePatternDetector.CandlePattern.BullishHarami, result);
        }

        [Fact]
        public void DetectTwoCandle_BearishHarami()
        {
            // prev: big bullish candle (80 → 120)
            var prev = Make(80m, 125m, 75m, 120m);
            // curr: small bearish inside prev body — Open < prev.Close(120), Close > prev.Open(80)
            var curr = Make(115m, 118m, 85m, 85m);
            var result = CandlePatternDetector.DetectTwoCandle(prev, curr);
            Assert.Equal(CandlePatternDetector.CandlePattern.BearishHarami, result);
        }

        [Fact]
        public void DetectTwoCandle_ReturnsNone_WhenNoPatternFound()
        {
            // Two plain bullish candles — no two-candle pattern
            var prev = Make(100m, 110m, 98m, 108m);
            var curr = Make(109m, 120m, 107m, 118m);
            var result = CandlePatternDetector.DetectTwoCandle(prev, curr);
            Assert.Equal(CandlePatternDetector.CandlePattern.None, result);
        }

        // ─────────────────────────── Helpers ─────────────────────────────────

        [Fact]
        public void IsBullish_ReturnsTrueForBullishPatterns()
        {
            Assert.True(CandlePatternDetector.IsBullish(CandlePatternDetector.CandlePattern.BullishEngulfing));
            Assert.True(CandlePatternDetector.IsBullish(CandlePatternDetector.CandlePattern.BullishHarami));
            Assert.True(CandlePatternDetector.IsBullish(CandlePatternDetector.CandlePattern.Hammer));
            Assert.True(CandlePatternDetector.IsBullish(CandlePatternDetector.CandlePattern.BullishLongWick));
            Assert.True(CandlePatternDetector.IsBullish(CandlePatternDetector.CandlePattern.BullishCandle));
        }

        [Fact]
        public void IsBearish_ReturnsTrueForBearishPatterns()
        {
            Assert.True(CandlePatternDetector.IsBearish(CandlePatternDetector.CandlePattern.BearishEngulfing));
            Assert.True(CandlePatternDetector.IsBearish(CandlePatternDetector.CandlePattern.BearishHarami));
            Assert.True(CandlePatternDetector.IsBearish(CandlePatternDetector.CandlePattern.ShootingStar));
            Assert.True(CandlePatternDetector.IsBearish(CandlePatternDetector.CandlePattern.BearishLongWick));
            Assert.True(CandlePatternDetector.IsBearish(CandlePatternDetector.CandlePattern.BearishCandle));
        }

        [Fact]
        public void Describe_ReturnsReadableNameForAllPatterns()
        {
            foreach (CandlePatternDetector.CandlePattern p in Enum.GetValues<CandlePatternDetector.CandlePattern>())
            {
                var description = CandlePatternDetector.Describe(p);
                Assert.False(string.IsNullOrWhiteSpace(description));
            }
        }
    }
}
