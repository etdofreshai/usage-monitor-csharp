using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record UsageWindow(
    double UsedPercent,
    double? ExpectedPercent,
    double? Slack,
    DateTimeOffset? ResetsAt
);

public record ClaudeBlock(UsageWindow FiveHour, UsageWindow SevenDay, UsageWindow? SevenDayDesign, string? SubscriptionType);
public record CodexBlock(UsageWindow Primary, UsageWindow Secondary, UsageWindow? SparkPrimary, UsageWindow? SparkSecondary, string? PlanType, string? CreditsBalance);
public record ZaiBlock(UsageWindow? FiveHour, UsageWindow? Monthly, long? MonthlyCurrent, long? MonthlyLimit, string? Level);
public record OpenRouterBlock(double Usage, double? Limit, double? LimitRemaining, bool? IsFreeTier);
public record OpenAiBlock(double SpendToday, double SpendMonth, string Currency);

public record UsageApiStatus(
    DateTimeOffset Timestamp,
    ClaudeBlock? Claude,
    CodexBlock? Codex,
    ZaiBlock? Zai,
    OpenRouterBlock? OpenRouter,
    OpenAiBlock? OpenAi
);

public class UsageApiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _url;

    public UsageApiService(string baseUrl)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _url = baseUrl.TrimEnd('/') + "/api/usage";
    }

    public async Task<UsageApiStatus?> GetStatusAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(_url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"usage-api HTTP {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ts = ReadDateTimeOffset(root, "timestamp") ?? DateTimeOffset.Now;
            var providers = root.TryGetProperty("providers", out var p) ? p : default;

            return new UsageApiStatus(
                ts,
                ParseClaude(GetData(providers, "claude")),
                ParseCodex(GetData(providers, "codex")),
                ParseZai(GetData(providers, "zai")),
                ParseOpenRouter(GetData(providers, "openrouter")),
                ParseOpenAi(GetData(providers, "openai"))
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"usage-api error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static JsonElement GetData(JsonElement providers, string key)
    {
        if (providers.ValueKind != JsonValueKind.Object) return default;
        if (!providers.TryGetProperty(key, out var entry)) return default;
        if (!entry.TryGetProperty("data", out var data)) return default;
        return data.ValueKind == JsonValueKind.Null ? default : data;
    }

    private static UsageWindow ParseWindow(JsonElement el, string usedKey)
    {
        return new UsageWindow(
            ReadDouble(el, usedKey) ?? 0,
            ReadDouble(el, "expected_percent"),
            ReadDouble(el, "slack"),
            ReadDateTimeOffset(el, "resets_at")
        );
    }

    private static ClaudeBlock? ParseClaude(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("five_hour", out var five) || !data.TryGetProperty("seven_day", out var seven))
            return null;

        UsageWindow? design = null;
        if (data.TryGetProperty("seven_day_design", out var des) && des.ValueKind == JsonValueKind.Object)
            design = ParseWindow(des, "utilization");

        return new ClaudeBlock(
            ParseWindow(five, "utilization"),
            ParseWindow(seven, "utilization"),
            design,
            ReadString(data, "subscription_type")
        );
    }

    private static CodexBlock? ParseCodex(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("primary", out var pri) || !data.TryGetProperty("secondary", out var sec))
            return null;

        UsageWindow? sparkPri = null, sparkSec = null;
        if (data.TryGetProperty("additional", out var add) && add.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in add.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(entry, "name") ?? "";
                var feat = ReadString(entry, "metered_feature") ?? "";
                if (!name.Contains("spark", StringComparison.OrdinalIgnoreCase) &&
                    !feat.Equals("codex_bengalfox", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.TryGetProperty("primary", out var ep) && ep.ValueKind == JsonValueKind.Object)
                    sparkPri = ParseWindow(ep, "used_percent");
                if (entry.TryGetProperty("secondary", out var es) && es.ValueKind == JsonValueKind.Object)
                    sparkSec = ParseWindow(es, "used_percent");
                break;
            }
        }

        return new CodexBlock(
            ParseWindow(pri, "used_percent"),
            ParseWindow(sec, "used_percent"),
            sparkPri,
            sparkSec,
            ReadString(data, "plan_type"),
            ReadString(data, "credits_balance")
        );
    }

    private static ZaiBlock? ParseZai(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        UsageWindow? fiveHour = null;
        if (data.TryGetProperty("five_hour", out var five) && five.ValueKind == JsonValueKind.Object)
            fiveHour = ParseWindow(five, "used_percent");

        UsageWindow? monthly = null;
        long? monthlyCurrent = null, monthlyLimit = null;
        if (data.TryGetProperty("monthly", out var mo) && mo.ValueKind == JsonValueKind.Object)
        {
            monthly = ParseWindow(mo, "used_percent");
            monthlyCurrent = ReadInt64(mo, "current");
            monthlyLimit = ReadInt64(mo, "limit");
        }

        return new ZaiBlock(fiveHour, monthly, monthlyCurrent, monthlyLimit, ReadString(data, "level"));
    }

    private static OpenRouterBlock? ParseOpenRouter(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return new OpenRouterBlock(
            ReadDouble(data, "usage") ?? 0,
            ReadDouble(data, "limit"),
            ReadDouble(data, "limit_remaining"),
            ReadBool(data, "is_free_tier")
        );
    }

    private static OpenAiBlock? ParseOpenAi(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return new OpenAiBlock(
            ReadDouble(data, "spend_today") ?? 0,
            ReadDouble(data, "spend_month") ?? 0,
            ReadString(data, "currency") ?? "USD"
        );
    }

    private static double? ReadDouble(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    private static long? ReadInt64(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    }

    private static bool? ReadBool(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static string? ReadString(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement el, string key)
    {
        var s = ReadString(el, key);
        return DateTimeOffset.TryParse(s, out var t) ? t : null;
    }
}
