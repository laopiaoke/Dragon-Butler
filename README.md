# 一条龙管家 · Dragon Butler

<p align="center">
  <img src="docs/icon.png" width="96" alt="一条龙管家">
</p>

<p align="center">
  <strong>多工具游戏日常队列调度器（Windows）</strong><br>
  <sub>将 BetterGI、三月七助手、OK-WW 等第三方自动化工具排成一条队列，按顺序拉起、等待完成并执行收尾。</sub>
</p>

<p align="center">
  <img alt="platform" src="https://img.shields.io/badge/Windows-10%2F11%20x64-0078D4?style=flat-square">
  <img alt="dotnet" src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square">
  <img alt="license" src="https://img.shields.io/badge/license-AGPL--3.0-6E7681?style=flat-square">
  <a href="https://github.com/laopiaoke/Dragon-Butler/releases/latest"><img alt="release" src="https://img.shields.io/github/v/release/laopiaoke/Dragon-Butler?color=2EA043&style=flat-square"></a>
  <a href="https://github.com/laopiaoke/Dragon-Butler/stargazers"><img alt="stars" src="https://img.shields.io/github/stars/laopiaoke/Dragon-Butler?color=2EA043&style=flat-square"></a>
</p>

<p align="center">
  <a href="README.md"><strong>简体中文</strong></a> · <a href="README.en.md">English</a>
</p>

---

## 简介

一条龙管家是一个基于 WPF 和 .NET 8 的桌面调度工具，本身不实现任何游戏内自动化操作，只负责外围编排与无人值守兜底：

- 按顺序启动你本地已安装的自动化工具进程
- 依据进程退出、日志关键字、文件或窗口标题判断完成状态
- 监听本机 Webhook，并在跑完后发送汇总或执行睡眠、关机等收尾动作

全程不修改游戏与第三方工具文件，不登录或读取游戏账号数据。

<p align="center">
  <img src="docs/proto/tasklist_collapsed_v147.png" width="820" alt="主界面">
</p>

<p align="center">
  <sub>主界面：左侧为定时与执行摘要，右侧为任务清单卡片，显示当前工具顺序与预计耗时。工具的主程序与完成方式在「设置 → 工具配置」里改。</sub>
</p>

## 下载与快速开始

1. 前往 [Releases](https://github.com/laopiaoke/Dragon-Butler/releases/latest) 下载压缩包：
   - **portable 版**（下载约 65 MB，解压后约 158 MB）：自带 .NET 8 运行时，解压即用。
   - **runtime-dependent 版**（下载约 2.7 MB）：需本机已安装 .NET 8 桌面运行时。
2. 解压后运行 `Dragon Butler.exe`，在 UAC 提示中选择「是」（程序需要跨会话外进程管理与停止目标工具进程）。
3. 首次启动会打开五步向导：勾选工具、确认可执行文件路径（会自动扫描本机常见位置）、设定每日定时与可选通知渠道，即可在主页点击「一键开跑」或等待定时触发。

即使不配置通知渠道和定时任务，也可以直接作为手动队列启动器使用。

## 主要功能

### 队列调度与任务清单

- **内置常用工具与通用接入**：内置 BetterGI（原神）、三月七助手 / m7a.exe（星铁）、OK-WW（鸣潮）、绝区零一条龙、MaaEnd、OK-NTE、MAA、奇想盒、BAAH、HSR、M9A、MSS、SRC、MFW 项目等模板，也支持通过「其它 exe…」接入任意自定义程序。
- **单卡完成管理**：主界面右侧任务清单以单行列表展示工具，支持按住 `≡` 拖拽调整顺序、双击单独运行、右键菜单（跳过这次、今晚不跑它、查看日志、编辑参数等）。
- **BetterGI 配置组支持**：点击 BetterGI 行可在卡片内直接展开配置组并按勾选顺序执行；未勾选配置组时按卡片中填写的「一条龙配置名」运行。
- **定时与断点续跑**：支持按星期几定时启动及错过补跑窗口（默认 120 分钟）；若中途中断且已有部分成功记录，主界面与托盘菜单会提供「接着跑」，只补跑未完成项。

<p align="center">
  <img src="docs/proto/tasklist_expanded_v1421.png" width="820" alt="任务清单展开配置组">
</p>

### 守护、现场留存与收尾

- **运行守护**：持续监测进程存活、疑似挂起（CPU 与日志长期无变化时暂停等待或按设置自动终止）、窗口冻结及游戏异常退出，支持崩溃后自动冷却重启。
- **挂机护航与息屏**：队列运行期间自动阻止系统空闲睡眠与屏保，结束后恢复原电源行为；可选开启运行指定分钟数后自动息屏。
- **失败现场包**：出现失败、超时或挂起时自动将末尾日志、屏幕截图及脱敏后的运行配置打包保存到 `全部日志\incidents\`，超出保留份数后旧现场包优先移入系统回收站。
- **安全收尾窗口**：队列全部完成后可执行「仅通知 / 再跑一轮 / 打开程序 / 自动睡眠 / 自动关机」；自动睡眠和关机前均提供 60 秒主页倒计时条，可随时取消或立即执行，全跳过、依赖中止或跑崩时不会误触发关机。

<p align="center">
  <img src="docs/proto/wrapupbar_v144.png" width="820" alt="收尾倒计时横条">
</p>

### 日报、收益统计与消息推送

- **结构化日报**：日报页按「异常与处理 / 配置组执行 / 每个工具 / 合计耗时」展示运行结果，并与过去 14 天中位数及昨日耗时作快慢对比；界面展示与手机推送使用同一份统计数据。
- **多渠道推送与重发**：支持系统通知、企业微信、飞书、钉钉、Telegram、Bark、邮件、自定义 Webhook、WebSocket 等推送渠道；遇到网络波动按 1 / 3 / 7 秒重试三次，失败记录可在消息中心内修改配置后就地重发。
- **不弹窗阻塞**：崩溃、启动失败或队列异常记入标题栏消息中心并亮起未读红点，不会弹出阻塞后续无人值守的模态框。
- **可选功能**：支持按配置开关晨报（次日首次启动播报昨日概览与漏跑提示）、周报及收益账本（从 BetterGI 日志汇总摩拉、狗粮与材料，记录于 `所有配置\harvest.json`）。

<p align="center">
  <img src="docs/proto/report_v141.png" width="820" alt="日报界面">
</p>

### 设置分类、接口与界面外观

- **五类设置页**：设置分为「守护 / 定时 / 通知 / 接口 / 界面与启动」五个分类卡片，详细说明自动折叠为行尾 `ⓘ` 悬停或点击查看。
- **快捷操作与窗口**：主窗口默认 `1080×720`（最小 `880×600`），支持深色、浅色、跟随系统与自定义背景图/配色方案、`Ctrl+K` 命令面板、全局热键以及可拖拽的迷你浮条。
- **本机控制 API 与 MCP**（默认关闭）：开启后于 `127.0.0.1:18235` 提供带 `X-Butler-Token` 鉴权的 HTTP 控制接口与基于 HTTP（JSON-RPC 2.0）的 MCP 服务，供脚本或 AI 客户端查询状态与触发队列。
- **只读局域网进度页**（默认关闭）：开启后可在同一局域网设备浏览器中只读查看当前运行进度与最新事件。

<p align="center">
  <img src="docs/proto/settings_v141.png" width="760" alt="设置页">
</p>

## 数据目录

所有配置、记录与日志全部保存在 `Dragon Butler.exe` 所在目录，不写 `%APPDATA%`，删除程序文件夹即可完成清理（开启过开机自启的请先在设置中关闭）：

```text
所有配置\settings.json        主配置文件
所有配置\group_archive.json   每日配置组执行归档
所有配置\durations.json       各工具与配置组耗时统计（日报数据源）
所有配置\harvest.json         收益账本记录（开启后生成，保留 400 天）
所有配置\notices.json         消息中心历史记录（最多保留 100 条）
所有配置\tools\*.json         各工具配置档案
所有配置\备份\                设置备份与恢复包
所有配置\sounds\              内置或自定义提示音效
插件\                         扩展插件目录
全部日志\                     运行日志
全部日志\snapshots\           托盘「现在就到」截图快照
全部日志\incidents\           失败现场包
```

## 常见问题

<details>
<summary><b>提示「找不到 BetterGI」怎么办？</b></summary>
路径需要选到 <code>BetterGI.exe</code> 可执行文件本身，而不是它所在的文件夹。
</details>

<details>
<summary><b>双击后没有反应，或提示缺少 .NET 运行时</b></summary>
当前下载的是 <code>runtime-dependent</code> 版本但系统未安装 .NET 8 Desktop Runtime。安装运行时，或直接改用体积较大的 <code>portable</code> 版本。
</details>

<details>
<summary><b>杀毒软件误报或 SmartScreen 拦截</b></summary>
发布包未购买商业代码签名证书。确认文件来源为本仓库 Releases 后可点击「更多信息 → 仍要运行」；也可按下方「从源码构建」自行编译，工程本身零第三方 NuGet 依赖。
</details>

<details>
<summary><b>守护功能会误杀游戏进程吗？</b></summary>
不会。进程查找与结束操作均限定在当前 Windows 用户会话内，游戏冻结或异常退出时默认仅记录通知并提醒，不会强行重启游戏。
</details>

<details>
<summary><b>日志界面怎么打开？</b></summary>
日志按上下文跳转展示：主页异常卡片点「看现场」看当次运行日志，工具行右键「查看日志」只看该工具，日报点击配置组名只看该组当天日志，主页操作栏「更多 ▾ → 全部日志」可查看全量日志。
</details>

<details>
<summary><b>配置组勾了多个，执行顺序怎么算？</b></summary>
展开区内的勾选序号即为实际执行顺序，取消勾选再重新勾选可调整先后；卡片标题栏的「今晚要跑：…」与展开区右下角的「今晚 N 项」会实时反映最终队列。
</details>

<details>
<summary><b>提示「疑似挂起」时如何处理？</b></summary>
表示进程仍在运行但长时间无 CPU 活动和日志输出（阈值可在「设置 → 守护」调整）。默认会暂停队列等待人工确认后再点「一键开跑」继续；如需自动结束并推进下一项，可开启「自动终止疑似挂起」。
</details>

<details>
<summary><b>如何接入内置清单以外的工具？</b></summary>
点击任务清单末尾的「＋ 添加工具」，在弹出面板底部选择「其它 exe…」，填写程序路径、进程名并选择完成判定方式（进程退出、到达时长或自定义日志/文件/窗口标题信号）。
</details>

## 从源码构建与测试

需要 .NET 8 SDK。仓库保持零第三方 NuGet 依赖与零前端构建工具链：

```powershell
# 构建主程序（Release，开启分析器且警告视为错误）
dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release -p:EnableNETAnalyzers=true -warnaserror

# 构建替身程序与集成探针
dotnet build tests\StubTool\StubTool.csproj -c Release
dotnet build tests\CoordinatorProbe\CoordinatorProbe.csproj -c Release

# 运行指定功能断言组或 UI 渲染检查（StubTool 需传入绝对路径）
$stub = (Resolve-Path tests\StubTool\bin\Release\net8.0\StubTool.exe).Path
.\tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.exe schedule $stub
.\tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.exe ui all
```

打包两个发行变体到 `发布\` 目录：

```powershell
.\scripts\package.ps1 -Variant all
```

## 隐私、使用须知与图形素材

- **无遥测数据**：程序不收集、不上报任何使用统计或崩溃遥测。除你主动配置的通知渠道外，仅在本地开启控制接口与局域网只读面板时监听指定端口。
- **AI 辅助开发声明**：项目代码与文档在 AI 辅助下编写维护，请在使用前自行评估稳定性风险并做好配置备份。
- **使用风险自负**：本项目与游戏开发商及下列第三方自动化工具作者均无关联；自动化脚本可能违反游戏用户协议，使用风险由用户自行承担。
- **图形素材归属**：程序界面所含的游戏名称、第三方工具名称、内置图标以及 `docs/` 目录下的界面截图与素材，版权均归各自权利人所有。

## 相关项目

- [BetterGI · 更好的原神](https://github.com/babalae/better-genshin-impact)
- [三月七助手 March7thAssistant](https://github.com/moesnow/March7thAssistant)
- [OK-WW](https://github.com/ok-oldking/ok-wuthering-waves)

## 开源许可与文档入口

本项目以 [GNU AGPL-3.0](LICENSE) 开源发布。使用、修改或再分发（含作为网络服务提供）均须遵守 AGPL-3.0 条款，继续开源并提供完整源码，同时请保留上述使用须知，避免下游用户误以为官方产品。

- 当前版本：**v1.47.0**（工程内 `1.47.0`）
- 版本更新日志：[RELEASE_NOTES.md](RELEASE_NOTES.md)
- 随包用户手册：[docs/使用说明.txt](docs/使用说明.txt)
- 演进记录与设计取舍：[docs/路线图.md](docs/路线图.md)
- 各版本交付报告索引：[docs/reports/](docs/reports/README.md)
- 文案与交互规范：[docs/VOICE.md](docs/VOICE.md)、[docs/PATTERNS.md](docs/PATTERNS.md)
