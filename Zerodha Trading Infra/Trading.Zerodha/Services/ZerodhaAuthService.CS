// =============================================================================
// ZerodhaAuthService_FIXED.cs
// =============================================================================
// FIXES APPLIED (search "// FIX #N" to jump to each change):
//
//   FIX #1 — Directory existence check before SaveCachedToken
//             (File.WriteAllText throws DirectoryNotFoundException if the
//              folder in TokenFilePath doesn't exist yet — e.g. first run)
//
//   FIX #2 — Token validation after cache load (verify token still works)
//             (cached token could be revoked mid-day by Zerodha; a quick
//              GetProfile call confirms it's still valid before trading starts)
//
//   FIX #3 — Hardcoded screenshot paths replaced with config-driven temp path
//             (was writing to "/Users/Lenovo/Desktop/..." — breaks on any
//              machine that isn't that specific Lenovo laptop; and "/tmp/..."
//              doesn't exist on Windows; now uses Path.GetTempPath() instead)
//
//   FIX #4 — TOTP secret validation on startup with clear error message
//             (if TotpSecret is missing/empty, OtpNet throws a cryptic
//              Base32 decode exception deep in Playwright flow; fail fast
//              with a clear message before launching the browser)
//
//   FIX #5 — Playwright browser resource guaranteed disposal on all paths
//             (original try/catch could leave browser open if an intermediate
//              step threw before reaching the using block's end; restructured
//              so browser is always disposed even on partial failures)
//
//   FIX #6 — Token file plaintext warning added with clear documentation
//             (token is still stored as plaintext — unchanged by design since
//              Playwright automation requires it — but a clear warning is now
//              logged and a code comment explains what to do in high-security
//              environments)
// =============================================================================

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
/// Access token is persisted to a local file to avoid re-login on restart.
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
        _config        = config;
        _logger        = logger;
        _apiKey        = config["Zerodha:ApiKey"]       ?? "";
        _apiSecret     = config["Zerodha:ApiSecret"]    ?? "";
        _userId        = config["Zerodha:UserId"]       ?? "";
        _password      = config["Zerodha:Password"]     ?? "";
        _totpSecret    = config["Zerodha:TotpSecret"]   ?? "";
        _tokenFilePath = config["Zerodha:TokenFilePath"] ?? "access_token.json";

        // -----------------------------------------------------------------
        // FIX #4 ↓ — Validate critical config on construction, not mid-flow.
        //
        // ORIGINAL: no validation — missing TotpSecret caused a cryptic
        // Base32Encoding.ToBytes exception deep inside the Playwright flow,
        // after the browser had already been launched and logged in.
        //
        // FIX: throw immediately at startup with a clear, actionable message.
        // -----------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(_apiKey))       // FIX #4
            throw new InvalidOperationException("Zerodha:ApiKey is missing from configuration.");
        if (string.IsNullOrWhiteSpace(_apiSecret))    // FIX #4
            throw new InvalidOperationException("Zerodha:ApiSecret is missing from configuration.");
        if (string.IsNullOrWhiteSpace(_userId))       // FIX #4
            throw new InvalidOperationException("Zerodha:UserId is missing from configuration.");
        if (string.IsNullOrWhiteSpace(_password))     // FIX #4
            throw new InvalidOperationException("Zerodha:Password is missing from configuration.");
        if (string.IsNullOrWhiteSpace(_totpSecret))   // FIX #4
            throw new InvalidOperationException(
                "Zerodha:TotpSecret is missing from configuration. " +
                "Add your TOTP Base32 secret from Zerodha's 2FA setup page.");
    }

    // =========================================================================
    /// <summary>
    /// Attempts to load and validate a cached access token.
    /// If not found, expired, or invalid, performs automated Playwright login.
    /// Returns a valid access token.
    /// </summary>
    // =========================================================================
    public async Task<string> EnsureAccessTokenAsync()
    {
        // ── Priority 1: Environment variable bypass
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
            // -----------------------------------------------------------------
            // FIX #2 ↓ — Validate cached token is still accepted by Zerodha.
            //
            // ORIGINAL: token loaded from file was used directly without
            // checking if it had been revoked (e.g. user logged in elsewhere,
            // Zerodha revoked it, or it expired mid-day). This caused silent
            // failures where all API calls returned 403 until manual restart.
            //
            // FIX: call GetProfile() as a lightweight validation. On failure,
            // fall through to fresh login automatically.
            // -----------------------------------------------------------------
            _logger.LogInformation("Validating cached token from {Path}...", _tokenFilePath);
            if (await IsTokenValidAsync(cached!))  // FIX #2
            {
                _logger.LogInformation("Cached token is valid. Skipping login.");
                AccessToken = cached;
                return cached!;
            }
            else
            {
                _logger.LogWarning("Cached token is no longer valid. Performing fresh login.");  // FIX #2
            }
        }

        // ── Perform Automated Playwright Login
        return await PerformPlaywrightLoginAsync();
    }

    // =========================================================================
    // FIX #5 — Playwright login extracted to its own method so all resources
    // are guaranteed to be disposed regardless of which step throws.
    //
    // ORIGINAL: the entire Playwright flow was inline in EnsureAccessTokenAsync
    // inside a try/catch. If an exception occurred after browser launch but
    // before the using block completed, the browser process could be left open.
    //
    // FIX: extracted to a dedicated method with proper using/await using
    // nesting so Playwright, browser, and page are always disposed.
    // =========================================================================
    private async Task<string> PerformPlaywrightLoginAsync()  // FIX #5
    {
        var loginUrl = $"https://kite.zerodha.com/connect/login?v=3&api_key={_apiKey}";
        _logger.LogInformation("Launching Playwright to automate Zerodha login at {Url}", loginUrl);

        // FIX #3 ↓ — use Path.GetTempPath() instead of hardcoded paths
        // ORIGINAL: "/Users/Lenovo/Desktop/auth_fail_..." and "/tmp/zerodha_debug.png"
        // PROBLEM: hardcoded paths break on any machine other than the original
        // dev machine (Windows has no /tmp; other users have different home dirs).
        // FIX: Path.GetTempPath() returns the correct temp dir on all platforms.
        var screenshotDir = Path.GetTempPath();  // FIX #3

        using var playwright  = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            SlowMo   = 100
        });

        var page = await browser.NewPageAsync();

        try
        {
            await page.GotoAsync(loginUrl);

            _logger.LogInformation("Step 1: Entering User ID...");

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
                await page.WaitForSelectorAsync("input[type='number']",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch (Exception ex)
            {
                // FIX #3 ↓ — platform-safe screenshot path
                var debugPath = Path.Combine(screenshotDir, $"zerodha_auth_fail_{DateTime.Now:HHmmss}.png");  // FIX #3
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = debugPath });
                _logger.LogError(ex, "TOTP prompt did not appear. Debug screenshot saved to {Path}", debugPath);
                throw;
            }

            // Generate TOTP dynamically
            var    totp = new Totp(Base32Encoding.ToBytes(_totpSecret));
            string pin  = totp.ComputeTotp();

            // Intercept the redirect request at network layer
            var redirectWaitTask = page.WaitForRequestAsync(
                req => req.Url.Contains("request_token="),
                new PageWaitForRequestOptions { Timeout = 30000 });

            _logger.LogInformation("Step 4: Injecting TOTP...");
            await Task.Delay(1000);
            await page.FillAsync("input[type='number']", pin);

            try
            {
                await Task.Delay(500);
                await page.ClickAsync("button[type='submit']",
                    new PageClickOptions { Timeout = 2000 });
            }
            catch { /* Ignore — some flows auto-submit on TOTP entry */ }

            _logger.LogInformation("Step 5: Waiting for redirect with request_token...");

            try
            {
                var request  = await redirectWaitTask;
                var finalUrl = request.Url;
                _logger.LogInformation("Redirect intercepted: {Url}", finalUrl);

                var match = Regex.Match(finalUrl, @"request_token=([^&]+)");
                if (match.Success)
                    return await GenerateAndCacheAccessTokenAsync(match.Groups[1].Value);

                throw new InvalidOperationException("Redirect URL did not contain request_token.");
            }
            catch (TimeoutException)
            {
                // FIX #3 ↓ — platform-safe screenshot path
                var screenshotPath = Path.Combine(screenshotDir, $"zerodha_timeout_{DateTime.Now:HHmmss}.png");  // FIX #3
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
                _logger.LogError("Timed out waiting for request_token redirect. Screenshot: {Path}", screenshotPath);
                throw new TimeoutException("Timed out waiting for Zerodha redirect containing request_token.");
            }
        }
        catch (Exception ex) when (ex is not TimeoutException && ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Playwright login failed unexpectedly. Check credentials or network.");
            throw;
        }
        // FIX #5: browser and playwright are disposed by using/await using — guaranteed
    }

    // =========================================================================
    /// <summary>
    /// Exchanges a request_token for an access_token and persists it.
    /// </summary>
    // =========================================================================
    public async Task<string> GenerateAndCacheAccessTokenAsync(string requestToken)
    {
        _logger.LogInformation("Generating Zerodha access token from request_token...");

        var kite    = new Kite(_apiKey, Debug: false);
        var session = await Task.Run(() => kite.GenerateSession(requestToken, _apiSecret));
        var token   = session.AccessToken;

        SaveCachedToken(token);

        _logger.LogInformation("Access token generated and cached to {Path}", _tokenFilePath);
        AccessToken = token;
        return token;
    }

    // =========================================================================
    // FIX #2 — Token validation helper
    // =========================================================================
    private async Task<bool> IsTokenValidAsync(string token)  // FIX #2
    {
        try
        {
            var kite = new Kite(_apiKey, Debug: false);
            kite.SetAccessToken(token);
            await Task.Run(() => kite.GetProfile());  // lightweight API call
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Token validation failed: {Message}", ex.Message);  // FIX #2
            return false;
        }
    }

    // =========================================================================
    // Token cache helpers
    // =========================================================================

    private bool TryLoadCachedToken(out string? token)
    {
        token = null;

        if (!File.Exists(_tokenFilePath))
            return false;

        try
        {
            var raw = File.ReadAllText(_tokenFilePath).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var parts = raw.Split('|', 2);
            if (parts.Length != 2) return false;

            if (!DateTime.TryParse(parts[0], out var saved) || saved.Date != DateTime.Today)
            {
                _logger.LogInformation("Cached token is from {Date} — requires fresh login.", saved.Date);
                return false;
            }

            token = parts[1];
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cached token file at {Path}.", _tokenFilePath);
            return false;
        }
    }

    private void SaveCachedToken(string token)
    {
        // -----------------------------------------------------------------
        // FIX #1 ↓ — Ensure the directory exists before writing.
        //
        // ORIGINAL: File.WriteAllText(_tokenFilePath, content) called directly.
        // PROBLEM: If TokenFilePath includes a subdirectory that doesn't exist
        // yet (e.g. "data/tokens/access_token.json"), File.WriteAllText throws
        // DirectoryNotFoundException on first run.
        // FIX: call Directory.CreateDirectory on the parent folder first.
        // It is safe to call even if the directory already exists.
        // -----------------------------------------------------------------
        var dir = Path.GetDirectoryName(_tokenFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);  // FIX #1: no-op if already exists

        var content = $"{DateTime.Today:yyyy-MM-dd}|{token}";
        File.WriteAllText(_tokenFilePath, content);

        // -----------------------------------------------------------------
        // FIX #6 ↓ — Plaintext token warning.
        //
        // The access token is stored as plaintext in a local file.
        // This is acceptable for a single-machine trading setup behind a
        // firewall, but in a high-security environment consider:
        //   - Windows: use Windows Data Protection API (DPAPI) via
        //     System.Security.Cryptography.ProtectedData
        //   - Linux: use a secrets manager or encrypted volume
        // For now we log a warning so operators are aware.
        // -----------------------------------------------------------------
        _logger.LogWarning(  // FIX #6
            "Access token written to {Path} as plaintext. " +
            "Ensure this file is excluded from source control (.gitignore) " +
            "and protected by filesystem permissions.",
            _tokenFilePath);
    }
}
