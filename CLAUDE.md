# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build the project
dotnet build

# Run the application
dotnet run --project src/UsageMonitor

# Build and run
dotnet build && dotnet run --project src/UsageMonitor

# Create release build
dotnet publish src/UsageMonitor -c Release
```

### macOS packaging

```bash
# Build a self-contained osx-arm64 bundle, install to /Applications, and launch it
./build-macos-app.sh

# Build into ./dist without touching /Applications
./build-macos-app.sh --no-install
```

`build-macos-app.sh` does a self-contained `osx-arm64` publish, assembles a proper
`/Applications/UsageMonitor.app` (bundle id `com.usage-monitor`, `LSUIElement=true`
so it's a menu-bar agent with no Dock tile), generates `AppIcon.icns` from
`Assets/usage-monitor.png`, clears quarantine, and ad-hoc signs the bundle (an
arm64 binary needs at least an ad-hoc signature to run). It mirrors the layout of
the sibling WhisperKeyboard.app. Requires the keg-only `dotnet@8` at
`/opt/homebrew/bin/dotnet`.

## Architecture

Usage Monitor is a cross-platform (Windows + macOS) menu-bar / system-tray
application that displays system resource usage and AI service credits in a
floating popup panel. Built with Avalonia UI.

### Core Components

- **App** - Application entry point. Creates a hidden window, system tray icon, and the usage popup. Tray icon click toggles the popup visibility.

- **UsagePopup** - Borderless, topmost, transparent window that floats above all other windows. Positioned near the taskbar. Two sections: System stats (CPU, memory, disk, network, uptime) and AI Credits (Codex incl. Spark, Claude + Claude2 each incl. a Design bar, Z.ai, plus OpenRouter/OpenAI when present). Each AI provider can be shown/hidden from the tray "Providers" menu (see Config). Has an X button to close/hide and supports dragging from the title bar.

- **Program** - Entry point. Acquires a process-lifetime single-instance lock
  (`instance.lock`, an exclusive `FileShare.None` file under the app-data dir) before
  starting Avalonia, so a login-launched instance and a manual launch can't stack two
  tray icons. A second instance fails to acquire the lock and exits quietly.

- **StartupService** (`Services/`) - Cross-platform "run on system start" toggle behind
  `IStartupService` (`StartupService.Create()` picks the implementation). The OS itself is
  the single source of truth — there is no config-backed flag to drift:
  - **macOS** (`MacStartupService`): a per-user LaunchAgent at
    `~/Library/LaunchAgents/com.usage-monitor.plist` (`RunAtLoad`, launching the bundle's
    inner Mach-O with `--from-login`). The plist's existence is the truth; `launchctl
    bootstrap`/`bootout` are best-effort "apply this session" and never gate the toggle.
    `IsSupported` is false unless running from an installed `.app`, so the menu item is
    hidden in `dotnet run` dev sessions (where the exe path would be wrong).
  - **Windows** (`WindowsStartupService`): the per-user `HKCU\...\CurrentVersion\Run` key.
  The tray menu's "Run on system start" item is a checkbox that reflects this live OS state
  and re-reads it after every toggle. When launched with `--from-login` the app stays quietly
  in the tray instead of popping the panel.

- **Config** - JSON configuration stored in `%AppData%\UsageMonitor\config.json`. API keys can also come from environment variables (`OPENAI_ADMIN_KEY`, `OPENROUTER_API_KEY`, `ANTHROPIC_ADMIN_KEY`, `ZAI_API_KEY`). `ShowClaude2` (default `true`, env override `USAGE_MONITOR_SHOW_CLAUDE2` with 1/true/yes/on or 0/false/no/off) AND-gates the second Claude account: it renders only when the server's `/api/usage` response includes a `providers.claude2` block AND this flag is true — server-side is the opt-in, this flag is a per-machine opt-out.

  **Per-provider visibility:** `ShowCodex`, `ShowCodexSpark`, `ShowClaude`, `ShowClaude2`, `ShowClaudeDesign`, `ShowClaude2Design`, `ShowZai` (all default `true`) toggle each provider's section/sub-bar, surfaced live in the tray **Providers** menu. Each AND-gates with data presence (renders only when the server returns that provider AND the flag is true). Each has a matching `USAGE_MONITOR_SHOW_*` env override (e.g. `USAGE_MONITOR_SHOW_CODEX_SPARK`). Env overrides are runtime-only: they take effect for the session but are **not** written back to `config.json` when a menu toggle saves, so removing the env var reverts to the on-disk preference. `UsagePopup` owns the single `Config`; the menu items just call `IsProviderVisible`/`SetProviderVisible`, which saves and re-renders (`ReapplyProviderVisibility` → `ReflowAiGrid` repacks the visible AI cards so hiding a subset leaves no gaps).

### AI Service Clients (`Services/`)

- **OpenRouterService** - `GET /api/v1/key` — shows remaining credits, limit, usage, and tier (best API — has direct `limit_remaining` field)
- **OpenAiService** - `GET /v1/organization/costs` — shows daily/monthly spend (requires admin key; no direct "balance" endpoint, so prepaid balance is set in config)
- **AnthropicService** - `GET /v1/organizations/cost_report` — shows daily/monthly spend (requires admin key; same prepaid balance approach as OpenAI)
- **ZaiService** - `GET /api/monitor/usage/quota/limit` — shows quota usage (unofficial endpoint)

### Key Behaviors

- System tray icon toggles the popup on click
- Popup is Topmost (always on top) with no taskbar entry
- WS_EX_NOACTIVATE on Windows so it doesn't steal focus
- System stats refresh every 2 seconds while visible
- AI credits refresh every 30 seconds while visible (configurable via `RefreshIntervalSeconds`)
- Uses Windows PerformanceCounter for CPU, GlobalMemoryStatusEx for RAM
- Network speeds calculated from delta of NetworkInterface stats
- AI service sections auto-hide when no API key is configured
- X button hides the popup in both Full and Compact views (doesn't quit the app)
- Compact view has X button next to restore icon for hiding back to system tray
- Right-click tray icon → Quit to fully exit

### Auto-Update

- `BuildInfo.cs` is auto-generated at build time with git commit SHA and UTC build date
- If `RepoPath` is set in config (or `USAGE_MONITOR_REPO` env var), the app checks for updates every 30 minutes
- Update detection: `git fetch origin main`, compares remote SHA vs local, checks if remote is newer
- When update available: green ↻ button appears in Full and Compact views, green dot on Icon-Only view
- Clicking update: pulls latest, builds, restarts the app
- If `RepoPath` is null (default), auto-update is disabled
