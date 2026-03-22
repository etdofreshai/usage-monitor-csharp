using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record OpenAiCostBucket(DateTime StartTime, DateTime EndTime, double AmountUsd);

public record OpenAiStatus(
    double TodayCostUsd,
    double MonthCostUsd,
    double? RemainingCredits // Only if prepaid balance is configured
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

    public async Task<OpenAiStatus?> GetStatusAsync()
    {
        if (string.IsNullOrEmpty(_adminKey)) return null;

        try
        {
            // Get costs for current month
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var todayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

            var monthStartUnix = new DateTimeOffset(monthStart).ToUnixTimeSeconds();

            var url = $"https://api.openai.com/v1/organization/costs?start_time={monthStartUnix}&bucket_width=1d";
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            double monthTotal = 0;
            double todayTotal = 0;

            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var bucket in dataArray.EnumerateArray())
                {
                    if (bucket.TryGetProperty("results", out var results))
                    {
                        foreach (var result in results.EnumerateArray())
                        {
                            if (result.TryGetProperty("amount", out var amount) &&
                                amount.TryGetProperty("value", out var value))
                            {
                                double cost = value.GetDouble();
                                monthTotal += cost;

                                // Check if this bucket is today
                                if (bucket.TryGetProperty("start_time", out var startTimeEl))
                                {
                                    var bucketStart = DateTimeOffset.FromUnixTimeSeconds(startTimeEl.GetInt64()).UtcDateTime;
                                    if (bucketStart.Date == todayStart.Date)
                                    {
                                        todayTotal += cost;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            double? remaining = _prepaidBalance > 0 ? _prepaidBalance - monthTotal : null;

            return new OpenAiStatus(todayTotal, monthTotal, remaining);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenAI API error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
