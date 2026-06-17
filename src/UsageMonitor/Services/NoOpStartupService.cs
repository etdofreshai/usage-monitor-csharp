namespace UsageMonitor.Services;

/// <summary>Fallback for platforms without a launch-at-login implementation; the menu item hides itself.</summary>
internal sealed class NoOpStartupService : IStartupService
{
    public bool IsSupported => false;
    public bool IsEnabled() => false;
    public void Enable() { }
    public void Disable() { }
}
