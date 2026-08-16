using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageMonitor;

public class Config
{
    // Property names whose live value came from an environment override this run. These
    // are NOT written to config.json, so removing the env var later reverts to the user's
    // real on-disk preference instead of the override becoming sticky. Private = not serialized.
    private readonly HashSet<string> _envOverridden = new();

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

    // Per-provider visibility, mostly toggled live from the tray "Providers" menu.
    // Codex #2 is config/env-only so it leaves no menu trace when unconfigured. Each
    // is AND-gated with data presence. All default true. Env: USAGE_MONITOR_SHOW_*.
    public bool ShowCodex { get; set; } = true;
    // Account #2 remains absent unless usage-api exposes providers.codex2.
    public bool ShowCodex2 { get; set; } = true;
    public bool ShowCodexSpark { get; set; } = true;
    public bool ShowClaude { get; set; } = true;
    public bool ShowClaudeDesign { get; set; } = true;
    public bool ShowClaude2Design { get; set; } = true;
    public bool ShowOpenAi { get; set; } = true;
    public bool ShowOpenRouter { get; set; } = true;
    public bool ShowZai { get; set; } = true;
    public bool ShowZaiRequests { get; set; } = true;

    // Per-drive visibility, keyed by the drive root/mount path. Missing entries
    // default to visible so newly attached fixed drives appear automatically.
    public Dictionary<string, bool> DriveVisibility { get; set; } = new();

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
                    // Before this setting existed, ShowZai controlled both token usage
                    // and the unrelated Web/MCP request allowance. Preserve that intent
                    // for existing configs instead of unexpectedly re-enabling requests.
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root is null || !root.ContainsKey(nameof(ShowZaiRequests)))
                        config.ShowZaiRequests = config.ShowZai;
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
        {
            config.UsageApiUrl = url;
            config._envOverridden.Add(nameof(UsageApiUrl));
        }

        var repo = Environment.GetEnvironmentVariable("USAGE_MONITOR_REPO");
        if (!string.IsNullOrEmpty(repo))
        {
            config.RepoPath = repo;
            config._envOverridden.Add(nameof(RepoPath));
        }

        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_CODEX", nameof(ShowCodex), v => config.ShowCodex = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_CODEX2", nameof(ShowCodex2), v => config.ShowCodex2 = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_CODEX_SPARK", nameof(ShowCodexSpark), v => config.ShowCodexSpark = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_CLAUDE", nameof(ShowClaude), v => config.ShowClaude = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_CLAUDE2", nameof(ShowClaude2), v => config.ShowClaude2 = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_CLAUDE_DESIGN", nameof(ShowClaudeDesign), v => config.ShowClaudeDesign = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_CLAUDE2_DESIGN", nameof(ShowClaude2Design), v => config.ShowClaude2Design = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_OPENAI", nameof(ShowOpenAi), v => config.ShowOpenAi = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_OPENROUTER", nameof(ShowOpenRouter), v => config.ShowOpenRouter = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_ZAI", nameof(ShowZai), v => config.ShowZai = v);
        ApplyFlagEnv(config, "USAGE_MONITOR_SHOW_ZAI_REQUESTS", nameof(ShowZaiRequests), v => config.ShowZaiRequests = v);
    }

    private static void ApplyFlagEnv(Config config, string envVar, string propertyName, Action<bool> set)
    {
        if (ParseBoolEnv(envVar) is bool value)
        {
            set(value);
            config._envOverridden.Add(propertyName);
        }
    }

    // Parses a tri-state on/off env var: true/false when recognized, null when unset
    // or unrecognized (so the config/default value is left untouched).
    private static bool? ParseBoolEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null,
        };
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
            var node = JsonSerializer.SerializeToNode(this, options)!.AsObject();

            // Don't persist env-sourced overrides: restore each overridden key to its
            // existing on-disk value (or drop it) so a transient override never becomes
            // a sticky preference once the env var is removed.
            if (_envOverridden.Count > 0)
            {
                JsonObject? onDisk = null;
                if (File.Exists(ConfigFilePath))
                {
                    try { onDisk = JsonNode.Parse(File.ReadAllText(ConfigFilePath)) as JsonObject; }
                    catch { onDisk = null; }
                }

                foreach (var name in _envOverridden)
                {
                    if (onDisk is not null && onDisk.TryGetPropertyValue(name, out var prev) && prev is not null)
                        node[name] = prev.DeepClone();
                    else
                        node.Remove(name);
                }
            }

            File.WriteAllText(ConfigFilePath, node.ToJsonString(options));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    public bool IsDriveVisible(string driveKey) =>
        DriveVisibility is null || !DriveVisibility.TryGetValue(driveKey, out var visible) || visible;

    public void SetDriveVisible(string driveKey, bool visible)
    {
        DriveVisibility ??= new Dictionary<string, bool>();
        DriveVisibility[driveKey] = visible;
        Save();
    }

    public static string GetConfigPath() => ConfigFilePath;
}
