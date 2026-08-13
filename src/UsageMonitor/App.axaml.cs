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

            var checkForUpdatesItem = new NativeMenuItem("Check for Updates");
            checkForUpdatesItem.Click += async (s, e) =>
            {
                checkForUpdatesItem.IsEnabled = false;
                checkForUpdatesItem.Header = "Checking for Updates…";
                try
                {
                    var result = await _popup.CheckForUpdatesAsync();
                    Dispatcher.UIThread.Post(() =>
                    {
                        checkForUpdatesItem.Header = result switch
                        {
                            UpdateChecker.CheckResult.UpdateAvailable => "Update Ready — Open Monitor",
                            UpdateChecker.CheckResult.UpToDate => "Up to Date",
                            UpdateChecker.CheckResult.Disabled => "Update Checks Unavailable",
                            UpdateChecker.CheckResult.Failed => "Update Check Failed",
                            _ => "Already Checking for Updates",
                        };
                        checkForUpdatesItem.IsEnabled = true;
                        if (result == UpdateChecker.CheckResult.UpdateAvailable)
                            _popup.ShowPopup();
                    });
                }
                catch (Exception ex)
                {
                    AppLog.WriteLine($"Manual update check failed: {ex.Message}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        checkForUpdatesItem.Header = "Update Check Failed";
                        checkForUpdatesItem.IsEnabled = true;
                    });
                }
            };
            trayMenu.Items.Add(checkForUpdatesItem);

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

            // Provider show/hide toggles, directly in the menu under a "Providers" header.
            // The popup owns the config; these items just read/flip its state and the popup
            // re-renders live. Checkmarks reflect the persisted per-provider flags.
            trayMenu.Items.Add(new NativeMenuItem("Providers") { IsEnabled = false });
            foreach (var (key, label) in UsagePopup.ProviderToggles)
            {
                var toggleKey = key;
                var providerItem = new NativeMenuItem(label)
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = _popup.IsProviderVisible(toggleKey),
                };
                providerItem.Click += (s, e) => Dispatcher.UIThread.Post(() =>
                {
                    _popup.SetProviderVisible(toggleKey, !_popup.IsProviderVisible(toggleKey));
                    providerItem.IsChecked = _popup.IsProviderVisible(toggleKey);
                });
                trayMenu.Items.Add(providerItem);
            }

            if (_popup.DriveToggles.Count > 0)
            {
                trayMenu.Items.Add(new NativeMenuItemSeparator());
                trayMenu.Items.Add(new NativeMenuItem("Drives") { IsEnabled = false });
                foreach (var drive in _popup.DriveToggles)
                {
                    var driveKey = drive.Key;
                    var driveItem = new NativeMenuItem(drive.Label)
                    {
                        ToggleType = NativeMenuItemToggleType.CheckBox,
                        IsChecked = _popup.IsDriveVisible(driveKey),
                    };
                    driveItem.Click += (s, e) => Dispatcher.UIThread.Post(() =>
                    {
                        _popup.SetDriveVisible(driveKey, !_popup.IsDriveVisible(driveKey));
                        driveItem.IsChecked = _popup.IsDriveVisible(driveKey);
                    });
                    trayMenu.Items.Add(driveItem);
                }
            }

            trayMenu.Items.Add(new NativeMenuItemSeparator());

            var quitItem = new NativeMenuItem("Quit");
            // Post like every other item: on macOS this handler fires inside the
            // NSStatusItem menu-tracking loop, and stopping the app from within it
            // frequently leaves the run loop wedged (the classic "Quit hangs" bug).
            quitItem.Click += (s, e) => Dispatcher.UIThread.Post(() =>
            {
                // Pull the status item out of the menu bar before stopping the run
                // loop — a live NSStatusItem can pin the dying process on macOS.
                if (_trayIcon != null)
                    _trayIcon.IsVisible = false;
                _popup?.ForceClose(); // tears down services and ends the lifetime
            });
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

            // OS-initiated termination (Cmd+Q, Activity Monitor quit, logout,
            // system shutdown): release timers/services up front. The popup's
            // OnClosing already scopes its hide-on-close veto to user closes, so
            // the lifetime can close our windows and terminate cleanly.
            desktop.ShutdownRequested += (_, _) => _popup?.PrepareShutdown();

            desktop.Exit += (_, e) =>
            {
                if (_trayIcon != null)
                    _trayIcon.IsVisible = false;
                _trayIcon?.Dispose();
                // Watchdog: if the native run loop fails to unwind after shutdown
                // (macOS NSApp.stop() only takes effect once another real event is
                // processed), don't leave a ghost process in the background.
                var exitCode = e.ApplicationExitCode;
                new Thread(() =>
                {
                    Thread.Sleep(3000);
                    try { AppLog.WriteLine("Exit watchdog: run loop did not unwind after shutdown; forcing process exit."); }
                    catch { /* racing normal teardown — exit regardless */ }
                    Environment.Exit(exitCode);
                }) { IsBackground = true }.Start();
            };

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
