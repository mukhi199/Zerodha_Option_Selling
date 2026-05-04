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
    private readonly TechnicalIndicatorsTracker _tracker;
    private readonly ILogger<StatusReporterService> _logger;
    private readonly HttpClient _httpClient = new();
    private readonly string _tgBotToken;
    private readonly string _tgChatId;
    private readonly string _tgProviderUrl;
    private int _reportCount = 0;

    public StatusReporterService(
        IStrategicStateStore stateStore,
        IEnumerable<IStrategy> strategies,
        TechnicalIndicatorsTracker tracker,
        IConfiguration config,
        ILogger<StatusReporterService> logger)
    {
        _stateStore = stateStore;
        _strategies = strategies;
        _tracker = tracker;
        _logger = logger;
        _tgBotToken = config["TelegramSettings:BotToken"] ?? string.Empty;
        _tgChatId = config["TelegramSettings:ChatId"] ?? string.Empty;
        _tgProviderUrl = config["TelegramSettings:ProviderUrl"] ?? "https://api.telegram.org";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StatusReporterService started. Waiting 60s for indicator warmup...");
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow.AddMinutes(330); // IST Offset
                var tod = now.TimeOfDay;
                
                if (tod >= new TimeSpan(9, 0, 0) && tod <= new TimeSpan(15, 30, 0))
                {
                    _reportCount++;
                    _logger.LogInformation("StatusReporterService: Sending report #{Count} at {Time}...", _reportCount, now);
                    await SendReportAsync(now);
                    _logger.LogInformation("StatusReporterService: Report #{Count} sent.", _reportCount);
                }
                else
                {
                    _logger.LogInformation("StatusReporterService: Outside market hours, skipping.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StatusReporterService: Failed to send status report.");
            }

            // 15-minute interval for better visibility
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    /// <summary>Sanitize extreme decimals (MinValue/MaxValue) to show "N/A" instead of garbage.</summary>
    private static string SafePrice(decimal val) =>
        val == 0 || val == decimal.MinValue || val == decimal.MaxValue || Math.Abs(val) > 1_000_000_000m
            ? "—"
            : val.ToString("N1");

    private async Task SendReportAsync(DateTime now)
    {
        var sb = new StringBuilder();
        var metrics = _stateStore.GetSystemMetrics();
        var states = _stateStore.GetAllStates().OrderBy(x => x.Symbol).ToList();
        
        sb.AppendLine($"📡 *Strategy Report #{_reportCount}* — {now:HH:mm dd-MMM}");
        sb.AppendLine($"WebSocket: {(metrics.WebSocketConnected ? "🟢" : "🔴")} | Ticks: {metrics.TicksProcessed:N0}");
        sb.AppendLine();

        // ── Market Levels per Symbol ──
        foreach (var s in states)
        {
            // Read fresh values from tracker
            var rsi = _tracker.GetRsiAndRma(s.Symbol);
            var vwap = _tracker.GetVwap(s.Symbol);
            var ema20 = _tracker.GetEMA(s.Symbol, 20);

            sb.AppendLine($"📈 *{s.Symbol}* `{s.Ltp:N1}`");
            
            // CPR
            if (s.Pivot > 0)
                sb.AppendLine($"  CPR: `{s.Bc:N0}`|`{s.Pivot:N0}`|`{s.Tc:N0}` {(s.IsVirginCpr ? "✨" : "")}");
            
            // PDH/PDL
            if (s.Pdh > 0)
                sb.AppendLine($"  PDH:`{s.Pdh:N0}` PDL:`{s.Pdl:N0}`");

            // 3-Day Range
            if (s.ThreeDayHigh > 0)
                sb.AppendLine($"  3D: `{s.ThreeDayHigh:N0}`–`{s.ThreeDayLow:N0}`");

            // ORB
            if (s.OrbHigh > 0 && s.OrbLow > 0)
                sb.AppendLine($"  ORB: `{s.OrbHigh:N0}`–`{s.OrbLow:N0}` (W:{s.OrbHigh-s.OrbLow:N0})");

            // PDLH (Consolidation)
            string chStr = SafePrice(s.ConsolidationHigh);
            string clStr = SafePrice(s.ConsolidationLow);
            if (chStr != "—" && clStr != "—")
                sb.AppendLine($"  PDLH: `{chStr}`–`{clStr}`");

            // Live indicators from tracker
            string emaLine = $"  EMA20:`{(ema20.HasValue ? ema20.Value.ToString("N0") : "—")}` ";
            emaLine += $"RSI:`{(rsi.HasValue ? rsi.Value.Rsi.ToString("N1") : "—")}` ";
            emaLine += $"VWAP:`{(vwap.HasValue ? vwap.Value.ToString("N0") : "—")}`";
            sb.AppendLine(emaLine);
            sb.AppendLine($"  Trend: *{s.Trend}*");
            
            // Strangle
            if (!string.IsNullOrEmpty(s.StrangleStatus) && s.StrangleStatus != "Not Started")
                sb.AppendLine($"  🔗 {s.StrangleStatus}");

            sb.AppendLine();
        }

        // ── Strategy Digest ──
        sb.AppendLine("━━ *Strategies* ━━");
        foreach (var strategy in _strategies)
        {
            try
            {
                var digest = strategy.GetStatusDigest();
                if (!string.IsNullOrWhiteSpace(digest))
                    sb.Append(digest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get status digest from {Strategy}", strategy.GetType().Name);
            }
        }

        // ── Next Expected Triggers ──
        sb.AppendLine();
        sb.AppendLine("━━ *What Next* ━━");
        var tod = now.TimeOfDay;
        if (tod < new TimeSpan(9, 30, 0))
            sb.AppendLine("ORB forming. VWAP building.");
        else if (tod < new TimeSpan(10, 30, 0))
            sb.AppendLine("ORB set. Scanning VWAP/PDLH/3Day.");
        else if (tod < new TimeSpan(12, 30, 0))
            sb.AppendLine("Prime window. All strategies active. BigMove scanning.");
        else if (tod < new TimeSpan(14, 30, 0))
            sb.AppendLine("BigMove closed. VWAP/ORB/3Day active until 14:30.");
        else if (tod < new TimeSpan(15, 15, 0))
            sb.AppendLine("Entry closed. Active positions square off at 15:15.");
        else
            sb.AppendLine("Market closed.");

        sb.AppendLine($"_Next update ~{now.AddMinutes(15):HH:mm}_");

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
            
            // Try Markdown first, fall back to plain text
            var payload = new 
            { 
                chat_id = _tgChatId, 
                text = message, 
                parse_mode = "Markdown" 
            };
            
            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Telegram API {Code}: {Error}", response.StatusCode, error);
                
                // Retry without Markdown if parsing failed
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    _logger.LogWarning("Retrying as plain text...");
                    var plainPayload = new { chat_id = _tgChatId, text = message };
                    var plainContent = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(plainPayload), 
                        Encoding.UTF8, "application/json");
                    var retryResp = await _httpClient.PostAsync(url, plainContent);
                    if (retryResp.IsSuccessStatusCode)
                        _logger.LogInformation("Plain text retry succeeded.");
                    else
                        _logger.LogError("Plain text retry also failed: {Code}", retryResp.StatusCode);
                }
            }
            else
            {
                _logger.LogInformation("Telegram message sent OK.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostToTelegram exception.");
        }
    }
}
