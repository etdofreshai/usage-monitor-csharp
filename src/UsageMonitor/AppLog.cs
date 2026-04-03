namespace UsageMonitor;

public static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UsageMonitor",
        "usage.log"
    );

    public static void WriteLine(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";

            lock (Sync)
            {
                var directory = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging should never break the app.
        }
    }

    public static string GetLogPath() => LogFilePath;
}
