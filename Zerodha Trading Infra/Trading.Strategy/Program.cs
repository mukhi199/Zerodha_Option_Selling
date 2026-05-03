using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Trading.Strategy;
using Trading.Strategy.Services;
using Trading.Strategy.Consumers;
using Trading.Zerodha.Services;
using Microsoft.EntityFrameworkCore;
using Trading.Core.Data;
using System.Text.Json;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

var config = builder.Configuration;
var apiKey = config["Zerodha:ApiKey"] ?? string.Empty;
var apiSecret = config["Zerodha:ApiSecret"] ?? string.Empty;
var tokenPath = config["Zerodha:TokenFilePath"] ?? "access_token.json";

// 1. Zerodha Config
builder.Services.AddSingleton(sp => new ZerodhaAuthService(config, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ZerodhaAuthService>>()));

// 2. Order Execution Service
builder.Services.AddSingleton<OrderExecutionService>(sp => 
    new OrderExecutionService(apiKey, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OrderExecutionService>>()));
        
builder.Services.AddSingleton<IOrderService>(sp => sp.GetRequiredService<OrderExecutionService>());

builder.Services.AddSingleton<TechnicalIndicatorsTracker>();
builder.Services.AddSingleton<MovingAverageService>();
builder.Services.AddSingleton<Trading.Zerodha.Services.INfoSymbolMaster, Trading.Zerodha.Services.NfoSymbolMaster>(sp => 
    new Trading.Zerodha.Services.NfoSymbolMaster(apiKey, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Trading.Zerodha.Services.NfoSymbolMaster>>()));

builder.Services.AddSingleton<IStrategicStateStore, StrategicStateStore>();

// 3. Register Strategies
builder.Services.AddDbContext<AppDbContext>(options => 
    options.UseSqlite("Data Source=TradingData.db"), ServiceLifetime.Singleton);

builder.Services.AddSingleton<IStrategy, SampleStrategy>();
builder.Services.AddSingleton<IStrategy, UltimateCombinedStrategy>();
builder.Services.AddSingleton<IStrategy, CprBounceStrategy>();
builder.Services.AddSingleton<IStrategy, RsiSmoothedStrategy>();
builder.Services.AddSingleton<IStrategy, Strangle920Strategy>();
builder.Services.AddSingleton<IStrategy, LevelStrangleStrategy>();
builder.Services.AddSingleton<IStrategy, IntradayBigMoveStrategy>();
builder.Services.AddSingleton<IStrategy, VwapStrategy>();
builder.Services.AddSingleton<IStrategy, StandardOrbStrategy>();


// 4. RabbitMQ Consumer
var mqHost = config["RabbitMQ:Host"] ?? "localhost";
var mqPort = int.TryParse(config["RabbitMQ:Port"], out var p) ? p : 5672;
var mqUser = config["RabbitMQ:User"] ?? "guest";
var mqPassword = config["RabbitMQ:Password"] ?? "guest";

builder.Services.AddHostedService<MQDataConsumer>(sp => new MQDataConsumer(
    sp.GetServices<IStrategy>(),
    sp.GetRequiredService<IStrategicStateStore>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MQDataConsumer>>(),
    config));


// 5. Worker
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<StatusReporterService>();
builder.Services.AddHostedService<MarketClosingService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// 6. Webhook Endpoint
app.MapPost("/zerodha/postback", async (Microsoft.AspNetCore.Http.HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var payload = JsonSerializer.Deserialize<JsonElement>(body);

    var strategy = app.Services.GetServices<IStrategy>().OfType<UltimateCombinedStrategy>().FirstOrDefault();
    if (strategy != null)
    {
        strategy.ProcessWebhook(payload);
    }
    
    return Microsoft.AspNetCore.Http.Results.Ok();
});

// 7. Dashboard API
app.MapGet("/api/state", (IStrategicStateStore stateStore) => 
{
    return Microsoft.AspNetCore.Http.Results.Ok(new 
    {
        Metrics = stateStore.GetSystemMetrics(),
        States = stateStore.GetAllStates().OrderBy(s => s.Symbol)
    });
});

app.MapPost("/api/override", (IStrategicStateStore stateStore, JsonElement payload) =>
{
    if (payload.TryGetProperty("symbol", out var sym))
    {
        string symbol = sym.GetString() ?? "";
        
        stateStore.UpdateSymbolState(symbol, s => 
        {
            if (payload.TryGetProperty("signal", out var signal))
                s.ManualOverrideSignal = signal.GetString() ?? "None";
            
            if (payload.TryGetProperty("level", out var level))
                s.ManualTriggerLevel = level.GetDecimal();
            
            if (payload.TryGetProperty("side", out var side))
                s.ManualTriggerSide = side.GetString() ?? "None";
            
            if (payload.TryGetProperty("sl", out var sl))
                s.ManualStopLoss = sl.GetDecimal();
        });

        return Microsoft.AspNetCore.Http.Results.Ok(new { success = true, symbol });
    }
    return Microsoft.AspNetCore.Http.Results.BadRequest("Invalid payload");
});

app.Run();
