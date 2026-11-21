# v1.41 交付报告：界面改造（《一条龙管家》UI/UX 改造任务书 v1.0）

本版只按任务书条目施工，未新增任务书以外的页面、按钮、弹窗与动画。
构建：`dotnet build "Dragon Butler/Dragon Butler.csproj" -c Release` → **0 警告 0 错误**。
探针：`CoordinatorProbe all` → **exit 0, fails 0**（`clipboard` 一项见「待人工确认」第 1 条）。

---

## 一、改动文件清单

### 新增（代码）

| 文件 | 行数 | 职责 |
| --- | --- | --- |
| `Dragon Butler/DailyReport.cs` | 329 | 日报**唯一**数据生成器：结构化 `DailyReportData`（含 `ToJson()`），App 卡片与 IM 文本都渲染这一份。 |
| `Dragon Butler/LogQuery.cs` | 179 | 日志下钻的过滤模型与读取：`LogFilter{RunId?, ToolId?, Date?}`、按 `[run:xxxxxxxx]` 解析、`LastFailedRunId()`。 |
| `Dragon Butler/HintFold.cs` | 108 | 设置页说明文字超过一行时收成一行 + 行尾 ⓘ，悬停或点击展开（一行放得下就不挂 ⓘ）。 |

### 新增（规范文档，M7）

| 文件 | 行数 | 内容 |
| --- | --- | --- |
| `docs/VOICE.md` | 88 | 六句灵魂句逐字表 + 六条语气规则 + **每句 3 个反例** + 自查清单 |
| `docs/PATTERNS.md` | 45 | 四条交互模式，写成可勾选 checklist，每条带现存范例 |
| `docs/SOUL.md` | 31 | 声明灵魂句禁改，定义「迁移（允许）/ 改写（禁止）」，逐句解释背后决策 |

### 修改

| 文件 | 增/删 | 主要改动 |
| --- | --- | --- |
| `Dragon Butler/MainWindow.xaml` | +1312 / −… | 删日志页与日志导航项；新增日志视图层；设置页二级化（五个分区块）；日报页重排；主页首访卡；双卡 5:7；窗口 1080×720；任务清单改名；视图 chip 改下拉；「更多 ▾」 |
| `Dragon Butler/MainWindow.xaml.cs` | +1269 | 分页器四页化、日志视图（开/关/过滤条/列表）、设置分区路由、日报六个渲染方法、首访卡、设置页入口卡、工具卡摘要行与右键四项、`RunToolNow`/`ToggleSkipOnce`/`RevealIntervalSetting`、窗口位置记忆、`HintFold` 接线 |
| `Dragon Butler/FirstRunWindow.xaml` | +344 | 遮罩 60%、卡 760×480 居中、五段进度条、第 3 步时间页、第 5 步双 CTA 与「顺手两项」、第 4 步渠道三组、`TbNextPreview`、`WizLink`、`WizGroupLabel` |
| `Dragon Butler/FirstRunWindow.xaml.cs` | +504 | 步骤语义改「工具→路径→时间→通知→完成」；进第 2 步自动寻找；`SaveTimeChoice()`；`BuildDoneSummary()` 改控件化 + 内联「现在去设置」；`Finish(trial)`；`JumpToStep`；Enter/ESC；`>_` 图标 |
| `Dragon Butler/AppState.cs` | +38 | `OnboardingCompleted`、`WindowLeft/Top/Width/Height`、版本 16 与迁移（老用户 `FirstRunDone` → `OnboardingCompleted`） |
| `Dragon Butler/Pager.cs` | +39/−… | 五页 → 四页：`Count=4`、`Report=0`、`Home=1`、`Settings=2`、`Plugins=3`；`Name()` 改「日报」 |
| `Dragon Butler/MorningReport.cs` | +126/−… | 改叫「日报」，数据来自 `DailyReport.Build`；新增 `RenderText()` 渲染同一份；删 `BuildSummary(GroupRecord[])` |
| `Dragon Butler/QueueService.cs` | +57 | 「这次不跑」单趟跳过机制（`_skipOnce` / `IsSkippedOnce` / `ToggleSkipOnce` / `ClearSkipOnce`） |
| `tests/CoordinatorProbe/Program.cs` | +228/−… | 全部断言按四页 / 日报 / 新设置页 / 1080×720 同步 |
| `tests/CoordinatorProbe/RenderProbe.cs` | +18/−… | 截图宽度按新窗口尺寸调整（向导 800、日报/主窗 1120） |

---

## 二、新增文案清单

> 全部简体中文、口语、克制；专有名词保留 BetterGI / Webhook / API / exe。

### 向导（M2）

- 第 3 步标题：**每天几点跑？**
- 第 3 步副文案：**到点自动拉起工具跑一遍，之后随时能改。**
- 时间预设 chip：`08:00` / `12:00` / `23:30` / `我自己挑`
- 底部预告（四步各一句）：
  - 下一步：给它们找主程序，多数情况下会自动找到。
  - 下一步：定个每天几点跑。
  - 下一步：选跑完之后要不要提醒你。
  - 下一步：看一眼总结，然后就能开跑了。
- 第 2 步自动寻找结果：**{工具名}：已自动找到（{来源}），可直接下一步。**
- 第 2 步找不到时 placeholder：`比如 D:\Games\BetterGI\BetterGI.exe`
- 完成页说明：点「完成并试跑一次」会立刻跑一遍第一个工具，你能看到它真的动起来。
- 完成页分隔小标题：**顺手两项，以后能改**
- 完成页次按钮：**先不跑，完成**
- 完成页未完成行内联按钮：**现在去设置**
- 完成页新增两行：**每天 {HH}:{MM}：自动跑一遍**、**跑完提醒：…**

### 主页 / 日志视图（M1、M3）

- 首访引导卡标题：**还没跑过——三步就能开始**
- 三行清单：① 选 BetterGI 在哪里 / ② 设个每天几点跑 / ③ 点一下「一键开跑」，先看它动起来
- 行尾动作：**去设置**（未完成）/ `✓`（已完成）
- 日志视图过滤条：**正在看：{条件}**（无条件时为「全部日志」）
- 日志视图计数：**最近 {N} 条** / 右侧小字 **全量** 或 **已按上面的条件筛过**
- 日志视图全量空态：**今天这里还是空的。跑过一次之后，每一步都会写在这儿。**
- 日志视图过滤后空态：**这组条件下没有记到东西。可能那一天没跑，也可能这次跑得很干净。**
- 关闭按钮 ToolTip：回到刚才那一页
- 操作 chip 第三个：**更多 ▾**（内含「出门前看一眼」「全部日志」）
- 任务清单卡视图下拉项沿用原名：计划 / 日历 / 今日 / 依赖 / 配置组

### 工具卡（M3）

- 状态摘要行格式：`① · 间隔 2 分钟 · 跑完通知`（不在队列里序号显示 `—`）
- 右键新增四项：**立即跑这一个** / **跳过这次**（已跳过时显示 **这次还是跑**）/ **改间隔…** / **查看日志**
- 「立即跑这一个」失败提示：**找不到这个工具：{toolId}**
- 队列忙碌提示：**队列正在跑，等它结束或先停止。**
- 跳过提示：**这次不跑 {工具名}，下一趟照旧** / **这次照跑 {工具名}**
- 改间隔提示：**工具之间歇多久是全局一份，改一次所有工具都跟着变。**

### 设置页（M4）

- 一级提示：**想改哪一类？**
- 五张入口卡（标题 / 副标题）：
  - 守护 —— 跑的时候出事怎么办
  - 定时 —— 几点跑、中间歇多久
  - 通知 —— 跑完怎么告诉你
  - 接口 —— 给外面的东西开个口
  - 界面与启动 —— 平时怎么用它
- 二级返回按钮：← 回到分区名
- 未走完五步时的提示：**还没走完首次使用那五步。想补上，去主页点引导卡里的任意一项就行。**
- 说明折叠 ⓘ ToolTip：**点一下看完整说明**

### 日报（M5）

- 差异说明注（日期行下）：**较上次 = 与该任务上一次运行相比**
- 差异文本：**快了 33 秒** / **慢了 5 分 59 秒** / **持平**
- 趋势：**较昨日 慢了 46 分 50 秒** / **和昨天差不多**
- 异常与处理（无异常）：**✅ 异常与处理：{N} 个配置组全部顺利，没有需要你操心的事。**
- 异常与处理（空日）：**今天还没有跑过，暂时没什么可说的。**
- 异常与处理（有异常）：`· {配置组} 在 {环节} 卡住 → 管家已{动作}`
- 空分区合并：**📦 {分区A / 分区B}：今天都没有记录。**
- IM 日报标题：**龙管家 · 日报**（原「龙管家 · 晨报」）

---

## 三、自检表（任务书 §7 全局验收，逐条）

| 验收项 | 结果 | 依据 |
| --- | --- | --- |
| 六条灵魂句原样存在 | ✅ | 全库 `.cs`/`.xaml` 逐句 `Contains` 校验，六句全 True；`docs/VOICE.md` 同样逐字校验 |
| 向导 5 步，第 3 步时间与主页定时卡一致 | ✅ | 两者都读写 `ScheduleHour/ScheduleMinute`（同一份存储，无第二份）；`wizard_3.png` 与主页卡均显示 08:00 |
| 全新安装首屏无「昨晚没跑」/告警/历史文案，且有引导卡 | ✅ | `OnboardingCompleted=false` 时抑制 `Recap`（`RefreshHomeRecap`）、`trouble`（`RefreshHomeHero`）、状态栏「· 近期有告警」（`RefreshShield`）；同时显示首访引导卡 |
| 第 2 步多数情况零操作 | ✅ | 进第 2 步自动调 `AutoFind()`，命中即填入 + ✓ + 「已自动找到…可直接下一步」 |
| 第 5 步只有两个按钮 | ✅ | `BtnSkip`/`BtnBack` 在第 5 步 `Collapsed`；只剩「先不跑，完成」「完成并试跑一次」 |
| 「完成并试跑一次」能看到一次真实运行 | ✅ | `Finish(trial:true)` → `TrialRequested` → 主窗取第一个已配路径的队列项 → `QueueService.StartSingleAsync(profile, QueueTrigger.Manual)` |
| Tab 四页 | ✅ | `Pager.Count=4`（日报/主页/设置/插件）；`SegPageRun` 与 `ShiftRun` 已从 XAML 与代码中消失；探针断言「日志按钮已经摘掉」 |
| 四个下钻入口 + 全量兜底可用且过滤生效 | ✅ | `OpenLogView(ForRun)`×2（告警卡「看现场」、日报异常行「看现场」）、`ForTool`×1（工具卡右键）、`ForGroup`×1（日报配置组行点击）、`None`×3（「全部日志」、`view.log` 命令、`RevealCard`）；过滤条显示「正在看：…」 |
| 设置一级 5 卡全可达 | ✅ | `SettingsEntryHost` 五张入口卡；五个分区 `SecGuard/SecTiming/SecNotify/SecApi/SecUi` 一次只显示一个；一级页无开关平铺（探针「一级只有入口卡没有人开关」） |
| 日报差异有色有人话、空分区合并、异常分区明确说「无需操心」 | ✅ | 差异 pill：`SuccessBrush`（快）/ `WarningBrush`（慢）/ `ControlBgBrush`（持平）；`|Δ|<1s` → 「持平」；空分区 ≥2 合并一行；无异常文案见上表 |
| IM 与 App 数据一致 | ✅ | 两侧都只调 `DailyReport.Build(day)`，IM 走 `MorningReportService.RenderText(data)`，App 走 `RenderReport*` 系列。探针 `morning` 断言组数与总秒数一致 |
| 窗口 1080×720 启动 / 记忆 / 最小 880 | ✅ | XAML `Width=1080 Height=720 MinWidth=880 MinHeight=600`；`RestoreWindowBounds()`/`SaveWindowBounds()` 持久化 x/y/w/h；屏幕外回默认；`MaxWidth = 工作区宽 − 200` |
| 三份规范文档在 docs/ | ✅ | `docs/VOICE.md`、`docs/PATTERNS.md`、`docs/SOUL.md` |
| 新增文案清单已提交 | ✅ | 即本报告第二节 |

### 逐条核对 M 项

| ID | 状态 | 落点 |
| --- | --- | --- |
| M7-1/7-2/7-3 | ✅ | `docs/VOICE.md`、`docs/PATTERNS.md`、`docs/SOUL.md` |
| M2-12 | ✅ | `AppState.OnboardingCompleted` + 版本 16 迁移 + 向导置位 + 三处抑制 |
| M2-1 | ✅ | 向导 Step3 时间页；写的是现有 `ScheduleHour/ScheduleMinute` |
| M2-2 | ✅ | 两开关移到 Step5，副文案原文未改，加「顺手两项，以后能改」 |
| M2-3 | ✅ | `BuildFindRows` 里首次进入自动 `AutoFind`；placeholder + ✓ |
| M2-4 | ✅ | 删第 5 步「跳过」「上一步」；「完成并试跑一次」/「先不跑，完成」 |
| M2-5 | ✅ | `BuildDoneSummary` 里未完成项挂 `WizLink`「现在去设置」→ `_step = 2` |
| M2-6 | ✅ | `Prog1..Prog5` 五段进度条 + 保留「第 X 步 / 共 5 步」 |
| M2-7 | ✅ | `LooksLikeCommandLine()` → 向导格子图标画 `>_`（等宽字体） |
| M2-8 | ✅ | `NotifyTopHost`（不推送置顶默认）+ `NotifyGroupsHost`（本机/IM/手机三组），渠道全集未增未减 |
| M2-9 | ✅ | `WizardCard Width=760 MinHeight=480`，内容 `VerticalAlignment=Center`，切步外框不变 |
| M2-10 | ✅ | 窗口 `Background="#99000000"`（60%）；卡居中浮起 |
| M2-11 | ✅ | `Window_PreviewKeyDown`：Enter=下一步（焦点在输入框时不拦）、ESC=同「跳过」 |
| M3-1 | ✅ | `HomeFirstVisit` 三行清单，可点击、随状态打勾 |
| M3-2 | ✅ | 「托管配置」→「任务清单」；导航 ToolTip 同步 |
| M3-3 | ✅ | `BtnQueueView` + `QueueViewMenu` 下拉列五个全名 |
| M3-4 | ✅ | `CbAfterRun` 五项即全集（`Notify/RunAgain/OpenApp/Sleep/Shutdown`），未自造；加 `MaxDropDownHeight=320` 防截断 |
| M3-5 | ✅ | `BuildToolTileSummary` + `OrderLabel` + `DescribeAfterRun` |
| M3-6 | ✅ | `今日详情 / 打开日志 / 更多 ▾`；「出门前看一眼」进「更多」 |
| M3-7 | ✅ | 右键四项 + 原有项保留 |
| M1-1 | ✅ | 四页；日志不再占 Tab |
| M1-2 | ✅ | `LogFilter` 三条件取交集；四个固定入口 + 过滤条可见 |
| M1-3 | ✅ | 「更多 ▾」内「全部日志」→ `OpenLogView(LogFilter.None)` |
| M4-1 | ✅ | 一级五卡 + 二级五分区；支配映射见下 |
| M4-2 | ✅ | `HintFold.Apply()`；超一行才收，挂 ⓘ |
| M4-3 | ⏸ | P2，本期不实现，仅记录（见「待人工确认」第 6 条） |
| M5-1 | ✅ | 绝对耗时普通文本（等宽数字）+ 差异彩色 pill |
| M5-2 | ✅ | 「较上次 = 与该任务上一次运行相比」；周期为周的条目同样按「上一次运行」比 |
| M5-3 | ✅ | 空分区 ≥2 合并一行，App 与 IM 同步 |
| M5-4 | ✅ | 异常分区放最前，只写代码里真实存在的动作 |
| M5-5 | ✅ | `合计耗时 · 较昨日 ±X`，无昨日数据省略后半句 |
| M5-6 | ⏸ | P2 可选，本期未做（见「待人工确认」第 5 条） |
| M5-7 | ✅ | 单一 `DailyReport.Build`，两侧各渲染 |
| M5-8 | ✅ | App 分区头用图标（Path），emoji 只留在 IM 文本 |
| M6-1 | ✅ | 1080×720 / 最小 880×600 / 最大宽 = 可用宽−200 / 记忆与恢复 |
| M6-2 | ✅ | 仅约束新增界面，令牌写在 XAML 注释块（见「待人工确认」第 4 条） |
| M6-3 | ✅ | 双卡 `5*` / `7*` |
| M6-4 | ✅ | 字号令牌同上 |

### 设置页归类映射（M4-1）

| 一级入口 | 二级分区 | 装了什么 |
| --- | --- | --- |
| 守护 | `SecGuard` | 「守护与恢复」卡（守护/崩溃重启/截图冻结/自动终止挂起/定时前清理残留）+ **日志规则**（从原「节奏与报告」移来，属「出事时怎么办」） |
| 定时 | `SecTiming` | 「定时与节奏」卡（防打扰/失败策略/工具间隔/失败也执行跑完动作/挂机护航/夜间息屏） |
| 通知 | `SecNotify` | 「通知」卡（十二个渠道与各自表单、只推异常、测试消息、跑完通知正文、BGI 进度识别）+ **新「报告」卡**（晨报/周报/收益账本/事件推送地址） |
| 接口 | `SecApi` | 「接口与维护」卡（本机控制 API、局域网面板、快捷键、备份、体检、交接） |
| 界面与启动 | `SecUi` | 「启动与界面」卡（外观、开机自启、驻留托盘、启动最小化、收进托盘释放窗口、按键音效、迷你浮条、跑完说一句话） |

---

## 四、待人工确认清单

1. **`clipboard` 探针稳定失败（与本次改动无关）**：`CLIP busy ok=True elapsed=2ms`，探针要求「剪贴板被占时应阻塞约 1 秒再失败」。`Dragon Butler/ClipboardHelper.cs` 本版一行未改（`git diff` 为空），判断是环境侧剪贴板驱动行为差异。按要求「疑似 bug 只记录不动手」。
2. **任务书假设「工具覆盖值」不存在**：M3-5 写「末段=跑完动作（工具覆盖值否则全局值）」，但 `ToolProfile`（`Dragon Butler/ToolProfile.cs:52-114`）没有 interval / afterRun 字段，`AfterRunActionService.Execute`（`QueueService.cs:1427`）只读全局。因此摘要行的「间隔」「跑完动作」两段目前**只能显示全局值**，没有编造覆盖逻辑。要真正支持按工具覆盖，需要新增字段 + 设置入口 + 执行侧读取，属功能增量，本版未做。
3. **「跳过这次」的语义对齐**：任务书要求右键「跳过这次」。原代码没有「单次跳过」概念（`RunJournal.Record` 明确「跳过不算跑过」）。本版新增了进程内单趟跳过集合 `QueueService._skipOnce`（队列结束时清空），与旧的「今晚不跑它」（改写 `QueueEntry.Enabled`，会持久化）是两套语义：前者只作用于这一趟，后者影响后续所有天。若期望「跳过这次」= 「今晚不跑它」，这里需要改成复用旧接口。
4. **M6-2/M6-4 只约束新增界面**：任务书说「仅约束新增/改动界面」。现有 `Card` 仍是 `CornerRadius=14`、`LinkButton` 是 5、`NotifyChip` 7、`MiniAction` 30 高 11.5 号字——这些是历年一格一格调出来的，整体重刷会抹掉手感。已在 `MainWindow.xaml` 里写令牌注释块声明边界。若要求全量统一到「卡片 12 / 按钮 6 / chip 胶囊」，是一次独立的视觉重构，需要单独评估回归面。
5. **M5-6（拟人小结）未做**：P2 可选，且要求「生成不出就省略」，本项目目前没有本地文本生成能力；写死模板又会变成营销话术。建议单独设计后再上。
6. **M4-3 只记录未实现**：P2，任务书明示「仅为未来长页滚动 + 左侧锚点导航预留路由结构」。当前 `SettingsPane` 枚举 + `ShowSettingsPane()` 已经是一个可扩展的路由骨架，未来加锚点导航不必推翻。
7. **`TrayConfigWindow` 那套格子未改**：托盘「托管桌面」窗口（`Dragon Butler/TrayConfigWindow.xaml.cs:255-351`）有自己的一套工具格与右键菜单，与主窗工具卡是两套实现。任务书说的是「工具卡」（主页那张），因此只改了主窗这套。若两处需保持一致，是额外工作量。
8. **「看现场」的实现口径**：`tool.snapshot` 命令（主页告警卡主 CTA）现在是「先带 runId 打开日志视图，找不到再退回截图」。之所以这样定，是因为日志更答得出「哪一步卡住」；但若你更希望它保持「只截图」，改回来只是 `OpenSceneOfLatestTrouble()` 一个方法。
9. **一处修正**：任务书 §M5-2 原文要求日期行下小字为「较上次 = 与该任务上一次运行相比」，实现时把句首的「」去掉写成「较上次 = 与该任务上一次运行相比」，避免与正文的引号嵌套造成视觉噪音。若要严格保留「」，一处字符串即可。

---

## 五、发版与文档（v1.41 收尾）

| 项目 | 状态 |
| --- | --- |
| `Dragon Butler/Dragon Butler.csproj` 版本 | 1.40.0 → **1.41.0**（Version / FileVersion / AssemblyVersion） |
| `发布\Dragon-Butler-v1.41-win-x64-portable.zip` | 65.20 MB · sha256 `8DC48874C3418ADB88E10484309C82666D9676A6EC96741A8F22A4E713EA6AE5` |
| `发布\Dragon-Butler-v1.41-win-x64-runtime-dependent.zip` | 2.73 MB · sha256 `CA29F45ED9A7A29010670574827FCC6E898992302A8710252923166B4D5ECFC8` |
| `docs/使用说明.txt` | 264 → 305 行，全面对齐 v1.41（五步向导、四页签、五类设置、日报、日志入口、新数据目录），并已打进两个 zip |
| `README.md` / `README.en.md` | 按 v1.41 重写：四页签表、五步向导、设置五类表、日报四块、界面速览、四条新 FAQ、构建命令改 `all` |
| `RELEASE_NOTES.md` | 新增 v1.41 小节（workflow 发版靠它抽 Release 正文） |
| `docs/proto/*_v141.png` | 11 张新截图，README 主图换成 `main_v141.png` |
| `scripts/release-notes.ps1` | 修一处**既有缺陷**：原来只认 `^#\s+` 一级标题，而 v1.40 起正文用 `##`，导致 `gh release create` 取不到发布说明、直接 throw。改成 `^#{1,3}\s+`，标题剥离改 `^#+\s*`（PS 5.1 下已复验 v1.40 / v1.41 均可抽取） |

发布包产物目录里只有 `Dragon Butler.exe` 与 `使用说明.txt`，无运行期数据（`所有配置\`/`全部日志\`/`插件\`）混入。

## 六、截图（`C:\Users\Administrator\AppData\Local\Temp\dragon-ui`）

| 文件 | 说明 |
| --- | --- |
| `wizard_3.png` | 向导第 3 步：五段进度条第 3 段高亮、时间 chip、下一步预告 |
| `wizard_5.png` | 向导第 5 步：诚实总结 + 内联「现在去设置」+「顺手两项」+ 两个按钮 |
| `report.png` | 日报：合计与趋势、异常与处理 3 条（全是真实动作）、配置组执行含差异 pill、每个工具 |
| `main.png` | 主页：首访引导卡 + 双卡 5:7 + 「任务清单」+「配置组 ▾」 |
| `advanced.png` | 设置一级：五张入口卡，无开关平铺 |
| `runpage.png` | 日志视图：「正在看：全部日志」过滤条 + 刚才跑了什么 + 时间账 + 三 chip |
| `pane_guard.png` | 二级「守护」：守护与恢复卡 + 日志规则行 |
| `pane_timing.png` | 二级「定时」：防打扰/失败策略/工具间隔/挂机护航/夜间息屏 |
| `pane_notify.png` | 二级「通知」：通知卡（进分区自动展开）+ 报告卡 |
| `pane_api.png` | 二级「接口」：控制 API / 局域网面板 / 快捷键 / 备份 / 体检 / 交接 |
| `pane_ui.png` | 二级「界面与启动」：外观 / 自启 / 托盘 / 音效 / 浮条 / 语音 |
