using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UsageMonitor;

namespace UsageMonitor.Services;

public record ClaudeCodeUsageWindow(
    double UtilizationPercent,
    DateTimeOffset? ResetsAt
);

public record ClaudeCodeLocalStatus(
    ClaudeCodeUsageWindow FiveHour,
    ClaudeCodeUsageWindow SevenDay,
    DateTimeOffset? FetchedAt,
    string? SubscriptionType = null,
    string? RateLimitTier = null
);

public class ClaudeCodeLocalService : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string OAuthBetaHeader = "oauth-2025-04-20";
    private const string ClaudeCodeUserAgent = "claude-cli/UsageMonitor";
    private const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RemoteFailureBackoff = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly string _credentialsPath;
    private readonly string _cachePath;
    private DateTimeOffset? _nextRemoteRetryAt;

    public ClaudeCodeLocalService()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _credentialsPath = Path.Combine(home, ".claude", ".credentials.json");
        _cachePath = Path.Combine(home, ".claude", ".usage_cache.json");
        _http = new HttpClient();
    }

    public bool IsAvailable() => File.Exists(_credentialsPath) || File.Exists(_cachePath);

    public async Task<ClaudeCodeLocalStatus?> GetStatusAsync()
    {
        if (!IsAvailable())
            return null;

        try
        {
            var cachedStatus = LoadCachedStatus();
            if (cachedStatus?.FetchedAt is { } fetchedAt &&
                DateTimeOffset.Now - fetchedAt < CacheTtl)
            {
                AppLog.WriteLine(
                    $"Claude Code usage loaded from cache: 5h={cachedStatus.FiveHour.UtilizationPercent:F0}% 7d={cachedStatus.SevenDay.UtilizationPercent:F0}%"
                );
                return cachedStatus;
            }

            var tokens = LoadOauthTokens();
            if (tokens is null)
                return cachedStatus;

            if (_nextRemoteRetryAt is { } nextRetryAt && DateTimeOffset.Now < nextRetryAt)
            {
                return cachedStatus;
            }

            if (IsTokenExpired(tokens.ExpiresAt))
            {
                var refreshed = await RefreshOauthTokensAsync(tokens);
                if (refreshed is null)
                {
                    if (cachedStatus != null)
                    {
                        AppLog.WriteLine("Claude Code OAuth token expired; using cached usage");
                        return cachedStatus;
                    }

                    return null;
                }

                tokens = refreshed;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd(ClaudeCodeUserAgent);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                AppLog.WriteLine(
                    $"Claude Code usage request failed ({(int)response.StatusCode}): {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(body) ? string.Empty : $" | {body}")}"
                );
                _nextRemoteRetryAt = DateTimeOffset.Now.Add(RemoteFailureBackoff);
                return cachedStatus;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var status = ParseStatus(doc.RootElement, tokens.SubscriptionType, tokens.RateLimitTier);

            SaveCache(status);
            _nextRemoteRetryAt = null;
            AppLog.WriteLine(
                $"Claude Code remote usage loaded from api.anthropic.com: 5h={status.FiveHour.UtilizationPercent:F0}% 7d={status.SevenDay.UtilizationPercent:F0}%"
            );
            return status;
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"Claude Code local status error: {ex.Message}");
            _nextRemoteRetryAt = DateTimeOffset.Now.Add(RemoteFailureBackoff);
            return LoadCachedStatus();
        }
    }

    public void Dispose() => _http.Dispose();

    private ClaudeCodeLocalStatus? LoadCachedStatus()
    {
        if (!File.Exists(_cachePath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));

            DateTimeOffset? fetchedAt = null;
            if (TryReadDateTimeOffset(doc.RootElement, "timestamp") is { } parsedFetchedAt)
                fetchedAt = parsedFetchedAt;

            return new ClaudeCodeLocalStatus(
                ParseUsageWindow(doc.RootElement.GetProperty("five_hour")),
                ParseUsageWindow(doc.RootElement.GetProperty("seven_day")),
                fetchedAt,
                ReadString(doc.RootElement, "subscription_type"),
                ReadString(doc.RootElement, "rate_limit_tier")
            );
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"Claude Code cache read error: {ex.Message}");
            return null;
        }
    }

    private ClaudeCodeOauthTokens? LoadOauthTokens()
    {
        if (!File.Exists(_credentialsPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_credentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var accessToken = ReadString(oauth, "accessToken");
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var refreshToken = ReadString(oauth, "refreshToken");
            var expiresAt = TryReadDateTimeOffset(oauth, "expiresAt");
            var subscriptionType = ReadString(oauth, "subscriptionType");
            var rateLimitTier = ReadString(oauth, "rateLimitTier");
            var scopes = ReadScopes(oauth);

            return new ClaudeCodeOauthTokens(
                accessToken,
                refreshToken,
                expiresAt,
                subscriptionType,
                rateLimitTier,
                scopes
            );
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"Claude Code credential read error: {ex.Message}");
            return null;
        }
    }

    private async Task<ClaudeCodeOauthTokens?> RefreshOauthTokensAsync(ClaudeCodeOauthTokens tokens)
    {
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd(ClaudeCodeUserAgent);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    grant_type = "refresh_token",
                    refresh_token = tokens.RefreshToken,
                    client_id = ClaudeCodeClientId,
                    scope = tokens.Scopes.Count > 0 ? string.Join(" ", tokens.Scopes) : null
                }),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                AppLog.WriteLine(
                    $"Claude Code token refresh failed ({(int)response.StatusCode}): {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(body) ? string.Empty : $" | {body}")}"
                );
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var accessToken = ReadString(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var refreshToken = ReadString(root, "refresh_token") ?? tokens.RefreshToken;
            var expiresIn = ReadInt64(root, "expires_in");
            var expiresAt = expiresIn.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value)
                : tokens.ExpiresAt;

            var scopes = ReadScopes(root);
            if (scopes.Count == 0)
                scopes = tokens.Scopes;

            AppLog.WriteLine("Claude Code OAuth token refreshed");

            return new ClaudeCodeOauthTokens(
                accessToken,
                refreshToken,
                expiresAt,
                tokens.SubscriptionType,
                tokens.RateLimitTier,
                scopes
            );
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"Claude Code token refresh error: {ex.Message}");
            return null;
        }
    }

    private void SaveCache(ClaudeCodeLocalStatus status)
    {
        try
        {
            var cache = new
            {
                timestamp = status.FetchedAt?.ToString("o"),
                subscription_type = status.SubscriptionType,
                rate_limit_tier = status.RateLimitTier,
                five_hour = new
                {
                    utilization = status.FiveHour.UtilizationPercent,
                    resets_at = status.FiveHour.ResetsAt?.ToString("o")
                },
                seven_day = new
                {
                    utilization = status.SevenDay.UtilizationPercent,
                    resets_at = status.SevenDay.ResetsAt?.ToString("o")
                }
            };

            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"Claude Code cache write error: {ex.Message}");
        }
    }

    private static ClaudeCodeLocalStatus ParseStatus(
        JsonElement root,
        string? subscriptionType,
        string? rateLimitTier)
    {
        return new ClaudeCodeLocalStatus(
            ParseUsageWindow(root.GetProperty("five_hour")),
            ParseUsageWindow(root.GetProperty("seven_day")),
            DateTimeOffset.Now,
            subscriptionType,
            rateLimitTier
        );
    }

    private static ClaudeCodeUsageWindow ParseUsageWindow(JsonElement element)
    {
        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var resetEl) &&
            resetEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetEl.GetString(), out var parsed))
        {
            resetsAt = parsed;
        }

        return new ClaudeCodeUsageWindow(
            element.TryGetProperty("utilization", out var utilEl) ? utilEl.GetDouble() : 0,
            resetsAt
        );
    }

    private static IReadOnlyList<string> ReadScopes(JsonElement element)
    {
        if (!element.TryGetProperty("scopes", out var scopesEl))
            return Array.Empty<string>();

        if (scopesEl.ValueKind == JsonValueKind.Array)
        {
            var scopes = new List<string>();
            foreach (var scope in scopesEl.EnumerateArray())
            {
                if (scope.ValueKind == JsonValueKind.String)
                {
                    var value = scope.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        scopes.Add(value);
                }
            }

            return scopes;
        }

        if (scopesEl.ValueKind == JsonValueKind.String)
        {
            var rawScopes = scopesEl.GetString();
            if (string.IsNullOrWhiteSpace(rawScopes))
                return Array.Empty<string>();

            return rawScopes
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return Array.Empty<string>();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? TryReadDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool IsTokenExpired(DateTimeOffset? expiresAt)
    {
        if (!expiresAt.HasValue)
            return false;

        return DateTimeOffset.UtcNow + ExpiryBuffer >= expiresAt.Value.ToUniversalTime();
    }

    private record ClaudeCodeOauthTokens(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string? SubscriptionType,
        string? RateLimitTier,
        IReadOnlyList<string> Scopes
    );
}
