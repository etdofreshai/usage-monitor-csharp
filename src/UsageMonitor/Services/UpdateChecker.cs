using System.Diagnostics;
using System.Threading;

namespace UsageMonitor.Services;

public class UpdateChecker : IDisposable
{
    public enum CheckResult
    {
        Disabled,
        AlreadyChecking,
        UpToDate,
        UpdateAvailable,
        Failed,
    }

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly string? _repoPath;
    private readonly Timer _timer;
    private int _checkInFlight;
    private int _applyInFlight;

    public bool UpdateAvailable { get; private set; }
    /// <summary>
    /// True after a macOS bundle swap helper has been launched. The caller should
    /// close the current app but must not start the old executable again.
    /// </summary>
    public bool RestartScheduled { get; private set; }
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

    public async Task<CheckResult> CheckAsync()
    {
        if (!Enabled) return CheckResult.Disabled;
        if (Interlocked.Exchange(ref _checkInFlight, 1) == 1) return CheckResult.AlreadyChecking;
        try
        {
            await RunGitAsync("fetch", "origin", "main");
            var sha = (await RunGitAsync("rev-parse", "--short", "origin/main")).Trim();
            var dateStr = (await RunGitAsync("log", "-1", "--format=%cI", "origin/main")).Trim();
            if (string.IsNullOrEmpty(sha)) return CheckResult.Failed;
            if (!DateTimeOffset.TryParse(dateStr, out var remoteDate)) return CheckResult.Failed;

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
                return CheckResult.UpdateAvailable;
            }

            UpdateAvailable = false;
            RemoteSha = null;
            RemoteDate = null;
            return CheckResult.UpToDate;
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"UpdateChecker error: {ex.Message}");
            return CheckResult.Failed;
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    public async Task<bool> ApplyUpdateAsync()
    {
        if (!Enabled) return false;

        // The update buttons exist in both the compact and full layouts. A second
        // click could otherwise launch another git pull while the first is still
        // using FETCH_HEAD, causing an avoidable pull failure.
        if (Interlocked.Exchange(ref _applyInFlight, 1) == 1) return false;

        try
        {
            RestartScheduled = false;
            await RunGitAsync("pull", "--ff-only", "origin", "main");

            if (OperatingSystem.IsMacOS() && TryGetCurrentAppBundlePath(out var installedApp))
            {
                // A plain `dotnet build` only updates the checkout. Build the
                // signed release bundle first, then let a short-lived external
                // helper replace this app after this process exits.
                var packagingScript = Path.Combine(_repoPath!, "build-macos-app.sh");
                if (!File.Exists(packagingScript))
                    throw new FileNotFoundException("macOS packaging script was not found", packagingScript);

                await RunCommandAsync("/bin/bash", new[] { packagingScript, "--no-install" });
                var stagedApp = Path.Combine(_repoPath!, "dist", "UsageMonitor.app");
                if (!File.Exists(Path.Combine(stagedApp, "Contents", "MacOS", "UsageMonitor")))
                    throw new InvalidOperationException("macOS update bundle was not produced");

                ScheduleMacBundleSwap(installedApp, stagedApp);
                RestartScheduled = true;
                return true;
            }

            await RunCommandAsync(ResolveDotnetExecutable(), new[] { "build", "-c", "Debug" });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"UpdateChecker apply failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _applyInFlight, 0);
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
            // Tells the new instance to wait briefly for this one to release the
            // single-instance lock instead of exiting as a "second instance".
            psi.ArgumentList.Add("--from-restart");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"UpdateChecker restart failed: {ex.Message}");
        }
    }

    private static bool TryGetCurrentAppBundlePath(out string appPath)
    {
        appPath = string.Empty;
        var executable = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
        const string marker = ".app/Contents/MacOS/";
        var markerIndex = executable?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (markerIndex < 0)
            return false;

        appPath = executable![..(markerIndex + 4)];
        return Directory.Exists(appPath);
    }

    private static void ScheduleMacBundleSwap(string installedApp, string stagedApp)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"usage-monitor-update-{Environment.ProcessId}-{Guid.NewGuid():N}.sh");
        File.WriteAllText(scriptPath, MacBundleSwapScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var psi = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(installedApp);
        psi.ArgumentList.Add(stagedApp);
        psi.ArgumentList.Add(AppLog.GetLogPath());

        if (Process.Start(psi) is null)
            throw new InvalidOperationException("Failed to launch the macOS update helper");

        AppLog.WriteLine($"Update staged; helper will replace {installedApp} after process exit.");
    }

    private const string MacBundleSwapScript = """
        #!/bin/bash
        set -euo pipefail

        pid="$1"
        target="$2"
        staged="$3"
        log="$4"
        temp="${target}.updating-$$"
        backup="${target}.backup-$$"

        log_line() {
          printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S %z')" "$1" >> "$log"
        }

        cleanup() {
          rm -rf "$temp" "$backup"
          rm -f "$0"
        }
        trap cleanup EXIT

        for _ in {1..100}; do
          if ! kill -0 "$pid" 2>/dev/null; then break; fi
          sleep 0.1
        done
        if kill -0 "$pid" 2>/dev/null; then
          log_line "Update helper timed out waiting for the previous app process to exit."
          exit 1
        fi

        rm -rf "$temp" "$backup"
        ditto "$staged" "$temp"
        test -x "$temp/Contents/MacOS/UsageMonitor"
        codesign --verify --deep --strict "$temp"

        if [[ -d "$target" ]]; then mv "$target" "$backup"; fi
        if ! mv "$temp" "$target"; then
          if [[ -d "$backup" ]]; then mv "$backup" "$target"; fi
          log_line "Update helper could not install the new app bundle; restored the previous bundle."
          exit 1
        fi
        rm -rf "$backup"
        xattr -dr com.apple.quarantine "$target" 2>/dev/null || true
        codesign --force --deep --sign - "$target"
        log_line "Installed updated Usage Monitor bundle; launching it now."
        open -n "$target" --args --from-restart
        """;

    private Task<string> RunGitAsync(params string[] args) => RunCommandAsync("git", args);

    private static string ResolveDotnetExecutable()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            CombineWithExecutable(Environment.GetEnvironmentVariable("DOTNET_ROOT"), executableName),
        };

        if (OperatingSystem.IsMacOS())
        {
            // GUI applications launched by macOS do not inherit the user's shell
            // PATH, so Homebrew's dotnet is otherwise invisible to the updater.
            candidates.Add("/opt/homebrew/bin/dotnet");
            candidates.Add("/usr/local/bin/dotnet");
            candidates.Add("/usr/local/share/dotnet/dotnet");
        }

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? executableName;
    }

    private static string? CombineWithExecutable(string? directory, string executableName) =>
        string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, executableName);

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
