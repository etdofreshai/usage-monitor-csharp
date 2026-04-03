using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record OpenAiCostStatus(
    double SpendToday,
    double SpendMonth,
    double? PrepaidBalance,
    double? Remaining
);

public class OpenAiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _adminKey;
    private readonly double _prepaidBalance;

    public OpenAiService(string adminKey, double prepaidBalance = 0)
    {
        _adminKey = adminKey;
        _prepaidBalance = prepaidBalance;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
    }

    public async Task<OpenAiCostStatus?> GetStatusAsync()
    {
        if (string.IsNullOrEmpty(_adminKey)) return null;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

            // Fetch costs for the entire month (covers both today and month-to-date)
            var monthStartUnix = monthStart.ToUnixTimeSeconds();
            var nowUnix = now.ToUnixTimeSeconds();
            var todayStartUnix = todayStart.ToUnixTimeSeconds();

            double spendMonth = 0;
            double spendToday = 0;
            string? nextPage = null;

            do
            {
                var url = $"https://api.openai.com/v1/organization/costs?start_time={monthStartUnix}&end_time={nowUnix}&limit=31";
                if (nextPage != null)
                    url += $"&page={nextPage}";

                var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bucket in data.EnumerateArray())
                    {
                        long bucketStart = 0;
                        if (bucket.TryGetProperty("start_time", out var stEl))
                            bucketStart = stEl.GetInt64();

                        if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var result in results.EnumerateArray())
                            {
                                if (result.TryGetProperty("amount", out var amount) &&
                                    amount.TryGetProperty("value", out var valueEl))
                                {
                                    if (double.TryParse(valueEl.GetString(), System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var cost))
                                    {
                                        spendMonth += cost;
                                        if (bucketStart >= todayStartUnix)
                                            spendToday += cost;
                                    }
                                }
                            }
                        }
                    }
                }

                nextPage = null;
                if (root.TryGetProperty("has_more", out var hasMore) && hasMore.GetBoolean() &&
                    root.TryGetProperty("next_page", out var np) && np.ValueKind == JsonValueKind.String)
                {
                    nextPage = np.GetString();
                }
            } while (nextPage != null);

            double? prepaid = _prepaidBalance > 0 ? _prepaidBalance : null;
            double? remaining = prepaid.HasValue ? Math.Max(0, prepaid.Value - spendMonth) : null;

            return new OpenAiCostStatus(spendToday, spendMonth, prepaid, remaining);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenAI API error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
