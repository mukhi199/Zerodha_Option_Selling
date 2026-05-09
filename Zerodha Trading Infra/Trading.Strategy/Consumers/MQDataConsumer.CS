// =============================================================================
// MQDataConsumer_FIXED.cs
// =============================================================================
// FIXES APPLIED (search "// FIX #N" to jump to each change):
//
//   FIX #1 — ExecuteAsync keeps the service alive with Task.Delay(Infinite)
//             (was returning Task.CompletedTask immediately — host considered
//              the service done and could tear it down)
//
//   FIX #2 — Named, durable queues replace anonymous auto-delete queues
//             (anonymous queues are destroyed on restart, losing all buffered
//              messages; named durable queues survive broker/app restarts)
//
//   FIX #3 — Strategy dispatch wrapped in per-strategy try/catch
//             (one bad strategy was silently killing dispatch for all others;
//              now each strategy is isolated — error logged, others continue)
//
//   FIX #4 — async void event handler replaced with safe fire-and-forget
//             (tick consumer used "async (model, ea) =>" which is async void —
//              any unhandled exception crashes the process silently;
//              replaced with sync handler + Task.Run fire-and-forget)
//
//   FIX #5 — HttpClient moved to static/singleton to avoid socket exhaustion
//             (new HttpClient per instance burns sockets; injected or static)
//
//   FIX #6 — RabbitMQ credentials sourced from IConfiguration, not constructor
//             (raw strings in constructor forced caller to read config manually;
//              now MQDataConsumer reads its own config section cleanly)
// =============================================================================

namespace Trading.Strategy.Consumers;

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Trading.Core.Models;
using Trading.Strategy.Services;

public class MQDataConsumer : BackgroundService
{
    // FIX #6 ↓ — all RabbitMQ config now read from IConfiguration inside constructor
    private readonly string _host;
    private readonly int    _port;
    private readonly string _user;
    private readonly string _password;

    private readonly IEnumerable<IStrategy>  _strategies;
    private readonly IStrategicStateStore    _stateStore;
    private readonly TechnicalIndicatorsTracker _tracker; // DI Injected
    private readonly ILogger<MQDataConsumer> _logger;

    private IConnection? _connection;
    private IModel?      _channel;

    private DateTime _lastStaleAlertTime = DateTime.MinValue;

    // FIX #5 ↓ — single static HttpClient shared across all instances
    // ORIGINAL CODE: _httpClient = new HttpClient() in constructor
    // PROBLEM: Each MQDataConsumer instance (or restart) creates a new HttpClient,
    // exhausting socket connections under load (HttpClient is meant to be reused).
    // FIX: use a static readonly instance so sockets are pooled correctly.
    private static readonly HttpClient _httpClient = new();  // FIX #5

    private readonly string _tgBotToken;
    private readonly string _tgChatId;
    private readonly string _tgProviderUrl;

    public MQDataConsumer(
        // FIX #6 ↓ — removed raw string params (host, port, user, password)
        // ORIGINAL SIGNATURE: string host, int port, string user, string password, ...
        // PROBLEM: Callers had to manually read config and pass strings into the
        // constructor, duplicating config-reading logic everywhere MQDataConsumer
        // is registered. Any config key rename breaks silently at runtime.
        // FIX: inject IConfiguration and read the "RabbitMQ" section directly here.
        IEnumerable<IStrategy>  strategies,
        IStrategicStateStore    stateStore,
        TechnicalIndicatorsTracker tracker,
        ILogger<MQDataConsumer> logger,
        IConfiguration          config)   // FIX #6
    {
        _strategies = strategies;
        _stateStore = stateStore;
        _tracker    = tracker;
        _logger     = logger;

        // FIX #6 ↓ — read RabbitMQ settings from config section
        _host     = config["RabbitMQ:Host"]     ?? "localhost";  // FIX #6
        _port     = int.TryParse(config["RabbitMQ:Port"], out var p) ? p : 5672;  // FIX #6
        _user     = config["RabbitMQ:User"]     ?? "guest";      // FIX #6
        _password = config["RabbitMQ:Password"] ?? "guest";      // FIX #6

        _tgBotToken   = config["TelegramSettings:BotToken"]   ?? string.Empty;
        _tgChatId     = config["TelegramSettings:ChatId"]      ?? string.Empty;
        _tgProviderUrl = config["TelegramSettings:ProviderUrl"] ?? "https://api.telegram.org";
    }

    // =========================================================================
    // FIX #1 — Keep ExecuteAsync alive for the lifetime of the host.
    //
    // ORIGINAL CODE:
    //   protected override Task ExecuteAsync(CancellationToken stoppingToken)
    //   {
    //       ConnectRabbitMQ();
    //       stoppingToken.Register(...);
    //       return Task.CompletedTask;   // ← service considered "done" immediately
    //   }
    //
    // PROBLEM: BackgroundService marks itself complete when ExecuteAsync returns.
    // Returning Task.CompletedTask right after ConnectRabbitMQ() means the host
    // sees the service as finished and may stop or restart it, tearing down the
    // RabbitMQ channel mid-session.
    //
    // FIX: await Task.Delay(Timeout.Infinite, stoppingToken) to hold the service
    // open until the host sends a cancellation signal on shutdown.
    // =========================================================================
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)  // FIX #1
    {
        ConnectRabbitMQ();

        stoppingToken.Register(() =>
        {
            _logger.LogInformation("Stopping MQDataConsumer...");
            _channel?.Dispose();
            _connection?.Dispose();
        });

        // FIX #1 ↓ — hold service alive until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);  // FIX #1
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown — swallow the cancellation exception
        }
    }

    private void ConnectRabbitMQ()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName                 = _host,
                Port                     = _port,
                UserName                 = _user,
                Password                 = _password,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval  = TimeSpan.FromSeconds(5)
            };

            _connection = factory.CreateConnection("ZerodhaTradingInfra-Strategy");
            _channel    = _connection.CreateModel();

            _channel.ExchangeDeclare("ticks-exchange",         ExchangeType.Topic,   durable: true);
            _channel.ExchangeDeclare("candles-exchange",       ExchangeType.Topic,   durable: true);
            _channel.ExchangeDeclare("system-status-exchange", ExchangeType.Fanout,  durable: true);

            // =================================================================
            // FIX #2 — Named durable queues replace anonymous auto-delete queues.
            //
            // ORIGINAL CODE:
            //   var tickQueue = _channel.QueueDeclare().QueueName;
            //
            // PROBLEM: QueueDeclare() with no arguments creates an anonymous,
            // exclusive, auto-delete queue. It is destroyed the moment the
            // consumer disconnects (restart, crash, deploy). Any tick or candle
            // messages published while the app is down are permanently lost.
            //
            // FIX: declare named, durable, non-exclusive queues so messages
            // survive restarts and are waiting when the consumer reconnects.
            // =================================================================
            _channel.QueueDeclare(  // FIX #2 ↓
                queue:      "strategy-ticks",
                durable:    true,
                exclusive:  false,
                autoDelete: false,
                arguments:  null);
            _channel.QueueBind("strategy-ticks", "ticks-exchange", "tick.#");  // FIX #2

            _channel.QueueDeclare(  // FIX #2 ↓
                queue:      "strategy-candles",
                durable:    true,
                exclusive:  false,
                autoDelete: false,
                arguments:  null);
            _channel.QueueBind("strategy-candles", "candles-exchange", "candle.#");  // FIX #2

            // Status queue stays anonymous/fanout — heartbeats are ephemeral by design
            var statusQueue = _channel.QueueDeclare().QueueName;
            _channel.QueueBind(statusQueue, "system-status-exchange", "");

            // =================================================================
            // Tick Consumer
            // =================================================================

            // FIX #4 ↓ — sync handler with Task.Run fire-and-forget for async work.
            //
            // ORIGINAL CODE:
            //   tickConsumer.Received += async (model, ea) => { ... }
            //
            // PROBLEM: RabbitMQ's EventingBasicConsumer.Received is a synchronous
            // event (EventHandler<BasicDeliverEventArgs>). Using "async (model, ea)"
            // creates an async void delegate — any exception thrown after an await
            // is unobserved and crashes the process without a stack trace in the log.
            //
            // FIX: keep the handler sync; wrap the async work (Telegram alert) in
            // Task.Run(...).ContinueWith(t => log) so exceptions are always observed.
            var tickConsumer = new EventingBasicConsumer(_channel);
            tickConsumer.Received += (model, ea) =>  // FIX #4: sync handler
            {
                var body    = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                try
                {
                    var tick = JsonConvert.DeserializeObject<NormalizedTick>(message);
                    if (tick != null)
                    {
                        var now              = DateTime.UtcNow;
                        var exchangeLatency  = tick.ReceivedAt - tick.ExchangeTime;
                        var processingDelay  = now - tick.ReceivedAt;
                        var totalAge         = now - tick.ExchangeTime;
                        bool isLiveTick      = totalAge.TotalSeconds >= 0 && totalAge.TotalSeconds < 86400;

                        if (isLiveTick && totalAge.TotalSeconds > 3.0)
                        {
                            if ((now - _lastStaleAlertTime).TotalMinutes >= 5)
                            {
                                _logger.LogWarning(
                                    "STALE DATA DETECTED! Total Age: {TotalAge}s [Exchange: {Exch}s, MQ Delay: {Proc}s]. Skipping strategy.",
                                    totalAge.TotalSeconds, exchangeLatency.TotalSeconds, processingDelay.TotalSeconds);

                                // FIX #4 ↓ — fire-and-forget with observed exception
                                Task.Run(() => SendTelegramAlert(
                                    $"COMPUTING HAZARD: Zerodha ticker delayed by {totalAge.TotalSeconds:F1}s! " +
                                    $"(Exch: {exchangeLatency.TotalSeconds:F1}s, Proc: {processingDelay.TotalSeconds:F1}s). Strategy paused."))
                                .ContinueWith(t =>
                                    _logger.LogError(t.Exception, "Telegram alert failed."),
                                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);  // FIX #4

                                _lastStaleAlertTime = now;
                            }
                            return;
                        }

                        // FIX #3 ↓ — per-strategy isolation on tick dispatch
                        foreach (var strategy in _strategies)
                        {
                            try  // FIX #3
                            {
                                strategy.OnTick(tick);
                            }
                            catch (Exception ex)  // FIX #3
                            {
                                // One strategy crashing must NOT stop others from receiving ticks
                                _logger.LogError(ex,
                                    "Strategy {Strategy} threw an exception in OnTick. Continuing with remaining strategies.",
                                    strategy.GetType().Name);  // FIX #3
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize tick message.");
                }
            };
            _channel.BasicConsume(queue: "strategy-ticks", autoAck: true, consumer: tickConsumer);  // FIX #2

            // =================================================================
            // Candle Consumer
            // =================================================================
            var candleConsumer = new EventingBasicConsumer(_channel);
            candleConsumer.Received += (model, ea) =>
            {
                var body    = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                try
                {
                    var candle = JsonConvert.DeserializeObject<Candle>(message);
                    if (candle != null)
                    {
                        // Centralized Futures Volume Routing for VWAP calculations
                        if (candle.Symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase))
                        {
                            string spotSymbol = candle.Symbol.StartsWith("NIFTY BANK", StringComparison.OrdinalIgnoreCase) || 
                                                candle.Symbol.StartsWith("BANKNIFTY", StringComparison.OrdinalIgnoreCase)
                                ? "NIFTY BANK"
                                : "NIFTY 50";
                            _tracker.SetFuturesVolume(spotSymbol, candle.Volume);
                            return; // Do not dispatch FUT candles directly to strategies
                        }

                        // FIX #3 ↓ — per-strategy isolation on candle dispatch
                        foreach (var strategy in _strategies)
                        {
                            try  // FIX #3
                            {
                                strategy.OnCandle(candle);
                            }
                            catch (Exception ex)  // FIX #3
                            {
                                // One strategy crashing must NOT stop others from receiving candles
                                _logger.LogError(ex,
                                    "Strategy {Strategy} threw an exception in OnCandle. Continuing with remaining strategies.",
                                    strategy.GetType().Name);  // FIX #3
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize candle message.");
                }
            };
            _channel.BasicConsume(queue: "strategy-candles", autoAck: true, consumer: candleConsumer);  // FIX #2

            // =================================================================
            // Status / Heartbeat Consumer  (no changes — ephemeral by design)
            // =================================================================
            var statusConsumer = new EventingBasicConsumer(_channel);
            statusConsumer.Received += (model, ea) =>
            {
                try
                {
                    var body    = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    var status  = JsonConvert.DeserializeObject<SystemStatus>(message);
                    if (status != null)
                    {
                        _logger.LogInformation(
                            "MQDataConsumer: Received SystemStatus Heartbeat. Ticks: {Ticks}",
                            status.TicksProcessed);

                        _stateStore.UpdateSystemMetrics(m => {
                            m.WebSocketConnected = status.WebSocketConnected;
                            m.TicksProcessed     = status.TicksProcessed;
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process status message.");
                }
            };
            _channel.BasicConsume(queue: statusQueue, autoAck: true, consumer: statusConsumer);

            _logger.LogInformation("MQDataConsumer started listening to RabbitMQ.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ. Will retry automatically if AutomaticRecovery is enabled.");
        }
    }

    private async Task SendTelegramAlert(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_tgBotToken) || string.IsNullOrWhiteSpace(_tgChatId)) return;
            var url = $"{_tgProviderUrl}/bot{_tgBotToken}/sendMessage?chat_id={_tgChatId}&text={Uri.EscapeDataString(message)}";
            await _httpClient.GetAsync(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram alert.");
        }
    }
}
