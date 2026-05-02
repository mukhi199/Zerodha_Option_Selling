using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Trading.Core;
using Trading.KX.Services;
using Trading.MQ.Services;
using Trading.Streamer;
using Trading.Zerodha.Services;

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration ──────────────────────────────────────────────────────────
var config     = builder.Configuration;
var apiKey     = config["Zerodha:ApiKey"]      ?? throw new InvalidOperationException("Zerodha:ApiKey not configured");
var apiSecret  = config["Zerodha:ApiSecret"]   ?? throw new InvalidOperationException("Zerodha:ApiSecret not configured");
var tokenFile  = config["Zerodha:TokenFilePath"] ?? "access_token.txt";

var kdbHost    = config["Kdb:Host"]     ?? "localhost";
var kdbPort    = int.Parse(config["Kdb:Port"] ?? "5001");

var mqHost     = config["RabbitMQ:Host"]     ?? "localhost";
var mqPort     = int.Parse(config["RabbitMQ:Port"] ?? "5672");
var mqUser     = config["RabbitMQ:User"]     ?? "guest";
var mqPassword = config["RabbitMQ:Password"] ?? "guest";

// ── Core Singletons (shared buffers) ──────────────────────────────────────
builder.Services.AddSingleton<TickBuffer>();
builder.Services.AddSingleton<CandleBuffer>();

// ── MQ Layer ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<MQPublisher>(sp => new MQPublisher(
    mqHost, mqPort, mqUser, mqPassword,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MQPublisher>>()));

builder.Services.AddSingleton<IMQCandlePublisher, MQCandlePublisher>(sp => new MQCandlePublisher(
    mqHost, mqPort, mqUser, mqPassword,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MQCandlePublisher>>()));

builder.Services.AddSingleton<CandleAggregator>(sp => new CandleAggregator(
    sp.GetRequiredService<IMQCandlePublisher>(), // Changed to IMQCandlePublisher
    sp.GetRequiredService<CandleBuffer>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CandleAggregator>>()));

// ── KDB+ Layer ────────────────────────────────────────────────────────────
builder.Services.AddSingleton<KdbWriter>(sp => new KdbWriter(
    kdbHost, kdbPort,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KdbWriter>>()));

// EOD flush runs as a hosted background service
builder.Services.AddHostedService<EodFlushService>(sp => new EodFlushService(
    sp.GetRequiredService<KdbWriter>(),
    sp.GetRequiredService<TickBuffer>(),
    sp.GetRequiredService<CandleBuffer>(),
    sp.GetRequiredService<CandleAggregator>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EodFlushService>>()));

builder.Services.AddSingleton<KdbBackfillService>(sp => new KdbBackfillService(
    kdbHost, kdbPort,
    sp.GetRequiredService<MQPublisher>(),
    sp.GetRequiredService<IMQCandlePublisher>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KdbBackfillService>>()));

builder.Services.AddSingleton<KdbTester>(sp => new KdbTester(
    kdbHost, kdbPort,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KdbTester>>()));

// ── Zerodha Layer ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<ZerodhaAuthService>(sp => new ZerodhaAuthService(
    config,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ZerodhaAuthService>>()));

builder.Services.AddSingleton<InstrumentService>(sp => new InstrumentService(
    kdbHost, kdbPort, apiKey,
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InstrumentService>>()));

builder.Services.AddSingleton<ZerodhaStreamer>(sp => new ZerodhaStreamer(
    apiKey,
    sp.GetRequiredService<MQPublisher>(),
    sp.GetRequiredService<TickBuffer>(),
    sp.GetRequiredService<CandleAggregator>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ZerodhaStreamer>>(),
    config["TelegramSettings:BotToken"] ?? string.Empty,
    config["TelegramSettings:ChatId"] ?? string.Empty,
    config["TelegramSettings:ProviderUrl"] ?? "https://api.telegram.org"));

// ── Main Worker ───────────────────────────────────────────────────────────
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<MarketClosingService>();

// ── Build & Run ───────────────────────────────────────────────────────────
var host = builder.Build();
host.Run();