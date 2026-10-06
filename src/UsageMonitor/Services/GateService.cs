using System.Text.Json;

namespace UsageMonitor.Services;

// One 9gate exchange, reduced to what the monitor shows. Family is the model
// auto-route actually picked (opus, sol, luna...), or the requested model when
// the client asked for a concrete one.
public record GateRequest(DateTimeOffset StartedAt, string Family, string? Effort, string? Resolved, string? Outcome, string? Title, string? Host);

public class GateService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _url;

    public GateService(string baseUrl) => _url = baseUrl.TrimEnd('/') + "/_9gate/exchanges";

    // Newest first, as 9gate returns them. In-flight exchanges have no meta yet and are skipped.
    public async Task<IReadOnlyList<GateRequest>?> GetRecentAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(_url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("exchanges", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<GateRequest>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!DateTimeOffset.TryParse(Str(e, "startedAt"), out var at)) continue;
                e.TryGetProperty("jev", out var jev);
                var family = Family(Str(jev, "model") ?? Str(e, "model"));
                list.Add(new GateRequest(at, family, Str(jev, "effort"), Str(jev, "resolved"),
                    Str(e, "outcome"), Str(e, "title"), Str(e, "host")));
            }
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"9gate error: {ex.Message}");
            return null;
        }
    }

    // "cc/claude-opus-5-5", "gpt-6-luna", "opus" all collapse to one family name.
    private static readonly string[] Families = ["fable", "astra", "opus", "sol", "sonnet", "terra", "luna", "glm", "haiku"];

    public static string Family(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "?";
        var m = model.ToLowerInvariant();
        foreach (var f in Families)
            if (m.Contains(f)) return f;
        var slash = m.LastIndexOf('/');
        return slash >= 0 ? m[(slash + 1)..] : m;
    }

    private static string? Str(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}
