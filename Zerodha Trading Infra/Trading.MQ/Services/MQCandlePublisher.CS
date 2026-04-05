using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using Trading.Core.Models;

namespace Trading.MQ.Services;

/// <summary>
/// Publishes closed OHLC candles to RabbitMQ candles-exchange (topic).
/// Routing key: candle.{SYMBOL}.{INTERVAL}m   e.g. candle.RELIANCE.1m
/// Shares the same connection factory pattern.
/// </summary>
public interface IMQCandlePublisher
{
    void PublishCandle(Candle candle);
}

/// <summary>
/// Publishes closed OHLC candles to RabbitMQ candles-exchange (topic).
/// Routing key: candle.{SYMBOL}.{INTERVAL}m   e.g. candle.RELIANCE.1m
/// Shares the same connection factory pattern.
/// </summary>
public class MQCandlePublisher : IMQCandlePublisher, IDisposable
{
    private readonly string _host;
    private readonly int    _port;
    private readonly string _user;
    private readonly string _password;
    private readonly ILogger<MQCandlePublisher> _logger;

    private IConnection? _connection;
    private IModel?      _channel;
    private readonly object _lock = new();

    public const string CandlesExchange = "candles-exchange";

    public MQCandlePublisher(
        string host,
        int port,
        string user,
        string password,
        ILogger<MQCandlePublisher> logger)
    {
        _host     = host;
        _port     = port;
        _user     = user;
        _password = password;
        _logger   = logger;

        EnsureConnected();
    }

    /// <summary>
    /// Publishes a closed candle to candles-exchange.
    /// </summary>
    public void PublishCandle(Candle candle)
    {
        var routingKey = $"candle.{candle.Symbol}.{candle.IntervalMinutes}m";
        lock (_lock)
        {
            try
            {
                EnsureConnected();
                var json = JsonConvert.SerializeObject(candle);
                var body = Encoding.UTF8.GetBytes(json);
                _channel!.BasicPublish(CandlesExchange, routingKey, basicProperties: null, body: body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQCandlePublisher: publish failed for {Key}", routingKey);
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

            _connection = factory.CreateConnection("ZerodhaTradingInfra-Candles");
            _channel    = _connection.CreateModel();

            _channel.ExchangeDeclare(CandlesExchange, ExchangeType.Topic, durable: true);

            _logger.LogInformation("MQCandlePublisher connected to RabbitMQ.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQCandlePublisher: RabbitMQ connection failed.");
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
