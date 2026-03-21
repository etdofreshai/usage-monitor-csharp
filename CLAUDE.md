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

Usage Monitor is a Windows system tray application that displays system resource usage (CPU, memory, disk, network) in a floating popup panel. Built with Avalonia UI.

### Core Components

- **App** - Application entry point. Creates a hidden window, system tray icon, and the usage popup. Tray icon click toggles the popup visibility.

- **UsagePopup** - Borderless, topmost, transparent window that floats above all other windows. Positioned near the taskbar. Shows CPU, memory, disk usage, network throughput, and system uptime. Has an X button to close/hide and supports dragging from the title bar.

### Key Behaviors

- System tray icon toggles the popup on click
- Popup is Topmost (always on top) with no taskbar entry
- WS_EX_NOACTIVATE on Windows so it doesn't steal focus
- Refreshes stats every 2 seconds while visible
- Uses Windows PerformanceCounter for CPU, GlobalMemoryStatusEx for RAM
- Network speeds calculated from delta of NetworkInterface stats
- X button hides the popup (doesn't quit the app)
- Right-click tray icon → Quit to fully exit
