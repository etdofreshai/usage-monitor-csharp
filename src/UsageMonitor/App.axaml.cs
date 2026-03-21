using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

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
            // Create a hidden window to keep the app running
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

            // Don't shutdown when the hidden window closes
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Create the usage popup (starts hidden)
            _popup = new UsagePopup();

            // Create tray icon
            var trayMenu = new NativeMenu();

            var showItem = new NativeMenuItem("Show Usage Monitor");
            showItem.Click += (s, e) => Dispatcher.UIThread.Post(() => _popup.TogglePopup());
            trayMenu.Items.Add(showItem);

            trayMenu.Items.Add(new NativeMenuItemSeparator());

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
                Menu = trayMenu,
                IsVisible = true
            };

            // Click tray icon to toggle popup
            _trayIcon.Clicked += (s, e) => Dispatcher.UIThread.Post(() => _popup.TogglePopup());

            var icons = new TrayIcons { _trayIcon };
            SetValue(TrayIcon.IconsProperty, icons);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
