using kx;
using Microsoft.Extensions.Logging;
using Trading.Core.Models;

namespace Trading.KX.Services;

/// <summary>
/// Writes tick data and OHLC candles into KDB+ via the CSharpKDB client (kx.c).
/// API reference: connection.k("insert", tableName, row_as_object_array)
/// For bulk insert, we execute a q expression that inserts a Flip (table) of column arrays.
///
/// KDB+ tables expected (auto-created if absent):
///   ticks:   ([]time:`timestamp$();sym:`symbol$();price:`float$();volume:`long$();oi:`float$();bid:`float$();ask:`float$())
///   candles: ([]time:`timestamp$();sym:`symbol$();interval:`int$();open:`float$();high:`float$();low:`float$();close:`float$();volume:`long$())
/// </summary>
public class KdbWriter
{
    private readonly string _host;
    private readonly int    _port;
    private readonly ILogger<KdbWriter> _logger;

    public KdbWriter(string host, int port, ILogger<KdbWriter> logger)
    {
        _host   = host;
        _port   = port;
        _logger = logger;
    }

    // ─────────────────── Ticks ───────────────────

    /// <summary>
    /// Batch-inserts a list of normalized ticks into KDB+ `ticks` table.
    /// Each tick is inserted as a separate row using connection.k("insert", ...).
    /// </summary>
    public void InsertTickBatch(List<NormalizedTick> ticks)
    {
        if (ticks.Count == 0) return;

        _logger.LogInformation("KDB+: inserting {Count} ticks...", ticks.Count);

        c? conn = null;
        try
        {
            conn = new c(_host, _port);
            c.e = System.Text.Encoding.UTF8;

            // Ensure table exists
            conn.k(
                "if[not `ticks in tables[];ticks:([]" +
                "time:`timestamp$();sym:`symbol$();price:`float$();" +
                "volume:`long$();oi:`float$();bid:`float$();ask:`float$())]");

            // Bulk insert via column arrays wrapped in a Flip
            var times   = ticks.Select(t => t.ExchangeTime.TimeOfDay).ToArray();
            var syms    = ticks.Select(t => t.Symbol).ToArray();
            var prices  = ticks.Select(t => (double)t.Price).ToArray();
            var volumes = ticks.Select(t => t.Volume).ToArray();
            var ois     = ticks.Select(t => (double)t.OpenInterest).ToArray();
            var bids    = ticks.Select(t => (double)t.BidPrice).ToArray();
            var asks    = ticks.Select(t => (double)t.AskPrice).ToArray();

            conn.k("insert",
                "ticks",
                new c.Flip(new c.Dict(
                    new[] { "time", "sym", "price", "volume", "oi", "bid", "ask" },
                    new object[] { times, syms, prices, volumes, ois, bids, asks })));

            _logger.LogInformation("KDB+: inserted {Count} ticks OK.", ticks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KDB+: failed to insert tick batch.");
        }
        finally
        {
            conn?.Close();
        }
    }

    // ─────────────────── Candles ───────────────────

    /// <summary>
    /// Batch-inserts closed OHLC candles into KDB+ `candles` table.
    /// </summary>
    public void InsertCandleBatch(List<Candle> candles)
    {
        if (candles.Count == 0) return;

        _logger.LogInformation("KDB+: inserting {Count} candles...", candles.Count);

        c? conn = null;
        try
        {
            conn = new c(_host, _port);
            c.e = System.Text.Encoding.UTF8;

            conn.k(
                "if[not `candles in tables[];candles:([]" +
                "time:`timestamp$();sym:`symbol$();interval:`int$();" +
                "open:`float$();high:`float$();low:`float$();close:`float$();volume:`long$())]");

            var times     = candles.Select(c => c.StartTime.TimeOfDay).ToArray();
            var syms      = candles.Select(c => c.Symbol).ToArray();
            var intervals = candles.Select(c => (int)c.IntervalMinutes).ToArray();
            var opens     = candles.Select(c => (double)c.Open).ToArray();
            var highs     = candles.Select(c => (double)c.High).ToArray();
            var lows      = candles.Select(c => (double)c.Low).ToArray();
            var closes    = candles.Select(c => (double)c.Close).ToArray();
            var volumes   = candles.Select(c => c.Volume).ToArray();

            conn.k("insert",
                "candles",
                new c.Flip(new c.Dict(
                    new[] { "time", "sym", "interval", "open", "high", "low", "close", "volume" },
                    new object[] { times, syms, intervals, opens, highs, lows, closes, volumes })));

            _logger.LogInformation("KDB+: inserted {Count} candles OK.", candles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KDB+: failed to insert candle batch.");
        }
        finally
        {
            conn?.Close();
        }
    }
}
