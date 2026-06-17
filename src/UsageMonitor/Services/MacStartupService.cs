using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace UsageMonitor.Services;

/// <summary>
/// macOS launch-at-login via a per-user LaunchAgent plist at
/// <c>~/Library/LaunchAgents/com.usage-monitor.plist</c>.
///
/// The plist file's existence is the single source of truth (it is also what
/// surfaces under System Settings &gt; General &gt; Login Items). Writing the file
/// is enough on its own for the next login, because loginwindow rescans
/// <c>~/Library/LaunchAgents</c> at login. The <c>launchctl bootstrap</c>/<c>bootout</c>
/// calls are best-effort "apply/undo for the current session" polish and never
/// gate the toggle result.
/// </summary>
internal sealed class MacStartupService : IStartupService
{
    // Must match CFBundleIdentifier in the .app bundle and the plist filename stem.
    private const string Label = "com.usage-monitor";

    [DllImport("libc")]
    private static extern uint getuid();

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    // Only meaningful when running from an installed .app bundle. Under `dotnet run`
    // (dev apphost in bin/) or the `dotnet App.dll` muxer form, the resolved exe path
    // is the dev host or the dotnet binary itself — baking those into a LaunchAgent
    // produces a broken/non-functional next-login launch, so hide the toggle there.
    public bool IsSupported => IsRunningFromAppBundle();

    // Cheap, synchronous, prompt-free, and matches what System Settings shows.
    public bool IsEnabled() => File.Exists(PlistPath);

    public void Enable()
    {
        var exePath = CurrentExePath();
        if (!IsRunningFromAppBundle())
        {
            AppLog.WriteLine($"Run-on-system-start skipped: not running from an installed .app bundle (exe: {exePath}). Install via build-macos-app.sh first.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        // Write the file first (atomically). This alone guarantees next-login
        // autostart, so a failed launchctl call below must NOT roll it back.
        WritePlistAtomic(exePath);
        // Apply for the current session too: bootout any stale job first, then
        // bootstrap the freshly written definition — a plain re-bootstrap of an
        // already-loaded job fails and would leave stale ProgramArguments loaded.
        RunLaunchctl("bootout", $"gui/{getuid()}", PlistPath);
        RunLaunchctl("bootstrap", $"gui/{getuid()}", PlistPath);
    }

    public void Disable()
    {
        // Remove from the running session first (best-effort), then delete the
        // file — deletion is the source of truth and clears it from Login Items.
        RunLaunchctl("bootout", $"gui/{getuid()}", PlistPath);
        if (File.Exists(PlistPath))
            File.Delete(PlistPath);
    }

    // When bundled this resolves to .../UsageMonitor.app/Contents/MacOS/UsageMonitor —
    // the inner Mach-O, which is what launchd should exec directly.
    private static string CurrentExePath()
    {
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrEmpty(path) ? Environment.ProcessPath ?? string.Empty : path;
    }

    // A real launch target is the inner Mach-O of an .app bundle, not the dev
    // apphost in bin/ and not the shared `dotnet` muxer.
    private static bool IsRunningFromAppBundle()
    {
        var exe = CurrentExePath();
        return exe.Contains("/Contents/MacOS/", StringComparison.Ordinal)
            && !exe.EndsWith("/dotnet", StringComparison.Ordinal);
    }

    private static void WritePlistAtomic(string exePath)
    {
        // Mirror AppLog's directory so the launchd logs sit next to the app log,
        // regardless of how .NET maps SpecialFolder.ApplicationData on this host.
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageMonitor");
        Directory.CreateDirectory(logDir);
        var outLog = Path.Combine(logDir, "launchagent.out.log");
        var errLog = Path.Combine(logDir, "launchagent.err.log");

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{SecurityElement.Escape(exePath)}</string>
                    <string>--from-login</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
                <key>EnvironmentVariables</key>
                <dict>
                    <key>PATH</key>
                    <string>/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
                </dict>
                <key>StandardOutPath</key>
                <string>{SecurityElement.Escape(outLog)}</string>
                <key>StandardErrorPath</key>
                <string>{SecurityElement.Escape(errLog)}</string>
            </dict>
            </plist>
            """;

        var tmp = PlistPath + ".tmp";
        File.WriteAllText(tmp, xml);
        File.Move(tmp, PlistPath, overwrite: true);
    }

    private static void RunLaunchctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/launchctl")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null)
                return;

            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            // Nonzero is usually benign — e.g. re-bootstrapping an already-loaded
            // job, or booting out a job that isn't loaded (both return exit 5 with
            // the path-target form used here). Log and move on; the plist file's
            // existence already decides the persisted state.
            if (proc.ExitCode != 0)
                AppLog.WriteLine($"launchctl {string.Join(' ', args)} exited {proc.ExitCode}: {stderr.Trim()}");
        }
        catch (Exception ex)
        {
            AppLog.WriteLine($"launchctl {string.Join(' ', args)} failed: {ex.Message}");
        }
    }
}
