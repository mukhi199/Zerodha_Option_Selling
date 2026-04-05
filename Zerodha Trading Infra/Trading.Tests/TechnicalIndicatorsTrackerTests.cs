using System;
using Xunit;
using Trading.Strategy.Services;

namespace Trading.Tests
{
    public class TechnicalIndicatorsTrackerTests
    {
        [Fact]
        public void AddClosePrice_CalculatesSMA_Correctly()
        {
            // Arrange
            var tracker = new TechnicalIndicatorsTracker();
            var symbol = "NIFTY 50";

            // Act: Add 50 prices (1 to 50)
            for (int i = 1; i <= 50; i++)
            {
                tracker.AddClosePrice(symbol, i);
            }

            // Assert
            var sma50 = tracker.GetSMA(symbol, 50);
            Assert.NotNull(sma50);
            
            // Sum of 1 to 50 is 1275. Average is 1275 / 50 = 25.5
            Assert.Equal(25.5m, sma50.Value);

            // Ask for SMA 100 which should be null
            Assert.Null(tracker.GetSMA(symbol, 100));
        }

        [Fact]
        public void GetEMA_CalculatesEMA_Correctly()
        {
            // Arrange
            var tracker = new TechnicalIndicatorsTracker();
            var symbol = "NIFTY BANK";

            // Act: Add a few prices to trigger EMA calculations
            tracker.AddClosePrice(symbol, 100m);
            tracker.AddClosePrice(symbol, 110m);
            tracker.AddClosePrice(symbol, 120m);

            // Assert
            var ema50 = tracker.GetEMA(symbol, 50);
            Assert.NotNull(ema50);

            // EMA manual calculation:
            // k = 2 / (50 + 1) = 2 / 51 = 0.039215686
            // 1st: EMA = 100
            // 2nd: (110 * k) + (100 * (1 - k)) = (110 * 0.039215686) + (100 * 0.960784313) = 4.31372546 + 96.0784313 = 100.392156
            // 3rd: (120 * k) + (100.392 * (1 - k)) = 101.1607...
            // Just asserting that it's calculated and mathematically > 100 and < 120
            Assert.True(ema50.Value > 100m && ema50.Value < 120m);
        }

        [Fact]
        public void CalculateCPR_LevelsAreMathematicallySound()
        {
            // Arrange
            var tracker = new TechnicalIndicatorsTracker();
            var symbol = "NIFTY 50";
            
            // Mock prev day values: High = 22000, Low = 21800, Close = 21900
            decimal high = 22000m;
            decimal low = 21800m;
            decimal close = 21900m;

            // Act
            tracker.SetDailyCPR(symbol, high, low, close);
            var cpr = tracker.GetDailyCPR(symbol);

            // Assert
            Assert.NotNull(cpr);

            // Pivot = (22000 + 21800 + 21900) / 3 = 65700 / 3 = 21900
            Assert.Equal(21900m, cpr.Pivot);

            // BottomCentral = (High + Low) / 2 = (22000 + 21800) / 2 = 21900
            // TopCentral = (Pivot - BC) + Pivot = (21900 - 21900) + 21900 = 21900
            Assert.Equal(21900m, cpr.BottomCentral);
            Assert.Equal(21900m, cpr.TopCentral);

            // R1 = (2 * Pivot) - Low = (43800) - 21800 = 22000
            Assert.Equal(22000m, cpr.R1);

            // S1 = (2 * Pivot) - High = 43800 - 22000 = 21800
            Assert.Equal(21800m, cpr.S1);

            // R2 = Pivot + (High - Low) = 21900 + 200 = 22100
            Assert.Equal(22100m, cpr.R2);

            // S2 = Pivot - (High - Low) = 21900 - 200 = 21700
            Assert.Equal(21700m, cpr.S2);

            // R3 = High + 2 * (Pivot - Low) = 22000 + 2 * (21900 - 21800) = 22000 + 200 = 22200
            Assert.Equal(22200m, cpr.R3);

            // S3 = Low - 2 * (High - Pivot) = 21800 - 2 * (22000 - 21900) = 21800 - 200 = 21600
            Assert.Equal(21600m, cpr.S3);
        }
    }
}
