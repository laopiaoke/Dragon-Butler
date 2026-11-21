# Dragon Butler

<p align="center">
  <img src="docs/icon.png" width="96" alt="Dragon Butler">
</p>

<p align="center">
  <strong>Click once before bed. Read the result tomorrow.</strong><br>
  <sub>Queues BetterGI / March7thAssistant / ok-ww into one run and tells you how it went</sub>
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

## What it is

A small Windows desktop app that **does no game dailies itself**. It's a dispatcher: it lines up the automation tools you already have, launches them in order, watches each one finish, then wraps up.

It does exactly four things: start processes, read the public logs those tools write, listen on a localhost webhook, and manage process lifetimes. **It never touches the game, the tools' files, or your account.**

<p align="center">
  <img src="docs/proto/tasklist_collapsed_v1421.png" width="820" alt="Home">
</p>

<p align="center">
  <sub>Home: the top card answers "when does it run today"; the right card, "Task list", holds one tool per row with tonight's plan in its headline.</sub>
</p>

## v1.42: manage it all inside the task-list card

This release only touches the right-hand card on the home page: **one tool per row**, tonight's plan as the card's headline, group checkboxes unfold in place, and the separate "managed config" window is gone.

<p align="center">
  <img src="docs/proto/tasklist_expanded_v1421.png" width="820" alt="Task list card with groups expanded">
</p>

<p align="center">
  <sub>Click the row (or the ⌄ on the right) to unfold: which groups are checked, in what order, and how many items are left. The leading number is the execution order.</sub>
</p>

- **Rows instead of a grid**: drag the `≡` handle to reorder, double-click to run just that one, right-click for a nine-item menu. Editing, launching, ordering, adding tools and checking state all happen right there.
- **Headline summary**: `今晚要跑：日常 + 活动 · 2 项 · 约 47 分钟`. It used to sit at the bottom of the card, below the fold at 720px — as a headline it finally answers "what is this card for tonight".
- **Zero scrolling when collapsed at 720px**: summary, start button and the add row are all above the fold. The "how each tool runs" section stays below it, one small scroll away.
- **Adding a tool happens in the card**: the dashed `＋ 添加工具` opens an icon grid with already-added ones ticked; anything else goes through "其它 exe…".
- **Nine-item right-click menu**: "skip this once" lasts one run, "don't run it tonight" persists — written separately because the cost of regretting them differs.
- **Removal in plain words**: `把 BetterGI 从今晚清单移除？配置会保留，随时可以再加回来。`
- **Group selection now has a single store**: the old global checkbox list and each tool's own list disagreed; now the tool's own list wins, and legacy data is migrated once on upgrade (never overwriting what you already picked).
- **The five-view dropdown and its four windows are gone** (daily plan / idle calendar / dependency matrix / group slices). Dependency data and execution logic are untouched — historical configs keep working, there is just no editor any more.

## v1.41: the interface was rebuilt

No new features in this release. The whole UI was gone through once: four tabs, a five-step wizard, settings split into five groups, and logs that arrive pre-filtered from wherever you clicked.

### Four tabs, each answering one question

The leftmost tab used to be called "Logs" and the second "Report" — neither name told you why you'd want to open it. Now:

| Tab | Answers |
| --- | --- |
| **日报** (Daily report) | How long today took, which group got faster or slower, whether anything went wrong |
| **主页** (Home) | When it runs today, what's queued tonight, run now |
| **设置** (Settings) | Five entry points; switches live one level down |
| **插件** (Plugins) | It runs a full set with zero plugins installed |

"Logs" is no longer a tab. It **opens with a filter already applied**, at the place you needed it:

- Something went wrong → **【看现场】** on the home card takes you straight to that run's log lines (filtered by `runId`).
- Tool card right-click → **"查看日志"** shows only what that tool wrote.
- Click a config group's name in the daily report → only that group, that day.
- Want everything → the third action chip **"更多 ▾"** → **【全部日志】**.

<p align="center">
  <img src="docs/proto/logview_v141.png" width="820" alt="Log view">
</p>

<p align="center">
  <sub>The bar on top reads "正在看：全部日志" (viewing: all logs). A filtered view says what it filtered on; 【关闭】 returns to the page you came from.</sub>
</p>

### First run: five steps

<p align="center">
  <img src="docs/proto/wizard3_v141.png" width="640" alt="Wizard step 3">
  <img src="docs/proto/wizard5_v141.png" width="640" alt="Wizard step 5">
</p>

Tools → find the executables → **pick a time** → notify or not → read the summary.

Step 2 **auto-detects on entry**, so most people do nothing there. The time you set in step 3 *is* the time on the home schedule card — one value, never two.

The last page has exactly two buttons: **【完成并试跑一次】** actually runs the first tool once so you can watch it move; **【先不跑，完成】** skips that.

The summary is honest about what's missing — e.g. "BetterGI：还没设置路径（可稍后在工具卡里设置）", with an inline "现在去设置" to go back and fix it. **It never says "all done" when something isn't.**

### Settings fold into five groups

One level shows five entry cards; switches live inside, with a back arrow top-left.

<p align="center">
  <img src="docs/proto/settings_v141.png" width="760" alt="Settings">
</p>

| Entry | Contains |
| --- | --- |
| **守护** (Watchdog) | Watchdog / restart on crash / frozen-screen check / auto-kill stalls / clean leftovers before a run + log rules |
| **定时** (Timing) | Do-not-disturb, failure policy, tool interval, keep-awake, night screen-off |
| **通知** (Notify) | Twelve push channels + Reports (morning, weekly, harvest ledger, event webhook) |
| **接口** (Interface) | Local control API, LAN panel, global hotkeys, backup, preflight, handover |
| **界面与启动** (UI & Startup) | Appearance, autostart, tray, sounds, mini bar, speech |

<p align="center">
  <img src="docs/proto/settings_timing_v141.png" width="760" alt="Timing pane">
</p>

<p align="center">
  <sub>Explanations longer than one line fold to a single line with a ⓘ — hover or click to expand.</sub>
</p>

### Daily report: how much faster than last time

<p align="center">
  <img src="docs/proto/report_v141.png" width="820" alt="Daily report">
</p>

Four blocks, and **"Anomalies" comes first**:

- When everything went fine it says so outright: "N 个配置组全部顺利，没有需要你操心的事" — that sentence is itself the information.
- When something broke, each line reads "which group, which stage, what the butler did" — **only actions that actually exist in code** (e.g. "停下等你", stopped and waited). Anything it can't prove, it leaves out. "看现场" at the end of the line jumps to that run.
- "Config groups" rows carry a coloured pill: **快了 33 秒** / **慢了 5 分 59 秒**, and "持平" when the difference is under a second.
- The total line adds "较昨日 慢了 46 分 50 秒"; with no yesterday data that half is simply omitted.

**The in-app report and the pushed one are rendered from one dataset** — group counts, durations and deltas can't disagree.

## What it fixes

- **You only find out afterwards** — one summary when the queue ends: how many succeeded, what failed, why, and where the evidence is.
- **You can't tell how far along it is** — live strip at the top: `● Daily · 18m   ~2m left · ends ~23:06   5/7`. Collapses when idle.
- **You don't dare walk away** — stalls, frozen screens, memory pressure and tool crashes are all detected; notify, capture evidence, or stop the queue, as you configure.
- **You still have to shut down by hand** — sleep or shut down after a successful run, cancellable for 60 seconds.
- **Nothing to investigate with** — on failure it packs log tail + screenshot + redacted config into `全部日志\incidents\`.
- **All you get afterwards is a wall of logs** — the home card gives you a plain-language recap: what succeeded, what ran long, which group never finished.
- **You can't tell what it decided for you** — every skip, defer, restart and abort leaves one sentence of *why*, listed by time under "Today".
- **No sense of "how does today compare"** — it compares against your own 14-day median, not a hard-coded threshold, and stays quiet when samples are thin.
- **No idea how much time it saved you** — one number: hours it ran for you this month.
- **A crash prompt ate the whole night** — no more dialogs. Crashes, tools that failed to launch and interrupted queues go into the message centre in the title bar, each translated into plain language (what happened / why / what you can do). The dot stays lit until you look, and never blocks startup.
- **Three nested fold-outs before you could edit a tool** — a tool card's right-click menu is the whole menu now: run this one now / skip this time / change interval / view log / edit launch settings / move it.

## One self-imposed rule

**This program's success metric is: you open it less often.** No daily-active chasing, no "come back and check", no streak counters. A good night means you glance once the next day and read "done". If you haven't opened it in a month, it did its job.

The one exception is **when something goes wrong**: a crash, a tool that wouldn't start, a queue that broke off. Those land in the message centre (envelope icon in the title bar, red dot when unread) and wait there. It would rather stay quiet than block an unattended night behind a dialog box.

## Quick start

1. Grab the **portable** build from [Releases](https://github.com/laopiaoke/Dragon-Butler/releases/latest) (bundles .NET, unzip and run; 3.5 MB runtime-dependent build if you already have .NET 8).
2. Unzip, run `Dragon Butler.exe`, accept the UAC prompt.
3. Follow the five-step wizard: tick tools → (it auto-detects) → pick a time → notify or not → done.
4. To see it move right away, hit 【完成并试跑一次】 on the last page.

**Nothing has to be configured.** No notification channel, no schedule — it still runs a full set on one click.

## Interface tour

<p align="center">
  <img src="docs/proto/settings_guard_v141.png" width="380" alt="Watchdog">
  <img src="docs/proto/settings_notify_v141.png" width="380" alt="Notify">
</p>

<p align="center">
  <img src="docs/proto/settings_api_v141.png" width="380" alt="Interface">
  <img src="docs/proto/settings_ui_v141.png" width="380" alt="UI and startup">
</p>

The window defaults to 1080×720, shrinks to 880×600; position and size are remembered.

## Where data lives

Everything sits next to the exe; deleting the folder uninstalls it (turn off autostart first, under Settings → UI & Startup).

```
所有配置\settings.json      all settings, hand-editable
所有配置\group_archive.json which config groups ran each day
所有配置\durations.json     per-tool / per-group durations (the daily report reads this)
所有配置\notices.json       message centre (capped at 100)
所有配置\tools\*.json       tool profiles
所有配置\备份\              settings backups
所有配置\sounds\            custom sounds
插件\                       plugins (the Plugins tab opens this folder)
全部日志\                    runtime log — check here first
全部日志\snapshots\          scheduled snapshots
全部日志\incidents\          failure bundles
```

Older builds scattered `config` / `tools` / `plugins` / `cache` / `sounds` / `备份` next to the exe. The first launch of 1.30 moves them into these two folders and leaves a `.moved-to-所有配置` marker behind so it never runs twice.

Nothing is written to `%APPDATA%`; the whole folder is portable.

## FAQ

<details>
<summary><b>Where did the log page go</b></summary>
There is no "Logs" tab any more. Logs open pre-filtered from wherever you clicked: 【看现场】 on the home card (that failing run), "查看日志" on a tool card (that tool only), a config group's name in the daily report (that group, that day). For everything, use "更多 ▾" → 【全部日志】. The top bar always says "正在看：…" so you know whether it's filtered.
</details>

<details>
<summary><b>I can't find a setting any more</b></summary>
v1.41 split settings into five groups: Watchdog / Timing / Notify / Interface / UI & Startup. The first level is just five cards; switches are one click in, back arrow top-left. See the table above — every existing setting is still there.
</details>

<details>
<summary><b>Does "完成并试跑一次" really run something</b></summary>
Yes. It runs the first tool with a configured path once, through the same single-tool path as **Run queue**, so you see it launch, run and finish. Prefer not to? 【先不跑，完成】. Either button marks the wizard complete.
</details>

<details>
<summary><b>Why is there no "didn't run last night" text on a fresh install</b></summary>
Because nothing has run yet. On a fresh install those history and alert lines are suppressed and replaced with a "还没跑过——三步就能开始" checklist, which goes away once you've actually done it.
</details>

<details>
<summary><b>"BetterGI not found"</b></summary>
Point at <code>BetterGI.exe</code> itself, not the folder containing it.
</details>

<details>
<summary><b>Nothing happens on launch / missing framework</b></summary>
You're on the runtime-dependent build without .NET 8 Desktop Runtime. Install it once, or use the portable build.
</details>

<details>
<summary><b>Antivirus flags it / SmartScreen blocks it</b></summary>
Releases aren't code-signed — click "More info → Run anyway". If quarantined, whitelist the folder or build from source; there are zero third-party dependencies.
</details>

<details>
<summary><b>Will the watchdog kill my game?</b></summary>
No. Process lookup and termination are filtered to the current Windows session; frozen screens and game-exit only produce a notice, never a restart.
</details>

<details>
<summary><b>Daily plan is set, but manual runs still do everything</b></summary>
The daily plan only constrains schedule-triggered runs. Manual **Run queue** always follows the current ticks, so you can catch up. See it under "配置组 ▾" → "计划" on the task list card.
</details>

<details>
<summary><b>"Possibly stalled" — what now?</b></summary>
Check whether the screen is actually stuck. The queue waits for you by default; continue with **Run queue**. To auto-kill instead, enable it under Settings → Watchdog.
</details>

<details>
<summary><b>Adding a tool that isn't built in</b></summary>
Bottom of the tools card → "Custom tool" → "＋ Add": pick the exe, name the process, choose how completion is detected (process exit / duration / custom signal).
</details>

## Privacy

**No telemetry, no analytics, no crash reporting.** The program sends usage data to nobody.

The only outbound connection is the notification endpoint you fill in yourself; the local listener accepts `127.0.0.1` requests only and can be switched off.

## Build

Needs .NET 8 SDK. Zero NuGet dependencies:

```
dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release
```

Tests run through a hand-written probe — 84 modes, well over a thousand assertions:

```
dotnet build tests\CoordinatorProbe\CoordinatorProbe.csproj -c Release
dotnet "tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.dll" all
```

Package both variants:

```
.\scripts\package.ps1 -Variant all
```

## Notes

- **AI-assisted development**: code and docs were produced with AI assistance. The author does not write code and cannot make professional guarantees about quality or stability. Assess the risk yourself and back up your data.
- **Not affiliated with any game publisher or the tools below.** No authorisation was granted. Game names, tool names and icons belong to their respective owners.
- **Automation carries risk**: third-party tools may violate a game's terms of service and put your account at risk. That risk is yours.
- **No account access**: it doesn't log into the game, doesn't read game data; it only reads logs those tools already write to disk.

## Related projects

This project implements no game dailies itself; it all goes to them. Thanks to their authors:

- [BetterGI](https://github.com/babalae/better-genshin-impact)
- [March7thAssistant](https://github.com/moesnow/March7thAssistant)
- [ok-ww](https://github.com/ok-oldking/ok-wuthering-waves)

## License

Released under [GNU AGPL-3.0](LICENSE). Use it, modify it, redistribute it — **but any distribution (including modified redistributions and network-service use) must be AGPL too, with full source provided**.

- **Why AGPL**: this README has always said "don't sell it, don't charge for boosting or repackaging", which MIT explicitly permits. AGPL turns that sentence into a binding term — you may make money with it, but the code has to stay open.
- **Third-party assets**: game names, tool names, and the screenshots and UI assets under `docs/` are outside this licence and belong to their respective owners.
- **When redistributing**: keep the "Notes" section above so downstream users don't mistake this for an official product.

## Version

Latest release **v1.42** (`1.42.0` in-app); per-release changes in [RELEASE_NOTES.md](RELEASE_NOTES.md), design trade-offs in [docs/路线图.md](docs/路线图.md).

The writing rules for interface text live in [docs/VOICE.md](docs/VOICE.md): six sentences are pinned — a revision may move them verbatim, never reword them.
