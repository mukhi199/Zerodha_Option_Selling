using KiteConnect;
using Microsoft.Extensions.Logging;
using Trading.Core;
using Trading.Core.Models;
using Trading.MQ.Services;

namespace Trading.Zerodha.Services;

/// <summary>
/// Manages the Zerodha WebSocket (Ticker) connection.
/// - Uses ModeFull to receive price, volume, OI, bid/ask
/// - Auto-reconnects on error or disconnect
/// - Feeds each tick into: MQPublisher, TickBuffer, CandleAggregator
/// </summary>
public class ZerodhaStreamer
{
    private readonly string    _apiKey;
    private string             _accessToken = string.Empty;  // set by Start()
    private readonly MQPublisher      _mqPublisher;
    private readonly TickBuffer       _tickBuffer;
    private readonly CandleAggregator _candleAggregator;
    private readonly ILogger<ZerodhaStreamer> _logger;

    private Ticker?            _ticker;
    private Dictionary<uint, string> _tokensToSymbols = new();
    private readonly object    _reconnectLock = new();
    private bool               _running;

    public ZerodhaStreamer(
        string apiKey,
        MQPublisher mqPublisher,
        TickBuffer tickBuffer,
        CandleAggregator candleAggregator,
        ILogger<ZerodhaStreamer> logger)
    {
        _apiKey          = apiKey;
        _mqPublisher     = mqPublisher;
        _tickBuffer      = tickBuffer;
        _candleAggregator = candleAggregator;
        _logger          = logger;
    }

    /// <summary>
    /// Connects to Zerodha WebSocket and subscribes to the given instrument tokens.
    /// Non-blocking: the WebSocket runs on its own internal thread.
    /// </summary>
    public void Start(string accessToken, Dictionary<uint, string> instruments)
    {
        _accessToken      = accessToken;
        _tokensToSymbols  = instruments;
        _running          = true;

        _logger.LogInformation("ZerodhaStreamer: connecting with {Count} instruments...", _tokensToSymbols.Count);

        Connect();
    }

    public void Stop()
    {
        _running = false;
        _ticker?.Close();
    }

    // ──────────────── connection management ────────────────

    private void Connect()
    {
        _ticker = new Ticker(_apiKey, _accessToken);

        _ticker.OnTick    += HandleTicks;
        _ticker.OnConnect += OnConnect;
        _ticker.OnClose   += OnClose;
        _ticker.OnError   += OnError;
        _ticker.OnNoReconnect += OnNoReconnect;

        _ticker.EnableReconnect(Interval: 5, Retries: 50);

        _ticker.Connect();
    }

    private void OnConnect()
    {
        _logger.LogInformation("ZerodhaStreamer: WebSocket connected. Subscribing to {Count} tokens.", _tokensToSymbols.Count);

        var tokensArray = _tokensToSymbols.Keys.ToArray();
        _ticker!.Subscribe(tokensArray);
        _ticker!.SetMode(tokensArray, "full");
    }

    private void OnClose()
    {
        _logger.LogWarning("ZerodhaStreamer: WebSocket closed. Reconnect will be attempted by the SDK.");
    }

    private void OnError(string message)
    {
        _logger.LogError("ZerodhaStreamer: WebSocket error: {Message}", message);
    }

    private void OnNoReconnect()
    {
        _logger.LogCritical("ZerodhaStreamer: all reconnect attempts exhausted. Attempting manual reconnect in 30s...");

        if (!_running) return;

        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));

            lock (_reconnectLock)
            {
                _logger.LogInformation("ZerodhaStreamer: manual reconnect triggered.");
                Connect();
            }
        });
    }

    // ──────────────── tick handler ────────────────

    private void HandleTicks(Tick tick)
    {
        var normalized = new NormalizedTick
        {
            Symbol          = _tokensToSymbols.GetValueOrDefault(tick.InstrumentToken, tick.InstrumentToken.ToString()),
            InstrumentToken = tick.InstrumentToken,
            Price           = tick.LastPrice,
            Volume          = tick.Volume,
            OpenInterest    = tick.OI,
            BidPrice        = tick.Bids?.Length > 0 ? tick.Bids[0].Price : 0m,
            AskPrice        = tick.Offers?.Length > 0 ? tick.Offers[0].Price : 0m,
            ExchangeTime    = tick.Timestamp ?? DateTime.Now,
            ReceivedAt      = DateTime.UtcNow
        };

        // 1. Publish raw tick to RabbitMQ
        _mqPublisher.PublishTick(normalized);

        // 2. Buffer raw tick for EOD KDB+ insert
        _tickBuffer.Add(normalized);

        // 3. Feed into OHLC aggregator (generates closed candles → MQ + CandleBuffer)
        _candleAggregator.ProcessTick(normalized);
    }
}