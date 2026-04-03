using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record ZaiQuota(
    string Type,
    long? Used,
    long? Limit,
    long? CurrentValue,
    long? Remaining,
    double? Percentage,
    DateTimeOffset? ResetsAt,
    IReadOnlyList<ZaiUsageDetail> UsageDetails
);

public record ZaiUsageDetail(string ModelCode, long Usage);

public record ZaiStatus(List<ZaiQuota> Quotas, string? Level);

public class ZaiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public ZaiService(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<ZaiStatus?> GetStatusAsync()
    {
        if (string.IsNullOrEmpty(_apiKey)) return null;

        try
        {
            var response = await _http.GetAsync("https://api.z.ai/api/monitor/usage/quota/limit");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var quotas = new List<ZaiQuota>();
            string? level = null;

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("limits", out var limits) &&
                        limits.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in limits.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object)
                                continue;

                            quotas.Add(ParseQuota(item));
                        }
                    }

                    if (data.TryGetProperty("level", out var levelEl) &&
                        levelEl.ValueKind == JsonValueKind.String)
                    {
                        level = levelEl.GetString();
                    }
                }
            }

            if (quotas.Count == 0)
            {
                Console.WriteLine($"Z.ai raw response: {json}");
                quotas.Add(new ZaiQuota("API_RESPONSE", null, null, null, null, null, null, Array.Empty<ZaiUsageDetail>()));
            }

            return new ZaiStatus(quotas, level);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Z.ai API error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static ZaiQuota ParseQuota(JsonElement item)
    {
        var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "UNKNOWN" : "UNKNOWN";

        long? used = null;
        long? limit = null;
        long? currentValue = null;
        long? remaining = null;
        double? percentage = null;
        DateTimeOffset? resetsAt = null;

        if (item.TryGetProperty("usage", out var usageEl) && usageEl.TryGetInt64(out var usage))
        {
            limit = usage;
        }

        if (item.TryGetProperty("currentValue", out var currentValueEl) && currentValueEl.TryGetInt64(out var current))
        {
            currentValue = current;
            used = current;
        }

        if (item.TryGetProperty("remaining", out var remainingEl) && remainingEl.TryGetInt64(out var remainingValue))
        {
            remaining = remainingValue;
        }

        if (limit.HasValue && used.HasValue && limit.Value > 0)
        {
            percentage = Math.Clamp((double)used.Value / limit.Value * 100.0, 0, 100);
        }
        else if (item.TryGetProperty("percentage", out var percentageEl) && percentageEl.TryGetDouble(out var rawPercentage))
        {
            percentage = rawPercentage;
        }

        if (item.TryGetProperty("nextResetTime", out var resetEl) &&
            resetEl.ValueKind == JsonValueKind.Number &&
            resetEl.TryGetInt64(out var resetMs))
        {
            resetsAt = DateTimeOffset.FromUnixTimeMilliseconds(resetMs);
        }

        var usageDetails = new List<ZaiUsageDetail>();
        if (item.TryGetProperty("usageDetails", out var detailsEl) &&
            detailsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in detailsEl.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object)
                    continue;

                var modelCode = detail.TryGetProperty("modelCode", out var codeEl) ? codeEl.GetString() ?? "unknown" : "unknown";
                var detailUsage = detail.TryGetProperty("usage", out var usageDetailEl) && usageDetailEl.TryGetInt64(out var usageCount)
                    ? usageCount
                    : 0;

                usageDetails.Add(new ZaiUsageDetail(modelCode, detailUsage));
            }
        }

        return new ZaiQuota(type, used, limit, currentValue, remaining, percentage, resetsAt, usageDetails);
    }
}
