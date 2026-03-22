using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record AnthropicStatus(
    double TodayCostUsd,
    double MonthCostUsd,
    double? RemainingCredits // Only if prepaid balance is configured
);

public class AnthropicService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _adminKey;
    private readonly double _prepaidBalance;

    public AnthropicService(string adminKey, double prepaidBalance = 0)
    {
        _adminKey = adminKey;
        _prepaidBalance = prepaidBalance;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("x-api-key", adminKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<AnthropicStatus?> GetStatusAsync()
    {
        if (string.IsNullOrEmpty(_adminKey)) return null;

        try
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var todayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

            var startingAt = monthStart.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var url = $"https://api.anthropic.com/v1/organizations/cost_report?starting_at={startingAt}&bucket_width=1d";
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
                    DateTime? bucketDate = null;
                    if (bucket.TryGetProperty("starting_at", out var startEl))
                    {
                        bucketDate = DateTime.Parse(startEl.GetString()!).ToUniversalTime();
                    }

                    if (bucket.TryGetProperty("results", out var results))
                    {
                        foreach (var result in results.EnumerateArray())
                        {
                            if (result.TryGetProperty("amount", out var amountEl))
                            {
                                // Amount is in cents as a string
                                double cents = 0;
                                if (amountEl.ValueKind == JsonValueKind.String)
                                    double.TryParse(amountEl.GetString(), out cents);
                                else if (amountEl.ValueKind == JsonValueKind.Number)
                                    cents = amountEl.GetDouble();

                                double dollars = cents / 100.0;
                                monthTotal += dollars;

                                if (bucketDate?.Date == todayStart.Date)
                                {
                                    todayTotal += dollars;
                                }
                            }
                        }
                    }
                }
            }

            double? remaining = _prepaidBalance > 0 ? _prepaidBalance - monthTotal : null;

            return new AnthropicStatus(todayTotal, monthTotal, remaining);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Anthropic API error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
