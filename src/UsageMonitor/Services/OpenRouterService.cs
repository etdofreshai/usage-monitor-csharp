using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record OpenRouterStatus(
    double? TotalCredits,
    double? TotalUsage,
    double? LimitRemaining,
    double? Limit,
    double Usage,
    double UsageDaily,
    double UsageWeekly,
    double UsageMonthly,
    string? LimitReset,
    bool IsFreeTier,
    string? Label
);

public class OpenRouterService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public OpenRouterService(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<OpenRouterStatus?> GetStatusAsync()
    {
        if (string.IsNullOrEmpty(_apiKey)) return null;

        try
        {
            var response = await _http.GetAsync("https://openrouter.ai/api/v1/key");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            double? limit = data.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind != JsonValueKind.Null
                ? limitEl.GetDouble() : null;
            double? limitRemaining = data.TryGetProperty("limit_remaining", out var lrEl) && lrEl.ValueKind != JsonValueKind.Null
                ? lrEl.GetDouble() : null;
            double usage = data.TryGetProperty("usage", out var usageEl) ? usageEl.GetDouble() : 0;
            double usageDaily = data.TryGetProperty("usage_daily", out var udEl) ? udEl.GetDouble() : 0;
            double usageWeekly = data.TryGetProperty("usage_weekly", out var uwEl) ? uwEl.GetDouble() : 0;
            double usageMonthly = data.TryGetProperty("usage_monthly", out var umEl) ? umEl.GetDouble() : 0;
            string? limitReset = data.TryGetProperty("limit_reset", out var resetEl) && resetEl.ValueKind != JsonValueKind.Null
                ? resetEl.GetString() : null;
            bool isFreeTier = data.TryGetProperty("is_free_tier", out var ftEl) && ftEl.GetBoolean();
            string? label = data.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null;

            double? totalCredits = null;
            double? totalUsage = null;

            try
            {
                var creditsResponse = await _http.GetAsync("https://openrouter.ai/api/v1/credits");
                if (creditsResponse.IsSuccessStatusCode)
                {
                    var creditsJson = await creditsResponse.Content.ReadAsStringAsync();
                    using var creditsDoc = JsonDocument.Parse(creditsJson);
                    var creditsData = creditsDoc.RootElement.GetProperty("data");

                    totalCredits = creditsData.TryGetProperty("total_credits", out var tcEl) && tcEl.ValueKind != JsonValueKind.Null
                        ? tcEl.GetDouble() : null;
                    totalUsage = creditsData.TryGetProperty("total_usage", out var tuEl) && tuEl.ValueKind != JsonValueKind.Null
                        ? tuEl.GetDouble() : null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OpenRouter credits API error: {ex.Message}");
            }

            return new OpenRouterStatus(
                totalCredits,
                totalUsage,
                limitRemaining,
                limit,
                usage,
                usageDaily,
                usageWeekly,
                usageMonthly,
                limitReset,
                isFreeTier,
                label
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenRouter API error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
