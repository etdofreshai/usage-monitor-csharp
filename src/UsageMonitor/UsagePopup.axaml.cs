using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    // Network tracking
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private DateTime _lastNetworkCheck = DateTime.MinValue;

    // CPU tracking (Windows)
    private PerformanceCounter? _cpuCounter;

    // Latest reset timestamps (driven by RefreshXxxAsync, read by UpdateCompactSummary)
    private DateTimeOffset? _codex5hReset, _codex7dReset;
    private DateTimeOffset? _claude5hReset, _claude7dReset;
    private DateTimeOffset? _zai5hReset, _zaiMoReset;
    private double? _zai5hPercent, _zaiMoPercent;

    // Single source of truth: the usage-api aggregator.
    private UsageApiService? _usageApiService;

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
        CompactButton.Click += (s, e) => SetViewMode(PopupViewMode.Compact);
        IconOnlyButton.Click += (s, e) => SetViewMode(PopupViewMode.IconOnly);
        RestoreFullButton.Click += (s, e) => SetViewMode(PopupViewMode.Full);
        RestoreIconButton.Click += (s, e) => SetViewMode(PopupViewMode.Full);
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

        // Initialize AI services
        InitializeAiServices();
        SetViewMode(PopupViewMode.Full);

        // Position near taskbar on first show only
        Opened += (s, e) =>
        {
            MakeWindowNonActivating();
            // Position after layout so Bounds reflects actual rendered size
            Dispatcher.UIThread.Post(() =>
            {
                if (Screens.Primary is { } screen)
                {
                    var workArea = screen.WorkingArea;
                    var scaling = screen.Scaling;
                    var pixelW = (int)(Bounds.Width * scaling);
                    var pixelH = (int)(Bounds.Height * scaling);
                    Position = new PixelPoint(
                        workArea.X + workArea.Width - pixelW,
                        workArea.Y + workArea.Height - pixelH);
                }
            }, DispatcherPriority.Loaded);
        };
    }

    private void InitializeAiServices()
    {
        _usageApiService = new UsageApiService(_config.UsageApiUrl);
        // Sections start hidden; the first successful refresh reveals whichever providers
        // returned data. NoKeysHint also flips off after the first successful response.
        NoKeysHint.IsVisible = false;
        ConfigPathText.Text = $"Source: {_config.UsageApiUrl}";
    }

    public void TogglePopup()
    {
        if (IsVisible)
        {
            if (_viewMode == PopupViewMode.Full)
                HidePopup();
            else
                SetViewMode(PopupViewMode.Full);
        }
        else
            ShowPopup();
    }

    public void ShowPopup()
    {
        SetViewMode(PopupViewMode.Full, anchorBottomRight: false);
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
        _allowClose = true;
        Close();
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
        CodexCreditsText.Text = $"{primary:F0}% / {secondary:F0}% used";
        CodexPrimaryBar.Value = primary;
        CodexSecondaryBar.Value = secondary;
        _codex5hReset = c.Primary.ResetsAt;
        _codex7dReset = c.Secondary.ResetsAt;
        CodexRangeText.Text = $"5h: in {FormatResetCountdown(c.Primary.ResetsAt)} • 7d: {FormatResetDate(c.Secondary.ResetsAt)}";
    }

    private void ApplyClaude(ClaudeBlock? c)
    {
        ClaudeCodeSection.IsVisible = c != null;
        if (c == null) return;
        ClaudeCodeCreditsText.Text = $"{c.FiveHour.UsedPercent:F0}% / {c.SevenDay.UsedPercent:F0}% used";
        ClaudeCodePrimaryBar.Value = c.FiveHour.UsedPercent;
        ClaudeCodeSecondaryBar.Value = c.SevenDay.UsedPercent;
        _claude5hReset = c.FiveHour.ResetsAt;
        _claude7dReset = c.SevenDay.ResetsAt;
        ClaudeCodeRangeText.Text = $"5h: in {FormatResetCountdown(c.FiveHour.ResetsAt)} • 7d: {FormatResetDate(c.SevenDay.ResetsAt)}";
    }

    private void ApplyZai(ZaiBlock? z)
    {
        ZaiSection.IsVisible = z != null;
        if (z == null) return;

        _zai5hPercent = z.FiveHour?.UsedPercent;
        _zai5hReset = z.FiveHour?.ResetsAt;
        _zaiMoPercent = z.Monthly?.UsedPercent;
        _zaiMoReset = z.Monthly?.ResetsAt;

        if (z.FiveHour != null)
        {
            ZaiTokenBar.Value = z.FiveHour.UsedPercent;
            ZaiTokenBar.IsVisible = true;
        }
        else
        {
            ZaiTokenBar.IsVisible = false;
        }

        if (z.Monthly != null)
        {
            ZaiMonthlyBar.Value = z.Monthly.UsedPercent;
            ZaiMonthlyBar.IsVisible = true;
        }
        else
        {
            ZaiMonthlyBar.IsVisible = false;
        }
        ZaiDetailText.IsVisible = false;

        var headerParts = new List<string>();
        if (_zai5hPercent.HasValue) headerParts.Add($"5h {_zai5hPercent.Value:F0}%");
        if (_zaiMoPercent.HasValue) headerParts.Add($"Mo {_zaiMoPercent.Value:F0}%");
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
        CodexCompact5hBar.Width = Math.Clamp(CodexPrimaryBar.Value / 100.0, 0, 1) * barWidth;
        CodexCompact7dBar.Width = Math.Clamp(CodexSecondaryBar.Value / 100.0, 0, 1) * barWidth;
        CodexCompact5hPct.Text = $"{CodexPrimaryBar.Value:F0}%";
        CodexCompact7dPct.Text = $"{CodexSecondaryBar.Value:F0}%";
        CodexCompact5hReset.Text = _codex5hReset.HasValue ? $"in {FormatResetCountdown(_codex5hReset)}" : "";
        CodexCompact7dReset.Text = _codex7dReset.HasValue ? FormatResetDate(_codex7dReset) : "";

        ClaudeCompact5hBar.Width = Math.Clamp(ClaudeCodePrimaryBar.Value / 100.0, 0, 1) * barWidth;
        ClaudeCompact7dBar.Width = Math.Clamp(ClaudeCodeSecondaryBar.Value / 100.0, 0, 1) * barWidth;
        ClaudeCompact5hPct.Text = $"{ClaudeCodePrimaryBar.Value:F0}%";
        ClaudeCompact7dPct.Text = $"{ClaudeCodeSecondaryBar.Value:F0}%";
        ClaudeCompact5hReset.Text = _claude5hReset.HasValue ? $"in {FormatResetCountdown(_claude5hReset)}" : "";
        ClaudeCompact7dReset.Text = _claude7dReset.HasValue ? FormatResetDate(_claude7dReset) : "";

        ZaiCompact5hBar.Width = Math.Clamp((_zai5hPercent ?? 0) / 100.0, 0, 1) * barWidth;
        ZaiCompactMoBar.Width = Math.Clamp((_zaiMoPercent ?? 0) / 100.0, 0, 1) * barWidth;
        ZaiCompact5hPct.Text = _zai5hPercent.HasValue ? $"{_zai5hPercent.Value:F0}%" : "—";
        ZaiCompactMoPct.Text = _zaiMoPercent.HasValue ? $"{_zaiMoPercent.Value:F0}%" : "—";
        ZaiCompact5hReset.Text = _zai5hReset.HasValue ? $"in {FormatResetCountdown(_zai5hReset)}" : "";
        ZaiCompactMoReset.Text = _zaiMoReset.HasValue ? FormatResetDate(_zaiMoReset) : "";
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

            if (CloseButton.IsPointerOver || CompactButton.IsPointerOver || IconOnlyButton.IsPointerOver || RestoreIconButton.IsPointerOver)
                return;

            var pos = point.Position;
            _isDragging = true;
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

