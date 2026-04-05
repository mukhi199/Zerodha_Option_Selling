using kx;
using Microsoft.Extensions.Logging;
using System.Data;

namespace Trading.KX.Services;

public class KdbTester
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<KdbTester> _logger;

    public KdbTester(string host, int port, ILogger<KdbTester> logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
    }

    public void VerifyData()
    {
        _logger.LogInformation("KDB+: Testing connection to {Host}:{Port}...", _host, _port);
        c? conn = null;
        try
        {
            conn = new c(_host, _port);
            _logger.LogInformation("KDB+: Connected successfully!");

            // 1. Check instruments table
            var instrumentsCount = (long)conn.k("count instruments");
            _logger.LogInformation("KDB+: Table 'instruments' has {Count} rows.", instrumentsCount);

            // 2. Check ticks table (if exists)
            var ticksExists = (bool)conn.k("`ticks in tables[]");
            if (ticksExists)
            {
                var ticksCount = (long)conn.k("count ticks");
                _logger.LogInformation("KDB+: Table 'ticks' has {Count} rows.", ticksCount);
            }
            else
            {
                _logger.LogWarning("KDB+: Table 'ticks' does not exist yet (waiting for first EOD flush).");
            }

            // 3. Check candles table
            var candlesExists = (bool)conn.k("`candles in tables[]");
            if (candlesExists)
            {
                var candlesCount = (long)conn.k("count candles");
                _logger.LogInformation("KDB+: Table 'candles' has {Count} rows.", candlesCount);
            }
            else
            {
                _logger.LogWarning("KDB+: Table 'candles' does not exist yet.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KDB+: Connection or query failed.");
        }
        finally
        {
            conn?.Close();
        }
    }
}
