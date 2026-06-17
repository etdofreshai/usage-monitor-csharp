using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UsageMonitor.Services;

namespace UsageMonitor;

public partial class App : Application
{
    private Window? _hiddenWindow;
    private TrayIcon? _trayIcon;
    private UsagePopup? _popup;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!OperatingSystem.IsMacOS())
            {
                _hiddenWindow = new Window
                {
                    Width = 1,
                    Height = 1,
                    ShowInTaskbar = false,
                    SystemDecorations = SystemDecorations.None,
                    Opacity = 0,
                    Focusable = false,
                    CanResize = false,
                };
                _hiddenWindow.Opened += (s, e) =>
                {
                    _hiddenWindow.Position = new PixelPoint(-10000, -10000);
                };
                desktop.MainWindow = _hiddenWindow;
            }

            // Create the usage popup (starts hidden)
            _popup = new UsagePopup();

            // Create tray icon
            var trayMenu = new NativeMenu();

            var showItem = new NativeMenuItem("Show Usage Monitor");
            showItem.Click += (s, e) => Dispatcher.UIThread.Post(() => _popup.TogglePopup());
            trayMenu.Items.Add(showItem);

            trayMenu.Items.Add(new NativeMenuItemSeparator());

            // "Run on system start" toggle — only shown on platforms we support.
            // The OS (LaunchAgent plist / HKCU Run key) is the source of truth, so
            // we read the live state to set the checkmark and re-read after toggling.
            var startup = StartupService.Create();
            if (startup.IsSupported)
            {
                var startupItem = new NativeMenuItem("Run on system start")
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = startup.IsEnabled(),
                };
                startupItem.Click += (s, e) => Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (startup.IsEnabled())
                            startup.Disable();
                        else
                            startup.Enable();
                    }
                    catch (Exception ex)
                    {
                        AppLog.WriteLine($"Run-on-system-start toggle failed: {ex.Message}");
                    }

                    // Re-read OS state so a partial/failed change shows honestly.
                    startupItem.IsChecked = startup.IsEnabled();
                });
                trayMenu.Items.Add(startupItem);

                trayMenu.Items.Add(new NativeMenuItemSeparator());
            }

            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (s, e) =>
            {
                _popup?.ForceClose();
                desktop.Shutdown();
            };
            trayMenu.Items.Add(quitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "Usage Monitor",
                Icon = AppIcon.Create(),
                Menu = trayMenu,
                IsVisible = true
            };

            // Click tray icon to toggle popup
            _trayIcon.Clicked += (s, e) => Dispatcher.UIThread.Post(() => _popup.TogglePopup());

            var icons = new TrayIcons { _trayIcon };
            SetValue(TrayIcon.IconsProperty, icons);

            // When launched at login (via the LaunchAgent's --from-login arg), stay
            // quietly in the tray instead of popping up the panel on every boot.
            var startedFromLogin = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--from-login", StringComparison.OrdinalIgnoreCase));
            if (!startedFromLogin)
                Dispatcher.UIThread.Post(() => _popup.ShowPopup());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
