using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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

    // CPU tracking (Windows)
    private PerformanceCounter? _cpuCounter;

    // Latest reset timestamps (driven by RefreshXxxAsync, read by UpdateCompactSummary)
    private DateTimeOffset? _codex5hReset, _codex7dReset;
    private DateTimeOffset? _codexSpark5hReset, _codexSpark7dReset;
    private DateTimeOffset? _claude5hReset, _claude7dReset, _claudeDesignReset;
    private DateTimeOffset? _zai5hReset, _zaiMoReset;
    private double? _zai5hPercent, _zaiMoPercent;

    // Latest used/expected percent per AI window, used for compact-view rendering.
    private double _codex5hUsed, _codex7dUsed;
    private double? _codex5hExpected, _codex7dExpected;
    private double? _codexSpark5hUsed, _codexSpark7dUsed;
    private double? _codexSpark5hExpected, _codexSpark7dExpected;
    private double _claude5hUsed, _claude7dUsed;
    private double? _claude5hExpected, _claude7dExpected;
    private double? _claudeDesignUsed;
    private double? _claudeDesignExpected;
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

    public UsagePopup()
    {
        InitializeComponent();
        Icon = AppIcon.Create();
        _appBitmap = AppIcon.CreateBitmap();
        RestoreFullIcon.Source = _appBitmap;
        IconOnlyButtonImage.Source = _appBitmap;
        RestoreIconImage.Source = _appBitmap;

        _config = Config.Load();

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
        RegisterTargetBar(ClaudeCodePrimaryBar, ClaudeCodePrimaryBarFill, ClaudeCodePrimaryBarTick, Color.FromRgb(0xFF, 0x8A, 0x65));
        RegisterTargetBar(ClaudeCodeSecondaryBar, ClaudeCodeSecondaryBarFill, ClaudeCodeSecondaryBarTick, Color.FromRgb(0xFF, 0xB7, 0x4D));
        RegisterTargetBar(ClaudeDesignBar, ClaudeDesignBarFill, ClaudeDesignBarTick, Color.FromRgb(0xF4, 0x8F, 0xB1));
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
            UpdateButton.IsVisible = true;
            UpdateButtonCompact.IsVisible = true;
            IconOnlyUpdateDot.IsVisible = true;
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

    public void ForceClose()
    {
        _usageApiService?.Dispose();
        _updateChecker?.Dispose();
        _allowClose = true;
        Close();
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateChecker == null || !_updateChecker.Enabled) return;
        try
        {
            var success = await _updateChecker.ApplyUpdateAsync();
            if (success)
            {
                UpdateChecker.RestartApp();
                ForceClose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update apply failed: {ex.Message}");
        }
    }

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

        CpuBar.Value = cpuPercent;
        CpuPercentText.Text = $"{cpuPercent:F0}%";
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

                    MemoryBar.Value = percent;
                    MemoryText.Text = $"{usedGB:F1} / {totalGB:F1} GB";
                }
            }
            catch { }
        }
        else
        {
            var info = GC.GetGCMemoryInfo();
            double totalGB = info.TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
            MemoryText.Text = $"~{totalGB:F1} GB total";
        }
    }

    private void UpdateDisk()
    {
        try
        {
            var drive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new DriveInfo("C")
                : new DriveInfo("/");

            double totalGB = drive.TotalSize / (1024.0 * 1024 * 1024);
            double usedGB = (drive.TotalSize - drive.AvailableFreeSpace) / (1024.0 * 1024 * 1024);
            double percent = (usedGB / totalGB) * 100;

            DiskBar.Value = percent;
            DiskText.Text = $"{usedGB:F0} / {totalGB:F0} GB";
        }
        catch { }
    }

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

        Dispatcher.UIThread.Post(() =>
        {
            ApplyOpenRouter(status.OpenRouter);
            ApplyOpenAi(status.OpenAi);
            ApplyCodex(status.Codex);
            ApplyClaude(status.Claude);
            ApplyZai(status.Zai);
            UpdateCompactSummary();
        });
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
        CodexSection.IsVisible = c != null;
        if (c == null) return;
        var primary = Math.Clamp(c.Primary.UsedPercent, 0, 100);
        var secondary = Math.Clamp(c.Secondary.UsedPercent, 0, 100);
        CodexCreditsText.Text = $"{primary:F0}% ({FormatExpectedShort(c.Primary.ExpectedPercent)}) / {secondary:F0}% ({FormatExpectedShort(c.Secondary.ExpectedPercent)}) used";
        _codex5hUsed = primary;
        _codex7dUsed = secondary;
        _codex5hExpected = c.Primary.ExpectedPercent;
        _codex7dExpected = c.Secondary.ExpectedPercent;
        SetTargetBar(CodexPrimaryBar, primary, _codex5hExpected);
        SetTargetBar(CodexSecondaryBar, secondary, _codex7dExpected);
        _codex5hReset = c.Primary.ResetsAt;
        _codex7dReset = c.Secondary.ResetsAt;

        // Spark (GPT-5.3-Codex-Spark) optional bars.
        var hasSpark5h = c.SparkPrimary != null;
        var hasSpark7d = c.SparkSecondary != null;
        CodexSpark5hLabel.IsVisible = hasSpark5h;
        CodexSparkPrimaryBar.IsVisible = hasSpark5h;
        CodexSpark7dLabel.IsVisible = hasSpark7d;
        CodexSparkSecondaryBar.IsVisible = hasSpark7d;

        if (c.SparkPrimary is { } sp)
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

        if (c.SparkSecondary is { } ss)
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

        var range = $"5h: in {FormatResetCountdown(c.Primary.ResetsAt)} • 7d: {FormatResetDate(c.Secondary.ResetsAt)}";
        if (hasSpark5h || hasSpark7d)
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

    private void ApplyClaude(ClaudeBlock? c)
    {
        ClaudeCodeSection.IsVisible = c != null;
        if (c == null) return;
        ClaudeCodeCreditsText.Text = $"{c.FiveHour.UsedPercent:F0}% ({FormatExpectedShort(c.FiveHour.ExpectedPercent)}) / {c.SevenDay.UsedPercent:F0}% ({FormatExpectedShort(c.SevenDay.ExpectedPercent)}) used";
        _claude5hUsed = c.FiveHour.UsedPercent;
        _claude7dUsed = c.SevenDay.UsedPercent;
        _claude5hExpected = c.FiveHour.ExpectedPercent;
        _claude7dExpected = c.SevenDay.ExpectedPercent;
        SetTargetBar(ClaudeCodePrimaryBar, _claude5hUsed, _claude5hExpected);
        SetTargetBar(ClaudeCodeSecondaryBar, _claude7dUsed, _claude7dExpected);
        _claude5hReset = c.FiveHour.ResetsAt;
        _claude7dReset = c.SevenDay.ResetsAt;

        var hasDesign = c.SevenDayDesign != null;
        ClaudeDesignLabel.IsVisible = hasDesign;
        ClaudeDesignBar.IsVisible = hasDesign;
        if (c.SevenDayDesign is { } d)
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

    private void ApplyZai(ZaiBlock? z)
    {
        ZaiSection.IsVisible = z != null;
        if (z == null) return;

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

        var headerParts = new List<string>();
        if (_zai5hPercent.HasValue) headerParts.Add($"5h {FormatUsedExpected(_zai5hPercent, _zai5hExpected)}");
        if (_zaiMoPercent.HasValue) headerParts.Add($"Mo {FormatUsedExpected(_zaiMoPercent, _zaiMoExpected)}");
        ZaiCreditsText.Text = headerParts.Count > 0 ? string.Join(" • ", headerParts) + " used" : "Connected";

        var resetParts = new List<string>();
        if (_zai5hReset.HasValue) resetParts.Add($"5h: in {FormatResetCountdown(_zai5hReset)}");
        if (_zaiMoReset.HasValue) resetParts.Add($"Mo: {FormatResetDate(_zaiMoReset)}");
        ZaiTokenText.Text = string.Join(" • ", resetParts);
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
        CpuCompactText.Text = CpuPercentText.Text;
        MemoryCompactText.Text = MemoryText.Text;
        DiskCompactText.Text = DiskText.Text;
        UptimeCompactText.Text = UptimeText.Text;
        NetCompactText.Text = $"{NetworkUpText.Text}  {NetworkDownText.Text}";

        // AI one-liners
        OpenRouterCompactSection.IsVisible = OpenRouterSection.IsVisible;
        OpenAiCompactRow.IsVisible = OpenAiRow.IsVisible;
        CodexCompactSection.IsVisible = CodexSection.IsVisible;
        ClaudeCompactSection.IsVisible = ClaudeCodeSection.IsVisible;
        ZaiCompactSection.IsVisible = ZaiSection.IsVisible;

        if (string.IsNullOrWhiteSpace(OpenRouterCompactText.Text))
            OpenRouterCompactText.Text = "—";

        OpenAiCompactText.Text = OpenAiCreditsText.Text ?? "—";

        const double barWidth = 62.0;
        RenderCompactBar(CodexCompact5hBar, CodexCompact5hTick, Color.FromRgb(0x8B, 0xC3, 0x4A), _codex5hUsed, _codex5hExpected, barWidth);
        RenderCompactBar(CodexCompact7dBar, CodexCompact7dTick, Color.FromRgb(0xB3, 0x9D, 0xDB), _codex7dUsed, _codex7dExpected, barWidth);
        CodexCompact5hPct.Text = FormatUsedExpected(_codex5hUsed, _codex5hExpected);
        CodexCompact7dPct.Text = FormatUsedExpected(_codex7dUsed, _codex7dExpected);
        CodexCompact5hReset.Text = _codex5hReset.HasValue ? $"in {FormatResetCountdown(_codex5hReset)}" : "";
        CodexCompact7dReset.Text = _codex7dReset.HasValue ? FormatResetDate(_codex7dReset) : "";

        CodexCompactSpark5hRow.IsVisible = _codexSpark5hUsed.HasValue;
        if (_codexSpark5hUsed.HasValue)
        {
            RenderCompactBar(CodexCompactSpark5hBar, CodexCompactSpark5hTick, Color.FromRgb(0x4D, 0xD0, 0xE1), _codexSpark5hUsed.Value, _codexSpark5hExpected, barWidth);
            CodexCompactSpark5hPct.Text = FormatUsedExpected(_codexSpark5hUsed, _codexSpark5hExpected);
            CodexCompactSpark5hReset.Text = _codexSpark5hReset.HasValue ? $"in {FormatResetCountdown(_codexSpark5hReset)}" : "";
        }
        CodexCompactSpark7dRow.IsVisible = _codexSpark7dUsed.HasValue;
        if (_codexSpark7dUsed.HasValue)
        {
            RenderCompactBar(CodexCompactSpark7dBar, CodexCompactSpark7dTick, Color.FromRgb(0x4D, 0xB6, 0xAC), _codexSpark7dUsed.Value, _codexSpark7dExpected, barWidth);
            CodexCompactSpark7dPct.Text = FormatUsedExpected(_codexSpark7dUsed, _codexSpark7dExpected);
            CodexCompactSpark7dReset.Text = _codexSpark7dReset.HasValue ? FormatResetDate(_codexSpark7dReset) : "";
        }

        RenderCompactBar(ClaudeCompact5hBar, ClaudeCompact5hTick, Color.FromRgb(0xFF, 0x8A, 0x65), _claude5hUsed, _claude5hExpected, barWidth);
        RenderCompactBar(ClaudeCompact7dBar, ClaudeCompact7dTick, Color.FromRgb(0xFF, 0xB7, 0x4D), _claude7dUsed, _claude7dExpected, barWidth);
        ClaudeCompact5hPct.Text = FormatUsedExpected(_claude5hUsed, _claude5hExpected);
        ClaudeCompact7dPct.Text = FormatUsedExpected(_claude7dUsed, _claude7dExpected);
        ClaudeCompact5hReset.Text = _claude5hReset.HasValue ? $"in {FormatResetCountdown(_claude5hReset)}" : "";
        ClaudeCompact7dReset.Text = _claude7dReset.HasValue ? FormatResetDate(_claude7dReset) : "";

        ClaudeCompactDesignRow.IsVisible = _claudeDesignUsed.HasValue;
        if (_claudeDesignUsed.HasValue)
        {
            RenderCompactBar(ClaudeCompactDesignBar, ClaudeCompactDesignTick, Color.FromRgb(0xF4, 0x8F, 0xB1), _claudeDesignUsed.Value, _claudeDesignExpected, barWidth);
            ClaudeCompactDesignPct.Text = FormatUsedExpected(_claudeDesignUsed, _claudeDesignExpected);
            ClaudeCompactDesignReset.Text = _claudeDesignReset.HasValue ? FormatResetDate(_claudeDesignReset) : "";
        }

        RenderCompactBar(ZaiCompact5hBar, ZaiCompact5hTick, Color.FromRgb(0xBA, 0x68, 0xC8), _zai5hPercent ?? 0, _zai5hExpected, barWidth);
        RenderCompactBar(ZaiCompactMoBar, ZaiCompactMoTick, Color.FromRgb(0x7E, 0x57, 0xC2), _zaiMoPercent ?? 0, _zaiMoExpected, barWidth);
        ZaiCompact5hPct.Text = FormatUsedExpected(_zai5hPercent, _zai5hExpected);
        ZaiCompactMoPct.Text = FormatUsedExpected(_zaiMoPercent, _zaiMoExpected);
        ZaiCompact5hReset.Text = _zai5hReset.HasValue ? $"in {FormatResetCountdown(_zai5hReset)}" : "";
        ZaiCompactMoReset.Text = _zaiMoReset.HasValue ? FormatResetDate(_zaiMoReset) : "";
    }

    private static string FormatUsedExpected(double? used, double? expected)
    {
        if (!used.HasValue) return "—";
        return expected.HasValue
            ? $"{used.Value:F0}% e{expected.Value:F0}%"
            : $"{used.Value:F0}%";
    }

    private static string FormatExpectedShort(double? expected)
    {
        return expected.HasValue ? $"e{expected.Value:F0}%" : "e—";
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
        if (!_allowClose)
        {
            e.Cancel = true;
            HidePopup();
        }
    }
}

