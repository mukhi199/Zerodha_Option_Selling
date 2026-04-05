namespace Trading.Backtest;

using Trading.Core.Models;

public static class BacktestDataGenerator
{
    public static List<Candle> GenerateBreakoutData(string symbol, decimal startingPrice)
    {
        var data = new List<Candle>();
        var random = new Random();
        var currentPrice = startingPrice;

        // Generate 5 days of data (at 5m intervals)
        for (int day = 0; day < 5; day++)
        {
            var date = DateTime.Today.AddDays(-5 + day);
            // 9:15 to 15:30 (NSE hours)
            var startTime = date.AddHours(9).AddMinutes(15);
            var endTime = date.AddHours(15).AddMinutes(30);

            while (startTime < endTime)
            {
                var open = currentPrice;
                var change = (decimal)(random.NextDouble() - 0.5) * 2; // -1 to 1 change
                
                // On the 4th day, simulate a major breakout!
                if (day == 3) change += 10; 

                var close = open + change;
                var high = Math.Max(open, close) + 1;
                var low = Math.Min(open, close) - 1;

                data.Add(new Candle
                {
                    Symbol = symbol,
                    StartTime = startTime,
                    IntervalMinutes = 5,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = 1000
                });

                currentPrice = close;
                startTime = startTime.AddMinutes(5);
            }
        }

        return data;
    }
}
