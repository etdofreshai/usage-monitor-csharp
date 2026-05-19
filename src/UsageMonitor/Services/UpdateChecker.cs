using System.Diagnostics;
using System.Threading;

namespace UsageMonitor.Services;

public class UpdateChecker : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly string? _repoPath;
    private readonly Timer _timer;
    private int _checkInFlight;

    public bool UpdateAvailable { get; private set; }
    public string? RemoteSha { get; private set; }
    public DateTimeOffset? RemoteDate { get; private set; }

    public event EventHandler? UpdateDetected;

    public UpdateChecker(string? repoPath)
    {
        _repoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath;
        _timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool Enabled => _repoPath != null && Directory.Exists(_repoPath);

    public void Start()
    {
        if (!Enabled) return;
        _timer.Change(InitialDelay, CheckInterval);
    }

    private void OnTimerTick(object? state) => _ = CheckAsync();

    public async Task CheckAsync()
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _checkInFlight, 1) == 1) return;
        try
        {
            await RunGitAsync("fetch", "origin", "main");
            var sha = (await RunGitAsync("rev-parse", "--short", "origin/main")).Trim();
            var dateStr = (await RunGitAsync("log", "-1", "--format=%cI", "origin/main")).Trim();
            if (string.IsNullOrEmpty(sha)) return;
            if (!DateTimeOffset.TryParse(dateStr, out var remoteDate)) return;

            var localSha = (BuildInfo.CommitSha ?? "").Trim();
            DateTimeOffset.TryParse(BuildInfo.BuildDate, out var localBuildDate);

            var shaDiffers = !string.Equals(localSha, sha, StringComparison.OrdinalIgnoreCase);
            var newer = remoteDate > localBuildDate;

            if (shaDiffers && newer)
            {
                RemoteSha = sha;
                RemoteDate = remoteDate;
                if (!UpdateAvailable)
                {
                    UpdateAvailable = true;
                    UpdateDetected?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"UpdateChecker error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    public async Task<bool> ApplyUpdateAsync()
    {
        if (!Enabled) return false;
        try
        {
            await RunGitAsync("pull", "--ff-only", "origin", "main");
            await RunCommandAsync("dotnet", new[] { "build", "-c", "Debug" });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"UpdateChecker apply failed: {ex.Message}");
            return false;
        }
    }

    public static void RestartApp()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"UpdateChecker restart failed: {ex.Message}");
        }
    }

    private Task<string> RunGitAsync(params string[] args) => RunCommandAsync("git", args);

    private async Task<string> RunCommandAsync(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = _repoPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} exited {p.ExitCode}: {stderr.Trim()}");
        return stdout;
    }

    public void Dispose() => _timer.Dispose();
}
