# Dragon Butler

<p align="center">
  <img src="docs/icon.png" width="96" alt="Dragon Butler">
</p>

<p align="center">
  <strong>Multi-tool Queue Dispatcher for Windows Game Automation</strong><br>
  <sub>Queues BetterGI, March7thAssistant, OK-WW, and custom executables in sequence, monitors execution, and handles post-run wrap-up.</sub>
</p>

<p align="center">
  <img alt="platform" src="https://img.shields.io/badge/Windows-10%2F11%20x64-0078D4?style=flat-square">
  <img alt="dotnet" src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square">
  <img alt="license" src="https://img.shields.io/badge/license-AGPL--3.0-6E7681?style=flat-square">
  <a href="https://github.com/laopiaoke/Dragon-Butler/releases/latest"><img alt="release" src="https://img.shields.io/github/v/release/laopiaoke/Dragon-Butler?color=2EA043&style=flat-square"></a>
  <a href="https://github.com/laopiaoke/Dragon-Butler/stargazers"><img alt="stars" src="https://img.shields.io/github/stars/laopiaoke/Dragon-Butler?color=2EA043&style=flat-square"></a>
</p>

<p align="center">
  <a href="README.md">简体中文</a> · <a href="README.en.md"><strong>English</strong></a>
</p>

---

## Overview

Dragon Butler is a WPF desktop queue dispatcher built on .NET 8. It does not implement any in-game automation logic itself; instead, it coordinates the local automation tools you already use:

- Launches configured tools one by one in order
- Tracks completion via process exit codes, log patterns, sentinel files, or window titles
- Listens on a local webhook, dispatches status notifications, and performs optional post-run wrap-up (notify, sleep, or shut down)

It never modifies game or tool files, and it does not log into or read game account data.

<p align="center">
  <img src="docs/proto/tasklist_collapsed_v1421.png" width="820" alt="Main window">
</p>

<p align="center">
  <sub>Main window: schedule and run summary on the left; ordered task list and estimated duration on the right.</sub>
</p>

## Download & Quick Start

1. Download a release package from [Releases](https://github.com/laopiaoke/Dragon-Butler/releases/latest):
   - **portable** (~65 MB download, ~158 MB extracted): bundles the .NET 8 runtime; extract and run directly.
   - **runtime-dependent** (~2.7 MB download): requires .NET 8 Desktop Runtime installed on the machine.
2. Extract the archive, run `Dragon Butler.exe`, and accept the UAC prompt (elevation is required to monitor and manage target tool processes in the active session).
3. Complete the five-step first-run wizard: select tools, verify executable paths (common install locations are scanned automatically), set an optional daily schedule, and optionally configure notifications.

You can skip both scheduling and notifications and use it purely as a one-click manual queue runner.

## Features

### Queue & Task List Management

- **Built-in profiles and custom tools**: Includes profiles for BetterGI, March7thAssistant / `m7a.exe`, OK-WW, ZenlessZoneZero-OneDragon (`zzz-od`), MaaEnd, OK-NTE, MAA, Whimbox, BAAH, HSR, M9A, MSS, SRC, and MaaFramework projects, plus an "Other exe…" flow for arbitrary executables.
- **Single-card controls**: Drag the `≡` handle to reorder tonight's queue, double-click a row to run that single tool immediately, or right-click to skip once, skip tonight, view logs, or edit launch settings.
- **BetterGI script-group support**: Click the BetterGI row to expand script groups inline and run checked groups in selection order, or fall back to the configured OneDragon profile name when none are checked.
- **Scheduling & resume**: Supports day-of-week scheduling with a configurable catch-up window (120 minutes by default). If a run stops partway after at least one tool succeeded, a Resume action appears to run only the remaining items.

<p align="center">
  <img src="docs/proto/tasklist_expanded_v1421.png" width="820" alt="Expanded task list card">
</p>

### Watchdog, Diagnostics & Post-Run Wrap-Up

- **Process watchdog**: Monitors process survival, possible stalls (no CPU activity and no log output over the configured threshold), frozen frames, and game exits, with cooldown restarts after unexpected tool crashes.
- **Keep-awake & display sleep**: Prevents Windows idle sleep and screensavers while a queue is active and restores original power behavior when finished; optionally turns off the display after N minutes of unattended execution.
- **Incident bundles**: Automatically saves the last 200 log lines, a screen capture, and a redacted config snapshot under `全部日志\incidents\` on failures or stalls; old bundles beyond the retention cap are moved to the Recycle Bin when available.
- **Safe post-run actions**: Supports notify-only, re-run once, launch app, sleep, or shut down after completion. Both sleep and shutdown present a 60-second cancellable bar on the home view and are blocked if all items were skipped, a dependency aborted, or a tool crashed.

<p align="center">
  <img src="docs/proto/wrapupbar_v144.png" width="820" alt="Post-run countdown bar">
</p>

### Daily Report, Harvest Ledger & Notifications

- **Unified daily report**: Summarizes anomalies, per-group durations compared against recent runs, per-tool timings, and total duration using the same underlying dataset for both UI rendering and mobile pushes.
- **Multi-channel notifications**: Supports Windows toast notifications, WeCom, Feishu, DingTalk, Telegram, Bark, email, custom webhooks, WebSockets, and more. Transient failures retry at 1s / 3s / 7s, and failed entries can be resent in place from the notice centre.
- **Non-blocking notice centre**: Crashes, failed launches, and queue interruptions are recorded in the title-bar notice centre instead of blocking unattended runs with modal dialogs.
- **Optional morning/weekly reports & harvest ledger**: Can summarize yesterday's runs (including missed schedules) on first launch, track 7-day completion rates, and accumulate BetterGI resource logs into `所有配置\harvest.json`.

<p align="center">
  <img src="docs/proto/report_v141.png" width="820" alt="Daily report">
</p>

### Settings, Control API & Customization

- **Categorized settings**: Organized into five cards (Watchdog, Timing, Notify, Interface, UI & Startup), with long explanations collapsed behind `ⓘ` tips.
- **Appearance & shortcuts**: Remembers window size and position (default `1080×720`, minimum `880×600`), supports Light/Dark/System themes, custom backgrounds and accent palettes, a `Ctrl+K` command palette, global hotkeys, and a draggable mini progress bar.
- **Local Control API & MCP** (disabled by default): Exposes token-authenticated (`X-Butler-Token`) control endpoints and an HTTP JSON-RPC 2.0 MCP endpoint on `127.0.0.1:18235` for external scripts or AI assistants.
- **Read-only LAN status page** (disabled by default): Serves a dual-theme mobile-friendly status page for viewing live queue progress across devices on the local network.

<p align="center">
  <img src="docs/proto/settings_v141.png" width="760" alt="Settings overview">
</p>

## Data Layout

All runtime data stays beside `Dragon Butler.exe`. Nothing is written to `%APPDATA%`; deleting the folder removes the app cleanly (turn off autostart in settings first if enabled):

```text
所有配置\settings.json        Main application settings
所有配置\group_archive.json   Daily executed script groups
所有配置\durations.json       Per-tool and per-group duration history
所有配置\harvest.json         Harvest ledger data (retained for 400 days when enabled)
所有配置\notices.json         Notice centre history (up to 100 entries)
所有配置\tools\*.json         Individual tool profiles
所有配置\备份\                Settings backup archives
所有配置\sounds\              UI sound effects
插件\                         Optional plugin directory
全部日志\                     Runtime logs
全部日志\snapshots\           On-demand tray screenshots
全部日志\incidents\           Failure incident bundles
```

## FAQ

<details>
<summary><b>"BetterGI not found"</b></summary>
Select <code>BetterGI.exe</code> itself rather than the folder containing it.
</details>

<details>
<summary><b>Nothing happens on launch or missing runtime error</b></summary>
If you downloaded the <code>runtime-dependent</code> package, install the .NET 8 Desktop Runtime first, or switch to the self-contained <code>portable</code> package.
</details>

<details>
<summary><b>SmartScreen or antivirus warnings</b></summary>
Release binaries are not code-signed. After verifying the download source is this repository's Releases page, choose "More info → Run anyway", or build directly from source (zero third-party NuGet packages).
</details>

<details>
<summary><b>Will the watchdog kill my game?</b></summary>
No. Process lookups and terminations are filtered to the current Windows user session, and frozen-screen or game-exit checks produce notices rather than forcefully restarting the game.
</details>

<details>
<summary><b>Where are the logs?</b></summary>
Logs open pre-filtered by context: click "看现场" on a failure card for that run's logs, right-click a tool row and choose "查看日志" for that tool, click a script group in the daily report for that group, or open "更多 ▾ → 全部日志" on the home action bar for full logs.
</details>

<details>
<summary><b>How is BetterGI script-group execution order determined?</b></summary>
Groups execute in the order they were checked inside the expanded row. The card headline and expanded summary counter update live to show what is queued tonight.
</details>

<details>
<summary><b>What should I do on a "Possibly stalled" notice?</b></summary>
Check whether the tool is actually frozen. By default the queue pauses so you can intervene and click Run queue to resume; enable auto-terminate under Watchdog settings if you prefer automatic skip-ahead.
</details>

<details>
<summary><b>How do I add a tool outside the built-in list?</b></summary>
Click "＋ 添加工具" at the bottom of the task list and choose "其它 exe…" at the bottom of the picker to specify the executable path, process name, and completion rule (exit code, duration, or log/file/window signal).
</details>

## Building & Testing from Source

Requires the .NET 8 SDK. There are zero NuGet dependencies or external frontend build tools:

```powershell
# Build main application (Release, analyzers enabled, warnings as errors)
dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release -p:EnableNETAnalyzers=true -warnaserror

# Build StubTool and CoordinatorProbe
dotnet build tests\StubTool\StubTool.csproj -c Release
dotnet build tests\CoordinatorProbe\CoordinatorProbe.csproj -c Release

# Run a functional probe group or UI render verification (pass an absolute path for StubTool)
$stub = (Resolve-Path tests\StubTool\bin\Release\net8.0\StubTool.exe).Path
.\tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.exe schedule $stub
.\tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.exe ui all
```

Package both release variants into `发布\`:

```powershell
.\scripts\package.ps1 -Variant all
```

## Privacy, Disclaimers & Third-Party Assets

- **No telemetry**: The application sends no analytics or crash reports. Network listeners are strictly bound to user-configured settings (localhost by default, read-only LAN status only when explicitly enabled).
- **AI-assisted development**: Code and documentation were developed with AI assistance. Evaluate risk independently and back up your settings before use.
- **No affiliation / user responsibility**: This project has no affiliation with any game publisher or automation tool author. Automated tooling may violate a game's terms of service; use at your own risk.
- **Third-party assets**: Game titles, tool names, bundled icons, and UI screenshots under `docs/` remain the property of their respective owners.

## Related Projects

- [BetterGI](https://github.com/babalae/better-genshin-impact)
- [March7thAssistant](https://github.com/moesnow/March7thAssistant)
- [OK-WW](https://github.com/ok-oldking/ok-wuthering-waves)

## License & Documentation

Licensed under [GNU AGPL-3.0](LICENSE). Any redistribution or modified network deployment must remain licensed under AGPL-3.0 with full source code provided, and must retain the disclaimers above.

- Latest release: **v1.46.1** (`1.46.1` in-app)
- Release notes: [RELEASE_NOTES.md](RELEASE_NOTES.md)
- User manual (Chinese): [docs/使用说明.txt](docs/使用说明.txt)
- Design history & trade-offs: [docs/路线图.md](docs/路线图.md)
- Release verification reports: [docs/reports/](docs/reports/README.md)
- Copy & UI guidelines: [docs/VOICE.md](docs/VOICE.md), [docs/PATTERNS.md](docs/PATTERNS.md)
