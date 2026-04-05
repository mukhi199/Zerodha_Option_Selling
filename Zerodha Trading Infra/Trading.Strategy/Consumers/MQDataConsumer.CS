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
    private readonly ILogger<MQDataConsumer> _logger;

    private IConnection? _connection;
    private IModel? _channel;

    public MQDataConsumer(
        string host, int port, string user, string password,
        IEnumerable<IStrategy> strategies,
        ILogger<MQDataConsumer> logger)
    {
        _host = host;
        _port = port;
        _user = user;
        _password = password;
        _strategies = strategies;
        _logger = logger;
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

            var tickQueue = _channel.QueueDeclare().QueueName;
            _channel.QueueBind(tickQueue, "ticks-exchange", "tick.#");

            var candleQueue = _channel.QueueDeclare().QueueName;
            _channel.QueueBind(candleQueue, "candles-exchange", "candle.#");

            // Ticks Consumer
            var tickConsumer = new EventingBasicConsumer(_channel);
            tickConsumer.Received += (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                try
                {
                    var tick = JsonConvert.DeserializeObject<NormalizedTick>(message);
                    if (tick != null)
                    {
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

            _logger.LogInformation("MQDataConsumer started listening to RabbitMQ.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ in Strategy engine. Will retry automatically if using recovery.");
        }
    }
}
