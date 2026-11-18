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
  <img src="docs/proto/main_folded.png" width="720" alt="Main window">
</p>

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

## One self-imposed rule

**This program's success metric is: you open it less often.** No daily-active chasing, no message centre, no "come back and check". A good night means you glance once the next day and read "done". If you haven't opened it in a month, it did its job.

## Quick start

1. Grab the **portable** build from [Releases](https://github.com/laopiaoke/Dragon-Butler/releases/latest) (bundles .NET, unzip and run; 0.3 MB runtime-dependent build if you already have .NET 8).
2. Unzip, run `Dragon Butler.exe`, accept the UAC prompt.
3. First-run wizard: tick your tools → **Auto-detect** (browse if it misses).
4. Back on the main window, hit **Run queue**.

Want it unattended? Tick on the schedule card (defaults to 08:00 daily). Want messages? Pick a channel under **Notifications** and press **Test**. **Neither is required.**

## Where data lives

Everything sits next to the exe; deleting the folder uninstalls it (turn off autostart first).

```
config\settings.json        all settings, hand-editable
config\group_archive.json   which config groups ran each day
tools\*.json                tool profiles
全部日志\                    runtime log — check here first
全部日志\incidents\          failure bundles
```

Nothing is written to `%APPDATA%`; the whole folder is portable.

## FAQ

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
The daily plan only constrains schedule-triggered runs. Manual **Run queue** always follows the current ticks, so you can catch up. See "today (Fri) runs 2 items" under the queue.
</details>

<details>
<summary><b>"Possibly stalled" — what now?</b></summary>
Check whether the screen is actually stuck. The queue waits for you by default; continue with **Run queue**. To auto-kill instead, enable it in Advanced.
</details>

<details>
<summary><b>Adding a tool that isn't built in</b></summary>
Bottom of the tools card → "Custom tool" → "＋ Add": pick the exe, name the process, choose how completion is detected (process exit / duration / custom signal).
</details>

## Privacy

**No telemetry, no analytics, no crash reporting.** Nothing about your usage leaves the machine.

The only outbound connection is the notification channel you configure. The local listener accepts `127.0.0.1` only, and can be turned off.

## Building

Requires .NET 8 SDK. Zero NuGet dependencies:

```
dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release
```

Tests run through a hand-written probe: 84 modes, several hundred assertions.

```
dotnet build tests\CoordinatorProbe\CoordinatorProbe.csproj -c Release
dotnet "tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.dll" live
```

## Notes

- **AI-assisted development**: code and docs were produced with AI help; the author is not a programmer and cannot professionally vouch for quality or stability. Evaluate the risk yourself and back up your data.
- **Not affiliated with** the game vendor or any tool listed below; no authorization implied. Names and icons belong to their owners.
- **Automation carries risk**: third-party tools may violate the game's terms of service and lead to account restrictions. You accept that risk.
- **No account access**: it never logs into the game or reads game data — only the log files the tools write.

## Related projects

This project implements no game dailies; all of that comes from:

- [BetterGI](https://github.com/babalae/better-genshin-impact)
- [March7thAssistant](https://github.com/moesnow/March7thAssistant)
- [ok-ww](https://github.com/ok-oldking/ok-wuthering-waves)

## License

Released under [GNU AGPL-3.0](LICENSE). You may use, modify and redistribute it freely, **but any distribution — including modified versions and network-service use — must also be AGPL-licensed with full source provided**.

- **Why AGPL**: this README has always said "no reselling, no paid boosting, no paid repackaging", and MIT permits exactly those. AGPL turns that sentence into an enforceable term — you may profit from it, but the code stays open.
- **Third-party assets**: game and tool names, plus screenshots and UI assets under `docs/`, are not covered by this license and belong to their respective owners.
- **When redistributing**: keep the Notes section above so downstream users don't mistake this for an official product.

## Version

Latest release **v1.27** (in-app `1.27.0`). Per-version changes: [RELEASE_NOTES.md](RELEASE_NOTES.md). Design and trade-offs: [docs/路线图.md](docs/路线图.md).
