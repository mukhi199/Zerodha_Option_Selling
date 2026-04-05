using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Trading.Core;
using Trading.Core.Models;
using Trading.MQ.Services;
using Xunit;

namespace Trading.Tests
{
    public class CandleAggregatorTests
    {
        [Fact]
        public void ProcessTick_GeneratesClosedCandle_WhenTimeWindowPasses()
        {
            // Arrange
            var mockMqPublisher = new Mock<IMQCandlePublisher>();
            mockMqPublisher.Setup(m => m.PublishCandle(It.IsAny<Candle>())).Callback<Candle>(c => { });

            var candleBuffer = new CandleBuffer();
            var logger = NullLogger<CandleAggregator>.Instance;

            var aggregator = new CandleAggregator(mockMqPublisher.Object, candleBuffer, logger);

            var symbol = "RELIANCE";
            var token = 12345u;
            var baseTime = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc); // 10:00:00

            // Act - Ticks within the first minute
            aggregator.ProcessTick(new NormalizedTick { Symbol = symbol, InstrumentToken = token, Price = 100m, Volume = 10, ExchangeTime = baseTime.AddSeconds(10) });
            aggregator.ProcessTick(new NormalizedTick { Symbol = symbol, InstrumentToken = token, Price = 105m, Volume = 20, ExchangeTime = baseTime.AddSeconds(30) });
            aggregator.ProcessTick(new NormalizedTick { Symbol = symbol, InstrumentToken = token, Price = 95m,  Volume = 15, ExchangeTime = baseTime.AddSeconds(50) });

            // No candles should be closed yet
            var bufferCountSoFar = candleBuffer.DrainAll().Count;
            Assert.Equal(0, bufferCountSoFar);

            // Act - Tick in the next minute triggers closing the previous 1m candle
            var nextMinuteTickTime = baseTime.AddSeconds(65); // 10:01:05
            aggregator.ProcessTick(new NormalizedTick { Symbol = symbol, InstrumentToken = token, Price = 102m, Volume = 5, ExchangeTime = nextMinuteTickTime });

            // Assert
            var closedCandles = candleBuffer.DrainAll();
            Assert.Contains(closedCandles, c => c.IntervalMinutes == 1 && c.Symbol == symbol);

            var oneMinCandle = closedCandles.First(c => c.IntervalMinutes == 1);
            Assert.Equal(100m, oneMinCandle.Open);
            Assert.Equal(105m, oneMinCandle.High);
            Assert.Equal(95m,  oneMinCandle.Low);
            Assert.Equal(95m,  oneMinCandle.Close);
            Assert.Equal(45,   oneMinCandle.Volume); // 10 + 20 + 15
            Assert.True(oneMinCandle.IsClosed);
            Assert.Equal(baseTime, oneMinCandle.StartTime);

            // Verify MQ publisher was called
            mockMqPublisher.Verify(m => m.PublishCandle(It.Is<Candle>(c => c.IntervalMinutes == 1 && c.IsClosed)), Times.Once);
        }
    }
}
