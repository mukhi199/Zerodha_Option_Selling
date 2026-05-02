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
    private long               _totalTicks;
    private Timer?             _statusTimer;

    private readonly string    _tgBotToken;
    private readonly string    _tgChatId;
    private readonly string    _tgProviderUrl;
    private readonly HttpClient _httpClient = new();

    // ── Debounce fields ────────────────────────────────────────────────────
    private System.Threading.CancellationTokenSource? _pendingAlertCts;
    private bool _isCurrentlyDisconnected = false;

    public ZerodhaStreamer(
        string apiKey,
        MQPublisher mqPublisher,
        TickBuffer tickBuffer,
        CandleAggregator candleAggregator,
        ILogger<ZerodhaStreamer> logger,
        string tgBotToken,
        string tgChatId,
        string tgProviderUrl)
    {
        _apiKey          = apiKey;
        _mqPublisher     = mqPublisher;
        _tickBuffer      = tickBuffer;
        _candleAggregator = candleAggregator;
        _logger          = logger;
        _tgBotToken      = tgBotToken;
        _tgChatId        = tgChatId;
        _tgProviderUrl   = tgProviderUrl;
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
        _statusTimer = new Timer(BroadcastStatus, null, 10000, 10000);
        Connect();
    }

    private void BroadcastStatus(object? state)
    {
        try
        {
            _logger.LogInformation("ZerodhaStreamer: Broadcasting SystemStatus Heartbeat. Ticks: {Ticks}", _totalTicks);
            _mqPublisher.PublishStatus(new SystemStatus
            {
                WebSocketConnected = true,
                TicksProcessed = _totalTicks,
                ServerTime = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast status.");
        }
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
        
        // Cancel any pending "Connection Lost" alert
        _pendingAlertCts?.Cancel();

        if (_isCurrentlyDisconnected)
        {
            SendTelegramAlert("✅ Zerodha WebSocket RECONNECTED.");
            _isCurrentlyDisconnected = false;
        }
        else
        {
            SendTelegramAlert($"✅ Zerodha WebSocket Connected. Subscribing to {_tokensToSymbols.Count} tokens.");
        }

        var tokensArray = _tokensToSymbols.Keys.ToArray();
        _ticker!.Subscribe(tokensArray);
        _ticker!.SetMode(tokensArray, "quote");
        
        // Tiered Subscription:
        // NSE Spot (256265, 260105) -> Use "ltp" 
        // Others (NFO) -> Use "quote"
        var indices = tokensArray.Where(t => t == 256265 || t == 260105).ToArray();
        var others  = tokensArray.Where(t => t != 256265 && t != 260105).ToArray();

        if (indices.Length > 0)
        {
            _logger.LogInformation("Setting mode:quote for {Count} indices.", indices.Length);
            _ticker!.SetMode(indices, "quote");
        }
        if (others.Length > 0)
        {
            _logger.LogInformation("Setting mode:quote for {Count} others.", others.Length);
            _ticker!.SetMode(others, "quote");
        }
    }

    private void OnClose()
    {
        _logger.LogWarning("ZerodhaStreamer: WebSocket closed. Reconnect will be attempted by the SDK.");
        ScheduleAlert("⚠️ Zerodha WebSocket CLOSED. Reconnecting...");
    }

    private void OnError(string message)
    {
        _logger.LogError("ZerodhaStreamer: WebSocket error: {Message}", message);
        ScheduleAlert($"❌ Zerodha WebSocket ERROR: {message}");
    }

    private void ScheduleAlert(string message, int delayMs = 5000)
    {
        _pendingAlertCts?.Cancel();
        _pendingAlertCts = new System.Threading.CancellationTokenSource();
        var token = _pendingAlertCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                if (!token.IsCancellationRequested)
                {
                    SendTelegramAlert(message);
                    _isCurrentlyDisconnected = true;
                }
            }
            catch (TaskCanceledException) { /* Flicker resolved */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed in ScheduleAlert task.");
            }
        });
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
        var symbol = _tokensToSymbols.GetValueOrDefault(tick.InstrumentToken, tick.InstrumentToken.ToString());
        
        // Explicit override for indices to ensure Strategy Engine recognizes them
        if (tick.InstrumentToken == 256265) symbol = "NIFTY 50";
        if (tick.InstrumentToken == 260105) symbol = "NIFTY BANK";

        var normalized = new NormalizedTick
        {
            Symbol          = symbol,
            InstrumentToken = tick.InstrumentToken,
            Price           = tick.LastPrice,
            Volume          = tick.Volume,
            OpenInterest    = tick.OI,
            BidPrice        = tick.Bids?.Length > 0 ? tick.Bids[0].Price : 0m,
            AskPrice        = tick.Offers?.Length > 0 ? tick.Offers[0].Price : 0m,
            ExchangeTime    = (tick.Timestamp ?? DateTime.Now).ToUniversalTime(),
            ReceivedAt      = DateTime.UtcNow
        };
        
        Interlocked.Increment(ref _totalTicks);

        // 1. Publish raw tick to RabbitMQ
        _mqPublisher.PublishTick(normalized);

        // 2. Buffer raw tick for EOD KDB+ insert
        _tickBuffer.Add(normalized);

        // 3. Feed into OHLC aggregator (generates closed candles → MQ + CandleBuffer)
        _candleAggregator.ProcessTick(normalized);
    }

    private void SendTelegramAlert(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_tgBotToken) || string.IsNullOrWhiteSpace(_tgChatId)) return;
            string url = $"{_tgProviderUrl}/bot{_tgBotToken}/sendMessage?chat_id={_tgChatId}&text={Uri.EscapeDataString(message)}";
            _httpClient.GetAsync(url); // Fire and forget
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram alert.");
        }
    }
}