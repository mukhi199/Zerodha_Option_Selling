namespace Trading.Strategy.Consumers;

using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Trading.Core.Models;
using Trading.Strategy.Services;

public class MQDataConsumer : BackgroundService
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly IEnumerable<IStrategy> _strategies;
    private readonly IStrategicStateStore _stateStore;
    private readonly ILogger<MQDataConsumer> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private DateTime _lastStaleAlertTime = DateTime.MinValue;
    private readonly HttpClient _httpClient;
    private readonly string _tgBotToken;
    private readonly string _tgChatId;
    private readonly string _tgProviderUrl;

    public MQDataConsumer(
        string host, int port, string user, string password,
        IEnumerable<IStrategy> strategies,
        IStrategicStateStore stateStore,
        ILogger<MQDataConsumer> logger,
        Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _host = host;
        _port = port;
        _user = user;
        _password = password;
        _strategies = strategies;
        _stateStore = stateStore;
        _logger = logger;
        _httpClient = new HttpClient();
        _tgBotToken = config["TelegramSettings:BotToken"] ?? string.Empty;
        _tgChatId = config["TelegramSettings:ChatId"] ?? string.Empty;
        _tgProviderUrl = config["TelegramSettings:ProviderUrl"] ?? "https://api.telegram.org";
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ConnectRabbitMQ();

        stoppingToken.Register(() =>
        {
            _logger.LogInformation("Stopping MQDataConsumer...");
            _channel?.Dispose();
            _connection?.Dispose();
        });

        return Task.CompletedTask;
    }

    private void ConnectRabbitMQ()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _host,
                Port = _port,
                UserName = _user,
                Password = _password,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
            };

            _connection = factory.CreateConnection("ZerodhaTradingInfra-Strategy");
            _channel = _connection.CreateModel();

            _channel.ExchangeDeclare("ticks-exchange", ExchangeType.Topic, durable: true);
            _channel.ExchangeDeclare("candles-exchange", ExchangeType.Topic, durable: true);
            _channel.ExchangeDeclare("system-status-exchange", ExchangeType.Fanout, durable: true);

            var tickQueue = _channel.QueueDeclare().QueueName;
            _channel.QueueBind(tickQueue, "ticks-exchange", "tick.#");

            var candleQueue = _channel.QueueDeclare().QueueName;
            _channel.QueueBind(candleQueue, "candles-exchange", "candle.#");

            // Ticks Consumer
            var tickConsumer = new EventingBasicConsumer(_channel);
            tickConsumer.Received += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                try
                {
                    var tick = JsonConvert.DeserializeObject<NormalizedTick>(message);
                    if (tick != null)
                    {
                        // Only run latency check on live ticks (ExchangeTime must be a recent valid UTC timestamp)
                        // Historical/backfill ticks often have epoch-like dates — skip latency check for those.
                        var now = DateTime.UtcNow;
                        var exchangeLatency = tick.ReceivedAt - tick.ExchangeTime;
                        var processingDelay = now - tick.ReceivedAt;
                        var totalAge = now - tick.ExchangeTime;

                        bool isLiveTick = totalAge.TotalSeconds >= 0 && totalAge.TotalSeconds < 86400;

                        if (isLiveTick && totalAge.TotalSeconds > 3.0) 
                        {
                            if ((now - _lastStaleAlertTime).TotalMinutes >= 5) 
                            {
                                _logger.LogWarning("STALE DATA DETECTED! Total Age: {TotalAge}s [Exchange Latency: {Exch}s, MQ Processing Delay: {Proc}s]. Skipping strategy execution.", 
                                    totalAge.TotalSeconds, exchangeLatency.TotalSeconds, processingDelay.TotalSeconds);
                                
                                await SendTelegramAlert($"COMPUTING HAZARD: Zerodha ticker delayed by {totalAge.TotalSeconds:F1}s! \n(Exch: {exchangeLatency.TotalSeconds:F1}s, Proc: {processingDelay.TotalSeconds:F1}s). Strategy paused.");
                                _lastStaleAlertTime = now;
                            }
                            return; 
                        }

                        foreach (var strategy in _strategies)
                        {
                            strategy.OnTick(tick);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize tick.");
                }
            };
            _channel.BasicConsume(queue: tickQueue, autoAck: true, consumer: tickConsumer);

            // Candles Consumer
            var candleConsumer = new EventingBasicConsumer(_channel);
            candleConsumer.Received += (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                try
                {
                    var candle = JsonConvert.DeserializeObject<Candle>(message);
                    if (candle != null)
                    {
                        foreach (var strategy in _strategies)
                        {
                            strategy.OnCandle(candle);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize candle.");
                }
            };
            _channel.BasicConsume(queue: candleQueue, autoAck: true, consumer: candleConsumer);
            
            // Status Consumer
            var statusQueue = _channel.QueueDeclare().QueueName;
            _channel.QueueBind(statusQueue, "system-status-exchange", "");
            var statusConsumer = new EventingBasicConsumer(_channel);
            statusConsumer.Received += (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    var status = JsonConvert.DeserializeObject<SystemStatus>(message);
                    if (status != null)
                    {
                        _logger.LogInformation("MQDataConsumer: Received SystemStatus Heartbeat. Ticks: {Ticks}", status.TicksProcessed);
                        _stateStore.UpdateSystemMetrics(m => {
                            m.WebSocketConnected = status.WebSocketConnected;
                            m.TicksProcessed = status.TicksProcessed;
                        });
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to process status message."); }
            };
            _channel.BasicConsume(queue: statusQueue, autoAck: true, consumer: statusConsumer);

            _logger.LogInformation("MQDataConsumer started listening to RabbitMQ.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ in Strategy engine. Will retry automatically if using recovery.");
        }
    }

    private async Task SendTelegramAlert(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_tgBotToken) || string.IsNullOrWhiteSpace(_tgChatId)) return;
            string url = $"{_tgProviderUrl}/bot{_tgBotToken}/sendMessage?chat_id={_tgChatId}&text={Uri.EscapeDataString(message)}";
            await _httpClient.GetAsync(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram alert.");
        }
    }
}
