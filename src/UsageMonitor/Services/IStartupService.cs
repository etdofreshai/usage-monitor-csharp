namespace UsageMonitor.Services;

/// <summary>
/// Abstracts per-user "launch at login" across platforms. The OS itself is the
/// single source of truth (a LaunchAgent plist on macOS, an HKCU Run value on
/// Windows) — there is no cached/config-backed flag that could drift out of sync
/// with what the user sees in System Settings.
/// </summary>
public interface IStartupService
{
    /// <summary>True when this platform has a launch-at-login implementation wired up.</summary>
    bool IsSupported { get; }

    /// <summary>Whether the app is currently registered to run at login.</summary>
    bool IsEnabled();

    /// <summary>Register the app to run at login.</summary>
    void Enable();

    /// <summary>Unregister the app from running at login.</summary>
    void Disable();
}

/// <summary>Factory that picks the right <see cref="IStartupService"/> for the host OS.</summary>
public static class StartupService
{
    public static IStartupService Create()
        => OperatingSystem.IsMacOS() ? new MacStartupService()
         : OperatingSystem.IsWindows() ? new WindowsStartupService()
         : new NoOpStartupService();
}
