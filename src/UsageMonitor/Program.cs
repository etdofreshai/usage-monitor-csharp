using Avalonia;

namespace UsageMonitor;

class Program
{
    // Held for the whole process lifetime to enforce a single instance. Without it,
    // a login-launched instance plus a manual launch would stack two tray icons,
    // two timer sets, and two API pollers.
    private static FileStream? _instanceLock;

    [STAThread]
    public static void Main(string[] args)
    {
        if (!TryAcquireSingleInstanceLock())
            return;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static bool TryAcquireSingleInstanceLock()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageMonitor");
            Directory.CreateDirectory(dir);
            // Exclusive open (FileShare.None) acts as a cross-process lock on both
            // macOS and Windows; a second instance throws here and exits quietly.
            _instanceLock = new FileStream(
                Path.Combine(dir, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false; // another instance already holds the lock
        }
        catch (Exception ex)
        {
            // Never let the guard itself prevent startup.
            AppLog.WriteLine($"Single-instance lock unavailable, continuing: {ex.Message}");
            return true;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
