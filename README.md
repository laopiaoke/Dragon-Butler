# 一条龙管家 · Dragon Butler

<p align="center">
  <img src="docs/icon.png" width="96" alt="一条龙管家">
</p>

<p align="center">
  <strong>睡前点一下，明天看结果。</strong><br>
  <sub>把 BetterGI、三月七助手、ok-ww 排成一条队列，跑完告诉你一句话</sub>
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

## 它是什么

一个 Windows 桌面小程序，**不自己做游戏日常**，只当调度员：把你装好的自动化工具排成队列，按顺序拉起来、盯着跑完、再收尾。

它只做四件事：启动进程、读工具自己写的公开日志、监听本机 Webhook、管理进程生命周期。**不改游戏、不改工具文件、不碰账号。**

<p align="center">
  <img src="docs/proto/main_folded.png" width="720" alt="主界面">
</p>

## 它解决什么

- **跑完才知道结果** → 队列结束发一条汇总：几项成功、哪项挂了、为什么、现场在哪。
- **不知道跑到哪了** → 顶部实时条：`● 日常 · 18 分　还要 2 分 · 预计 23:06 结束　5/7`，空闲时自动收起。
- **不敢走开** → 卡住/冻结/内存爆/工具崩溃都能识别，按你的设定提醒、留现场或停下队列。
- **跑完还得手动关机** → 队列成功结束后可自动睡眠、关机，60 秒内可撤销。
- **出事了找不到证据** → 失败时自动打包：日志尾巴 + 截图 + 脱敏配置，放 `全部日志\incidents\`。
- **崩溃提示挡住整晚** → 不再弹窗。崩溃、工具没启动、队列中断都记进消息中心，每条自动翻成人话（发生了什么 / 因为什么 / 你可以做什么），红点亮着等你回来看。
- **配置工具要先展开三层折叠** → 启动页标题行一个「⚙ 配置」，点开就是 3×2 的工具桌面：左键改设置、双击直接跑、右键改「今晚跑不跑」、按住拖动换顺序，鼠标停上去会写明这三个键各做什么。
- **同一套流程要跑不同账号/配置** → 「配置组合」记住几套「一条龙配置 + 配置组」，开跑时挑一套。只换参数，不写任何文件。
- **跑完只有一堆日志** → 首屏直接给一段人话复盘：几项成了、哪项拖长了、哪组没结束。
- **不知道它替你做了哪些决定** → 每次跳过、推迟、重启、中止都留一句为什么，在「今日」里按时间排好。
- **说不清「和平时比怎么样」** → 跟自己过去 14 天的中位数比，不是跟某个写死的阈值比；样本不够就不说话。
- **不知道省了多少时间** → 就一个数字：这个月它替你跑了多少小时。

## 一条自我要求

**这个程序的成功指标是：你打开它的次数变少。** 它不追求日活、不催你回来、不给你发「连续使用 7 天」这种话。跑得好的一晚，你第二天只需要看一眼「今天跑完了」。哪天你一个月都没打开过它，那说明它做对了。

唯一的例外是**出事的时候**：程序崩了、工具没能启动、整条队列中断——这些会记进标题栏的消息中心（一个信封图标，有未读时带红点），红点亮着等你回来看，**绝不弹窗拦在前面**。它宁可安静地等，也不会在你不在电脑前的时候，用一个对话框把整晚的自动开跑堵死。

## 快速开始

1. 到 [Releases](https://github.com/laopiaoke/Dragon-Butler/releases/latest) 下 **portable** 包（自带 .NET，解压即用；机器上已有 .NET 8 可下 0.3 MB 的 runtime-dependent 包）。
2. 解压，双击 `Dragon Butler.exe`，UAC 点「是」。
3. 首启向导：勾选工具 →「自动寻找」（找不到再浏览到 exe）。
4. 回主界面点「一键开跑」。

想挂机再加两步：定时卡点「就这样，打开定时」（默认每天 08:00）；想收消息就去「通知」卡填一个渠道地址，点「测试」。**都不填也能用。**

## 数据在哪

全部在 exe 旁边，删掉文件夹就是卸载（开过自启的先去「系统」里关掉）。

```
所有配置\settings.json      所有设置，可手改
所有配置\group_archive.json 每天跑了哪些配置组
所有配置\notices.json       消息中心（最多留 100 条）
所有配置\tools\*.json       工具档案
所有配置\plugins\           插件
所有配置\备份\              设置备份与恢复包
所有配置\sounds\            自定义音效
全部日志\                    运行日志（出问题先看这里）
全部日志\snapshots\          定时快照
全部日志\incidents\          失败现场包
```

老版本散在 exe 旁边的 `config` / `tools` / `plugins` / `cache` / `sounds` / `备份`，第一次启动 1.30 会自动搬进这两个文件夹，老的 `config` 里留一个 `.moved-to-所有配置` 标记防止重复搬。

不写 `%APPDATA%`，整个文件夹可以拷走接着用。

## 常见问题

<details>
<summary><b>标题栏那个信封是什么</b></summary>
消息中心。程序崩了、工具没能启动、队列中断过，都记在这里，每条都写了「发生了什么 / 因为什么 / 你可以怎么做」。有未读时信封右上角有红点，点开就算看过了。它不弹窗、不拦启动——以前那句「检测到上次为异常退出」会挡在自动开跑前面，人不在电脑前，整晚就白等了。
</details>

<details>
<summary><b>提示「找不到 BetterGI」</b></summary>
路径要选到 <code>BetterGI.exe</code> 本身，不是它所在的文件夹。
</details>

<details>
<summary><b>双击没反应 / 报缺框架</b></summary>
用的是 runtime-dependent 包但没装 .NET 8 桌面运行时。装一次，或换 portable 包。
</details>

<details>
<summary><b>杀毒软件报毒 / SmartScreen 拦截</b></summary>
发行包没有代码签名，点「更多信息 → 仍要运行」。若被隔离，把程序目录加入信任，或按下面自己从源码构建——零第三方依赖。
</details>

<details>
<summary><b>守护会误杀游戏吗</b></summary>
不会。进程查找和结束都按当前 Windows 会话过滤；冻结与原神退出只提醒不重启。
</details>

<details>
<summary><b>按日计划设了，手动开跑还是全跑</b></summary>
按日计划只约束定时触发。手动「一键开跑」永远按当前勾选跑，方便临时补跑。今天会跑哪几项，看队列下面那行「今天（周五）跑 2 项」。
</details>

<details>
<summary><b>提示「疑似挂起」怎么办</b></summary>
先看画面是否真的卡住。默认队列会停下等你，处理完点「一键开跑」继续；不想要这个停顿，到高级设置打开「自动终止疑似挂起」。
</details>

<details>
<summary><b>想接入任务书之外的工具</b></summary>
工具卡最下方「自定义工具」→「＋ 添加」，选 exe、填进程名，再选完成方式（进程退出 / 到达时长 / 自定义信号）。
</details>

## 隐私

**没有遥测、没有统计、没有崩溃上报。** 程序不向任何第三方发送使用数据。

唯一的对外连接是你自己填的通知地址；本地开的监听口只接受 `127.0.0.1` 请求，不用可以关掉。

## 构建

需要 .NET 8 SDK。零 NuGet 依赖，clone 下来直接：

```
dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release
```

测试是靠一个手写探针跑的，85 个模式、上千条断言：

```
dotnet build tests\CoordinatorProbe\CoordinatorProbe.csproj -c Release
dotnet "tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.dll" live
```

## 使用须知

- **AI 辅助开发**：代码与文档在 AI 协助下生成，作者不具备编程能力，无法对质量与稳定性作专业保证。请自行评估风险、备份数据。
- **与游戏厂商、下列工具作者均无关联**，未获任何授权。游戏名、工具名、图标归各自权利人所有。
- **自动化有风险**：第三方工具可能违反游戏用户协议并导致账号受限，风险由使用者承担。
- **不接触账号信息**：不登录游戏、不读游戏数据，只读工具写在磁盘上的日志。

## 相关项目

本项目不实现游戏日常，全部交给它们，感谢作者：

- [BetterGI · 更好的原神](https://github.com/babalae/better-genshin-impact)
- [三月七助手 March7thAssistant](https://github.com/moesnow/March7thAssistant)
- [ok-ww](https://github.com/ok-oldking/ok-wuthering-waves)

## 许可

以 [GNU AGPL-3.0](LICENSE) 发布。你可以自由使用、修改、再分发，**但分发（含修改后分发、以及作为网络服务提供）时必须同样以 AGPL 开源，并提供完整源码**。

- **为什么是 AGPL**：本项目 README 一直写着「请勿售卖、代练、打包收费」，而 MIT 恰好允许这些。AGPL 把这句话变成了强制条款——你可以拿它赚钱，但代码必须继续开源。
- **第三方素材**：游戏名、工具名、`docs/` 下的截图与界面素材不在本许可范围内，归各自权利人所有。
- **再分发时**：请保留上面的「使用须知」，别让下游使用者误以为这是官方产品。

## 版本

最新发布 **v1.30**（程序内 `1.30.0`）；逐版变更见 [RELEASE_NOTES.md](RELEASE_NOTES.md)，设计与取舍见 [docs/路线图.md](docs/路线图.md)。
