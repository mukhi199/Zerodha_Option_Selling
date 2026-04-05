using kx;
using Microsoft.Extensions.Logging;
using Trading.Core.Models;
using Trading.MQ.Services;
using System.Data;

namespace Trading.KX.Services;

/// <summary>
/// Service to query historical ticks/candles from KDB+ and replay them into RabbitMQ.
/// This allows a strategy to "warm up" with today's data from KDB+ if it starts mid-day.
/// </summary>
public class KdbBackfillService
{
    private readonly string _host;
    private readonly int _port;
    private readonly MQPublisher _tickPublisher;
    private readonly IMQCandlePublisher _candlePublisher;
    private readonly ILogger<KdbBackfillService> _logger;

    public KdbBackfillService(
        string host, 
        int port, 
        MQPublisher tickPublisher, 
        IMQCandlePublisher candlePublisher, 
        ILogger<KdbBackfillService> logger)
    {
        _host = host;
        _port = port;
        _tickPublisher = tickPublisher;
        _candlePublisher = candlePublisher;
        _logger = logger;
    }

    /// <summary>
    /// Pulls today's candles for a symbol from KDB+ and publishes them to MQ.
    /// </summary>
    public async Task BackfillTodayCandlesAsync(string symbol)
    {
        await Task.Run(() => 
        {
            _logger.LogInformation("KDB Backfill: Pulling today's candles for {Symbol}...", symbol);
        
            try
            {
                using var conn = new c(_host, _port);
                
                // Query today's candles. 
                var query = $"select from candles where sym=`{symbol}";
                var result = conn.k(query);

                if (result is c.Flip flip)
                {
                    var n = ((Array)flip.y[0]).Length;
                    _logger.LogInformation("KDB Backfill: Found {Count} candles. Replaying to MQ...", n);

                    var times = (TimeSpan[])flip.y[Array.IndexOf(flip.x, "time")];
                    var opens = (double[])flip.y[Array.IndexOf(flip.x, "open")];
                    var highs = (double[])flip.y[Array.IndexOf(flip.x, "high")];
                    var lows = (double[])flip.y[Array.IndexOf(flip.x, "low")];
                    var closes = (double[])flip.y[Array.IndexOf(flip.x, "close")];
                    var volumes = (long[])flip.y[Array.IndexOf(flip.x, "volume")];
                    var intervals = (int[])flip.y[Array.IndexOf(flip.x, "interval")];

                    for (int i = 0; i < n; i++)
                    {
                        var candle = new Candle
                        {
                            Symbol = symbol,
                            StartTime = DateTime.Today.Add(times[i]),
                            Open = (decimal)opens[i],
                            High = (decimal)highs[i],
                            Low = (decimal)lows[i],
                            Close = (decimal)closes[i],
                            Volume = volumes[i],
                            IntervalMinutes = intervals[i]
                        };
                        _candlePublisher.PublishCandle(candle);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KDB Backfill: Failed to backfill candles for {Symbol}", symbol);
            }
        });
    }

    /// <summary>
    /// Pulls today's ticks for a symbol from KDB+ and publishes them to MQ.
    /// </summary>
    public async Task BackfillTodayTicksAsync(string symbol)
    {
        await Task.Run(() => 
        {
            _logger.LogInformation("KDB Backfill: Pulling today's ticks for {Symbol}...", symbol);

            try
            {
                using var conn = new c(_host, _port);
                var query = $"select from ticks where sym=`{symbol}";
                var result = conn.k(query);

                if (result is c.Flip flip)
                {
                    var n = ((Array)flip.y[0]).Length;
                    _logger.LogInformation("KDB Backfill: Found {Count} ticks. Replaying to MQ...", n);

                    var times = (TimeSpan[])flip.y[Array.IndexOf(flip.x, "time")];
                    var prices = (double[])flip.y[Array.IndexOf(flip.x, "price")];
                    var volumes = (long[])flip.y[Array.IndexOf(flip.x, "volume")];
                    var ois = (double[])flip.y[Array.IndexOf(flip.x, "oi")];
                    var bids = (double[])flip.y[Array.IndexOf(flip.x, "bid")];
                    var asks = (double[])flip.y[Array.IndexOf(flip.x, "ask")];

                    for (int i = 0; i < n; i++)
                    {
                        var tick = new NormalizedTick
                        {
                            Symbol = symbol,
                            ExchangeTime = DateTime.Today.Add(times[i]),
                            Price = (decimal)prices[i],
                            Volume = volumes[i],
                            OpenInterest = (decimal)ois[i],
                            BidPrice = (decimal)bids[i],
                            AskPrice = (decimal)asks[i]
                        };
                        _tickPublisher.PublishTick(tick);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KDB Backfill: Failed to backfill ticks for {Symbol}", symbol);
            }
        });
    }
}
