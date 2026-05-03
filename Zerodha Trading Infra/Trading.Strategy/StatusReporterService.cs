using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Trading.Strategy.Services;
using System.Text;
using Trading.Core.Models;

namespace Trading.Strategy;

public class StatusReporterService : BackgroundService
{
    private readonly IStrategicStateStore _stateStore;
    private readonly IEnumerable<IStrategy> _strategies;
    private readonly ILogger<StatusReporterService> _logger;
    private readonly HttpClient _httpClient = new();
    private readonly string _tgBotToken;
    private readonly string _tgChatId;
    private readonly string _tgProviderUrl;

    public StatusReporterService(
        IStrategicStateStore stateStore,
        IEnumerable<IStrategy> strategies,
        IConfiguration config,
        ILogger<StatusReporterService> logger)
    {
        _stateStore = stateStore;
        _strategies = strategies;
        _logger = logger;
        _tgBotToken = config["TelegramSettings:BotToken"] ?? string.Empty;
        _tgChatId = config["TelegramSettings:ChatId"] ?? string.Empty;
        _tgProviderUrl = config["TelegramSettings:ProviderUrl"] ?? "https://api.telegram.org";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StatusReporterService started. Waiting 10s for initial sync...");
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("StatusReporterService: Attempting to send report...");
                await SendReportAsync();
                _logger.LogInformation("StatusReporterService: Report cycle complete.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StatusReporterService: Failed to send status report.");
            }

            _logger.LogInformation("StatusReporterService: Sleeping for 30 minutes...");
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    private async Task SendReportAsync()
    {
        var sb = new StringBuilder();
        var metrics = _stateStore.GetSystemMetrics();
        var states = _stateStore.GetAllStates().OrderBy(x => x.Symbol).ToList();
        
        var now = DateTime.Now;
        sb.AppendLine($"📡 *Strategy Engine Report* — {now:HH:mm dd-MMM}");
        sb.AppendLine($"• WebSocket: {(metrics.WebSocketConnected ? "🟢 Connected" : "🔴 Disconnected")}");
        sb.AppendLine($"• Data: `{metrics.TicksProcessed:N0}` ticks processed");
        sb.AppendLine();

        // ── Market Levels per Symbol ──
        foreach (var s in states)
        {
            sb.AppendLine($"📈 *{s.Symbol}* (LTP: `{s.Ltp:N1}`)");
            
            if (s.Pivot > 0)
                sb.AppendLine($"  CPR: BC:`{s.Bc:N1}` P:`{s.Pivot:N1}` TC:`{s.Tc:N1}` {(s.IsVirginCpr ? "✨Virgin" : "")}");
            
            if (s.Pdh > 0)
                sb.AppendLine($"  PDH:`{s.Pdh:N1}` PDL:`{s.Pdl:N1}`");

            if (s.ThreeDayHigh > 0)
                sb.AppendLine($"  3D: H:`{s.ThreeDayHigh:N1}` L:`{s.ThreeDayLow:N1}`");

            sb.AppendLine($"  EMA50:`{s.Ema50:N0}` EMA200:`{s.Ema200:N0}` | Trend: *{s.Trend}*");
            
            if (!string.IsNullOrEmpty(s.StrangleStatus) && s.StrangleStatus != "Not Started")
            {
                sb.AppendLine($"  Strangle: {s.StrangleStatus}");
                if (!string.IsNullOrEmpty(s.StrangleLegs))
                    sb.AppendLine($"    _{s.StrangleLegs}_");
            }
            sb.AppendLine();
        }

        // ── Strategy Digest ──
        sb.AppendLine("── *Strategy Status* ──");
        foreach (var strategy in _strategies)
        {
            try
            {
                var digest = strategy.GetStatusDigest();
                if (!string.IsNullOrWhiteSpace(digest))
                {
                    sb.Append(digest);
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"⚠️ {strategy.GetType().Name}: Error getting status");
                _logger.LogError(ex, "Failed to get status digest from {Strategy}", strategy.GetType().Name);
            }
        }

        // ── Next Expected Triggers ──
        sb.AppendLine("── *What to Watch Next* ──");
        var tod = now.TimeOfDay;
        if (tod < new TimeSpan(9, 15, 0))
        {
            sb.AppendLine("• Pre-market. All strategies armed for 9:15 open.");
        }
        else if (tod < new TimeSpan(9, 30, 0))
        {
            sb.AppendLine("• ORB forming (9:15-9:30). Watch for range width.");
            sb.AppendLine("• VWAP building. Cross signals armed.");
        }
        else if (tod < new TimeSpan(12, 30, 0))
        {
            sb.AppendLine("• Prime window open. ORB breakout, VWAP cross, 3-Day & PDLH breakouts all scanning.");
            sb.AppendLine("• BigMove scanning for anomaly 5m candles (10:30-12:30).");
        }
        else if (tod < new TimeSpan(14, 30, 0))
        {
            sb.AppendLine("• BigMove window closed. VWAP, ORB, PDLH & 3-Day still active until 14:30.");
        }
        else if (tod < new TimeSpan(15, 15, 0))
        {
            sb.AppendLine("• Entry window closed. Any active positions will square off at 15:15.");
        }
        else
        {
            sb.AppendLine("• Market closed. All positions should be squared off.");
        }

        sb.AppendLine($"\n_Next update in 30 mins_");

        await PostToTelegram(sb.ToString());
    }

    private async Task PostToTelegram(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_tgBotToken) || string.IsNullOrWhiteSpace(_tgChatId)) 
            {
                _logger.LogWarning("StatusReporterService: Telegram Token or ChatId is missing.");
                return;
            }

            string url = $"{_tgProviderUrl}/bot{_tgBotToken}/sendMessage";
            
            var payload = new 
            { 
                chat_id = _tgChatId, 
                text = message, 
                parse_mode = "Markdown" 
            };
            
            var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            _logger.LogInformation("StatusReporterService: Sending JSON payload to Telegram...");
            var response = await _httpClient.PostAsync(url, content);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("StatusReporterService: Telegram API returned {Code}: {Error}", response.StatusCode, error);
                
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    _logger.LogWarning("StatusReporterService: Retrying in plain text (No Markdown)...");
                    var recoveryPayload = new { chat_id = _tgChatId, text = message };
                    var recoveryContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(recoveryPayload), Encoding.UTF8, "application/json");
                    await _httpClient.PostAsync(url, recoveryContent);
                }
            }
            else
            {
                _logger.LogInformation("StatusReporterService: Message sent successfully.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StatusReporterService: Exception in PostToTelegram.");
        }
    }
}
