using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trading.MQ.Services;
using Trading.Zerodha.Services;
using Trading.KX.Services;

namespace Trading.Streamer;

/// <summary>
/// The main orchestration worker.
/// Startup sequence:
///   1.  Ensure valid Zerodha access token (console OAuth flow or cached)
///   2.  Load instruments from KDB+ (or download from Kite + seed KDB+)
///   3.  Extract instrument tokens (uint list)
///   4.  Start WebSocket streamer (non-blocking)
///   5.  Keep running; EOD flush is handled by EodFlushService
/// </summary>
public class Worker : BackgroundService
{
    private readonly ZerodhaAuthService   _authService;
    private readonly InstrumentService    _instrumentService;
    private readonly ZerodhaStreamer      _streamer;
    private readonly KdbBackfillService   _backfillService;
    private readonly ILogger<Worker>      _logger;

    public Worker(
        ZerodhaAuthService authService,
        InstrumentService  instrumentService,
        ZerodhaStreamer    streamer,
        KdbBackfillService backfillService,
        ILogger<Worker>    logger)
    {
        _authService        = authService;
        _instrumentService  = instrumentService;
        _streamer           = streamer;
        _backfillService    = backfillService;
        _logger             = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== Zerodha Trading Infra starting at {Time} ===", DateTime.Now);

        // ── Step 1: Zerodha login ─────────────────────────────────────
        string accessToken;
        try
        {
            accessToken = await _authService.EnsureAccessTokenAsync();
            _logger.LogInformation("Zerodha authentication successful.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to authenticate with Zerodha. Exiting.");
            return;
        }

        // ── Step 2: Load instruments ──────────────────────────────────
        List<Trading.Core.Models.InstrumentInfo> instruments;
        try
        {
            instruments = await _instrumentService.LoadInstrumentsAsync(accessToken, "NSE");
            _logger.LogInformation("Loaded {Count} NSE instruments.", instruments.Count);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to load instruments. Exiting.");
            return;
        }

        if (instruments.Count == 0)
        {
            _logger.LogCritical("No instruments found. Please check KDB+ instruments table or Kite API.");
            return;
        }

        // ── Step 3: Extract tokens ────────────────────────────────────
        var tokensToSymbols = instruments.ToDictionary(i => i.InstrumentToken, i => i.TradingSymbol);
        _logger.LogInformation("Subscribing to {Count} instrument tokens.", tokensToSymbols.Count);

        // ── Step 4: Backfill from KDB+ (Warm Start) ───────────────────
        try
        {
            _logger.LogInformation("Warming up MQ with today's data from KDB+...");
            await _backfillService.BackfillTodayCandlesAsync("NIFTY 50");
            await _backfillService.BackfillTodayTicksAsync("NIFTY 50");
            await _backfillService.BackfillTodayCandlesAsync("BANKNIFTY");
            await _backfillService.BackfillTodayTicksAsync("BANKNIFTY");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KDB+ Backfill failed. Continuing with live stream only.");
        }

        // ── Step 5: Start WebSocket streamer ──────────────────────────
        try
        {
            _streamer.Start(accessToken, tokensToSymbols);
            _logger.LogInformation("WebSocket streamer started. Data flowing to RabbitMQ.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Streamer failed to start.");
            return;
        }

        // ── Step 6: Keep alive until cancellation ─────────────────────
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Worker stopping...");
            _streamer.Stop();
        }
    }
}