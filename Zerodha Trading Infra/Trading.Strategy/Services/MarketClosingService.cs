using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Trading.Strategy.Services;

public class MarketClosingService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MarketClosingService> _logger;

    public MarketClosingService(IHostApplicationLifetime lifetime, ILogger<MarketClosingService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketClosingService started. Monitoring for 15:35 IST market close.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            // Market closes at 15:35 IST
            if (now.Hour == 15 && now.Minute >= 35)
            {
                _logger.LogWarning("Market Closing Service: 15:35 IST reached. Triggering system shutdown...");
                _lifetime.StopApplication();
                break;
            }

            // Check every 35 seconds
            await Task.Delay(TimeSpan.FromSeconds(35), stoppingToken);
        }
    }
}
