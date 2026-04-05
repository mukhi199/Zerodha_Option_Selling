using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using Trading.Core.Models;

namespace Trading.MQ.Services;

/// <summary>
/// Publishes raw normalized ticks to the RabbitMQ ticks-exchange (topic).
/// Routing key: tick.{EXCHANGE}.{SYMBOL}  e.g. tick.NSE.RELIANCE
/// Uses a persistent channel and reconnects on error.
/// </summary>
public class MQPublisher : IDisposable
{
    private readonly string _host;
    private readonly int    _port;
    private readonly string _user;
    private readonly string _password;
    private readonly ILogger<MQPublisher> _logger;

    private IConnection? _connection;
    private IModel?      _channel;
    private readonly object _lock = new();

    public const string TicksExchange = "ticks-exchange";

    public MQPublisher(
        string host,
        int port,
        string user,
        string password,
        ILogger<MQPublisher> logger)
    {
        _host     = host;
        _port     = port;
        _user     = user;
        _password = password;
        _logger   = logger;

        EnsureConnected();
    }

    /// <summary>
    /// Publishes a normalized tick to the ticks-exchange.
    /// </summary>
    public void PublishTick(NormalizedTick tick)
    {
        var routingKey = $"tick.{tick.Symbol}";
        Publish(TicksExchange, routingKey, tick);
    }

    // ─────────────────── internals ───────────────────

    private void Publish(string exchange, string routingKey, object payload)
    {
        lock (_lock)
        {
            try
            {
                EnsureConnected();
                var json = JsonConvert.SerializeObject(payload);
                var body = Encoding.UTF8.GetBytes(json);
                _channel!.BasicPublish(exchange, routingKey, basicProperties: null, body: body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQPublisher: Failed to publish to {Exchange}/{Key}. Reconnecting...", exchange, routingKey);
                Reconnect();
            }
        }
    }

    private void EnsureConnected()
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
            return;

        Reconnect();
    }

    private void Reconnect()
    {
        try
        {
            _channel?.Dispose();
            _connection?.Dispose();

            var factory = new ConnectionFactory
            {
                HostName = _host,
                Port     = _port,
                UserName = _user,
                Password = _password,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval  = TimeSpan.FromSeconds(5)
            };

            _connection = factory.CreateConnection("ZerodhaTradingInfra");
            _channel    = _connection.CreateModel();

            // Declare both exchanges (idempotent)
            _channel.ExchangeDeclare(TicksExchange, ExchangeType.Topic, durable: true);

            _logger.LogInformation("MQPublisher connected to RabbitMQ at {Host}:{Port}", _host, _port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQPublisher: RabbitMQ connection failed.");
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
