using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace UsageMonitor;

public partial class UsagePopup : Window
{
    private readonly DispatcherTimer _refreshTimer;
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

    public UsagePopup()
    {
        InitializeComponent();

        // Wire up close button
        CloseButton.Click += (s, e) => HidePopup();

        // Enable dragging from title bar area
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;

        // Setup refresh timer (updates every 2 seconds)
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += RefreshTimer_Tick;

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

        // Position near taskbar on first show
        Opened += (s, e) =>
        {
            PositionNearTaskbar();
            MakeWindowNonActivating();
        };
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
        RefreshAll();
        _refreshTimer.Start();
    }

    public void HidePopup()
    {
        _refreshTimer.Stop();
        Hide();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void PositionNearTaskbar()
    {
        if (Screens.Primary is { } screen)
        {
            var workArea = screen.WorkingArea;
            // Position at bottom-right, just above the taskbar
            Position = new PixelPoint(
                workArea.X + workArea.Width - (int)Width - 12,
                workArea.Y + workArea.Height - (int)Height - 12
            );
        }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        RefreshAll();
    }

    private void RefreshAll()
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
            Console.WriteLine($"Refresh error: {ex.Message}");
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
        else
        {
            // Unix: read /proc/stat or use 'top' — simplified fallback
            cpuPercent = 0;
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
            // Unix fallback
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

    #region Drag Support

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            var pos = point.Position;
            // Only drag from the title bar area (top 40px, excluding close button)
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
            // Prevent closing, just hide
            e.Cancel = true;
            HidePopup();
        }
    }
}
