using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UsageMonitor.Services;

/// <summary>
/// Windows launch-at-login via the per-user <c>HKCU\...\CurrentVersion\Run</c>
/// registry value. Per-user means no admin/UAC and it mirrors the per-user
/// LaunchAgent on macOS. The presence of the value is the source of truth.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsStartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsageMonitor";

    // Quoted full path to the running executable. Environment.ProcessPath is the
    // launchable apphost (Assembly.Location is empty for single-file publishes).
    private static string ExePath => $"\"{Environment.ProcessPath}\"";

    public bool IsSupported => true;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, ExePath);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
