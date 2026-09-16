using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record ProxyCooldown(string? Scope, string? ModelKey, string? Reason, long RemainingSeconds);

public record ProxyAccount(
    string Provider,
    string Account,
    string? Status,
    string? StatusDetail,
    bool Disabled,
    bool Unavailable,
    long Success,
    long Failed,
    IReadOnlyList<ProxyCooldown> Cooldowns
)
{
    // The longest cooldown is the one that actually gates the account, so it is the
    // only one worth a row of its own.
    public ProxyCooldown? WorstCooldown =>
        Cooldowns.Count == 0 ? null : Cooldowns.MaxBy(c => c.RemainingSeconds);

    // Three levels, because a model-scoped cooldown is not the same failure as a dead
    // credential: the proxy can still route other models through an account that has one
    // model throttled, so that case must not read as loudly as an unusable account.
    public ProxyHealth Health =>
        Disabled || Unavailable || string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase)
            ? ProxyHealth.Down
            : Cooldowns.Count > 0
                ? ProxyHealth.Limited
                : ProxyHealth.Ok;
}

public enum ProxyHealth
{
    Ok = 0,
    Limited = 1,
    Down = 2,
}

public record CliProxyStatus(IReadOnlyList<ProxyAccount> Accounts, string? Error);

// Reads per-account health from CLIProxyAPI's management API. This is deliberately
// separate from UsageApiService: usage-api answers "how much quota is left", while the
// proxy answers "which credentials can still serve a request right now".
public class CliProxyService : IDisposable
{
    // The management API bans a source IP after a handful of rejected keys. A monitor
    // polling on a timer would burn through that allowance in seconds and lock the
    // machine out of the proxy, so a rejected key stops polling for a long while
    // instead of retrying on the next tick.
    private static readonly TimeSpan AuthFailureBackoff = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly string? _url;
    private readonly string? _key;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public CliProxyService(string? baseUrl, string? managementKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _key = string.IsNullOrWhiteSpace(managementKey) ? null : managementKey.Trim();
        _url = string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : baseUrl.TrimEnd('/') + "/v0/management/auth-files";
    }

    // Without both a host and a key there is nothing to ask and no safe way to ask it,
    // so the section stays absent rather than showing a permanent error.
    public bool IsConfigured => _url != null && _key != null;

    public async Task<CliProxyStatus?> GetStatusAsync()
    {
        if (!IsConfigured) return null;
        if (DateTimeOffset.UtcNow < _blockedUntil)
            return new CliProxyStatus(Array.Empty<ProxyAccount>(), "auth failed");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            using var resp = await _http.SendAsync(req);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _blockedUntil = DateTimeOffset.UtcNow + AuthFailureBackoff;
                AppLog.WriteLine(
                    $"cli-proxy: management key rejected ({(int)resp.StatusCode}); " +
                    $"pausing {AuthFailureBackoff.TotalMinutes:0} min to avoid an IP ban.");
                return new CliProxyStatus(Array.Empty<ProxyAccount>(), "auth failed");
            }

            if (!resp.IsSuccessStatusCode)
                return new CliProxyStatus(Array.Empty<ProxyAccount>(), $"HTTP {(int)resp.StatusCode}");

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                return new CliProxyStatus(Array.Empty<ProxyAccount>(), null);

            var accounts = new List<ProxyAccount>();
            foreach (var file in files.EnumerateArray())
                accounts.Add(ParseAccount(file));

            // Unhealthy credentials first: a burned account is the only thing here that
            // needs acting on, and it should not be pushed off the bottom of the list.
            accounts.Sort((a, b) =>
            {
                var health = b.Health.CompareTo(a.Health);
                if (health != 0) return health;
                var provider = string.CompareOrdinal(a.Provider, b.Provider);
                return provider != 0 ? provider : string.CompareOrdinal(a.Account, b.Account);
            });

            return new CliProxyStatus(accounts, null);
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"cli-proxy error: {ex.Message}");
            return new CliProxyStatus(Array.Empty<ProxyAccount>(), "unreachable");
        }
    }

    private static ProxyAccount ParseAccount(JsonElement file)
    {
        var cooldowns = new List<ProxyCooldown>();
        if (file.TryGetProperty("cooldowns", out var cds) && cds.ValueKind == JsonValueKind.Array)
        {
            foreach (var cd in cds.EnumerateArray())
            {
                cooldowns.Add(new ProxyCooldown(
                    ReadString(cd, "scope"),
                    ReadString(cd, "model_key"),
                    ReadString(cd, "reason"),
                    ReadLong(cd, "remaining_seconds")));
            }
        }

        return new ProxyAccount(
            ReadString(file, "provider") ?? "?",
            ReadString(file, "account") ?? ReadString(file, "label") ?? "",
            ReadString(file, "status"),
            ExtractStatusDetail(ReadString(file, "status_message")),
            ReadBool(file, "disabled"),
            ReadBool(file, "unavailable"),
            ReadLong(file, "success"),
            ReadLong(file, "failed"),
            cooldowns);
    }

    // status_message carries the upstream error verbatim, which is usually a JSON
    // envelope. The error type ("usage_limit_reached") is the part worth a label; the
    // rest is prose that will not fit. Non-JSON messages are passed through trimmed.
    private static string? ExtractStatusDetail(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var trimmed = message.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    var type = ReadString(err, "type");
                    if (!string.IsNullOrWhiteSpace(type)) return type;
                    var msg = ReadString(err, "message");
                    if (!string.IsNullOrWhiteSpace(msg)) return Shorten(msg);
                }
            }
            catch (JsonException)
            {
                // Fall through to the raw text below.
            }
        }
        return Shorten(trimmed);
    }

    private static string Shorten(string value) =>
        value.Length <= 40 ? value : value[..39] + "\u2026";

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : 0;

    public void Dispose() => _http.Dispose();
}

// Presentation text for a credential row. Kept out of the view so the wording can be
// exercised without a display attached; the view only maps health to a colour.
public static class ProxyDisplay
{
    // "codex" alone is ambiguous once a second account exists, so an address with a plus
    // tag is labelled by that tag. Anything else is just the provider name.
    public static string AccountLabel(ProxyAccount account)
    {
        var local = account.Account;
        var at = local.IndexOf('@');
        if (at > 0) local = local[..at];
        var plus = local.IndexOf('+');
        return plus >= 0 ? $"{account.Provider} {local[plus..]}" : account.Provider;
    }

    public static string FormatRetry(long seconds)
    {
        if (seconds <= 0) return "";
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public static (string State, string Detail) Describe(ProxyAccount account)
    {
        var cooldown = account.WorstCooldown;
        var retry = cooldown is null ? "" : FormatRetry(cooldown.RemainingSeconds);
        var retryText = retry.Length > 0 ? $"retry {retry}" : "";

        if (account.Disabled)
            return ("disabled", "");

        if (account.Health == ProxyHealth.Down)
            return (account.StatusDetail ?? cooldown?.Reason ?? account.Status ?? "error", retryText);

        if (account.Health == ProxyHealth.Limited)
            // Name the throttled model when there is one: which model is cooling down is
            // the actionable part, not the bare word "quota".
            return (cooldown?.ModelKey ?? cooldown?.Reason ?? "limited", retryText);

        var counts = account.Failed > 0
            ? $"{account.Success} ok, {account.Failed} fail"
            : $"{account.Success} ok";
        return ("ready", counts);
    }
}
