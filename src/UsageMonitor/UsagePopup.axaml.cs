using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UsageMonitor.Services;

namespace UsageMonitor;

public partial class UsagePopup : Window
{
    private enum PopupViewMode
    {
        Full,
        Compact,
        IconOnly,
    }

    private const double FullWidth = 572;
    private const double FullHeight = 450;
    private const double CompactWidth = 390;
    private const double CompactHeight = 124;
    private const double IconOnlySize = 54;

    private readonly Config _config;
    private readonly DispatcherTimer _systemRefreshTimer;
    private readonly DispatcherTimer _aiRefreshTimer;
    private readonly Bitmap _appBitmap;
    private PopupViewMode _viewMode = PopupViewMode.Full;
    private bool _allowClose;
    private bool _shutdownStarted;

    // Drag state
    private bool _isDragging;
    private PixelPoint _dragStartScreenPoint;
    private PixelPoint _windowStartPosition;

    // Pinned bottom-right corner in screen pixels. When set, the window re-anchors
    // here on every Bounds change so async content growth (AI sections appearing,
    // view-mode switches) does not push the bottom edge past the taskbar.
    // Cleared the moment the user drags, so a dragged window stays put.
    private PixelPoint? _anchorBottomRight;

    // Network tracking
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private DateTime _lastNetworkCheck = DateTime.MinValue;

    // CPU tracking. Windows uses PerformanceCounter; Unix-like systems sample
    // cumulative kernel counters and calculate the delta between refreshes.
    private PerformanceCounter? _cpuCounter;
    private ulong _lastCpuTotal;
    private ulong _lastCpuIdle;
    private bool _hasCpuSample;
    private double _cpuPercent;
    private double _memoryPercent;
    private double _memoryUsedGB;
    private double _memoryTotalGB;

    public sealed record DriveToggle(string Key, string Label);

    private sealed class DriveDisplay
    {
        public required DriveInfo Drive { get; init; }
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required Grid FullRow { get; init; }
        public required Grid FullBar { get; init; }
        public required Border FullFill { get; init; }
        public required TextBlock FullUsage { get; init; }
        public required Grid CompactRow { get; init; }
        public required Border CompactFill { get; init; }
        public required TextBlock CompactPercent { get; init; }
        public required TextBlock CompactUsage { get; init; }
    }

    private readonly List<DriveDisplay> _driveDisplays = new();
    public IReadOnlyList<DriveToggle> DriveToggles { get; private set; } = Array.Empty<DriveToggle>();

    // Latest reset timestamps (driven by RefreshXxxAsync, read by UpdateCompactSummary)
    private DateTimeOffset? _codex5hReset, _codex7dReset;
    private DateTimeOffset? _codexSpark5hReset, _codexSpark7dReset;
    private DateTimeOffset? _codex2FiveHourReset, _codex2SevenDayReset;
    private DateTimeOffset? _codex2SparkFiveHourReset, _codex2SparkSevenDayReset;
    private DateTimeOffset? _claude5hReset, _claude7dReset, _claudeDesignReset;
    private DateTimeOffset? _claude2FiveHourReset, _claude2SevenDayReset, _claude2DesignReset;
    private DateTimeOffset? _zai5hReset, _zaiMoReset;
    private double? _zai5hPercent, _zaiMoPercent;

    // Latest used/expected percent per AI window, used for compact-view rendering.
    private double? _codex5hUsed;
    private double _codex7dUsed;
    private double? _codex5hExpected, _codex7dExpected;
    private double? _codexSpark5hUsed, _codexSpark7dUsed;
    private double? _codexSpark5hExpected, _codexSpark7dExpected;
    private double? _codex2FiveHourUsed;
    private double _codex2SevenDayUsed;
    private double? _codex2FiveHourExpected, _codex2SevenDayExpected;
    private double? _codex2SparkFiveHourUsed, _codex2SparkSevenDayUsed;
    private double? _codex2SparkFiveHourExpected, _codex2SparkSevenDayExpected;
    private double _claude5hUsed, _claude7dUsed;
    private double? _claude5hExpected, _claude7dExpected;
    private double? _claudeDesignUsed;
    private double? _claudeDesignExpected;
    private double _claude2FiveHourUsed, _claude2SevenDayUsed;
    private double? _claude2FiveHourExpected, _claude2SevenDayExpected;
    private double? _claude2DesignUsed;
    private double? _claude2DesignExpected;
    private double? _zai5hExpected, _zaiMoExpected;

    // Per-bar state for target-aware rendering: used%, expected% (pace target), base color.
    private sealed class TargetBarState
    {
        public Grid Container = null!;
        public Border Fill = null!;
        public Border Tick = null!;
        public Color BaseColor;
        public double Used;
        public double? Expected;
    }
    private readonly List<TargetBarState> _targetBars = new();

    // Single source of truth: the usage-api aggregator.
    private UsageApiService? _usageApiService;

    // Auto-update checker
    private UpdateChecker? _updateChecker;
    private bool _updateAvailable;

    // Last successful usage snapshot, retained so provider show/hide toggles can
    // re-render visibility immediately without waiting for the next poll.
    private UsageApiStatus? _lastStatus;

    // Provider visibility toggles surfaced (in this order) in the tray "Providers" menu.
    public enum ProviderToggle { Codex, Codex2, CodexSpark, Claude, Claude2, ClaudeDesign, Claude2Design, Zai }

    public static readonly IReadOnlyList<(ProviderToggle Key, string Label)> ProviderToggles = new[]
    {
        (ProviderToggle.Codex, "Codex"),
        (ProviderToggle.CodexSpark, "Codex Spark"),
        (ProviderToggle.Claude, "Claude"),
        (ProviderToggle.Claude2, "Claude2"),
        (ProviderToggle.ClaudeDesign, "Claude Design"),
        (ProviderToggle.Claude2Design, "Claude2 Design"),
        (ProviderToggle.Zai, "Z.ai"),
    };

    public UsagePopup()
    {
        InitializeComponent();
        Icon = AppIcon.Create();
        _appBitmap = AppIcon.CreateBitmap();
        RestoreFullIcon.Source = _appBitmap;
        IconOnlyButtonImage.Source = _appBitmap;
        RestoreIconImage.Source = _appBitmap;

        _config = Config.Load();
        InitializeDriveDisplays();

        // Wire up close button
        CloseButton.Click += (s, e) => HidePopup();
        CloseCompactButton.Click += (s, e) => HidePopup();
        CompactButton.Click += (s, e) => SetViewMode(PopupViewMode.Compact);
        IconOnlyButton.Click += (s, e) => SetViewMode(PopupViewMode.IconOnly);
        RestoreFullButton.Click += (s, e) => SetViewMode(PopupViewMode.Full);
        RestoreIconButton.Click += (s, e) => SetViewMode(PopupViewMode.Full);
        UpdateButton.Click += async (_, _) => await ApplyUpdateAsync();
        UpdateButtonCompact.Click += async (_, _) => await ApplyUpdateAsync();
        MonitorTitleText.PointerPressed += (_, _) => OpenUsageDashboard();
        // Enable dragging from title bar area (works in all modes including compact)
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        // System stats refresh (every 2 seconds)
        _systemRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _systemRefreshTimer.Tick += SystemRefreshTimer_Tick;

        // AI credits refresh — fast, since usage-api is just a local aggregator with cached snapshots.
        var aiRefreshIntervalSeconds = Math.Max(1, _config.RefreshIntervalSeconds);
        _aiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(aiRefreshIntervalSeconds) };
        _aiRefreshTimer.Tick += AiRefreshTimer_Tick;

        // Initialize CPU counter on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // First call always returns 0, prime it
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize CPU counter: {ex.Message}");
            }
        }

        // Register target-aware bars so they re-render on resize.
        RegisterTargetBar(CodexPrimaryBar, CodexPrimaryBarFill, CodexPrimaryBarTick, Color.FromRgb(0x8B, 0xC3, 0x4A));
        RegisterTargetBar(CodexSecondaryBar, CodexSecondaryBarFill, CodexSecondaryBarTick, Color.FromRgb(0xB3, 0x9D, 0xDB));
        RegisterTargetBar(CodexSparkPrimaryBar, CodexSparkPrimaryBarFill, CodexSparkPrimaryBarTick, Color.FromRgb(0x4D, 0xD0, 0xE1));
        RegisterTargetBar(CodexSparkSecondaryBar, CodexSparkSecondaryBarFill, CodexSparkSecondaryBarTick, Color.FromRgb(0x4D, 0xB6, 0xAC));
        RegisterTargetBar(Codex2PrimaryBar, Codex2PrimaryBarFill, Codex2PrimaryBarTick, Color.FromRgb(0x64, 0xB5, 0xF6));
        RegisterTargetBar(Codex2SecondaryBar, Codex2SecondaryBarFill, Codex2SecondaryBarTick, Color.FromRgb(0x90, 0xCA, 0xF9));
        RegisterTargetBar(Codex2SparkPrimaryBar, Codex2SparkPrimaryBarFill, Codex2SparkPrimaryBarTick, Color.FromRgb(0x95, 0x75, 0xCD));
        RegisterTargetBar(Codex2SparkSecondaryBar, Codex2SparkSecondaryBarFill, Codex2SparkSecondaryBarTick, Color.FromRgb(0xB3, 0x9D, 0xDB));
        RegisterTargetBar(ClaudeCodePrimaryBar, ClaudeCodePrimaryBarFill, ClaudeCodePrimaryBarTick, Color.FromRgb(0xFF, 0x8A, 0x65));
        RegisterTargetBar(ClaudeCodeSecondaryBar, ClaudeCodeSecondaryBarFill, ClaudeCodeSecondaryBarTick, Color.FromRgb(0xFF, 0xB7, 0x4D));
        RegisterTargetBar(ClaudeDesignBar, ClaudeDesignBarFill, ClaudeDesignBarTick, Color.FromRgb(0xF4, 0x8F, 0xB1));
        RegisterTargetBar(ClaudeCode2PrimaryBar, ClaudeCode2PrimaryBarFill, ClaudeCode2PrimaryBarTick, Color.FromRgb(0xFF, 0x8A, 0x65));
        RegisterTargetBar(ClaudeCode2SecondaryBar, ClaudeCode2SecondaryBarFill, ClaudeCode2SecondaryBarTick, Color.FromRgb(0xFF, 0xB7, 0x4D));
        RegisterTargetBar(ClaudeDesign2Bar, ClaudeDesign2BarFill, ClaudeDesign2BarTick, Color.FromRgb(0xF4, 0x8F, 0xB1));
        RegisterTargetBar(ZaiTokenBar, ZaiTokenBarFill, ZaiTokenBarTick, Color.FromRgb(0xBA, 0x68, 0xC8));
        RegisterTargetBar(ZaiMonthlyBar, ZaiMonthlyBarFill, ZaiMonthlyBarTick, Color.FromRgb(0x7E, 0x57, 0xC2));

        // Initialize AI services
        InitializeAiServices();

        // Show build info
        BuildInfoText.Text = $"{BuildInfo.CommitSha} · {BuildInfo.BuildDate}";

        // Initialize auto-update checker
        _updateChecker = new UpdateChecker(_config.RepoPath);
        _updateChecker.UpdateDetected += (s, e) => Dispatcher.UIThread.Post(() =>
        {
            _updateAvailable = true;
            UpdateUpdateAffordances();
        });
        _updateChecker.Start();

        SetViewMode(PopupViewMode.Compact);

        // Position near taskbar on first show only
        Opened += (s, e) =>
        {
            MakeWindowNonActivating();
            PinToTaskbarCorner();
            Dispatcher.UIThread.Post(ReanchorIfPinned, DispatcherPriority.Loaded);
        };

        // Re-anchor whenever the window resizes (e.g., async AI data revealing
        // sections, view-mode toggles), so the bottom edge never overlaps the taskbar.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                ReanchorIfPinned();
        };
    }

    private void PinToTaskbarCorner()
    {
        if (Screens.Primary is not { } screen) return;
        var workArea = screen.WorkingArea;
        var scaling = screen.Scaling;
        var margin = (int)(4 * scaling);
        _anchorBottomRight = new PixelPoint(
            workArea.X + workArea.Width - margin,
            workArea.Y + workArea.Height - margin);
    }

    private void ReanchorIfPinned()
    {
        if (_anchorBottomRight is not PixelPoint anchor) return;
        var scaling = Screens.Primary?.Scaling ?? 1;
        var pixelW = (int)(Bounds.Width * scaling);
        var pixelH = (int)(Bounds.Height * scaling);
        Position = new PixelPoint(anchor.X - pixelW, anchor.Y - pixelH);
    }

    private void InitializeAiServices()
    {
        _usageApiService = new UsageApiService(_config.UsageApiUrl);
        // Sections start hidden; the first successful refresh reveals whichever providers
        // returned data. NoKeysHint also flips off after the first successful response.
        NoKeysHint.IsVisible = false;
        ConfigPathText.Text = $"Source: {_config.UsageApiUrl}";
    }

    private void OpenUsageDashboard()
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(_config.UsageApiUrl)
                ? "https://usage.etdofresh.com"
                : _config.UsageApiUrl.TrimEnd('/');
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open usage dashboard: {ex.Message}");
        }
    }

    private void RegisterTargetBar(Grid container, Border fill, Border tick, Color baseColor)
    {
        var state = new TargetBarState
        {
            Container = container,
            Fill = fill,
            Tick = tick,
            BaseColor = baseColor,
        };
        _targetBars.Add(state);
        container.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                RenderTargetBar(state);
        };
    }

    private void SetTargetBar(Grid container, double used, double? expected)
    {
        var state = _targetBars.FirstOrDefault(s => ReferenceEquals(s.Container, container));
        if (state == null) return;
        state.Used = used;
        state.Expected = expected;
        RenderTargetBar(state);
    }

    private static void RenderTargetBar(TargetBarState s)
    {
        var w = s.Container.Bounds.Width;
        if (w <= 0) return;

        var used = Math.Clamp(s.Used, 0, 100);
        s.Fill.Width = used / 100.0 * w;
        s.Fill.Background = new SolidColorBrush(BlendOverColor(s.BaseColor, s.Expected, used));

        if (s.Expected is double exp && exp > 0 && exp < 100)
        {
            var x = exp / 100.0 * w;
            s.Tick.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
            s.Tick.IsVisible = true;
        }
        else
        {
            s.Tick.IsVisible = false;
        }
    }

    private static void RenderCompactBar(Border fill, Border tick, Color baseColor, double used, double? expected, double width)
    {
        var u = Math.Clamp(used, 0, 100);
        fill.Width = u / 100.0 * width;
        fill.Background = new SolidColorBrush(BlendOverColor(baseColor, expected, u));
        if (expected is double exp && exp > 0 && exp < 100)
        {
            var x = exp / 100.0 * width;
            tick.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
            tick.IsVisible = true;
        }
        else
        {
            tick.IsVisible = false;
        }
    }

    // Blend the base color toward yellow then red as used grows past the target.
    private static Color BlendOverColor(Color baseColor, double? expected, double used)
    {
        if (expected is not double exp || used <= exp) return baseColor;
        var headroom = Math.Max(1.0, 100.0 - exp);
        var t = Math.Clamp((used - exp) / headroom, 0, 1);
        var yellow = Color.FromRgb(0xFF, 0xEB, 0x3B);
        var red = Color.FromRgb(0xF4, 0x43, 0x36);
        return t < 0.5 ? Lerp(baseColor, yellow, t * 2) : Lerp(yellow, red, (t - 0.5) * 2);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public void TogglePopup()
    {
        if (IsVisible)
            HidePopup();
        else
            ShowPopup();
    }

    public void ShowPopup()
    {
        SetViewMode(PopupViewMode.Compact, anchorBottomRight: false);
        PinToTaskbarCorner();
        Show();
        RefreshSystem();
        _systemRefreshTimer.Start();

        // Fetch AI credits immediately, then on timer
        _ = RefreshAiCreditsAsync();
        _aiRefreshTimer.Start();
    }

    public void HidePopup()
    {
        _systemRefreshTimer.Stop();
        _aiRefreshTimer.Stop();
        Hide();
    }

    // Idempotent pre-shutdown teardown. Called from the quit path, from the
    // lifetime's ShutdownRequested hook (Cmd+Q / logout / OS shutdown), or both.
    public void PrepareShutdown()
    {
        _allowClose = true;
        _systemRefreshTimer.Stop();
        _aiRefreshTimer.Stop();
        _usageApiService?.Dispose();
        _updateChecker?.Dispose();
    }

    public void ForceClose()
    {
        // A double-activated Quit posts this twice; the second pass must not call
        // desktop.Shutdown() again (it would re-raise Exit and re-dispose the tray).
        if (_shutdownStarted) return;
        _shutdownStarted = true;

        PrepareShutdown();
        Close();
        // ShutdownMode is OnExplicitShutdown, so closing the last window is not
        // enough — end the lifetime here so both Quit and the update-restart
        // path fully exit the process instead of lingering with a live tray icon.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateChecker == null || !_updateChecker.Enabled) return;
        UpdateButton.IsEnabled = false;
        UpdateButtonCompact.IsEnabled = false;
        try
        {
            var success = await _updateChecker.ApplyUpdateAsync();
            if (success)
            {
                if (!_updateChecker.RestartScheduled)
                    UpdateChecker.RestartApp();
                ForceClose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update apply failed: {ex.Message}");
        }
        finally
        {
            // A successful update closes this window. If it failed, make the
            // visible update affordance available again for a later retry.
            UpdateButton.IsEnabled = true;
            UpdateButtonCompact.IsEnabled = true;
        }
    }

    // Shared by the automatic timer and the tray-menu command. The caller uses
    // the result for feedback; UpdateDetected still reveals the normal update
    // buttons whenever a newer build is found.
    public Task<UpdateChecker.CheckResult> CheckForUpdatesAsync() =>
        _updateChecker?.CheckAsync() ?? Task.FromResult(UpdateChecker.CheckResult.Disabled);

    private void PositionNearTaskbar(double? targetWidth = null, double? targetHeight = null)
    {
        if (Screens.Primary is { } screen)
        {
            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling;
            var pixelWidth = (int)((targetWidth ?? Width) * scaling);
            var pixelHeight = (int)((targetHeight ?? Height) * scaling);
            var margin = (int)(4 * scaling);
            Position = new PixelPoint(
                workArea.X + workArea.Width - pixelWidth - margin,
                workArea.Y + workArea.Height - pixelHeight - margin
            );
        }
    }

    #region System Stats

    private void SystemRefreshTimer_Tick(object? sender, EventArgs e)
    {
        RefreshSystem();
    }

    private void RefreshSystem()
    {
        try
        {
            UpdateCpu();
            UpdateMemory();
            UpdateDisk();
            UpdateNetwork();
            UpdateUptime();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"System refresh error: {ex.Message}");
        }
    }

    private void UpdateCpu()
    {
        double cpuPercent = 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _cpuCounter != null)
        {
            try
            {
                cpuPercent = _cpuCounter.NextValue();
            }
            catch { }
        }
        else if (TryReadCpuTimes(out var total, out var idle))
        {
            if (_hasCpuSample && total > _lastCpuTotal)
            {
                var totalDelta = total - _lastCpuTotal;
                var idleDelta = idle >= _lastCpuIdle ? idle - _lastCpuIdle : 0;
                cpuPercent = Math.Clamp((1.0 - idleDelta / (double)totalDelta) * 100.0, 0, 100);
            }
            _lastCpuTotal = total;
            _lastCpuIdle = idle;
            _hasCpuSample = true;
        }

        _cpuPercent = Math.Clamp(cpuPercent, 0, 100);
        SetPlainBar(CpuBar, CpuBarFill, _cpuPercent);
        CpuPercentText.Text = $"{cpuPercent:F0}%";
        CpuDetailsText.Text = $"{Environment.ProcessorCount} logical cores";
        UpdateCompactSystemBars();
    }

    private void UpdateMemory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref status))
                {
                    double totalGB = status.ullTotalPhys / (1024.0 * 1024 * 1024);
                    double usedGB = (status.ullTotalPhys - status.ullAvailPhys) / (1024.0 * 1024 * 1024);
                    double percent = (usedGB / totalGB) * 100;

                    SetMemoryDisplay(usedGB, totalGB, percent);
                }
            }
            catch { }
        }
        else
        {
            if (TryReadUnixMemory(out var usedBytes, out var totalBytes) && totalBytes > 0)
            {
                var divisor = 1024.0 * 1024 * 1024;
                SetMemoryDisplay(usedBytes / divisor, totalBytes / divisor, usedBytes / (double)totalBytes * 100);
            }
        }
    }

    private void UpdateDisk()
    {
        try
        {
            foreach (var display in _driveDisplays)
            {
                var visible = _config.IsDriveVisible(display.Key);
                display.FullRow.IsVisible = visible;
                display.CompactRow.IsVisible = visible;
                if (!visible || !display.Drive.IsReady || display.Drive.TotalSize <= 0)
                    continue;

                var percent = Math.Clamp(
                    (display.Drive.TotalSize - display.Drive.AvailableFreeSpace) /
                    (double)display.Drive.TotalSize * 100.0, 0, 100);
                SetPlainBar(display.FullBar, display.FullFill, percent);
                display.CompactFill.Width = percent / 100.0 * 62.0;
                display.CompactPercent.Text = $"{percent:F0}%";
                var usedBytes = display.Drive.TotalSize - display.Drive.AvailableFreeSpace;
                display.FullUsage.Text = FormatDriveUsage(usedBytes, display.Drive.TotalSize, compact: false);
                display.CompactUsage.Text = FormatDriveUsage(usedBytes, display.Drive.TotalSize, compact: true);
            }
            DriveSection.IsVisible = _driveDisplays.Any(d => d.FullRow.IsVisible);
        }
        catch { }
    }

    private void SetMemoryDisplay(double usedGB, double totalGB, double percent)
    {
        _memoryPercent = Math.Clamp(percent, 0, 100);
        _memoryUsedGB = usedGB;
        _memoryTotalGB = totalGB;
        SetPlainBar(MemoryBar, MemoryBarFill, _memoryPercent);
        MemoryPercentText.Text = $"{_memoryPercent:F0}%";
        MemoryText.Text = $"{usedGB:F1} / {totalGB:F1} GB";
        UpdateCompactSystemBars();
    }

    private static void SetPlainBar(Grid container, Border fill, double percent)
    {
        var width = container.Bounds.Width;
        if (width > 0)
            fill.Width = Math.Clamp(percent, 0, 100) / 100.0 * width;
    }

    private void UpdateCompactSystemBars()
    {
        const double barWidth = 62.0;
        CpuCompactBar.Width = _cpuPercent / 100.0 * barWidth;
        MemoryCompactBar.Width = _memoryPercent / 100.0 * barWidth;
        CpuCompactText.Text = $"{_cpuPercent:F0}%";
        MemoryCompactText.Text = $"{_memoryPercent:F0}%";
        CpuCompactDetailsText.Text = $"{Environment.ProcessorCount}c";
        MemoryCompactDetailsText.Text = _memoryTotalGB > 0
            ? $"{_memoryUsedGB:F0}/{_memoryTotalGB:F0}G"
            : "—";
    }

    private void InitializeDriveDisplays()
    {
        var drives = DriveInfo.GetDrives()
            .Where(IsUserDrive)
            .OrderBy(d => d.Name == Path.DirectorySeparatorChar.ToString() ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DriveToggles = drives.Select(d => new DriveToggle(d.Name, GetDriveLabel(d))).ToArray();
        foreach (var drive in drives)
        {
            var label = GetDriveLabel(drive);
            var fullFill = NewBarFill();
            var fullBar = NewBarGrid(fullFill, stretch: true);
            var fullRow = new Grid { ColumnDefinitions = new ColumnDefinitions("84,*,106"), ClipToBounds = true };
            fullRow.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(fullBar, 1);
            fullRow.Children.Add(fullBar);
            var fullUsage = NewUsageText();
            Grid.SetColumn(fullUsage, 2);
            fullRow.Children.Add(fullUsage);

            var compactFill = NewBarFill();
            var compactBar = NewBarGrid(compactFill, stretch: false);
            var compactRow = new Grid { ColumnDefinitions = new ColumnDefinitions("58,68,42,48"), ClipToBounds = true };
            compactRow.Children.Add(new TextBlock
            {
                Text = CompactDriveLabel(label),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(compactBar, 1);
            compactRow.Children.Add(compactBar);
            var compactPercent = NewPercentText();
            Grid.SetColumn(compactPercent, 2);
            compactRow.Children.Add(compactPercent);
            var compactUsage = NewUsageText(fontSize: 7);
            Grid.SetColumn(compactUsage, 3);
            compactRow.Children.Add(compactUsage);

            FullDriveBarsHost.Children.Add(fullRow);
            CompactDriveBarsHost.Children.Add(compactRow);
            _driveDisplays.Add(new DriveDisplay
            {
                Drive = drive,
                Key = drive.Name,
                Label = label,
                FullRow = fullRow,
                FullBar = fullBar,
                FullFill = fullFill,
                FullUsage = fullUsage,
                CompactRow = compactRow,
                CompactFill = compactFill,
                CompactPercent = compactPercent,
                CompactUsage = compactUsage,
            });
        }
    }

    private static bool IsUserDrive(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.TotalSize <= 0)
                return false;
            if (OperatingSystem.IsWindows())
                return drive.DriveType is DriveType.Fixed or DriveType.Removable;
            if (OperatingSystem.IsMacOS())
                return drive.Name == "/" || drive.Name.StartsWith("/Volumes/", StringComparison.Ordinal);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch { return false; }
    }

    private static string GetDriveLabel(DriveInfo drive)
    {
        if (drive.Name == "/") return "Macintosh HD";
        var trimmed = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? drive.Name : name;
    }

    private static string CompactDriveLabel(string label) => label.Length <= 8 ? label : label[..8];

    private static Border NewBarFill() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
        CornerRadius = new CornerRadius(2),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        Width = 0,
    };

    private static Grid NewBarGrid(Border fill, bool stretch)
    {
        var grid = new Grid
        {
            Width = stretch ? double.NaN : 62,
            Height = 4,
            Margin = new Thickness(stretch ? 4 : 2, 0, 4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = stretch
                ? Avalonia.Layout.HorizontalAlignment.Stretch
                : Avalonia.Layout.HorizontalAlignment.Left,
            ClipToBounds = true,
        };
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), CornerRadius = new CornerRadius(2) });
        grid.Children.Add(fill);
        return grid;
    }

    private static TextBlock NewPercentText() => new()
    {
        Text = "—",
        FontSize = 8,
        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDA, 0xA6)),
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private static TextBlock NewUsageText(double fontSize = 8) => new()
    {
        Text = "—",
        FontSize = fontSize,
        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xDA, 0xA6)),
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static string FormatDriveUsage(long usedBytes, long totalBytes, bool compact)
    {
        const double gibibyte = 1024d * 1024 * 1024;
        const double tebibyte = 1024d * 1024 * 1024 * 1024;
        if (totalBytes >= tebibyte)
            return compact
                ? $"{usedBytes / tebibyte:F1}/{totalBytes / tebibyte:F1}T"
                : $"{usedBytes / tebibyte:F1} / {totalBytes / tebibyte:F1} TB";

        return compact
            ? $"{usedBytes / gibibyte:F0}/{totalBytes / gibibyte:F0}G"
            : $"{usedBytes / gibibyte:F0} / {totalBytes / gibibyte:F0} GB";
    }

    public bool IsDriveVisible(string driveKey) => _config.IsDriveVisible(driveKey);

    public void SetDriveVisible(string driveKey, bool visible)
    {
        _config.SetDriveVisible(driveKey, visible);
        UpdateDisk();
        UpdateCompactSummary();
        InvalidateMeasure();
    }

    private static bool TryReadCpuTimes(out ulong total, out ulong idle)
    {
        total = idle = 0;
        if (OperatingSystem.IsMacOS())
        {
            var ticks = new uint[4];
            var count = (uint)ticks.Length;
            if (host_statistics(mach_host_self(), HostCpuLoadInfo, ticks, ref count) != 0 || count < 4)
                return false;
            total = ticks.Aggregate<uint, ulong>(0, (sum, value) => sum + value);
            idle = ticks[CpuStateIdle];
            return total > 0;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var values = fields.Skip(1).Select(ulong.Parse).ToArray();
                total = values.Aggregate<ulong, ulong>(0, (sum, value) => sum + value);
                idle = values.ElementAtOrDefault(3) + values.ElementAtOrDefault(4);
                return total > 0;
            }
            catch { }
        }
        return false;
    }

    private static bool TryReadUnixMemory(out ulong usedBytes, out ulong totalBytes)
    {
        usedBytes = totalBytes = 0;
        if (OperatingSystem.IsMacOS())
        {
            var stats = new uint[64];
            var count = (uint)stats.Length;
            if (host_statistics64(mach_host_self(), HostVmInfo64, stats, ref count) != 0 || count < 8)
                return false;
            var pageSize = (ulong)Environment.SystemPageSize;
            // Inactive pages are reclaimable cache, so count them as available.
            var freeBytes = (stats[0] + stats[2]) * pageSize;
            var total = (ulong)GetMacPhysicalMemory();
            if (total <= freeBytes) return false;
            totalBytes = total;
            usedBytes = total - freeBytes;
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var values = File.ReadLines("/proc/meminfo")
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => ulong.Parse(parts[1].Trim().Split(' ')[0]) * 1024);
                totalBytes = values["MemTotal"];
                var available = values.TryGetValue("MemAvailable", out var value) ? value : values.GetValueOrDefault("MemFree");
                usedBytes = totalBytes - available;
                return totalBytes > 0;
            }
            catch { }
        }
        return false;
    }

    private static long GetMacPhysicalMemory()
    {
        nuint length = sizeof(long);
        return sysctlbyname("hw.memsize", out var memory, ref length, IntPtr.Zero, 0) == 0 ? memory : 0;
    }

    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const int CpuStateIdle = 2;

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int host_statistics64(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int sysctlbyname(string name, out long oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);

    private void UpdateNetwork()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            long totalSent = 0;
            long totalReceived = 0;

            foreach (var ni in interfaces)
            {
                var stats = ni.GetIPv4Statistics();
                totalSent += stats.BytesSent;
                totalReceived += stats.BytesReceived;
            }

            var now = DateTime.Now;
            if (_lastNetworkCheck > DateTime.MinValue)
            {
                var elapsed = (now - _lastNetworkCheck).TotalSeconds;
                if (elapsed > 0)
                {
                    double upSpeed = (totalSent - _lastBytesSent) / elapsed;
                    double downSpeed = (totalReceived - _lastBytesReceived) / elapsed;

                    NetworkUpText.Text = $"↑ {FormatSpeed(upSpeed)}";
                    NetworkDownText.Text = $"↓ {FormatSpeed(downSpeed)}";
                }
            }

            _lastBytesSent = totalSent;
            _lastBytesReceived = totalReceived;
            _lastNetworkCheck = now;
        }
        catch { }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private void UpdateUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
    }

    #endregion

    #region AI Credits

    private async void AiRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAiCreditsAsync();
    }

    private async Task RefreshAiCreditsAsync()
    {
        if (_usageApiService == null) return;
        var status = await _usageApiService.GetStatusAsync();
        if (status == null) return;
        _lastStatus = status;

        Dispatcher.UIThread.Post(() =>
        {
            ApplyOpenRouter(status.OpenRouter);
            ApplyOpenAi(status.OpenAi);
            ApplyCodex(status.Codex);
            ApplyCodex2(status.Codex2);
            ApplyClaude(status.Claude);
            ApplyClaude2(status.Claude2);
            ApplyZai(status.Zai);
            ReflowAiGrid();
            UpdateCompactSummary();
        });
    }

    public bool IsProviderVisible(ProviderToggle toggle) => toggle switch
    {
        ProviderToggle.Codex => _config.ShowCodex,
        ProviderToggle.Codex2 => _config.ShowCodex2,
        ProviderToggle.CodexSpark => _config.ShowCodexSpark,
        ProviderToggle.Claude => _config.ShowClaude,
        ProviderToggle.Claude2 => _config.ShowClaude2,
        ProviderToggle.ClaudeDesign => _config.ShowClaudeDesign,
        ProviderToggle.Claude2Design => _config.ShowClaude2Design,
        ProviderToggle.Zai => _config.ShowZai,
        _ => true,
    };

    public void SetProviderVisible(ProviderToggle toggle, bool visible)
    {
        switch (toggle)
        {
            case ProviderToggle.Codex: _config.ShowCodex = visible; break;
            case ProviderToggle.Codex2: _config.ShowCodex2 = visible; break;
            case ProviderToggle.CodexSpark: _config.ShowCodexSpark = visible; break;
            case ProviderToggle.Claude: _config.ShowClaude = visible; break;
            case ProviderToggle.Claude2: _config.ShowClaude2 = visible; break;
            case ProviderToggle.ClaudeDesign: _config.ShowClaudeDesign = visible; break;
            case ProviderToggle.Claude2Design: _config.ShowClaude2Design = visible; break;
            case ProviderToggle.Zai: _config.ShowZai = visible; break;
        }
        _config.Save();
        ReapplyProviderVisibility();
    }

    // Re-render section/bar visibility against the last snapshot so a menu toggle takes
    // effect immediately, even between polls. Safe to call from any thread.
    private void ReapplyProviderVisibility()
    {
        void Apply()
        {
            ApplyCodex(_lastStatus?.Codex);
            ApplyCodex2(_lastStatus?.Codex2);
            ApplyClaude(_lastStatus?.Claude);
            ApplyClaude2(_lastStatus?.Claude2);
            ApplyZai(_lastStatus?.Zai);
            ReflowAiGrid();
            UpdateCompactSummary();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    // The full-view AI cards live in a fixed 2-column grid. Repack the *visible* cards
    // top-left (and recompute their gutter margins) so hiding any subset never leaves an
    // interior hole. With all cards visible this reproduces the original cell layout.
    private void ReflowAiGrid()
    {
        Control[] sections =
        {
            OpenRouterSection,
            CodexSection,
            Codex2Section,
            ClaudeCodeSection,
            ZaiSection,
            ClaudeCode2Section,
        };

        var slot = 0;
        foreach (var section in sections)
        {
            if (!section.IsVisible) continue;
            var row = slot / 2;
            var col = slot % 2;
            Grid.SetRow(section, row);
            Grid.SetColumn(section, col);
            section.Margin = new Thickness(
                col == 1 ? 3 : 0,   // left gutter for the right column
                row == 0 ? 0 : 3,   // top gap below the first row
                col == 0 ? 3 : 0,   // right gutter for the left column
                0);
            slot++;
        }
    }

    private void ApplyOpenRouter(OpenRouterBlock? r)
    {
        OpenRouterSection.IsVisible = r != null;
        if (r == null) return;
        var remaining = r.LimitRemaining ?? (r.Limit.HasValue ? Math.Max(0, r.Limit.Value - r.Usage) : (double?)null);
        OpenRouterCreditsText.Text = remaining.HasValue
            ? $"${r.Usage:F2} used | ${remaining.Value:F2} left"
            : $"${r.Usage:F2} used";
        OpenRouterCompactText.Text = remaining.HasValue ? $"${remaining.Value:F2} left" : "—";
        OpenRouterRangeText.Text = r.IsFreeTier == true ? "Free tier" : "";
    }

    private void ApplyOpenAi(OpenAiBlock? o)
    {
        OpenAiRow.IsVisible = o != null;
        if (o == null) return;
        OpenAiCreditsText.Text = $"${o.SpendMonth:F2} this month";
    }

    private void ApplyCodex(CodexBlock? c)
    {
        var show = c != null && _config.ShowCodex;
        CodexSection.IsVisible = show;
        if (!show || c == null) return;
        var primary = c.Primary is { } primaryWindow
            ? Math.Clamp(primaryWindow.UsedPercent, 0, 100)
            : (double?)null;
        var secondary = Math.Clamp(c.Secondary.UsedPercent, 0, 100);
        if (primary.HasValue)
            SetTwoUsedExpectedInlines(CodexCreditsText, primary.Value, c.Primary!.ExpectedPercent, secondary, c.Secondary.ExpectedPercent);
        else
        {
            SetUsedExpectedInlines(CodexCreditsText, secondary, c.Secondary.ExpectedPercent);
            CodexCreditsText.Inlines!.Add(new Run(" used"));
        }
        _codex5hUsed = primary;
        _codex7dUsed = secondary;
        _codex5hExpected = c.Primary?.ExpectedPercent;
        _codex7dExpected = c.Secondary.ExpectedPercent;
        Codex5hLabel.IsVisible = primary.HasValue;
        CodexPrimaryBar.IsVisible = primary.HasValue;
        if (primary.HasValue)
            SetTargetBar(CodexPrimaryBar, primary.Value, _codex5hExpected);
        SetTargetBar(CodexSecondaryBar, secondary, _codex7dExpected);
        _codex5hReset = c.Primary?.ResetsAt;
        _codex7dReset = c.Secondary.ResetsAt;

        // Spark (GPT-5.3-Codex-Spark) optional bars, gated by the Codex Spark toggle.
        var showSpark5h = c.SparkPrimary != null && _config.ShowCodexSpark;
        var showSpark7d = c.SparkSecondary != null && _config.ShowCodexSpark;
        CodexSpark5hLabel.IsVisible = showSpark5h;
        CodexSparkPrimaryBar.IsVisible = showSpark5h;
        CodexSpark7dLabel.IsVisible = showSpark7d;
        CodexSparkSecondaryBar.IsVisible = showSpark7d;

        if (showSpark5h && c.SparkPrimary is { } sp)
        {
            _codexSpark5hUsed = Math.Clamp(sp.UsedPercent, 0, 100);
            _codexSpark5hExpected = sp.ExpectedPercent;
            _codexSpark5hReset = sp.ResetsAt;
            SetTargetBar(CodexSparkPrimaryBar, _codexSpark5hUsed.Value, _codexSpark5hExpected);
        }
        else
        {
            _codexSpark5hUsed = null;
            _codexSpark5hExpected = null;
            _codexSpark5hReset = null;
        }

        if (showSpark7d && c.SparkSecondary is { } ss)
        {
            _codexSpark7dUsed = Math.Clamp(ss.UsedPercent, 0, 100);
            _codexSpark7dExpected = ss.ExpectedPercent;
            _codexSpark7dReset = ss.ResetsAt;
            SetTargetBar(CodexSparkSecondaryBar, _codexSpark7dUsed.Value, _codexSpark7dExpected);
        }
        else
        {
            _codexSpark7dUsed = null;
            _codexSpark7dExpected = null;
            _codexSpark7dReset = null;
        }

        var range = primary.HasValue
            ? $"5h: in {FormatResetCountdown(c.Primary!.ResetsAt)} • 7d: {FormatResetDate(c.Secondary.ResetsAt)}"
            : $"7d: {FormatResetDate(c.Secondary.ResetsAt)}";
        if (showSpark5h || showSpark7d)
        {
            var sparkPct = _codexSpark5hUsed.HasValue && _codexSpark7dUsed.HasValue
                ? $"{_codexSpark5hUsed.Value:F0}% / {_codexSpark7dUsed.Value:F0}%"
                : _codexSpark5hUsed.HasValue
                    ? $"{_codexSpark5hUsed.Value:F0}%"
                    : $"{_codexSpark7dUsed!.Value:F0}%";
            range += $" • Spark: {sparkPct}";
        }
        CodexRangeText.Text = range;
    }

    private void ApplyCodex2(CodexBlock? c)
    {
        // Absence is intentional: no auth/no successful data means no card or
        // compact rows. The primary labels gain "1" only while account #2 is visible.
        var show = c != null && _config.ShowCodex2;
        Codex2Section.IsVisible = show;
        CodexTitleText.Text = show ? "Codex #1" : "Codex";
        CodexCompactFiveHourLabelText.Text = show ? "Codex1 5h" : "Codex 5h";
        CodexCompactSevenDayLabelText.Text = show ? "Codex1 7d" : "Codex 7d";

        if (!show || c == null)
        {
            _codex2FiveHourUsed = null;
            _codex2SevenDayUsed = 0;
            _codex2FiveHourExpected = null;
            _codex2SevenDayExpected = null;
            _codex2FiveHourReset = null;
            _codex2SevenDayReset = null;
            _codex2SparkFiveHourUsed = null;
            _codex2SparkSevenDayUsed = null;
            _codex2SparkFiveHourExpected = null;
            _codex2SparkSevenDayExpected = null;
            _codex2SparkFiveHourReset = null;
            _codex2SparkSevenDayReset = null;
            Codex2FiveHourLabel.IsVisible = false;
            Codex2PrimaryBar.IsVisible = false;
            Codex2SparkFiveHourLabel.IsVisible = false;
            Codex2SparkPrimaryBar.IsVisible = false;
            Codex2SparkSevenDayLabel.IsVisible = false;
            Codex2SparkSecondaryBar.IsVisible = false;
            return;
        }

        var fiveHour = c.Primary is { } primaryWindow
            ? Math.Clamp(primaryWindow.UsedPercent, 0, 100)
            : (double?)null;
        var sevenDay = Math.Clamp(c.Secondary.UsedPercent, 0, 100);
        if (fiveHour.HasValue)
            SetTwoUsedExpectedInlines(Codex2CreditsText, fiveHour.Value, c.Primary!.ExpectedPercent, sevenDay, c.Secondary.ExpectedPercent);
        else
        {
            SetUsedExpectedInlines(Codex2CreditsText, sevenDay, c.Secondary.ExpectedPercent);
            Codex2CreditsText.Inlines!.Add(new Run(" used"));
        }

        _codex2FiveHourUsed = fiveHour;
        _codex2SevenDayUsed = sevenDay;
        _codex2FiveHourExpected = c.Primary?.ExpectedPercent;
        _codex2SevenDayExpected = c.Secondary.ExpectedPercent;
        _codex2FiveHourReset = c.Primary?.ResetsAt;
        _codex2SevenDayReset = c.Secondary.ResetsAt;
        Codex2FiveHourLabel.IsVisible = fiveHour.HasValue;
        Codex2PrimaryBar.IsVisible = fiveHour.HasValue;
        if (fiveHour.HasValue)
            SetTargetBar(Codex2PrimaryBar, fiveHour.Value, _codex2FiveHourExpected);
        SetTargetBar(Codex2SecondaryBar, sevenDay, _codex2SevenDayExpected);

        var showSparkFiveHour = c.SparkPrimary != null && _config.ShowCodexSpark;
        var showSparkSevenDay = c.SparkSecondary != null && _config.ShowCodexSpark;
        Codex2SparkFiveHourLabel.IsVisible = showSparkFiveHour;
        Codex2SparkPrimaryBar.IsVisible = showSparkFiveHour;
        Codex2SparkSevenDayLabel.IsVisible = showSparkSevenDay;
        Codex2SparkSecondaryBar.IsVisible = showSparkSevenDay;

        if (showSparkFiveHour && c.SparkPrimary is { } sparkFiveHour)
        {
            _codex2SparkFiveHourUsed = Math.Clamp(sparkFiveHour.UsedPercent, 0, 100);
            _codex2SparkFiveHourExpected = sparkFiveHour.ExpectedPercent;
            _codex2SparkFiveHourReset = sparkFiveHour.ResetsAt;
            SetTargetBar(Codex2SparkPrimaryBar, _codex2SparkFiveHourUsed.Value, _codex2SparkFiveHourExpected);
        }
        else
        {
            _codex2SparkFiveHourUsed = null;
            _codex2SparkFiveHourExpected = null;
            _codex2SparkFiveHourReset = null;
        }

        if (showSparkSevenDay && c.SparkSecondary is { } sparkSevenDay)
        {
            _codex2SparkSevenDayUsed = Math.Clamp(sparkSevenDay.UsedPercent, 0, 100);
            _codex2SparkSevenDayExpected = sparkSevenDay.ExpectedPercent;
            _codex2SparkSevenDayReset = sparkSevenDay.ResetsAt;
            SetTargetBar(Codex2SparkSecondaryBar, _codex2SparkSevenDayUsed.Value, _codex2SparkSevenDayExpected);
        }
        else
        {
            _codex2SparkSevenDayUsed = null;
            _codex2SparkSevenDayExpected = null;
            _codex2SparkSevenDayReset = null;
        }

        var range = fiveHour.HasValue
            ? $"5h: in {FormatResetCountdown(c.Primary!.ResetsAt)} • 7d: {FormatResetDate(c.Secondary.ResetsAt)}"
            : $"7d: {FormatResetDate(c.Secondary.ResetsAt)}";
        if (showSparkFiveHour || showSparkSevenDay)
        {
            var sparkPercent = _codex2SparkFiveHourUsed.HasValue && _codex2SparkSevenDayUsed.HasValue
                ? $"{_codex2SparkFiveHourUsed.Value:F0}% / {_codex2SparkSevenDayUsed.Value:F0}%"
                : _codex2SparkFiveHourUsed.HasValue
                    ? $"{_codex2SparkFiveHourUsed.Value:F0}%"
                    : $"{_codex2SparkSevenDayUsed!.Value:F0}%";
            range += $" • Spark: {sparkPercent}";
        }
        Codex2RangeText.Text = range;
    }

    private void ApplyClaude(ClaudeBlock? c)
    {
        var show = c != null && _config.ShowClaude;
        ClaudeCodeSection.IsVisible = show;
        if (!show || c == null) return;
        SetTwoUsedExpectedInlines(ClaudeCodeCreditsText, c.FiveHour.UsedPercent, c.FiveHour.ExpectedPercent, c.SevenDay.UsedPercent, c.SevenDay.ExpectedPercent);
        _claude5hUsed = c.FiveHour.UsedPercent;
        _claude7dUsed = c.SevenDay.UsedPercent;
        _claude5hExpected = c.FiveHour.ExpectedPercent;
        _claude7dExpected = c.SevenDay.ExpectedPercent;
        SetTargetBar(ClaudeCodePrimaryBar, _claude5hUsed, _claude5hExpected);
        SetTargetBar(ClaudeCodeSecondaryBar, _claude7dUsed, _claude7dExpected);
        _claude5hReset = c.FiveHour.ResetsAt;
        _claude7dReset = c.SevenDay.ResetsAt;

        var showDesign = c.SevenDayDesign != null && _config.ShowClaudeDesign;
        ClaudeDesignLabel.IsVisible = showDesign;
        ClaudeDesignBar.IsVisible = showDesign;
        if (showDesign && c.SevenDayDesign is { } d)
        {
            _claudeDesignUsed = Math.Clamp(d.UsedPercent, 0, 100);
            _claudeDesignExpected = d.ExpectedPercent;
            _claudeDesignReset = d.ResetsAt;
            SetTargetBar(ClaudeDesignBar, _claudeDesignUsed.Value, _claudeDesignExpected);
        }
        else
        {
            _claudeDesignUsed = null;
            _claudeDesignExpected = null;
            _claudeDesignReset = null;
        }

        var range = $"5h: in {FormatResetCountdown(c.FiveHour.ResetsAt)} • 7d: {FormatResetDate(c.SevenDay.ResetsAt)}";
        if (_claudeDesignUsed.HasValue)
            range += $" • Des: {_claudeDesignUsed.Value:F0}%";
        ClaudeCodeRangeText.Text = range;
    }

    private void ApplyClaude2(ClaudeBlock? c)
    {
        // AND-gate: the second account renders only when the server exposes
        // providers.claude2 AND the local config flag approves it.
        var show = c != null && _config.ShowClaude2;
        ClaudeCode2Section.IsVisible = show;
        if (!show)
        {
            // Clear all claude2 state so the compact view never renders stale data.
            _claude2FiveHourUsed = 0;
            _claude2SevenDayUsed = 0;
            _claude2FiveHourExpected = null;
            _claude2SevenDayExpected = null;
            _claude2FiveHourReset = null;
            _claude2SevenDayReset = null;
            _claude2DesignUsed = null;
            _claude2DesignExpected = null;
            _claude2DesignReset = null;
            ClaudeDesign2Label.IsVisible = false;
            ClaudeDesign2Bar.IsVisible = false;
            return;
        }
        SetTwoUsedExpectedInlines(ClaudeCode2CreditsText, c!.FiveHour.UsedPercent, c.FiveHour.ExpectedPercent, c.SevenDay.UsedPercent, c.SevenDay.ExpectedPercent);
        _claude2FiveHourUsed = c.FiveHour.UsedPercent;
        _claude2SevenDayUsed = c.SevenDay.UsedPercent;
        _claude2FiveHourExpected = c.FiveHour.ExpectedPercent;
        _claude2SevenDayExpected = c.SevenDay.ExpectedPercent;
        SetTargetBar(ClaudeCode2PrimaryBar, _claude2FiveHourUsed, _claude2FiveHourExpected);
        SetTargetBar(ClaudeCode2SecondaryBar, _claude2SevenDayUsed, _claude2SevenDayExpected);
        _claude2FiveHourReset = c.FiveHour.ResetsAt;
        _claude2SevenDayReset = c.SevenDay.ResetsAt;

        var showDesign = c.SevenDayDesign != null && _config.ShowClaude2Design;
        ClaudeDesign2Label.IsVisible = showDesign;
        ClaudeDesign2Bar.IsVisible = showDesign;
        if (showDesign && c.SevenDayDesign is { } d)
        {
            _claude2DesignUsed = Math.Clamp(d.UsedPercent, 0, 100);
            _claude2DesignExpected = d.ExpectedPercent;
            _claude2DesignReset = d.ResetsAt;
            SetTargetBar(ClaudeDesign2Bar, _claude2DesignUsed.Value, _claude2DesignExpected);
        }
        else
        {
            _claude2DesignUsed = null;
            _claude2DesignExpected = null;
            _claude2DesignReset = null;
        }

        var range = $"5h: in {FormatResetCountdown(c.FiveHour.ResetsAt)} • 7d: {FormatResetDate(c.SevenDay.ResetsAt)}";
        if (_claude2DesignUsed.HasValue)
            range += $" • Des: {_claude2DesignUsed.Value:F0}%";
        ClaudeCode2RangeText.Text = range;
    }

    private void ApplyZai(ZaiBlock? z)
    {
        var show = z != null && _config.ShowZai;
        ZaiSection.IsVisible = show;
        if (!show || z == null) return;

        _zai5hPercent = z.FiveHour?.UsedPercent;
        _zai5hReset = z.FiveHour?.ResetsAt;
        _zai5hExpected = z.FiveHour?.ExpectedPercent;
        _zaiMoPercent = z.Monthly?.UsedPercent;
        _zaiMoReset = z.Monthly?.ResetsAt;
        _zaiMoExpected = z.Monthly?.ExpectedPercent;

        ZaiTokenBar.IsVisible = z.FiveHour != null;
        if (z.FiveHour != null)
            SetTargetBar(ZaiTokenBar, z.FiveHour.UsedPercent, _zai5hExpected);

        ZaiMonthlyBar.IsVisible = z.Monthly != null;
        if (z.Monthly != null)
            SetTargetBar(ZaiMonthlyBar, z.Monthly.UsedPercent, _zaiMoExpected);
        ZaiDetailText.IsVisible = false;

        ZaiCreditsText.Inlines!.Clear();
        bool zaiAnyHeader = false;
        if (_zai5hPercent.HasValue)
        {
            ZaiCreditsText.Inlines.Add(new Run($"5h {_zai5hPercent.Value:F0}%"));
            if (_zai5hExpected.HasValue)
                ZaiCreditsText.Inlines.Add(ExpectedRun($" {_zai5hExpected.Value:F0}%", ZaiCreditsText.FontSize, _zai5hPercent.Value, _zai5hExpected));
            zaiAnyHeader = true;
        }
        if (_zaiMoPercent.HasValue)
        {
            if (zaiAnyHeader) ZaiCreditsText.Inlines.Add(new Run(" • "));
            ZaiCreditsText.Inlines.Add(new Run($"Mo {_zaiMoPercent.Value:F0}%"));
            if (_zaiMoExpected.HasValue)
                ZaiCreditsText.Inlines.Add(ExpectedRun($" {_zaiMoExpected.Value:F0}%", ZaiCreditsText.FontSize, _zaiMoPercent.Value, _zaiMoExpected));
            zaiAnyHeader = true;
        }
        if (!zaiAnyHeader) ZaiCreditsText.Inlines.Add(new Run("Connected"));
        else ZaiCreditsText.Inlines.Add(new Run(" used"));

        var resetParts = new List<string>();
        if (_zai5hReset.HasValue) resetParts.Add($"5h: in {FormatResetCountdown(_zai5hReset)}");
        if (_zaiMoReset.HasValue) resetParts.Add($"Mo: {FormatResetDate(_zaiMoReset)}");
        ZaiTokenText.Text = string.Join(" • ", resetParts);
    }

    private void UpdateUpdateAffordances()
    {
        UpdateButton.IsVisible = _updateAvailable && _viewMode == PopupViewMode.Full;
        UpdateButtonCompact.IsVisible = _updateAvailable && _viewMode == PopupViewMode.Compact;
        IconOnlyUpdateDot.IsVisible = _updateAvailable && _viewMode == PopupViewMode.IconOnly;
    }

    private void SetViewMode(PopupViewMode mode, bool anchorBottomRight = true)
    {
        // Capture current bottom-right pixel position before resizing
        var scaling = Screens.Primary?.Scaling ?? 1;
        var oldPixelW = (int)(Bounds.Width * scaling);
        var oldPixelH = (int)(Bounds.Height * scaling);
        var bottomRight = new PixelPoint(Position.X + oldPixelW, Position.Y + oldPixelH);

        _viewMode = mode;

        FullView.IsVisible = mode == PopupViewMode.Full;
        CompactView.IsVisible = mode == PopupViewMode.Compact;
        IconOnlyView.IsVisible = mode == PopupViewMode.IconOnly;

        CompactButton.IsVisible = mode == PopupViewMode.Full;
        IconOnlyButton.IsVisible = mode == PopupViewMode.Full;
        CloseButton.IsVisible = mode == PopupViewMode.Full;
        UpdateUpdateAffordances();

        // Full mode: outer border visible; compact/icon: transparent wrapper
        if (mode == PopupViewMode.Full)
        {
            OuterBorder.Background = Avalonia.Media.Brush.Parse("#E6181818");
            OuterBorder.BorderThickness = new Thickness(1);
            OuterBorder.Padding = new Thickness(12);
            OuterBorder.CornerRadius = new CornerRadius(18);
            SizeToContent = SizeToContent.Height;
            Width = FullWidth;
        }
        else
        {
            OuterBorder.Background = Avalonia.Media.Brushes.Transparent;
            OuterBorder.BorderThickness = new Thickness(0);
            OuterBorder.Padding = new Thickness(0);
            OuterBorder.CornerRadius = new CornerRadius(0);
            SizeToContent = SizeToContent.WidthAndHeight;
        }

        UpdateCompactSummary();
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();

        if (anchorBottomRight)
        {
            // Reposition so bottom-right stays at the same spot
            Dispatcher.UIThread.Post(() =>
            {
                var s = Screens.Primary?.Scaling ?? 1;
                var newPixelW = (int)(Bounds.Width * s);
                var newPixelH = (int)(Bounds.Height * s);
                Position = new PixelPoint(
                    bottomRight.X - newPixelW,
                    bottomRight.Y - newPixelH);
            }, DispatcherPriority.Loaded);
        }
    }

    private void UpdateCompactSummary()
    {
        if (OpenRouterCompactSection == null)
            return;

        // System one-liners
        UpdateCompactSystemBars();
        UptimeCompactText.Text = UptimeText.Text;
        NetCompactText.Text = $"{NetworkUpText.Text}  {NetworkDownText.Text}";

        // AI one-liners
        OpenRouterCompactSection.IsVisible = OpenRouterSection.IsVisible;
        OpenAiCompactRow.IsVisible = OpenAiRow.IsVisible;
        CodexCompactSection.IsVisible = CodexSection.IsVisible;
        Codex2CompactSection.IsVisible = Codex2Section.IsVisible;
        ClaudeCompactSection.IsVisible = ClaudeCodeSection.IsVisible;
        Claude2CompactSection.IsVisible = ClaudeCode2Section.IsVisible;
        ZaiCompactSection.IsVisible = ZaiSection.IsVisible;

        if (string.IsNullOrWhiteSpace(OpenRouterCompactText.Text))
            OpenRouterCompactText.Text = "—";

        OpenAiCompactText.Text = OpenAiCreditsText.Text ?? "—";

        const double barWidth = 62.0;
        CodexCompact5hRow.IsVisible = _codex5hUsed.HasValue;
        if (_codex5hUsed.HasValue)
            RenderCompactBar(CodexCompact5hBar, CodexCompact5hTick, Color.FromRgb(0x8B, 0xC3, 0x4A), _codex5hUsed.Value, _codex5hExpected, barWidth);
        RenderCompactBar(CodexCompact7dBar, CodexCompact7dTick, Color.FromRgb(0xB3, 0x9D, 0xDB), _codex7dUsed, _codex7dExpected, barWidth);
        if (_codex5hUsed.HasValue)
            SetUsedExpectedInlines(CodexCompact5hPct, _codex5hUsed, _codex5hExpected);
        SetUsedExpectedInlines(CodexCompact7dPct, _codex7dUsed, _codex7dExpected);
        CodexCompact5hReset.Text = _codex5hReset.HasValue ? $"in {FormatResetCountdown(_codex5hReset)}" : "";
        CodexCompact7dReset.Text = _codex7dReset.HasValue ? FormatResetDate(_codex7dReset) : "";

        CodexCompactSpark5hRow.IsVisible = _codexSpark5hUsed.HasValue;
        if (_codexSpark5hUsed.HasValue)
        {
            RenderCompactBar(CodexCompactSpark5hBar, CodexCompactSpark5hTick, Color.FromRgb(0x4D, 0xD0, 0xE1), _codexSpark5hUsed.Value, _codexSpark5hExpected, barWidth);
            SetUsedExpectedInlines(CodexCompactSpark5hPct, _codexSpark5hUsed, _codexSpark5hExpected);
            CodexCompactSpark5hReset.Text = _codexSpark5hReset.HasValue ? $"in {FormatResetCountdown(_codexSpark5hReset)}" : "";
        }
        CodexCompactSpark7dRow.IsVisible = _codexSpark7dUsed.HasValue;
        if (_codexSpark7dUsed.HasValue)
        {
            RenderCompactBar(CodexCompactSpark7dBar, CodexCompactSpark7dTick, Color.FromRgb(0x4D, 0xB6, 0xAC), _codexSpark7dUsed.Value, _codexSpark7dExpected, barWidth);
            SetUsedExpectedInlines(CodexCompactSpark7dPct, _codexSpark7dUsed, _codexSpark7dExpected);
            CodexCompactSpark7dReset.Text = _codexSpark7dReset.HasValue ? FormatResetDate(_codexSpark7dReset) : "";
        }

        Codex2CompactFiveHourRow.IsVisible = _codex2FiveHourUsed.HasValue;
        if (Codex2Section.IsVisible)
        {
            if (_codex2FiveHourUsed.HasValue)
            {
                RenderCompactBar(Codex2CompactFiveHourBar, Codex2CompactFiveHourTick, Color.FromRgb(0x64, 0xB5, 0xF6), _codex2FiveHourUsed.Value, _codex2FiveHourExpected, barWidth);
                SetUsedExpectedInlines(Codex2CompactFiveHourPercent, _codex2FiveHourUsed, _codex2FiveHourExpected);
                Codex2CompactFiveHourReset.Text = _codex2FiveHourReset.HasValue ? $"in {FormatResetCountdown(_codex2FiveHourReset)}" : "";
            }
            RenderCompactBar(Codex2CompactSevenDayBar, Codex2CompactSevenDayTick, Color.FromRgb(0x90, 0xCA, 0xF9), _codex2SevenDayUsed, _codex2SevenDayExpected, barWidth);
            SetUsedExpectedInlines(Codex2CompactSevenDayPercent, _codex2SevenDayUsed, _codex2SevenDayExpected);
            Codex2CompactSevenDayReset.Text = _codex2SevenDayReset.HasValue ? FormatResetDate(_codex2SevenDayReset) : "";
        }

        Codex2CompactSparkFiveHourRow.IsVisible = _codex2SparkFiveHourUsed.HasValue && Codex2Section.IsVisible;
        if (_codex2SparkFiveHourUsed.HasValue && Codex2Section.IsVisible)
        {
            RenderCompactBar(Codex2CompactSparkFiveHourBar, Codex2CompactSparkFiveHourTick, Color.FromRgb(0x95, 0x75, 0xCD), _codex2SparkFiveHourUsed.Value, _codex2SparkFiveHourExpected, barWidth);
            SetUsedExpectedInlines(Codex2CompactSparkFiveHourPercent, _codex2SparkFiveHourUsed, _codex2SparkFiveHourExpected);
            Codex2CompactSparkFiveHourReset.Text = _codex2SparkFiveHourReset.HasValue ? $"in {FormatResetCountdown(_codex2SparkFiveHourReset)}" : "";
        }
        Codex2CompactSparkSevenDayRow.IsVisible = _codex2SparkSevenDayUsed.HasValue && Codex2Section.IsVisible;
        if (_codex2SparkSevenDayUsed.HasValue && Codex2Section.IsVisible)
        {
            RenderCompactBar(Codex2CompactSparkSevenDayBar, Codex2CompactSparkSevenDayTick, Color.FromRgb(0xB3, 0x9D, 0xDB), _codex2SparkSevenDayUsed.Value, _codex2SparkSevenDayExpected, barWidth);
            SetUsedExpectedInlines(Codex2CompactSparkSevenDayPercent, _codex2SparkSevenDayUsed, _codex2SparkSevenDayExpected);
            Codex2CompactSparkSevenDayReset.Text = _codex2SparkSevenDayReset.HasValue ? FormatResetDate(_codex2SparkSevenDayReset) : "";
        }

        RenderCompactBar(ClaudeCompact5hBar, ClaudeCompact5hTick, Color.FromRgb(0xFF, 0x8A, 0x65), _claude5hUsed, _claude5hExpected, barWidth);
        RenderCompactBar(ClaudeCompact7dBar, ClaudeCompact7dTick, Color.FromRgb(0xFF, 0xB7, 0x4D), _claude7dUsed, _claude7dExpected, barWidth);
        SetUsedExpectedInlines(ClaudeCompact5hPct, _claude5hUsed, _claude5hExpected);
        SetUsedExpectedInlines(ClaudeCompact7dPct, _claude7dUsed, _claude7dExpected);
        ClaudeCompact5hReset.Text = _claude5hReset.HasValue ? $"in {FormatResetCountdown(_claude5hReset)}" : "";
        ClaudeCompact7dReset.Text = _claude7dReset.HasValue ? FormatResetDate(_claude7dReset) : "";

        ClaudeCompactDesignRow.IsVisible = _claudeDesignUsed.HasValue;
        if (_claudeDesignUsed.HasValue)
        {
            RenderCompactBar(ClaudeCompactDesignBar, ClaudeCompactDesignTick, Color.FromRgb(0xF4, 0x8F, 0xB1), _claudeDesignUsed.Value, _claudeDesignExpected, barWidth);
            SetUsedExpectedInlines(ClaudeCompactDesignPct, _claudeDesignUsed, _claudeDesignExpected);
            ClaudeCompactDesignReset.Text = _claudeDesignReset.HasValue ? FormatResetDate(_claudeDesignReset) : "";
        }

        // Second Claude account — fields are cleared whenever the section hides,
        // but keep the visibility guard for symmetry and safety.
        Claude2CompactDesignRow.IsVisible = _claude2DesignUsed.HasValue && ClaudeCode2Section.IsVisible;
        if (ClaudeCode2Section.IsVisible)
        {
            RenderCompactBar(Claude2Compact5hBar, Claude2Compact5hTick, Color.FromRgb(0xFF, 0x8A, 0x65), _claude2FiveHourUsed, _claude2FiveHourExpected, barWidth);
            RenderCompactBar(Claude2Compact7dBar, Claude2Compact7dTick, Color.FromRgb(0xFF, 0xB7, 0x4D), _claude2SevenDayUsed, _claude2SevenDayExpected, barWidth);
            SetUsedExpectedInlines(Claude2Compact5hPct, _claude2FiveHourUsed, _claude2FiveHourExpected);
            SetUsedExpectedInlines(Claude2Compact7dPct, _claude2SevenDayUsed, _claude2SevenDayExpected);
            Claude2Compact5hReset.Text = _claude2FiveHourReset.HasValue ? $"in {FormatResetCountdown(_claude2FiveHourReset)}" : "";
            Claude2Compact7dReset.Text = _claude2SevenDayReset.HasValue ? FormatResetDate(_claude2SevenDayReset) : "";

            if (_claude2DesignUsed.HasValue)
            {
                RenderCompactBar(Claude2CompactDesignBar, Claude2CompactDesignTick, Color.FromRgb(0xF4, 0x8F, 0xB1), _claude2DesignUsed.Value, _claude2DesignExpected, barWidth);
                SetUsedExpectedInlines(Claude2CompactDesignPct, _claude2DesignUsed, _claude2DesignExpected);
                Claude2CompactDesignReset.Text = _claude2DesignReset.HasValue ? FormatResetDate(_claude2DesignReset) : "";
            }
        }

        RenderCompactBar(ZaiCompact5hBar, ZaiCompact5hTick, Color.FromRgb(0xBA, 0x68, 0xC8), _zai5hPercent ?? 0, _zai5hExpected, barWidth);
        RenderCompactBar(ZaiCompactMoBar, ZaiCompactMoTick, Color.FromRgb(0x7E, 0x57, 0xC2), _zaiMoPercent ?? 0, _zaiMoExpected, barWidth);
        SetUsedExpectedInlines(ZaiCompact5hPct, _zai5hPercent, _zai5hExpected);
        SetUsedExpectedInlines(ZaiCompactMoPct, _zaiMoPercent, _zaiMoExpected);
        ZaiCompact5hReset.Text = _zai5hReset.HasValue ? $"in {FormatResetCountdown(_zai5hReset)}" : "";
        ZaiCompactMoReset.Text = _zaiMoReset.HasValue ? FormatResetDate(_zaiMoReset) : "";
    }

    private static string FormatUsedExpected(double? used, double? expected)
    {
        if (!used.HasValue) return "—";
        return expected.HasValue
            ? $"{used.Value:F0}% {expected.Value:F0}%"
            : $"{used.Value:F0}%";
    }

    private static string FormatExpectedShort(double? expected)
    {
        return expected.HasValue ? $"{expected.Value:F0}%" : "—";
    }

    private static Color ExpectedTint(double used, double? expected)
    {
        if (expected is not double exp || used <= exp) return Color.FromRgb(0xC8, 0xE6, 0xC9); // light green
        var headroom = Math.Max(1.0, 100.0 - exp);
        var t = Math.Clamp((used - exp) / headroom, 0, 1);
        if (t <= 0.33) return Color.FromRgb(0xFF, 0xF5, 0x9D); // light yellow
        if (t <= 0.66) return Color.FromRgb(0xFF, 0xCC, 0x80); // light orange
        return Color.FromRgb(0xEF, 0x9A, 0x9A);                 // light red
    }

    private static Run ExpectedRun(string text, double baseFontSize, double used, double? expected)
    {
        return new Run(text)
        {
            Foreground = new SolidColorBrush(ExpectedTint(used, expected)),
            FontSize = Math.Max(6, baseFontSize - 2),
        };
    }

    private static void SetUsedExpectedInlines(TextBlock tb, double? used, double? expected)
    {
        tb.Inlines!.Clear();
        if (!used.HasValue)
        {
            tb.Inlines.Add(new Run("—"));
            return;
        }
        tb.Inlines.Add(new Run($"{used.Value:F0}%"));
        if (expected.HasValue)
        {
            tb.Inlines.Add(ExpectedRun($" {expected.Value:F0}%", tb.FontSize, used.Value, expected));
        }
    }

    private static void SetTwoUsedExpectedInlines(TextBlock tb, double a, double? aExp, double b, double? bExp)
    {
        tb.Inlines!.Clear();
        tb.Inlines.Add(new Run($"{a:F0}%"));
        if (aExp.HasValue)
            tb.Inlines.Add(ExpectedRun($" ({aExp.Value:F0}%)", tb.FontSize, a, aExp));
        tb.Inlines.Add(new Run($" / {b:F0}%"));
        if (bExp.HasValue)
            tb.Inlines.Add(ExpectedRun($" ({bExp.Value:F0}%)", tb.FontSize, b, bExp));
        tb.Inlines.Add(new Run(" used"));
    }

    private static string FormatResetCountdown(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue) return "—";
        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{remaining.Minutes}m";
    }

    private static string FormatResetDate(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue) return "—";
        try
        {
            var central = GetCentralTimeZone();
            var converted = TimeZoneInfo.ConvertTime(resetAt.Value, central);
            return $"{converted:ddd MMM d}";
        }
        catch
        {
            return $"{resetAt.Value.ToLocalTime():ddd MMM d}";
        }
    }
    #endregion

    private static string FormatCodexPlan(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
            return "Plan unknown";

        var normalized = planType.Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized);
    }

    private static string FormatFetchedAt(DateTimeOffset? fetchedAt)
    {
        return FormatCentralTime(fetchedAt);
    }

    private static string EnsureUsedSuffix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "—";

        return text.Contains("used", StringComparison.OrdinalIgnoreCase)
            ? text
            : $"{text} used";
    }

    private static string FormatCentralTime(DateTimeOffset? value)
    {
        if (!value.HasValue)
            return "unknown";

        try
        {
            var central = GetCentralTimeZone();
            var converted = TimeZoneInfo.ConvertTime(value.Value, central);
            var isDst = central.IsDaylightSavingTime(converted.DateTime);
            var suffix = isDst ? "CDT" : "CST";
            return $"{converted:MMM d h:mm tt} {suffix}";
        }
        catch
        {
            return $"{value.Value.ToLocalTime():MMM d h:mm tt}";
        }
    }

    private static TimeZoneInfo GetCentralTimeZone()
    {
        try
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time")
                : TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string FormatTokenCount(long totalTokens)
    {
        if (totalTokens >= 1_000_000)
            return $"{totalTokens / 1_000_000.0:F2}M";
        if (totalTokens >= 1_000)
            return $"{totalTokens / 1_000.0:F1}K";
        return totalTokens.ToString("N0");
    }

    #region Drag Support

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            if (e.Source is Control sourceControl)
            {
                if (sourceControl is Button)
                    return;

                if (sourceControl.GetVisualAncestors().OfType<Button>().Any())
                    return;
            }

            if (CloseButton.IsPointerOver || CompactButton.IsPointerOver || IconOnlyButton.IsPointerOver || RestoreIconButton.IsPointerOver
                || CloseCompactButton.IsPointerOver || UpdateButton.IsPointerOver || UpdateButtonCompact.IsPointerOver)
                return;

            var pos = point.Position;
            _isDragging = true;
            _anchorBottomRight = null;
            var screenPos = this.PointToScreen(pos);
            _dragStartScreenPoint = new PixelPoint((int)screenPos.X, (int)screenPos.Y);
            _windowStartPosition = Position;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            var currentPos = e.GetPosition(this);
            var screenPos = this.PointToScreen(currentPos);
            var currentScreenPoint = new PixelPoint((int)screenPos.X, (int)screenPos.Y);

            Position = new PixelPoint(
                _windowStartPosition.X + (currentScreenPoint.X - _dragStartScreenPoint.X),
                _windowStartPosition.Y + (currentScreenPoint.Y - _dragStartScreenPoint.Y)
            );
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
        }
    }

    #endregion

    #region Non-Activating Window (Windows)

    private void MakeWindowNonActivating()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return;

                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting non-activating: {ex.Message}");
            }
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    #endregion

    #region Windows Memory API

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    #endregion

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Hide-on-close applies only to user-initiated closes. App- or OS-driven
        // closes (Cmd+Q, logout, macOS system shutdown) must never be vetoed —
        // cancelling those makes macOS report "UsageMonitor interrupted shutdown"
        // and hangs the whole machine's shutdown until the OS force-kills us.
        if (!_allowClose && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            HidePopup();
        }
        else
        {
            _systemRefreshTimer.Stop();
            _aiRefreshTimer.Stop();
        }
        base.OnClosing(e);
    }
}
