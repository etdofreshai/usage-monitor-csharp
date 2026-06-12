using System.Text.Json;

namespace UsageMonitor;

public class Config
{
    private static string GetConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = Path.Combine(home, ".config");
        }
        return Path.Combine(appData, "UsageMonitor");
    }

    private static string ConfigDirectory => GetConfigDirectory();
    private static string ConfigFilePath => Path.Combine(ConfigDirectory, "config.json");

    // Where to fetch usage from. The server aggregates Claude/Codex/Z.ai/OpenRouter/OpenAI
    // and exposes a single /api/usage endpoint — see https://github.com/etdofreshai/usage-api.
    public string UsageApiUrl { get; set; } = "https://usage.etdofresh.com";

    // Refresh interval in seconds. Default 5 — usage-api caches snapshots so polling
    // fast is cheap on its end.
    public int RefreshIntervalSeconds { get; set; } = 5;

    // Second Claude account. Renders only when the server exposes providers.claude2
    // AND this flag is true — default true so enabling it server-side lights it up;
    // set false per machine to opt out.
    public bool ShowClaude2 { get; set; } = true;

    // Path to the local git checkout used for auto-update. null disables update checks.
    public string? RepoPath { get; set; }

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<Config>(json);
                if (config != null)
                {
                    OverrideFromEnv(config);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading config: {ex.Message}");
        }

        var defaultConfig = new Config();
        OverrideFromEnv(defaultConfig);
        defaultConfig.Save();
        return defaultConfig;
    }

    private static void OverrideFromEnv(Config config)
    {
        var url = Environment.GetEnvironmentVariable("USAGE_API_URL");
        if (!string.IsNullOrEmpty(url))
            config.UsageApiUrl = url;

        var repo = Environment.GetEnvironmentVariable("USAGE_MONITOR_REPO");
        if (!string.IsNullOrEmpty(repo))
            config.RepoPath = repo;

        var showClaude2 = Environment.GetEnvironmentVariable("USAGE_MONITOR_SHOW_CLAUDE2");
        switch (showClaude2?.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "yes" or "on":
                config.ShowClaude2 = true;
                break;
            case "0" or "false" or "no" or "off":
                config.ShowClaude2 = false;
                break;
        }
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(ConfigDirectory))
            {
                Directory.CreateDirectory(ConfigDirectory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    public static string GetConfigPath() => ConfigFilePath;
}
