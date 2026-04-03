using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using UsageMonitor.Services;

namespace UsageMonitor;

public partial class UsagePopup : Window
{
    private readonly Config _config;
    private readonly DispatcherTimer _systemRefreshTimer;
    private readonly DispatcherTimer _aiRefreshTimer;
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
    private AnthropicService? _anthropicService;
    private ZaiService? _zaiService;

    public UsagePopup()
    {
        InitializeComponent();

        _config = Config.Load();

        // Wire up close button
        CloseButton.Click += (s, e) => HidePopup();

        // Enable dragging from title bar area
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        // System stats refresh (every 2 seconds)
        _systemRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _systemRefreshTimer.Tick += SystemRefreshTimer_Tick;

        // AI credits refresh (every 30 seconds, configurable)
        _aiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_config.RefreshIntervalSeconds) };
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

        // Position near taskbar on first show
        Opened += (s, e) =>
        {
            PositionNearTaskbar();
            MakeWindowNonActivating();
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
            OpenAiSection.IsVisible = true;
            anyConfigured = true;
        }

        if (!string.IsNullOrEmpty(_config.AnthropicAdminKey))
        {
            _anthropicService = new AnthropicService(_config.AnthropicAdminKey, _config.AnthropicPrepaidBalance);
            AnthropicSection.IsVisible = true;
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
            HidePopup();
        else
            ShowPopup();
    }

    public void ShowPopup()
    {
        PositionNearTaskbar();
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
        _anthropicService?.Dispose();
        _zaiService?.Dispose();

        _allowClose = true;
        Close();
    }

    private void PositionNearTaskbar()
    {
        if (Screens.Primary is { } screen)
        {
            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling;
            var pixelWidth = (int)(Width * scaling);
            var pixelHeight = (int)(Height * scaling);
            var margin = (int)(12 * scaling);
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

                    NetworkUpText.Text = FormatSpeed(upSpeed);
                    NetworkDownText.Text = FormatSpeed(downSpeed);
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
        if (_anthropicService != null)
            tasks.Add(RefreshAnthropicAsync());
        if (_zaiService != null)
            tasks.Add(RefreshZaiAsync());

        await Task.WhenAll(tasks);
    }

    private async Task RefreshOpenRouterAsync()
    {
        try
        {
            var status = await _openRouterService!.GetStatusAsync();
            if (status == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (status.LimitRemaining.HasValue)
                {
                    OpenRouterCreditsText.Text = $"${status.LimitRemaining:F2}";

                    if (status.Limit.HasValue && status.Limit > 0)
                    {
                        OpenRouterBar.IsVisible = true;
                        OpenRouterBar.Value = (status.LimitRemaining.Value / status.Limit.Value) * 100;
                    }
                }
                else
                {
                    OpenRouterCreditsText.Text = $"${status.Usage:F2} used";
                    OpenRouterBar.IsVisible = false;
                }

                var tier = status.IsFreeTier ? "Free tier" : "Paid";
                OpenRouterDetailText.Text = $"{tier} | Total used: ${status.Usage:F2}";
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
                if (status.RemainingCredits.HasValue)
                {
                    OpenAiCreditsText.Text = $"${status.RemainingCredits:F2} left";
                    OpenAiBar.IsVisible = true;
                    var total = _config.OpenAiPrepaidBalance;
                    OpenAiBar.Value = total > 0 ? (status.RemainingCredits.Value / total) * 100 : 0;
                }
                else
                {
                    OpenAiCreditsText.Text = $"${status.MonthCostUsd:F2} /mo";
                    OpenAiBar.IsVisible = false;
                }

                OpenAiDetailText.Text = $"Today: ${status.TodayCostUsd:F2} | Month: ${status.MonthCostUsd:F2}";
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenAI refresh error: {ex.Message}");
        }
    }

    private async Task RefreshAnthropicAsync()
    {
        try
        {
            var status = await _anthropicService!.GetStatusAsync();
            if (status == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (status.RemainingCredits.HasValue)
                {
                    AnthropicCreditsText.Text = $"${status.RemainingCredits:F2} left";
                    AnthropicBar.IsVisible = true;
                    var total = _config.AnthropicPrepaidBalance;
                    AnthropicBar.Value = total > 0 ? (status.RemainingCredits.Value / total) * 100 : 0;
                }
                else
                {
                    AnthropicCreditsText.Text = $"${status.MonthCostUsd:F2} /mo";
                    AnthropicBar.IsVisible = false;
                }

                AnthropicDetailText.Text = $"Today: ${status.TodayCostUsd:F2} | Month: ${status.MonthCostUsd:F2}";
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Anthropic refresh error: {ex.Message}");
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
                    var primary = status.Quotas[0];
                    if (primary.Limit > 0)
                    {
                        var remaining = primary.Limit - primary.Used;
                        ZaiCreditsText.Text = $"{remaining:N0} / {primary.Limit:N0}";
                    }
                    else
                    {
                        ZaiCreditsText.Text = $"{primary.Used:N0} used";
                    }

                    var details = status.Quotas
                        .Where(q => q.Limit > 0 || q.Used > 0)
                        .Select(q => $"{q.Name}: {q.Used:N0}/{q.Limit:N0}");
                    ZaiDetailText.Text = string.Join(" | ", details);
                }
                else
                {
                    ZaiCreditsText.Text = "Connected";
                }

                // Show reset time from the first quota that has one
                var resetQuota = status.Quotas.FirstOrDefault(q => !string.IsNullOrEmpty(q.ResetsAt));
                var resetText = FormatResetTime(resetQuota?.ResetsAt);
                if (resetText != null)
                {
                    ZaiResetText.Text = resetText;
                    ZaiResetText.IsVisible = true;
                }
                else
                {
                    ZaiResetText.IsVisible = false;
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Z.ai refresh error: {ex.Message}");
        }
    }

    private static string? FormatResetTime(string? resetsAtRaw)
    {
        if (string.IsNullOrEmpty(resetsAtRaw)) return null;
        if (!DateTime.TryParse(resetsAtRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var resetUtc))
            return null;
        var resetLocal = resetUtc.ToLocalTime();
        var remaining = resetLocal - DateTime.Now;
        if (remaining.TotalSeconds <= 0) return null;
        if (remaining.TotalHours <= 24)
            return $"Resets {resetLocal:h:mm tt}";
        return $"Resets {resetLocal:MMM d h:mm tt}";
    }

    #endregion

    #region Drag Support

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            var pos = point.Position;
            if (pos.Y < 40 && pos.X < Width - 40)
            {
                _isDragging = true;
                var screenPos = this.PointToScreen(pos);
                _dragStartScreenPoint = new PixelPoint((int)screenPos.X, (int)screenPos.Y);
                _windowStartPosition = Position;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
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
