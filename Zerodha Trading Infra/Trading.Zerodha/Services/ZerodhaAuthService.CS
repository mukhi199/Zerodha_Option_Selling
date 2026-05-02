using KiteConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using OtpNet;
using System.Text.RegularExpressions;

namespace Trading.Zerodha.Services;

/// <summary>
/// Handles Zerodha Kite Connect OAuth login flow via Playwright automation.
/// Flow: API Key + Playwright Login → request_token → access_token
/// Access token is persisted to a local JSON file to avoid re-login on restart.
/// </summary>
public class ZerodhaAuthService
{
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _userId;
    private readonly string _password;
    private readonly string _totpSecret;
    private readonly string _tokenFilePath;
    private readonly IConfiguration _config;
    private readonly ILogger<ZerodhaAuthService> _logger;

    public string? AccessToken { get; private set; }

    public ZerodhaAuthService(
        IConfiguration config,
        ILogger<ZerodhaAuthService> logger)
    {
        _config       = config;
        _apiKey       = config["Zerodha:ApiKey"] ?? "";
        _apiSecret    = config["Zerodha:ApiSecret"] ?? "";
        _userId       = config["Zerodha:UserId"] ?? "";
        _password     = config["Zerodha:Password"] ?? "";
        _totpSecret   = config["Zerodha:TotpSecret"] ?? "";
        _tokenFilePath = config["Zerodha:TokenFilePath"] ?? "access_token.json";
        _logger       = logger;
    }

    /// <summary>
    /// Attempts to load a cached access token.
    /// If not found or expired, walks the user through the OAuth flow via console.
    /// Returns the valid access token.
    /// </summary>
    public async Task<string> EnsureAccessTokenAsync()
    {
        // ── Priority 1: Environment variable bypass (e.g. ZERODHA_ACCESS_TOKEN)
        var envToken = Environment.GetEnvironmentVariable("ZERODHA_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            _logger.LogInformation("Using pre-set ZERODHA_ACCESS_TOKEN from environment.");
            AccessToken = envToken;
            return envToken;
        }

        // ── Priority 2: Cached token from file
        if (TryLoadCachedToken(out var cached))
        {
            _logger.LogInformation("Loaded cached Zerodha access token from {Path}", _tokenFilePath);
            AccessToken = cached;
            return cached!;
        }

        // Perform Automated Playwright Login
        var loginUrl = $"https://kite.zerodha.com/connect/login?v=3&api_key={_apiKey}";
        _logger.LogInformation("Launching Playwright to automate login at {Url}", loginUrl);

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions 
            { 
                Headless = true,
                SlowMo = 100 // More human-like
            });
            var page = await browser.NewPageAsync();
            
            await page.GotoAsync(loginUrl);

            _logger.LogInformation("Step 1 & 2: Entering Credentials...");
            
            // If the browser remembers a user, we might see a "Change user" link. Click it if visible.
            var changeUser = page.Locator("a:has-text('Change user')");
            if (await changeUser.IsVisibleAsync())
            {
                _logger.LogInformation("'Change user' detected. Clicking to show User ID field.");
                await changeUser.ClickAsync();
            }

            await page.Locator("#userid").WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await page.Locator("#userid").FillAsync(_userId);
            
            _logger.LogInformation("Step 2: Entering Password...");
            await page.Locator("#password").FillAsync(_password);
            await page.Keyboard.PressAsync("Enter");

            _logger.LogInformation("Step 3: Waiting for TOTP prompt...");
            try 
            {
                await page.WaitForSelectorAsync("input[type='number']", new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch (Exception ex)
            {
                var debugPath = $"/Users/Lenovo/Desktop/auth_fail_{DateTime.Now:HHmmss}.png";
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = debugPath });
                _logger.LogError(ex, "TOTP prompt did not appear. Screenshot saved to {Path}", debugPath);
                throw;
            }

            // Generate TOTP dynamically using the Base32 secret stored in appsettings
            var totp = new Totp(Base32Encoding.ToBytes(_totpSecret));
            string pin = totp.ComputeTotp();

            // Intercept the request to our redirect_uri at the network layer.
            // This works even if port 5000 rejects the connection!
            var redirectWaitTask = page.WaitForRequestAsync(req => req.Url.Contains("request_token="), new PageWaitForRequestOptions { Timeout = 30000 });

            _logger.LogInformation("Injecting Generated TOTP ({Pin})...", pin);
            await Task.Delay(1000);
            await page.FillAsync("input[type='number']", pin);

            _logger.LogInformation("Clicking TOTP Continue button (ignoring if missing due to auto-submit)...");
            try 
            {
                await Task.Delay(500);
                await page.ClickAsync("button[type='submit']", new PageClickOptions { Timeout = 2000 });
            } 
            catch { /* Ignore auto-submit */ }

            _logger.LogInformation("Waiting for redirect request to be fired...");
            
            try 
            {
                var request = await redirectWaitTask;
                var finalUrl = request.Url;
                _logger.LogInformation("Redirect request intercepted: {Url}", finalUrl);
                
                var match = Regex.Match(finalUrl, @"request_token=([^&]+)");
                if (match.Success)
                {
                    return await GenerateAndCacheAccessTokenAsync(match.Groups[1].Value);
                }
                throw new Exception("Could not find request_token in the final URL.");
            }
            catch (TimeoutException)
            {
                var screenshotPath = "/tmp/zerodha_debug.png";
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
                _logger.LogError("Never intercepted a request containing request_token. Screenshot: {Path}", screenshotPath);
                throw new TimeoutException("Timed out waiting for Zerodha redirect URL containing request_token.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to automate browser login safely. Check permissions or network.");
            throw;
        }
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
