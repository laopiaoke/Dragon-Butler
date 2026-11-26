# v1.44 交付报告

> 三份修复任务书（#4 / #5 / #6）的施工结果、「举一反三」清单，以及本轮发现并顺手修掉的问题。

## 〇 一句话结论

三份任务书全部按任务书实现，没有扩展范围。**收尾动作现在有一个 60 秒的、看得见、点得动、退出后仍有效的反悔窗口**；端口被占时不再是一句「换个端口再试」的死胡同；推送失败三次之后，除消息中心之外「今日」里也有带渠道名与错误摘要的痕迹。主工程与探针 0 警告 0 错误，25 组探针全绿，新增 `wrapup` 组 24 条断言。

## 一 #4 收尾动作 60 秒撤销窗口（P0 / S1）

### 落点

| 文件 | 改动 |
| --- | --- |
| `Dragon Butler/WrapUpCountdown.cs` | **新增**。倒计时状态机：`Start` / `Cancel` / `RunNow` / `Tick` / `AbortIfQueueActive` / `ResetForNewRun`，`WindowSeconds = 60` |
| `Dragon Butler/QueueService.cs` | `Execute` 的 Sleep / Shutdown 分支收成 `WrapUpCountdown.Start(...)`；新增 `RequestSystemShutdown` / `ExecuteShutdownNow` / `TrySleep` 三个 `internal` 执行器；`StartAsync` 开头调 `ResetForNewRun()` |
| `Dragon Butler/MainWindow.xaml` | 新增 `WrapUpBar` 横条（图标 / 文案 / 提示 / 秒数 / 取消 / 立即执行）；删掉旧的 `BtnQueueCancelShutdown` |
| `Dragon Butler/MainWindow.xaml.cs` | `OnWrapUpChanged` / `RefreshWrapUpBar` / `BtnWrapUpCancel_Click` / `BtnWrapUpNow_Click`；`UpdateQueueButtons` 里的显隐逻辑收成一处 |

### 两条路径为什么不一样

- **关机走系统倒计时**（`shutdown /s /t 60`）：系统自己记着这件事，管家被杀掉、崩溃、用户注销，它也照样到点关。所以「取消」必须真的执行 `shutdown /a` 并检查退出码——这是任务书二的结论，本轮沿用，没有改写。
- **睡眠走进程内倒计时**：Windows 没有「60 秒后睡眠」这种可撤销的系统命令，只能自己数。归零那一刻才调 `SetSuspendState`，中途取消就什么都没发生。

### 需求逐条对照

| 任务书要求 | 实现 |
| --- | --- |
| 倒计时 60 秒，含实时数字 | `WrapUpBar` 里 `TbWrapUpLeft` 显示 `RemainingSeconds`（`Math.Ceiling`，不会出现 59.4 秒） |
| 取消入口 | `BtnWrapUpCancel`，关机路径先 `shutdown /a` 成功才收窗 |
| 立即执行 | `BtnWrapUpNow`；关机先撤销系统倒计时再 `shutdown /s /t 0`，否则会变成「60 秒后才关」 |
| 三种留痕 | 「收尾已取消（睡眠）」「用户确认立即关机」「倒计时结束自动睡眠」，全部走 `DecisionLog`，进「今日」 |
| 倒计时内队列再活动 → 自动取消 | `ResetForNewRun()` 在 `StartAsync` 开头调用；队列在跑时走带留痕的 `AbortIfQueueActive()`，否则静默收窗 |
| 不动判定逻辑 | 只把「执行」这一步延后，`ShouldRun` / `NothingRan` / `DependencyAbort` 一行没改 |

### 实现中主动加的两条保护

- **200ms 一跳，但一秒只通知一次。** `Tick()` 只在 `left != _lastNotifiedSecond` 时才 `Changed?.Invoke()`。第一版是每跳都通知，实测主页每秒被整轮重算 5 次（`RefreshHomeHero` + `RefreshToolCards` + `LiveStrip`），鼠标都有点飘。归零那一下仍然准，因为 200ms 的粒度远小于一秒。
- **撤销失败时窗口不关。** `Cancel()` 在关机路径上如果 `CancelShutdown()` 返回 false，直接 return false，横条留着。原来的写法是「先清状态再尝试撤销」，失败也照样收起，用户以为取消了，60 秒后电脑灭了。

## 二 #5 Webhook 端口冲突就地可修（P1 / S1 变体）

### 落点

| 文件 | 改动 |
| --- | --- |
| `Dragon Butler/AppDialog.xaml` | 新增 `DlgInput` 样式（可编辑，和只读的 `DlgCopyBox` 分开）+ `PromptBox`（标签 / 输入框 / 提示 / 错误行） |
| `Dragon Butler/AppDialog.xaml.cs` | 新增 `Prompt` 类（`Label` / `Value` / `Hint` / `Validate`）、`ShowInput(...)`、`PromptValue`；`BtnPrimary_Click` 先跑校验 |
| `Dragon Butler/WebhookHub.cs` | 新增 `public static string? ValidatePortText(string?)` |
| `Dragon Butler/MainWindow.xaml.cs` | `TgLanPanel_Click` 的失败分支改调 `OfferPortChange(bind)`；新增该方法的完整重试序列 |

### 为什么校验抽成 `WebhookHub` 的静态函数

探针只能测 `public` 成员。写在 `OfferPortChange` 里的 lambda 是死的，测不到；抽成静态纯函数后，`wrapup` 组直接断言 8 条边界（含 `"8080 "` 前后空格、`null`、`""`、`80`、`70000`）。

### 决策（D20）没有变

- **不自动换端口**：换了之后 BetterGI 里填的地址会静默失效，用户只会看到「完成事件突然不灵了」。
- **不放到设置页**：用户是在「打开面板失败」这一刻才知道端口冲突的，让他去翻设置页等于让他把问题放下。
- 所以只能是「在这个报错框里，就地改」。

### 重试序列

```
WebhookHub.Stop()
  → AppState.Settings.BgiWebhookPort = newPort; SaveSettings()
  → WebhookHub.Start(newPort, LanPanelEnabled)
  → RefreshEventUrl()
```

失败：留痕「换端口后仍然没绑上」+ 系统原始错误显示在 `TbLanPanelHint` 与通知里。成功且端口变了：留痕「监听端口 18235→18240」。

## 三 #6 推送重试节奏常量化（P1 / S4）

| 项 | 结果 |
| --- | --- |
| 行为 | 不变：1 秒 / 3 秒 / 7 秒，最多 4 次尝试 |
| 单点常量 | `public static readonly int[] PushRetryDelaysMs = { 1000, 3000, 7000 };`，循环与日志全部改引它 |
| 公开的理由 | 探针要能断言「还是 1-3-7」，私有字段测不到 |
| 配置化 | **不做**（D21）。用户词汇里没有「退避间隔」，加一个只会让人不敢改 |
| 失败留痕 | `NoticeCenter.AddError`（原有）+ `DecisionLog.Add(DecisionSource.System, "推送没送到（企业微信）", "重试三次都失败了，最后一次说：…。这一条「…」今晚没发出去。")` |

## 四 「举一反三」清单（只上报，未实现）

### #4 类：其他「立即执行且不可逆」的动作

全库扫描 `Delete(|RemoveAll|Clear()|EmptyRecycl|Format|Directory.Delete`，去掉 `Children.Clear()`（UI 重建，正常）后，**用户可见的破坏性按钮共 4 个**，逐个查了 Click 处理器：

| 位置 | 文案 | 现状 | 建议 |
| --- | --- | --- | --- |
| `Dragon Butler/CustomThemeWindow.xaml:312` | 恢复默认主题 | **无确认，且当场 `DialogResult = true` 写盘** | **最该补的一个**。它一次改 6 个字段（主题 / 背景类型 / 背景色 / 背景图 / 遮罩 / 配色），点错了没有撤销，只能照着记忆再调一遍 |
| `Dragon Butler/CustomToolWindow.xaml:335` | 删除这个工具 | 有确认（`AppDialog.Show`，含「这个操作不可撤销」） | 文案已达标，可不动 |
| `Dragon Butler/NoticeWindow.xaml:112` | 清空 | 有确认（Question，含「清掉之后就没了」+ 说明日志不受影响） | 已达标 |
| `Dragon Butler/ToolLaunchWindow.xaml:256` | 删除 | 有确认（内置工具先劝退，第三方才删） | 已达标 |

另外两处**不是按钮但同样不可逆**：

- `Dragon Butler/IncidentBundle.cs:132` `Directory.Delete(d, true)` —— 现场包保留策略（默认留 10 份）。超出的会被**直接删掉**，没有回收站。建议：要么进回收站，要么在设置里说清「旧的会被删」。
- `Dragon Butler/LogService.cs:90/101` 日志按天清理、`LogRulesWindow` 的规则「全部清除」（有确认）。日志删除影响小，不建议动。

### #5 类：死胡同文案（说了「再试」但界面上没有地方能试）

扫描 `再试一次|请重试|再试|稍后再|换个|重新试`，共 12 处命中，逐个判断：

| 位置 | 文案 | 是不是死胡同 |
| --- | --- | --- |
| `Dragon Butler/BgiConfigWriter.cs:107` | 「配置文件已经不在了…刷新一下再试」 | 否——刷新是用户能做的动作 |
| `Dragon Butler/BgiConfigWriter.cs:114` | 「文件被别的程序改过…重新打开再试」 | 否——重开窗口就行 |
| `Dragon Butler/ErrorExplain.cs:47/62/149` | 「挪到普通目录再试」「关掉占着文件的程序再试」「清一点空间再试」 | 否——都在教具体动作 |
| `Dragon Butler/LogRules.cs:322` | 「换个更短的词试试」 | 否 |
| `Dragon Butler/LogRulesWindow.xaml.cs:248` | 「先写一个关键词再试」 | 否——输入框就在旁边 |
| `Dragon Butler/TodayWindow.xaml.cs:435` | 「剪贴板可能被别的程序占着，稍后再试」 | **是**。用户唯一的动作是再点一次复制，界面上没有任何「重试」按钮，只能自己想起来 |
| `Dragon Butler/NotifyService.cs:206` | 「网络恢复后可以手动重发，或者换个渠道」 | **是**。`NoticeWindow` 里没有「重发」按钮（已核实 grep 无 `重发|重试|Resend`），这条建议等于「你自己想办法」 |
| `Dragon Butler/MainWindow.xaml.cs` 原 :7115「换个端口再试」 | 已被 #5 修掉 | ✅ |

### #6 类：其他固定节奏值

扫描 `Task.Delay(|new Timer(|TimeSpan.From(...)|Interval =`，按「是否该收敛」分三类：

**已收敛或本来就有单一来源的（不用动）**：

| 值 | 位置 | 说明 |
| --- | --- | --- |
| `StallThreshold = 3 分钟` | `Dragon Butler/SentinelService.cs:126` | 单一属性，可被探针改 |
| `IdleThreshold = 5 分钟` | `Dragon Butler/QueueService.cs:1620` | 同上 |
| `PostponeDelay = 15 分钟` | `Dragon Butler/QueueService.cs:1623` | 同上 |
| `IdleThreshold = 20 分钟` | `Dragon Butler/AwaySense.cs:25` | `static readonly` |
| 心跳 5 秒 / 轮询 2 秒 | `SentinelService.cs:147` / `CustomSignalSources.cs:39/239` | 有 `*Override` 供探针替换 |

**写死在字符串里的（与实际阈值可能不一致 —— 建议后续处理）**：

- `Dragon Butler/QueueService.cs:1115` `"疑似挂起（3 分钟无任何活动）"` 与 `:1420` 同一句 —— 而 `StallThreshold` 是变量。一旦有人把它改成 5 分钟，通知里还会说 3 分钟。`SentinelService.cs:359/360` 用的是 `StallThreshold.TotalMinutes:F0`（正确）。**同一件事两处写法不一致**，建议把 `QueueService` 那两句也改成插值。

**纯 UI 节奏（不建议动）**：`MainWindow.xaml.cs` 的一堆 `DoubleAnimation` 毫秒数（150–380）、`_uiRefreshTimer` 1 秒、`_scheduleTimer` 30 秒、`_groupScanTimer` 8 秒、`_dotHold` 600ms、`_dragHold` 350ms。这些是手感参数，收敛成常量只会让调动画变麻烦，且不影响任何判断。

## 五 本轮发现并顺手修掉的问题

1. **`WrapUpBar` 一开始占满整个内容区。** `Border` 放进 `Grid.Row="1"` 默认会被拉伸成整片 `BrandSoftBrush` 绿底，把页面全盖住。加 `VerticalAlignment="Top"` 修好。
2. **`MainWindow.xaml` 第二次被截断。** 替换脚本写成 `$a.Substring(0,$i) + @(...) -join $nl + $a.Substring(...)`，`-join` 的优先级高于 `+`，导致前半段被丢弃、文件从 235KB 掉到 107KB。已从 `HEAD` 恢复并重做两处改动。**教训：先把拼接内容赋给变量，再做 `+`。**
3. **「取消关机」有两个入口。** 新增 `WrapUpBar` 之后，旧的 `BtnQueueCancelShutdown` 与横条上的「取消」做同一件事，但名字不同（睡眠时它写着「取消关机」）。撤掉旧按钮，`MainWindow.xaml.cs` 里对它的引用清零。
4. **`resume` / `dag` 探针用相对路径调 StubTool 会假失败。** 日志是「启动失败：… 系统找不到指定的文件」，连带 `端到端/父项成功` 等断言一起红。用绝对路径即通过。**这不是代码回归**，已在报告里记明。

## 六 验证

| 项 | 结果 |
| --- | --- |
| 主工程 `-c Release -warnaserror` | 0 警告 0 错误 |
| 探针工程 | 0 警告 0 错误 |
| `wrapup` 组（新增） | 24 / 24 PASS |
| 全量探针 | 25 组全绿（`schedule` `resume` `bundle` `explain` `retry` `api` `weekly` `hotkey` `progress` `command` `dag` `groups` `bgiupdate` `harvest` `mcp` `uirules` `pager` `morning` `recap` `toolgrid` `logpattern` `migrate` `afterrun` `signals` `wrapup`） |
| 真机截图 | `wrapupbar.png`：横条只占顶部一条，下面的主页、任务清单、一键开跑都完整可见 |

`wrapup` 组里最有价值的三条：

- `显隐只有一处说了算` —— 断言 `RefreshWrapUpBar()` 存在，且 `MainWindow.xaml.cs` 里 `BtnQueueCancelShutdown` 出现 0 次。防的是「两个地方各写一遍可见性，有一天打架」。
- `改秒数才刷界面` —— 断言 `_lastNotifiedSecond` + `if (left != _lastNotifiedSecond)`。防的是「以后有人把节流删了，没人发现主页开始每秒重算五次」。
- `节奏还是 1-3-7 秒` —— 直接比对 `PushRetryDelaysMs` 的三项。防的是「改这一版顺手调成 2-5-10，用户以为推送坏了」。

## 七 未做的事（明确记录）

- **`恢复默认主题` 没有补确认框。** 它属于 #4 的「举一反三」范围，任务书要求只上报不实现。已列在第四节，建议下一版补。
- **`NoticeWindow` 没有加「重发」按钮。** 同上，属于 #5 举一反三。
- **`QueueService.cs:1115/1420` 的「3 分钟」没有改成插值。** 同上，属于 #6 举一反三。
- **60 秒没有做成可配置。** 任务书明确禁止，D21 同理。

## 八 改动文件清单

**新增（1）**：`Dragon Butler/WrapUpCountdown.cs`

**生产代码（9）**：`Dragon Butler/AppDialog.xaml`、`Dragon Butler/AppDialog.xaml.cs`、`Dragon Butler/MainWindow.xaml`、`Dragon Butler/MainWindow.xaml.cs`、`Dragon Butler/NotifyService.cs`、`Dragon Butler/QueueService.cs`、`Dragon Butler/WebhookHub.cs`、`Dragon Butler/Dragon Butler.csproj`（版本号 1.44.0）

**测试（2）**：`tests/CoordinatorProbe/Program.cs`（新增 `wrapup` 组）、`tests/CoordinatorProbe/RenderProbe.cs`（`wrapupbar.png` 真机断言）

**文档（4）**：`RELEASE_NOTES.md`、`README.md`、`README.en.md`、`docs/使用说明.txt`

合计 **16 个文件，+649 / −109 行**（不含新增文件的行数）。
