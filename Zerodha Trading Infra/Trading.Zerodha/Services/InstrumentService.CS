using KiteConnect;
using kx;
using Microsoft.Extensions.Logging;
using Trading.Core.Models;

namespace Trading.Zerodha.Services;

/// <summary>
/// Loads instrument list from KDB+ table (instruments).
/// If the table is empty or missing, falls back to downloading from Kite API
/// and then seeds the KDB+ table for future use.
/// </summary>
public class InstrumentService
{
    private readonly string _kdbHost;
    private readonly int _kdbPort;
    private readonly string _apiKey;
    private readonly ILogger<InstrumentService> _logger;

    public InstrumentService(
        string kdbHost,
        int kdbPort,
        string apiKey,
        ILogger<InstrumentService> logger)
    {
        _kdbHost = kdbHost;
        _kdbPort = kdbPort;
        _apiKey  = apiKey;
        _logger  = logger;
    }

    /// <summary>
    /// Returns the list of instruments to stream.
    /// Priority: KDB+ instruments table → Kite API download.
    /// </summary>
    public async Task<List<InstrumentInfo>> LoadInstrumentsAsync(
        string accessToken,
        string exchange = "NSE")
    {
        _logger.LogInformation("Attempting to load instruments from KDB+ table...");

        var fromKdb = TryLoadFromKdb(exchange);

        if (fromKdb.Count > 0)
        {
            _logger.LogInformation("Loaded {Count} instruments from KDB+ table.", fromKdb.Count);
            return fromKdb;
        }

        _logger.LogWarning("KDB+ instruments table is empty. Downloading from Kite API...");

        var fromKite = await DownloadFromKiteAsync(accessToken, exchange);

        _logger.LogInformation("Downloaded {Count} instruments from Kite API. Seeding KDB+...", fromKite.Count);

        SeedKdb(fromKite);

        return fromKite;
    }

    // ─────────────────────── KDB+ load ───────────────────────

    private List<InstrumentInfo> TryLoadFromKdb(string exchange)
    {
        var result = new List<InstrumentInfo>();

        try
        {
            using var conn = new c(_kdbHost, _kdbPort);

            // Query: select from instruments where exchange=`NSE
            var query = $"select from instruments where exchange=`{exchange}";
            var raw   = conn.k(query);

            if (raw is c.Flip flip)
            {
                var n = ((Array)flip.y[0]).Length;

                var tokens  = (long[])  flip.y[Array.IndexOf(flip.x, "instrumenttoken")];
                var syms    = (string[])flip.y[Array.IndexOf(flip.x, "tradingsymbol")];
                var exch    = (string[])flip.y[Array.IndexOf(flip.x, "exchange")];
                var names   = (string[])flip.y[Array.IndexOf(flip.x, "name")];
                var ticks   = (double[])flip.y[Array.IndexOf(flip.x, "ticksize")];
                var lots    = (long[])  flip.y[Array.IndexOf(flip.x, "lotsize")];
                var types   = (string[])flip.y[Array.IndexOf(flip.x, "instrumenttype")];

                for (int i = 0; i < n; i++)
                {
                    result.Add(new InstrumentInfo
                    {
                        InstrumentToken = (uint)tokens[i],
                        TradingSymbol   = syms[i],
                        Exchange        = exch[i],
                        Name            = names[i],
                        TickSize        = (decimal)ticks[i],
                        LotSize         = (int)lots[i],
                        InstrumentType  = types[i]
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query KDB+ instruments table. Will fall back to Kite API.");
        }

        return result;
    }

    // ─────────────────────── Kite API download ───────────────────────

    private async Task<List<InstrumentInfo>> DownloadFromKiteAsync(string accessToken, string exchange)
    {
        var kite = new Kite(_apiKey, accessToken);

        // GetInstruments is synchronous; wrap in Task.Run
        var instruments = await Task.Run(() => kite.GetInstruments(exchange));

        return instruments.Select(i => new InstrumentInfo
        {
            InstrumentToken = (uint)i.InstrumentToken,
            TradingSymbol   = i.TradingSymbol ?? string.Empty,
            Exchange        = i.Exchange       ?? string.Empty,
            Name            = i.Name           ?? string.Empty,
            TickSize        = (decimal)i.TickSize,
            LotSize         = (int)i.LotSize,
            InstrumentType  = i.InstrumentType ?? string.Empty,
            Segment         = i.Segment        ?? string.Empty
        }).ToList();
    }

    // ─────────────────────── KDB+ seed ───────────────────────

    private void SeedKdb(List<InstrumentInfo> instruments)
    {
        try
        {
            using var conn = new c(_kdbHost, _kdbPort);

            // Ensure the instruments table exists in KDB+
            // Schema: instrumenttoken(long), tradingsymbol(sym), exchange(sym),
            //         name(sym), ticksize(float), lotsize(long), instrumenttype(sym)
            conn.k("if[not `instruments in tables[];instruments:([]" +
                   "instrumenttoken:`long$();tradingsymbol:`symbol$();" +
                   "exchange:`symbol$();name:`symbol$();" +
                   "ticksize:`float$();lotsize:`long$();instrumenttype:`symbol$())]");

            // Batch insert
            var tokens  = instruments.Select(i => (long)i.InstrumentToken).ToArray();
            var syms    = instruments.Select(i => (object)i.TradingSymbol).ToArray();
            var exch    = instruments.Select(i => (object)i.Exchange).ToArray();
            var names   = instruments.Select(i => (object)i.Name).ToArray();
            var ticks   = instruments.Select(i => (double)i.TickSize).ToArray();
            var lots    = instruments.Select(i => (long)i.LotSize).ToArray();
            var types   = instruments.Select(i => (object)i.InstrumentType).ToArray();

            conn.k("insert",
                "instruments",
                new c.Flip(new c.Dict(
                    new[] { "instrumenttoken","tradingsymbol","exchange","name","ticksize","lotsize","instrumenttype" },
                    new object[] { tokens, syms, exch, names, ticks, lots, types })));

            _logger.LogInformation("Seeded KDB+ instruments table with {Count} records.", instruments.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed KDB+ instruments table.");
        }
    }
}
