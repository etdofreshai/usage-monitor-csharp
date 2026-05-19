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

## Architecture

Usage Monitor is a Windows system tray application that displays system resource usage and AI service credits in a floating popup panel. Built with Avalonia UI.

### Core Components

- **App** - Application entry point. Creates a hidden window, system tray icon, and the usage popup. Tray icon click toggles the popup visibility.

- **UsagePopup** - Borderless, topmost, transparent window that floats above all other windows. Positioned near the taskbar. Two sections: System stats (CPU, memory, disk, network, uptime) and AI Credits (OpenRouter, OpenAI, Anthropic, Z.ai). Has an X button to close/hide and supports dragging from the title bar.

- **Config** - JSON configuration stored in `%AppData%\UsageMonitor\config.json`. API keys can also come from environment variables (`OPENAI_ADMIN_KEY`, `OPENROUTER_API_KEY`, `ANTHROPIC_ADMIN_KEY`, `ZAI_API_KEY`).

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
