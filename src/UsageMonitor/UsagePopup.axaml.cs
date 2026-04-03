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

    // AI service clients
    private OpenRouterService? _openRouterService;
    private OpenAiService? _openAiService;
    private CodexLocalService? _codexLocalService;
    private ClaudeCodeLocalService? _claudeCodeLocalService;
    private ZaiService? _zaiService;

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

        // AI credits refresh (every 30 seconds, configurable)
        var aiRefreshIntervalSeconds = Math.Max(_config.RefreshIntervalSeconds, 300);
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
        bool anyConfigured = false;

        if (!string.IsNullOrEmpty(_config.OpenRouterApiKey))
        {
            _openRouterService = new OpenRouterService(_config.OpenRouterApiKey);
            OpenRouterSection.IsVisible = true;
            anyConfigured = true;
        }

        if (!string.IsNullOrEmpty(_config.OpenAiAdminKey))
        {
            _openAiService = new OpenAiService(_config.OpenAiAdminKey, _config.OpenAiPrepaidBalance);
            OpenAiRow.IsVisible = true;
        }

        _codexLocalService = new CodexLocalService();
        if (_codexLocalService.IsAvailable())
        {
            CodexSection.IsVisible = true;
            anyConfigured = true;
        }

        _claudeCodeLocalService = new ClaudeCodeLocalService();
        if (_claudeCodeLocalService.IsAvailable())
        {
            ClaudeCodeSection.IsVisible = true;
            anyConfigured = true;
        }

        if (!string.IsNullOrEmpty(_config.ZaiApiKey))
        {
            _zaiService = new ZaiService(_config.ZaiApiKey);
            ZaiSection.IsVisible = true;
            anyConfigured = true;
        }

        if (anyConfigured)
        {
            NoKeysHint.IsVisible = false;
        }
        else
        {
            ConfigPathText.Text = $"Config: {Config.GetConfigPath()}";
        }
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
        _openRouterService?.Dispose();
        _openAiService?.Dispose();
        _claudeCodeLocalService?.Dispose();
        _zaiService?.Dispose();

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
        var tasks = new List<Task>();

        if (_openRouterService != null)
            tasks.Add(RefreshOpenRouterAsync());
        if (_openAiService != null)
            tasks.Add(RefreshOpenAiAsync());
        if (_codexLocalService != null)
            tasks.Add(RefreshCodexAsync());
        if (_claudeCodeLocalService != null)
            tasks.Add(RefreshClaudeCodeAsync());
        if (_zaiService != null)
            tasks.Add(RefreshZaiAsync());

        await Task.WhenAll(tasks);
        UpdateCompactSummary();
    }

    private async Task RefreshOpenRouterAsync()
    {
        try
        {
            var status = await _openRouterService!.GetStatusAsync();
            if (status == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                var usedCredits = GetOpenRouterUsedCredits(status);
                var remainingCredits = GetOpenRouterRemainingCredits(status);
                var totalCredits = status.TotalCredits ?? status.Limit;

                if (usedCredits.HasValue)
                {
                    var remaining = remainingCredits ?? (totalCredits.HasValue
                        ? Math.Max(0, totalCredits.Value - usedCredits.Value)
                        : (double?)null);
                    OpenRouterCreditsText.Text = remaining.HasValue
                        ? $"${usedCredits.Value:F2} used | ${remaining.Value:F2} left"
                        : $"${usedCredits.Value:F2} used";
                }
                else
                {
                    var remaining = remainingCredits ?? (totalCredits.HasValue
                        ? Math.Max(0, totalCredits.Value - status.Usage)
                        : (double?)null);
                    OpenRouterCreditsText.Text = remaining.HasValue
                        ? $"${status.Usage:F2} used | ${remaining.Value:F2} left"
                        : $"${status.Usage:F2} used";
                }

                if (remainingCredits.HasValue)
                {
                    OpenRouterCompactText.Text = $"${remainingCredits.Value:F2} left";
                }
                else if (totalCredits.HasValue && usedCredits.HasValue)
                {
                    OpenRouterCompactText.Text = $"${Math.Max(0, totalCredits.Value - usedCredits.Value):F2} left";
                }
                else
                {
                    OpenRouterCompactText.Text = "—";
                }

                OpenRouterRangeText.Text = GetOpenRouterPeriodText(status);
                UpdateCompactSummary();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenRouter refresh error: {ex.Message}");
        }
    }

    private async Task RefreshOpenAiAsync()
    {
        try
        {
            var status = await _openAiService!.GetStatusAsync();
            if (status == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                OpenAiCreditsText.Text = $"${status.SpendMonth:F2} this month";
                UpdateCompactSummary();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenAI refresh error: {ex.Message}");
        }
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

        CodexCompactText.Text = $"{CodexPrimaryBar.Value:F0}%/{CodexSecondaryBar.Value:F0}% used";

        ClaudeCompactText.Text = $"{ClaudeCodePrimaryBar.Value:F0}%/{ClaudeCodeSecondaryBar.Value:F0}% used";

        ZaiCompactText.Text = EnsureUsedSuffix((ZaiDetailText.Text ?? "—")
            .Replace(" monthly prompts", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" / ", "/", StringComparison.OrdinalIgnoreCase));
    }
    private async Task RefreshCodexAsync()
    {
        try
        {
            var status = await _codexLocalService!.GetStatusAsync();
            if (status == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                var primaryUsed = Math.Clamp(status.Primary.UsedPercent, 0, 100);
                var secondaryUsed = Math.Clamp(status.Secondary.UsedPercent, 0, 100);

                CodexCreditsText.Text = $"{primaryUsed:F0}% / {secondaryUsed:F0}% used";
                CodexPrimaryBar.Value = primaryUsed;
                CodexSecondaryBar.Value = secondaryUsed;

                CodexRangeText.Text = $"Resets 5h {FormatResetTime(status.Primary.ResetsAt)} | 7d {FormatResetTime(status.Secondary.ResetsAt)}";
                UpdateCompactSummary();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Codex refresh error: {ex.Message}");
        }
    }

    private async Task RefreshClaudeCodeAsync()
    {
        try
        {
            var status = await _claudeCodeLocalService!.GetStatusAsync();
            if (status == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                ClaudeCodeCreditsText.Text = $"{status.FiveHour.UtilizationPercent:F0}% / {status.SevenDay.UtilizationPercent:F0}% used";
                ClaudeCodePrimaryBar.Value = status.FiveHour.UtilizationPercent;
                ClaudeCodeSecondaryBar.Value = status.SevenDay.UtilizationPercent;

                ClaudeCodeRangeText.Text = $"Resets 5h {FormatResetTime(status.FiveHour.ResetsAt)} | 7d {FormatResetTime(status.SevenDay.ResetsAt)}";
                UpdateCompactSummary();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Claude Code refresh error: {ex.Message}");
        }
    }

    private async Task RefreshZaiAsync()
    {
        try
        {
            var status = await _zaiService!.GetStatusAsync();
            if (status == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (status.Quotas.Count > 0)
                {
                    var tokenQuota = status.Quotas.FirstOrDefault(q => q.Type.Equals("TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase));
                    var monthlyQuota = status.Quotas.FirstOrDefault(q => q.Type.Equals("TIME_LIMIT", StringComparison.OrdinalIgnoreCase));

                    if (tokenQuota != null)
                    {
                        var tokenPercent = tokenQuota.Percentage ?? 0;
                        ZaiCreditsText.Text = $"{tokenPercent:F0}% used";
                        ZaiTokenBar.Value = tokenPercent;
                        ZaiTokenBar.IsVisible = true;
                        ZaiTokenText.Text = tokenQuota.ResetsAt.HasValue
                            ? $"Resets {FormatResetTime(tokenQuota.ResetsAt)}"
                            : "Resets unknown";
                    }
                    else
                    {
                        ZaiCreditsText.Text = "Connected";
                        ZaiTokenBar.IsVisible = false;
                        ZaiTokenText.Text = string.Empty;
                    }

                    if (monthlyQuota != null && monthlyQuota.Limit.HasValue)
                    {
                        var used = monthlyQuota.CurrentValue ?? monthlyQuota.Used ?? 0;
                        var usedPercent = monthlyQuota.Percentage ?? (used / (double)monthlyQuota.Limit.Value * 100.0);

                        ZaiMonthlyBar.Value = usedPercent;
                        ZaiMonthlyBar.IsVisible = true;
                        ZaiDetailText.Text = $"{used:N0} / {monthlyQuota.Limit.Value:N0} monthly prompts";
                        ZaiTokenText.Text = monthlyQuota.ResetsAt.HasValue
                            ? $"Resets {FormatResetTime(monthlyQuota.ResetsAt)}"
                            : "Resets unknown";
                    }
                    else
                    {
                        ZaiMonthlyBar.IsVisible = false;
                        ZaiDetailText.Text = "Monthly prompts not exposed here";
                        ZaiTokenText.Text = string.Empty;
                    }
                }
                else
                {
                    ZaiCreditsText.Text = "Connected";
                }

                UpdateCompactSummary();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Z.ai refresh error: {ex.Message}");
        }
    }

    #endregion

    private static double? GetOpenRouterUsedCredits(OpenRouterStatus status)
    {
        if (status.TotalUsage.HasValue)
            return Math.Max(0, status.TotalUsage.Value);

        if (status.Usage > 0)
            return Math.Max(0, status.Usage);

        if (status.TotalCredits.HasValue && status.LimitRemaining.HasValue)
            return Math.Max(0, status.TotalCredits.Value - status.LimitRemaining.Value);

        return null;
    }

    private static double? GetOpenRouterRemainingCredits(OpenRouterStatus status)
    {
        if (status.LimitRemaining.HasValue)
            return Math.Max(0, status.LimitRemaining.Value);

        if (status.TotalCredits.HasValue && status.TotalUsage.HasValue)
            return Math.Max(0, status.TotalCredits.Value - status.TotalUsage.Value);

        if (status.TotalCredits.HasValue && status.Usage >= 0)
            return Math.Max(0, status.TotalCredits.Value - status.Usage);

        return null;
    }

    private static string GetOpenRouterPeriodText(OpenRouterStatus status)
    {
        var now = DateTime.UtcNow;

        if (string.Equals(status.LimitReset, "daily", StringComparison.OrdinalIgnoreCase))
            return $"Current UTC day ({FormatUtcDateRange(now.Date, now.Date.AddDays(1).AddTicks(-1))}): ${status.UsageDaily:F2} used";

        if (string.Equals(status.LimitReset, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            var weekStart = StartOfUtcWeek(now);
            return $"Current UTC week ({FormatUtcDateRange(weekStart, weekStart.AddDays(7).AddTicks(-1))}): ${status.UsageWeekly:F2} used";
        }

        if (string.Equals(status.LimitReset, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return $"Current UTC month ({FormatUtcDateRange(monthStart, monthStart.AddMonths(1).AddTicks(-1))}): ${status.UsageMonthly:F2} used";
        }

        var defaultMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return $"Current UTC month ({FormatUtcDateRange(defaultMonthStart, defaultMonthStart.AddMonths(1).AddTicks(-1))}): ${status.UsageMonthly:F2} used";
    }

    private static DateTime StartOfUtcWeek(DateTime utcNow)
    {
        var date = utcNow.Date;
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private static string FormatUtcDateRange(DateTime startUtc, DateTime endUtc)
    {
        if (startUtc.Month == endUtc.Month && startUtc.Year == endUtc.Year)
            return $"{startUtc:MMM d}-{endUtc:dd}";

        if (startUtc.Year == endUtc.Year)
            return $"{startUtc:MMM d}-{endUtc:MMM d}";

        return $"{startUtc:MMM d, yyyy}-{endUtc:MMM d, yyyy}";
    }

    private static string FormatWindowLabel(int windowMinutes)
    {
        return windowMinutes switch
        {
            300 => "5h",
            10080 => "7d",
            _ when windowMinutes % 1440 == 0 => $"{windowMinutes / 1440}d",
            _ when windowMinutes % 60 == 0 => $"{windowMinutes / 60}h",
            _ => $"{windowMinutes}m"
        };
    }

    private static string FormatResetTime(DateTimeOffset? resetAt)
    {
        return FormatCentralTime(resetAt);
    }

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

