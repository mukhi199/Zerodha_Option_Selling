using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Trading.Strategy;
using Trading.Strategy.Services;
using Trading.Strategy.Consumers;
using Trading.Zerodha.Services;
using Microsoft.EntityFrameworkCore;
using Trading.Core.Data;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((hostContext, services) =>
    {
        var config = hostContext.Configuration;

        // 1. Zerodha Config
        var apiKey = config["Zerodha:ApiKey"] ?? string.Empty;
        var apiSecret = config["Zerodha:ApiSecret"] ?? string.Empty;
        var tokenPath = config["Zerodha:TokenFilePath"] ?? "access_token.json";
        
        // Register Auth Service to fetch access token
        services.AddSingleton(sp => new ZerodhaAuthService(apiKey, apiSecret, tokenPath, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ZerodhaAuthService>>()));

        // 2. Order Execution Service
        // We use a factory since it needs the access token, but the token might be fetched at runtime.
        // For simplicity, we initialize it empty here and set the token inside the OrderExecutionService 
        // OR we can make the OrderExecutionService fetch from AuthService directly.
        // But since we designed it to take accessToken in constructor, let's inject it via a provider if needed.
        // Actually, let's just make OrderExecutionService resolve the token at runtime to avoid circular injection.
        
        // Wait, a better approach is to let Worker fetch token, and then initialize OrderExecutionService.
        // I will change OrderExecutionService to accept the token dynamically.
        // Let's modify OrderExecutionService via file replacement after this.
        services.AddSingleton<OrderExecutionService>(sp => 
            new OrderExecutionService(apiKey, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OrderExecutionService>>()));

        services.AddSingleton<TechnicalIndicatorsTracker>();
        services.AddSingleton<MovingAverageService>();

        // 3. Register Strategies
        services.AddDbContext<AppDbContext>(options => 
            options.UseSqlite("Data Source=TradingData.db"));

        services.AddSingleton<IStrategy, SampleStrategy>();
        services.AddSingleton<IStrategy, ThreeDayBreakoutStrategy>();
        services.AddSingleton<IStrategy, CprBounceStrategy>();
        services.AddSingleton<IStrategy, RsiSmoothedStrategy>();

        // 4. RabbitMQ Consumer
        var mqHost = config["RabbitMQ:Host"] ?? "localhost";
        var mqPort = int.TryParse(config["RabbitMQ:Port"], out var p) ? p : 5672;
        var mqUser = config["RabbitMQ:User"] ?? "guest";
        var mqPassword = config["RabbitMQ:Password"] ?? "guest";

        services.AddHostedService<MQDataConsumer>(sp => new MQDataConsumer(
            mqHost, mqPort, mqUser, mqPassword,
            sp.GetServices<IStrategy>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MQDataConsumer>>()));

        // 5. Worker
        services.AddHostedService<Worker>();
    });

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

host.Run();
