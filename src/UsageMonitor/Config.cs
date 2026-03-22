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

    // API Keys
    public string OpenAiAdminKey { get; set; } = "";
    public string OpenRouterApiKey { get; set; } = "";
    public string AnthropicAdminKey { get; set; } = "";
    public string ZaiApiKey { get; set; } = "";

    // Known balances (for services without a "remaining" endpoint)
    public double OpenAiPrepaidBalance { get; set; } = 0;
    public double AnthropicPrepaidBalance { get; set; } = 0;

    // Refresh interval in seconds
    public int RefreshIntervalSeconds { get; set; } = 30;

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
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_ADMIN_KEY");
        if (!string.IsNullOrEmpty(openAiKey))
            config.OpenAiAdminKey = openAiKey;

        var openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrEmpty(openRouterKey))
            config.OpenRouterApiKey = openRouterKey;

        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_ADMIN_KEY");
        if (!string.IsNullOrEmpty(anthropicKey))
            config.AnthropicAdminKey = anthropicKey;

        var zaiKey = Environment.GetEnvironmentVariable("ZAI_API_KEY");
        if (!string.IsNullOrEmpty(zaiKey))
            config.ZaiApiKey = zaiKey;
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
