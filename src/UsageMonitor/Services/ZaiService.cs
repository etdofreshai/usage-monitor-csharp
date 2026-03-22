using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageMonitor.Services;

public record ZaiQuota(
    string Name,
    long Used,
    long Limit,
    string? ResetsAt
);

public record ZaiStatus(List<ZaiQuota> Quotas);

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

            // Try to parse the response — structure is not well documented
            // so we handle it flexibly
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Try to find quota data in the response
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        long used = 0, limit = 0;
                        string? resetsAt = null;

                        if (prop.Value.TryGetProperty("used", out var usedEl))
                            used = usedEl.GetInt64();
                        if (prop.Value.TryGetProperty("limit", out var limitEl))
                            limit = limitEl.GetInt64();
                        if (prop.Value.TryGetProperty("reset_at", out var resetEl))
                            resetsAt = resetEl.GetString();

                        if (limit > 0 || used > 0)
                        {
                            quotas.Add(new ZaiQuota(prop.Name, used, limit, resetsAt));
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                string name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? prop.Name : prop.Name;
                                long used = item.TryGetProperty("used", out var usedEl) ? usedEl.GetInt64() : 0;
                                long limit = item.TryGetProperty("limit", out var limitEl) ? limitEl.GetInt64() : 0;
                                string? resetsAt = item.TryGetProperty("reset_at", out var resetEl) ? resetEl.GetString() : null;

                                quotas.Add(new ZaiQuota(name, used, limit, resetsAt));
                            }
                        }
                    }
                }
            }

            // If we couldn't parse structured data, log the raw response for debugging
            if (quotas.Count == 0)
            {
                Console.WriteLine($"Z.ai raw response: {json}");
                // Return a status with the raw data indicator
                quotas.Add(new ZaiQuota("API Response", 0, 0, null));
            }

            return new ZaiStatus(quotas);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Z.ai API error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
