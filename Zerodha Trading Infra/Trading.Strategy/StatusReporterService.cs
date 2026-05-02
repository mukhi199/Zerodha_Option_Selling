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
    private readonly ILogger<StatusReporterService> _logger;
    private readonly HttpClient _httpClient = new();
    private readonly string _tgBotToken;
    private readonly string _tgChatId;
    private readonly string _tgProviderUrl;

    public StatusReporterService(
        IStrategicStateStore stateStore,
        IConfiguration config,
        ILogger<StatusReporterService> logger)
    {
        _stateStore = stateStore;
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
        
        _logger.LogInformation("StatusReporterService: Reporting on {Count} symbols. Metrics Ticks: {Ticks}", states.Count, metrics.TicksProcessed);

        sb.AppendLine("🛰 *Zerodha Ticker Status*");
        sb.AppendLine($"• WebSocket: {(metrics.WebSocketConnected ? "🟢 Connected" : "🔴 Disconnected")}");
        sb.AppendLine($"• Data Flow: 🟢 Active (`{metrics.TicksProcessed:N0}` ticks)");

        foreach (var s in states)
        {
            sb.AppendLine($"\n📈 *{s.Symbol}* (LTP: `{s.Ltp:N1}`)");
            
            // CPR & Virgin Status
            if (s.Pivot > 0)
            {
                sb.AppendLine($"• CPR: BC:`{s.Bc:N1}` | P:`{s.Pivot:N1}` | TC:`{s.Tc:N1}` ({(s.IsVirginCpr ? "✨ Virgin" : "🚫 Touched")})");
            }
            
            // PDH / PDL
            if (s.Pdh > 0)
            {
                sb.AppendLine($"• Prev Day: High:`{s.Pdh:N1}` | Low:`{s.Pdl:N1}`");
            }

            // 3-Day Breakout Range
            if (s.ThreeDayHigh > 0)
            {
                sb.AppendLine($"• 3-Day: High:`{s.ThreeDayHigh:N1}` | Low:`{s.ThreeDayLow:N1}`");
            }

            // Trend & EMA
            sb.AppendLine($"• Trend: *{s.Trend}* (EMA50: `{s.Ema50:N0}` / EMA200: `{s.Ema200:N0}`)");
            
            // Strangle Info
            if (!string.IsNullOrEmpty(s.StrangleStatus) && s.StrangleStatus != "Not Started")
            {
                sb.AppendLine($"• Strangle: {s.StrangleStatus}");
                if (!string.IsNullOrEmpty(s.StrangleLegs))
                    sb.AppendLine($"  _{s.StrangleLegs}_");
            }
        }

        sb.AppendLine("\n_Next automated update in 30 mins_");

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
