using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record CodexRateLimitWindow(
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAt
);

public record CodexLocalStatus(
    string? PlanType,
    CodexRateLimitWindow Primary,
    CodexRateLimitWindow Secondary,
    long TotalTokens,
    string? CreditsBalance,
    bool IsRemote
);

public class CodexLocalService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _authPath;
    private readonly string _sessionsPath;

    public CodexLocalService()
    {
        var codexHome = ResolveCodexHome();
        _authPath = Path.Combine(codexHome, "auth.json");
        _sessionsPath = Path.Combine(codexHome, "sessions");

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsAvailable() => File.Exists(_authPath) || Directory.Exists(_sessionsPath);

    public async Task<CodexLocalStatus?> GetStatusAsync()
    {
        try
        {
            var remoteStatus = await TryGetRemoteStatusAsync();
            if (remoteStatus != null)
            {
                if (remoteStatus.IsRemote)
                {
                    var message =
                        $"Codex remote usage loaded from chatgpt.com: 5h={remoteStatus.Primary.UsedPercent:F0}% 7d={remoteStatus.Secondary.UsedPercent:F0}%";
                    Console.WriteLine(message);
                    AppLog.WriteLine(message);
                }
                return remoteStatus;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Codex remote status error: {ex.Message}");
        }

        var localStatus = TryGetLocalStatus();
        if (localStatus != null)
        {
            var message =
                $"Codex local usage loaded from session logs: 5h={localStatus.Primary.UsedPercent:F0}% 7d={localStatus.Secondary.UsedPercent:F0}%";
            Console.WriteLine(message);
            AppLog.WriteLine(message);
        }

        return localStatus;
    }

    public void Dispose() => _http.Dispose();

    private async Task<CodexLocalStatus?> TryGetRemoteStatusAsync()
    {
        var auth = LoadAuthBundle();
        if (auth == null || string.IsNullOrWhiteSpace(auth.AccessToken))
            return null;

        using var response = await SendWhamUsageRequestAsync(auth.AccessToken, auth.AccountId);
        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            !string.IsNullOrWhiteSpace(auth.RefreshToken))
        {
            var refreshedAccessToken = await TryRefreshAccessTokenAsync(auth.RefreshToken);
            if (!string.IsNullOrWhiteSpace(refreshedAccessToken))
            {
                using var retry = await SendWhamUsageRequestAsync(refreshedAccessToken, auth.AccountId);
                if (retry.IsSuccessStatusCode)
                    return ParseRemoteStatus(await retry.Content.ReadAsStringAsync());
            }
        }

        if (!response.IsSuccessStatusCode)
            return null;

        return ParseRemoteStatus(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendWhamUsageRequestAsync(string accessToken, string? accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.TryAddWithoutValidation("User-Agent", "codex-cli");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(accountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

        return await _http.SendAsync(request);
    }

    private async Task<string?> TryRefreshAccessTokenAsync(string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "app_EMoamEEZ73f0CkXaXp7hrann",
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "codex-cli");

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("access_token", out var tokenEl) &&
               tokenEl.ValueKind == JsonValueKind.String
            ? tokenEl.GetString()
            : null;
    }

    private CodexLocalStatus? TryGetLocalStatus()
    {
        if (!Directory.Exists(_sessionsPath))
            return null;

        try
        {
            CodexLocalStatus? latestStatus = null;
            DateTimeOffset latestTimestamp = DateTimeOffset.MinValue;

            foreach (var filePath in Directory.EnumerateFiles(_sessionsPath, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    foreach (var line in File.ReadLines(filePath))
                    {
                        if (!line.Contains("\"token_count\"", StringComparison.Ordinal))
                            continue;

                        using var doc = JsonDocument.Parse(line);

                        if (!doc.RootElement.TryGetProperty("payload", out var payload) ||
                            !payload.TryGetProperty("type", out var typeEl) ||
                            typeEl.GetString() != "token_count" ||
                            !payload.TryGetProperty("rate_limits", out var rateLimits) ||
                            !rateLimits.TryGetProperty("primary", out var primary) ||
                            !rateLimits.TryGetProperty("secondary", out var secondary))
                        {
                            continue;
                        }

                        long totalTokens = 0;
                        if (payload.TryGetProperty("info", out var info) &&
                            info.ValueKind != JsonValueKind.Null &&
                            info.TryGetProperty("total_token_usage", out var totalUsage) &&
                            totalUsage.TryGetProperty("total_tokens", out var totalTokensEl))
                        {
                            totalTokens = totalTokensEl.GetInt64();
                        }

                        var status = new CodexLocalStatus(
                            rateLimits.TryGetProperty("plan_type", out var planEl) ? planEl.GetString() : null,
                            ParseWindow(primary),
                            ParseWindow(secondary),
                            totalTokens,
                            null,
                            false
                        );

                        var timestamp = DateTimeOffset.MinValue;
                        if (doc.RootElement.TryGetProperty("timestamp", out var tsEl) &&
                            tsEl.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(tsEl.GetString(), out var parsedTimestamp))
                        {
                            timestamp = parsedTimestamp;
                        }

                        if (timestamp >= latestTimestamp)
                        {
                            latestTimestamp = timestamp;
                            latestStatus = status;
                        }
                    }
                }
                catch (Exception fileEx)
                {
                    Console.WriteLine($"Codex local file error ({Path.GetFileName(filePath)}): {fileEx.Message}");
                }
            }

            return latestStatus;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Codex local status error: {ex.Message}");
            return null;
        }
    }

    private static CodexLocalStatus? ParseRemoteStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("rate_limit", out var rateLimit) ||
            !rateLimit.TryGetProperty("primary_window", out var primary) ||
            !rateLimit.TryGetProperty("secondary_window", out var secondary))
        {
            return null;
        }

        string? creditsBalance = null;
        if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            if (credits.TryGetProperty("unlimited", out var unlimitedEl) &&
                unlimitedEl.ValueKind == JsonValueKind.True)
            {
                creditsBalance = "unlimited";
            }
            else if (credits.TryGetProperty("balance", out var balanceEl) &&
                     balanceEl.ValueKind == JsonValueKind.String)
            {
                creditsBalance = balanceEl.GetString();
            }
        }

        var totalTokens = 0L;

        return new CodexLocalStatus(
            root.TryGetProperty("plan_type", out var planEl) ? planEl.GetString() : null,
            ParseRemoteWindow(primary),
            ParseRemoteWindow(secondary),
            totalTokens,
            creditsBalance,
            true
        );
    }

    private static CodexRateLimitWindow ParseRemoteWindow(JsonElement window)
    {
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("reset_at", out var resetEl) &&
            resetEl.ValueKind == JsonValueKind.Number &&
            resetEl.TryGetInt64(out var unixSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        var windowMinutes = 0;
        if (window.TryGetProperty("limit_window_seconds", out var secondsEl) &&
            secondsEl.ValueKind == JsonValueKind.Number &&
            secondsEl.TryGetInt32(out var seconds))
        {
            windowMinutes = seconds / 60;
        }

        return new CodexRateLimitWindow(
            window.TryGetProperty("used_percent", out var usedEl) ? usedEl.GetDouble() : 0,
            windowMinutes,
            resetsAt
        );
    }

    private static CodexRateLimitWindow ParseWindow(JsonElement window)
    {
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resetEl) &&
            resetEl.ValueKind == JsonValueKind.Number &&
            resetEl.TryGetInt64(out var unixSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return new CodexRateLimitWindow(
            window.TryGetProperty("used_percent", out var usedEl) ? usedEl.GetDouble() : 0,
            window.TryGetProperty("window_minutes", out var minutesEl) ? minutesEl.GetInt32() : 0,
            resetsAt
        );
    }

    private CodexAuthBundle? LoadAuthBundle()
    {
        if (!File.Exists(_authPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_authPath));
            var root = doc.RootElement;

            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return null;

            var accessToken = tokens.TryGetProperty("access_token", out var accessEl) &&
                              accessEl.ValueKind == JsonValueKind.String
                ? accessEl.GetString()
                : null;
            var refreshToken = tokens.TryGetProperty("refresh_token", out var refreshEl) &&
                               refreshEl.ValueKind == JsonValueKind.String
                ? refreshEl.GetString()
                : null;
            var accountId = tokens.TryGetProperty("account_id", out var accountEl) &&
                            accountEl.ValueKind == JsonValueKind.String
                ? accountEl.GetString()
                : null;

            return new CodexAuthBundle(accessToken, refreshToken, accountId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Codex auth read error: {ex.Message}");
            return null;
        }
    }

    private static string ResolveCodexHome()
    {
        var envValue = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex");
    }

    private sealed record CodexAuthBundle(string? AccessToken, string? RefreshToken, string? AccountId);
}
