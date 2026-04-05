using KiteConnect;
using Microsoft.Extensions.Logging;

namespace Trading.Zerodha.Services;

/// <summary>
/// Handles Zerodha Kite Connect OAuth login flow.
/// Flow: API Key + API Secret → browser login → request_token → access_token
/// Access token is persisted to a local JSON file to avoid re-login on restart.
/// </summary>
public class ZerodhaAuthService
{
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _tokenFilePath;
    private readonly ILogger<ZerodhaAuthService> _logger;

    public string? AccessToken { get; private set; }

    public ZerodhaAuthService(
        string apiKey,
        string apiSecret,
        string tokenFilePath,
        ILogger<ZerodhaAuthService> logger)
    {
        _apiKey       = apiKey;
        _apiSecret    = apiSecret;
        _tokenFilePath = tokenFilePath;
        _logger       = logger;
    }

    /// <summary>
    /// Attempts to load a cached access token.
    /// If not found or expired, walks the user through the OAuth flow via console.
    /// Returns the valid access token.
    /// </summary>
    public async Task<string> EnsureAccessTokenAsync()
    {
        // Try cached token first
        if (TryLoadCachedToken(out var cached))
        {
            _logger.LogInformation("Loaded cached Zerodha access token from {Path}", _tokenFilePath);
            AccessToken = cached;
            return cached!;
        }

        // Print login URL
        var loginUrl = $"https://kite.zerodha.com/connect/login?v=3&api_key={_apiKey}";
        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine("  ZERODHA LOGIN REQUIRED");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"  Open this URL in your browser and log in:");
        Console.WriteLine();
        Console.WriteLine($"  {loginUrl}");
        Console.WriteLine();
        Console.WriteLine("  After login, Zerodha will redirect to your redirect URL.");
        Console.WriteLine("  Copy the 'request_token' query parameter from the URL.");
        Console.WriteLine("  Example redirect: https://yourapp.com/?request_token=XXXX&action=login&status=success");
        Console.WriteLine();
        Console.Write("  Paste the request_token here: ");

        var requestToken = Console.ReadLine()?.Trim()
            ?? throw new InvalidOperationException("No request token provided.");

        return await GenerateAndCacheAccessTokenAsync(requestToken);
    }

    /// <summary>
    /// Exchanges a request_token for an access_token and persists it.
    /// </summary>
    public async Task<string> GenerateAndCacheAccessTokenAsync(string requestToken)
    {
        _logger.LogInformation("Generating Zerodha access token...");

        var kite = new Kite(_apiKey, Debug: false);

        // GenerateSession is synchronous in the KiteConnect SDK, wrap in Task.Run
        var session = await Task.Run(() => kite.GenerateSession(requestToken, _apiSecret));

        var token = session.AccessToken;

        SaveCachedToken(token);

        _logger.LogInformation("Access token generated and cached to {Path}", _tokenFilePath);

        AccessToken = token;
        return token;
    }

    // ───────────────────────── helpers ─────────────────────────

    private bool TryLoadCachedToken(out string? token)
    {
        token = null;

        if (!File.Exists(_tokenFilePath))
            return false;

        try
        {
            var raw = File.ReadAllText(_tokenFilePath).Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            // Token format: "<date_ISO>|<token>"
            var parts = raw.Split('|', 2);

            if (parts.Length != 2)
                return false;

            // Invalidate token if it's from a previous trading day
            if (!DateTime.TryParse(parts[0], out var saved) ||
                saved.Date != DateTime.Today)
            {
                _logger.LogInformation("Cached token is from {Date}, requiring fresh login.", saved.Date);
                return false;
            }

            token = parts[1];
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cached token file.");
            return false;
        }
    }

    private void SaveCachedToken(string token)
    {
        var content = $"{DateTime.Today:yyyy-MM-dd}|{token}";
        File.WriteAllText(_tokenFilePath, content);
    }
}
