# v1.43 交付报告：六条危险链加固

> 这一版不加功能。六份任务书治的都是同一类病：**出问题的时候，程序会自信地告诉你「一切都好」**。
> 下面按任务书写，每一条都写清「原来错在哪 → 改成什么 → 怎么证明它改对了」。

---

## 〇 一句话结论

六个模块全部按任务书落地，`signals` / `afterrun` / `migrate` 三组新探针进了 CI 闸门，
24 组探针全绿，主工程与探针均 0 警告 0 错误。**没有新增任何用户可见设置项**，
`AfterRunAction` 取值、`FailStrategy` 语义、60 秒撤销时长、默认回环绑定全部保持原样。

---

## 一 任务书一：完成判定 → 队列结果 → 睡眠/关机授权链

### A 信号仲裁（`Dragon Butler/RunCoordinator.cs`，249 → 351 行）

**原来错在哪**：`OnSignal` 只读当前状态、不记事实。

1. `Running` 状态下，第一行就是 `if (signal.Kind != _profile.PrimarySignal) return;` ——
   主信号是 LogPattern/Webhook 时，进程崩溃退出（`ProcessExit0` 带非零码，作为副信号）被当成
   「非主信号」整个丢掉，只能等 `MaxTimer` 到 120 分钟才判 `Timeout`。任务书里说的
   「无声忽略后等待数小时」就是这一段。
2. `Resolving` 状态下，非零退出只写一行日志 `忽略非 0 退出作为互证`，**不留任何证据**，
   紧接着 `Move(Done, "副信号 … 互证完成")` —— 之后随便再来一个副信号（游戏退出 / Webhook）就翻成 `Done`。

**改成什么**：新增三个锁存字段，把「已经知道的事实」攒住，不让后来的信号翻转。

```csharp
// ===== v1.43 / 任务一：把「已经知道的事实」锁存下来，别让后来的信号把它翻转 =====
//
// 这三个字段存在的理由，是原来那段 OnSignal 只读当前状态、不记事实：
// 非零退出被忽略之后什么都没留下，于是下一个副信号（游戏退出 / Webhook）就把
// 状态推进了 Done。判定看的是「到过什么信号」，而事实是「进程用几退出的、
// 主信号到底来没来」。事实必须攒住，否则谁最后到谁说话。
private bool _primaryArrived;
private bool _secondarySeen;
private string? _exitFailure;
```

三条出边的实际行为：

| 场景 | 行为 |
|---|---|
| `Running` + `ProcessExit0`（非零） | **先于「是不是主信号」判断**：`LatchExitFailure` + `Move(Failed, "进程异常退出 exit=N")` |
| `Running` + `ProcessExit0`（零）且是主信号 | `Move(Done, "进程正常退出 exit=0")` |
| `Running` + `ProcessExit0`（零）但不是主信号 | `Move(Resolving, "进程已退出 exit=0，等待主信号互证")` + 宽限计时 |
| `Running` + 其它副信号 | 记日志 + `_secondarySeen = true`，状态不动 |
| `Resolving` + 任意信号 | **先查 `_exitFailure`**，有证据就只记日志、不推进；否则才看主/副信号 |
| `Resolving` 收尾 | `if (_primaryArrived && _secondarySeen)` 才 `Done`，否则记「证据还不齐」继续等 |

- `GameProcessExit` 在任何分支都只置 `_secondarySeen`，**单独不能证明成功**（`Running` 下保持 `Running`）。
- `StartGraceTimer` 开头加了 `_graceTimer?.Dispose()`：「进程先退、主信号后到」都会调到这里，
  不保护就会留下一个没人管的旧 Timer，两个宽限计时同时跑。
- `Halt` 的文案改成按事实说话：`「{DisplayName}」这次没法确认结果，已暂停等你处理：{reason}`，
  而 `reason` 由新增的 `GraceExpiredReason(int minutes)` 分四种情况生成（已发现非零退出 /
  进程已退但主信号没来 / 主信号到了但进程没退 / 两个都没来）。原来固定写「工具报告完成但未完全退出」，
  在进程明明崩了的情况下是误导。
- 状态体系不变：仍是 `Idle / Launching / Running / Resolving / Done / Failed / Timeout / Halted / Stalled`。

### B 队列收尾事实（`Dragon Butler/QueueService.cs`，1625 → 1877 行）

**原来错在哪** 四处：

1. 依赖门 `abort` 只 `break` —— 不写 `failedThisRun`、不增 `finished`，`LastResults` 里那条还是
   `Skipped = true`，收尾时被 `Where(r => !r.Skipped)` 整体排除，于是「整队中止」在事实里消失。
2. `FinishQueue` 的 `finished` 参数**在函数体里从头到尾没被用过**。
3. 全部跳过时 `total = 0 / ok = 0 / anyFailed = false` → `ShouldRun` 放行 → 关机。
4. 收尾总结那行 `LogService.LogEvent` 写在 `IncidentBundle.Capture` **之后**，永远进不了现场包（P2-2）。

**改成什么**：

- `RunListAsync` 新增 `string? dependencyAbort = null`，依赖门里写
  `dependencyAbort = $"{profile.DisplayName} 的依赖没跑成（{gate.Reason}）"` 再 `break`。
- `AfterRunContext` 新增两个字段：`NothingRan`、`DependencyAbortReason`。
- `ShouldRun` 做成两个重载：四参版原样保留（探针 15 条旧断言与 CI 的 `afterrun` 不必改），
  六参版加两条：
  ```csharp
  if (nothingRan && IsDestructive(a)) return false;   // 一项都没跑，不许碰电脑
  if (dependencyAbort) hasFailure = true;             // 依赖中止等同于有失败
  ```
- `FinishQueue(trigger, finished, dependencyAbort)` 重写，收尾分成五种可辨别的情况：

| 情况 | 判定 | 通知正文 | 破坏性动作 |
|---|---|---|---|
| 全部实际执行且成功 | `ok == total && total > 0 && !dependencyFailed` | 「今晚队列完成：N/N 项成功…晚安。」 | 允许 |
| 全部跳过 | `nothingRan` | 「队列结束：没有可执行项，今晚一个都没跑。」 | **不允许** |
| 依赖中止 | `dependencyFailed` | 「队列停在中途：{哪一项的依赖没跑成}。前面 N/M 项成功，剩下的没跑，今晚不算完成。」 | **不允许**（除非勾了失败也执行） |
| 有失败/超时/悬置/挂起 | `anyFailed` | 按既有分支（halted / stalled / ok==0 / 部分成功） | **不允许**（除非勾了失败也执行） |
| 用户主动停止 | `_stopRequested` | — | **不允许** |

- `LastRunOk = ok == total && total > 0 && !dependencyFailed;`（「依赖中止」不再算成功的一天）。
- `BuildTemplateValues` 的「结果」从二分类变成四分类：`中止` / `没跑` / `成功` / `有失败`。
- 语音播报补两句：没有可执行项时念「队列结束了，今晚没有可执行项。」，依赖中止时念
  「队列停在中途，有一项依赖没跑成。」

### 任务书一点名的八条验收场景 → 探针断言

新增 `signals` 组（`RunSignalArbiter()`，8 条，**真构造状态机喂信号，不看源码字符串**）：

| 场景 | 断言 |
|---|---|
| 主信号 → 非零退出 → 游戏退出 | `仲裁/崩溃退出判失败`、`仲裁/锁存后副信号不翻完成` |
| 主信号 → 零退出互证 | `仲裁/主信号加零退出算完成`、`仲裁/零退出主信号算完成` |
| 只有零退出、主信号还没到 | `仲裁/只有零退出先等主信号`（停在 `Resolving`） |
| 游戏进程消失单独 | `仲裁/游戏退出不单独算完成`（仍是 `Running`） |
| 宽限为 0 | `仲裁/宽限为零就悬置`（`Halted`） |
| 全部跳过 | `判定/全跳过不关机`、`判定/全跳过不睡眠`、`判定/全跳过照样发通知` |
| 依赖中止 | `判定/依赖中折算作失败`、`判定/依赖中止但勾了失败也跑`、`判定/依赖中止仍可打开程序` |
| 主动停止优先 | `判定/被停止优先于依赖中止` |

---

## 二 任务书二：关机 → 60 秒撤销 → 睡眠结果链

**原来错在哪**（三条，全在 `QueueService.CancelShutdown` 与 `AfterRunActionService.Execute`）：

1. `CancelShutdown` 第一行 `_shutdownPending = false;` —— **先清状态，再尝试 `shutdown /a`，还不看退出码**。
   撤销失败也照样提示「已取消关机，电脑继续运行」，然后按钮消失，用户再也没有入口。
2. 关机分支 `Process.Start(psi)` 之后立刻 `MarkShutdownPending()` + 报「60 秒后将关机」——
   `Process.Start` 返回非空只说明进程起来了，不代表关机被接受。
3. 睡眠 `SetSuspendState(false, false, false);` 返回值丢掉，失败也写「跑完动作：执行睡眠」。
4. 状态没有生命周期：外部 `shutdown /a`、倒计时走完、系统关机取消之后，按钮永远挂着。

**改成什么**：

- `CancelShutdown()` 从 `void` 变 `public static bool`，顺序反过来：
  ```csharp
  if (!_shutdownPending) return false;
  // 启动 shutdown /a，重定向输出，WaitForExit(5000) —— 有超时，否则一个卡住的
  // shutdown 会把 UI 线程拖死
  bool aborted = proc != null && proc.ExitCode == 0;
  ```
  成功才 `_shutdownPending = false` + 提示 + `NotifyShutdownStateChanged()`；失败**保留状态**，
  提示写明「倒计时可能还在，请再点一次或手动运行 shutdown /a」。
- `MarkShutdownPending()` 现在记一个 65 秒的截止时间（60 秒倒计时 + 5 秒余量），
  `IsShutdownPending` 变成 `_shutdownPending && DateTime.Now <= _shutdownPendingUntil`。
- 新增 `ExpireShutdownPendingIfStale()`，由 `RefreshQueueUi` 每次刷新时调；外部取消或倒计时走完之后
  按钮自己消失，不会永久失真（P2-3）。
- 新增 `NotifyShutdownStateChanged()`（`internal`，因为 `AfterRunActionService` 是独立类，
  调不到 `private` 的 `RaiseState`）。
- 关机分支：`using (Process? proc = …)` + `WaitForExit(5000)` + `ExitCode == 0` 才 `MarkShutdownPending()`；
  不接受就只写「关机失败：{detail}」+ 通知「该关机了，但没能排上（{detail}）。电脑还开着。」并 `return`。
- 睡眠分支：
  ```csharp
  if (!SetSuspendState(false, false, false))
  {
      int err = Marshal.GetLastWin32Error();
      LogService.LogEvent(err != 0 ? $"睡眠失败：系统拒绝（Win32 {err}）" : "睡眠失败：系统没有进入睡眠");
      NotifyService.Show(NotifyLevel.Warning, "龙管家", "要睡眠的时候系统没答应，电脑还醒着。");
  }
  ```
- 界面：`BtnQueueCancelShutdown_Click` 按返回值决定是否隐藏（`if (!QueueService.CancelShutdown()) { …; return; }`），
  不再无条件 `Collapsed`。
- **睡眠是否也要 60 秒撤销窗口 —— 未做**，任务书标了「需产品确认」，按原样保持立即执行，只修结果真实性。

---

## 三 任务书三：Webhook 监听 → 配置指引 → 实际绑定状态链

**原来错在哪**：`WebhookListener.Start` 返回 `void`，`bool boundWildcard` 是局部变量；调用方
`WebhookHub` 只能拿**请求参数** `_wildcard` 猜结果。于是通配绑定失败、静默退回回环之后，
`PanelAddress()` 仍然给出局域网 IP，界面照样提示「手机浏览器打开：192.168.x.x」。

**改成什么**：

- 新增两个 record（`Dragon Butler/WebhookListener.cs` 头部）：
  ```csharp
  public sealed record WebhookBindResult(bool Started, bool Wildcard, int Port, string Prefix, string Error);
  public sealed record WebhookParseResult(int Status, string Body, SentinelEvent? Event);
  ```
- `Start` 返回 `WebhookBindResult`；连回环都绑定失败时也返回 `NotStarted(port, 原因)` 并把
  `_listener = null; _running = false; _boundPrefix = ""`。成功时记下**实际前缀**：
  `_boundPrefix = boundWildcard ? $"http://+:{port}/" : $"http://127.0.0.1:{port}/"`，并暴露
  `public bool IsWildcardBound => _running && _boundPrefix.StartsWith("http://+:")`。
- `WebhookHub`：`_lastBind` 记实际结果，`Start` 返回 `WebhookBindResult`，`PanelAddress()` 改判
  `Listener.IsWildcardBound`（不是请求参数），未启动时返回空串；新增 `LastBind` 与 `IsLanAvailable`。
- 界面三处：
  - `LoadSettingsToUi` 里的 `TbEventUrl.Text = "http://127.0.0.1:{port}/webhook"` 改成 `RefreshEventUrl()`，
    未启动时显示「（监听没起来：原因）」，不再展示一个打不开的地址。
  - `TgLanPanel_Click` 按三种实际结果分支：**没起来** → 「监听没能启动（原因）。面板现在打不开，
    地址也不能复制给别人。」+ 提示端口可能被占；**退回回环** → 「只在你这台电脑上能用：{prefix}」
    + 给出 `netsh http add urlacl url=http://+:{port}/ user=Everyone`；**真通配** → 才给手机地址。
  - `RefreshApiRows` 里只有 `IsLanAvailable` 时才把提示写成「手机连同一个 Wi-Fi，浏览器打开：…」。
- 默认仍是 `127.0.0.1`；`wildcard` 只为局域网面板而开，**没有把默认监听扩大到 `0.0.0.0` 或通配**。
- **端口编辑 UI —— 未加**，任务书标了「需产品确认」，只修状态真实性。

---

## 四 任务书四：Webhook 报文接收 → 解析 → HTTP 应答链

**原来错在哪**：`Parse(string body)` 是 `private void`，五种失败（非法 JSON、非对象、缺 event、
未知事件、空报体）全都 `return`，Loop 里**无条件** `StatusCode = 200` 写 `"ok"`。
发送方拿不到任何区别，坏报文和成功事件长得一模一样。

**改成什么**：

- `Parse` 变成 `public static WebhookParseResult Parse(string? body)` —— **纯函数**，不碰队列、
  不碰计数器，探针可以直接断言（原先 `WebhookListener` 没有任何可测的公开解析入口）。

| 输入 | 状态码 | 正文 | 是否入队 |
|---|---|---|---|
| 合法已知事件 | 200 | `ok` | 是 |
| `null` / 空白 | 400 | `空报体` | 否 |
| 非法 JSON | 400 | `不是合法 JSON` | 否 |
| JSON 数组 / 字符串 / 数字 | 400 | `报体必须是 JSON 对象` | 否 |
| 缺 `event` 字段 | 400 | `缺少 event 字段` | 否 |
| 未知事件名 | 400 | `不认识的事件：{截断 40 字}…` | 否 |
| 非 POST | 405 | `只接受 POST` | 否 |

- 入队与计数从 `Parse` 移到 Loop（`if (parsed.Accepted) { _queue.Enqueue(parsed.Event!); … }`）。
- 新增 `WritePlain(ctx, status, text)` 统一写响应（`text/plain; charset=utf-8` + `Cache-Control: no-store`）。
- 拒绝正文限长：未知事件名截断到 40 字，实测响应体 22 字符 —— 不回显完整请求体。
- 事件白名单与 `CompletionSignalSources` 的二次过滤原样保留；JSON 异常仍在 `Parse` 内部消化，
  不会冒出 Loop 终止监听线程；异常隔离保持（`catch { if (!_running) break; }`）。

---

## 五 任务书五：通知发送 → 失败留痕 → 本地气泡回退链

**原来错在哪**：

1. 远程推送 fire-and-forget，一次失败只写一行日志，**没有任何重试**，也没有进消息中心。
2. `App.ShowBalloon` 返回 `void`：`_tray == null` 时静默 `return`，调用方永远不知道没弹出来。
3. `SendTestAsync` 的 Balloon 分支 `return (true, "")` —— 固定报成功。
4. Toast 降级到 Balloon 再失败，静默结束。

**改成什么**：

- `App.ShowBalloon` → `public bool`：`_tray == null` 返回 `false`；成功 `ShowBalloonTip` 后返回 `true`；
  异常记日志返回 `false`。跨线程分支用 `d.Invoke(new Func<bool>(…))` 而不是 `BeginInvoke`，
  调用方才能拿到结果。
- `NotifyService` 新增有限重试：`RetryDelaysMs = { 1000, 3000, 7000 }`（共 4 次尝试），
  `IsRetriable(err)` 过滤掉配置缺失（`尚未填写`）、不支持远程发送、`HTTP 4xx` 这类不该重试的。
  重试期间退出应用不会挂住：`StopPendingSends()` 在 `App.OnExit` 里取消 `RetryCts`。
- 最终失败写两处：运行日志「推送最终失败（渠道）：原因」+ `NoticeCenter.AddError("…推送没送到", …)`，
  消息中心里留着渠道、标题、原因（不新增任何重试配置项，次数与间隔写死在业务层）。
- 新增 `ShowBalloonOrRecord`：气泡没弹出来就记一条日志；`Show` 的 Toast 分支在
  Toast 与 Balloon **都失败**时才往消息中心写一条，不再静默。
- `SendTestAsync` 的 Balloon 分支返回真实结果：`shown ? (true, "") : (false, "气泡没弹出来：托盘图标可能还没建好，或系统把通知关掉了")`。
- `NotifyOnlyImportant` 过滤逻辑未动。

---

## 六 任务书六：Incident 采集 → 脱敏 → 完整性反馈链

**原来错在哪**（两类相反的故障同时存在）：

1. **该遮的没遮**：`WriteEnvironment` 把 `profile.ExecutablePath`、`profile.LaunchArguments` 明文写进
   `env.json`；`SettingsSnapshot` 的字段名匹配漏掉了 `NotifyEmailUser`、`NotifyTelegramChat`、`BgiPath`、
   `CustomBgImage`、`AfterRunAppPath`，以及全部工具路径。
2. **不该遮的遮了**：`SensitiveFragments` 里含 `webhook` / `email` / `key` 三个词，于是
   `BgiWebhookPort`、`NotifyEmailPort`、`HotkeyStart/Stop` 全被盖成 `***` —— 排障最需要的端口和热键没了。
3. **复杂对象退化成类型名**：`ToNode` 的 `default: return value.ToString();` 让 `Tools` 字典整段变成
   `"DragonButler.ToolProfile"`（P2-1）。
4. **完整性靠猜**：三个 `Write*` 全返回 `void`，`Capture` 一律记「现场包：已保存」，
   缺日志、缺截图、缺环境文件都看不出来。
5. `BitBlt` / `GetDIBits` 返回值不检查，失败缓冲区照样编码成「正常截图」；
   多显示器回退到主屏时没有任何标记（P2-4）。

**改成什么**（`Dragon Butler/IncidentBundle.cs` 293 → 552 行，整篇重写）：

- 新增 `public sealed record Piece(string File, bool Ok, string Detail)`，三个 `Write*` 全部返回 `Piece`，
  `Capture` 收集后分两段记日志：
  - 全成：`现场包：已保存 {reason} 的现场到 {dir}（log.txt（最后 200 行）、screen.png（…））`
  - 有缺：`现场包：部分保存 {reason} 的现场到 {dir}；采到：…；缺：screen.png（BitBlt 失败（Win32 5））`
  - `env.json` 里新增 `采集结果` 数组，逐项 `{文件, 采到, 说明}`。
- **先写一行日志再抄日志尾巴**：`Capture` 建目录后先 `LogService.LogEvent("现场包：开始采集（…）")`，
  否则「今天第一次失败」时日志文件还没建起来，`WriteLogTail` 空手而归（这条是探针 `有 log.txt` 第一次
  跑红之后才发现的）。
- **结构化脱敏**：`ToNode` 的 `default` 分支改成 `DescribeObject(value, depth + 1)`，用反射展开真实字段；
  字典的 **key 也过 `IsSensitiveKey`**；`MaxDepth = 6` 防环。
- **字段名判定的两道闸**：
  - `NonSensitiveKeys` 白名单（`BgiWebhookPort` / `NotifyEmailPort` / `HotkeyEnabled` / `HotkeyStart` /
    `HotkeyStop`）精确放行，先于包含匹配。
  - `SensitiveFragments` 扩充到 `token/secret/password/pass/key/auth/url/webhook/email/chat/user/host/path/dir/folder/argument/args/image`，
    补上原来漏掉的账号、路径、参数、聊天 ID。
- **值层面的分级处理**：
  - 看着像路径 → `RedactPath`，只留 `…\BetterGI.exe`。
  - 自由文本（`argument/args/customLogTexts/pattern`）→ `RedactText`：正则盖掉 `token=xxx`、`--password xxx`
    这类嵌在字符串里的秘密，同时盖掉参数里的绝对路径，**保留 `--startOneDragon` 这种结构**。
  - 其余敏感键 → 整体 `***`。
- **截图真实性**：新增 `ScreenshotMonitor.CaptureEx()` 返回
  `record ScreenCapture(byte[]? Buf, int W, int H, string Scope, bool GameWindow, string? Error)`：
  - `BitBlt` 返回 false → `Buf = null` + `Error = "BitBlt 失败（Win32 N）"`；
  - `GetDIBits` 返回 ≤ 0 → `Error = "GetDIBits 没取到像素"`；
  - `GetDC` / `CreateCompatibleDC` / `CreateCompatibleBitmap` 空 → 各自的原因；
  - 多显示器回退时 `Scope = "主屏 1920x1080（有 2 台显示器，没找到游戏窗口，其余屏幕没抓到）"`；
  - `WriteScreenshot` 写盘后再用 `new FileInfo(target).Length > 0` 复核，空文件也记为缺。
  - `Capture()` 保留为薄封装，`SnapshotService` 与心跳调用点不受影响。
- **顺序修正（P2-2）**：收尾总结 `summaryLine` 的日志改到 `IncidentBundle.Capture` **之前**，
  并且 Capture 成功后额外写一份 `queue-summary.txt` 进包里 —— 现场包从此自带「那一趟的结论」。

### 现场包实测（真跑出来的包体，`stall` 组产出）

```
log.txt            4000 B   最后 25 行
env.json           8046 B   含「采集结果」两项
queue-summary.txt   105 B   队列结束：0/1 成功（实际执行 1 项），耗时 不到 1 分钟，触发 Manual，停止=False
screen.png       278051 B   主屏 1920x1080（没找到游戏窗口）
```

`env.json` 抽样：`BgiWebhookPort: 18235`（保留）、`NotifyEmailPort: 465`（保留）、
`HotkeyStart: "Ctrl+Alt+R"`（保留）、`NotifyTelegramToken: ""`、
`失败工具` 段已是结构化真字段（`ToolId / DisplayName / ExecutablePath / LaunchArguments / GameProcessNames[] / … / CustomData{}`）。

---

## 七 P2 四项处置

| 项 | 处置 |
|---|---|
| P2-1 `ToolProfile` 退化成 `ToString()` 类型名 | 并入任务六：`ToNode` 的 `default` 改 `DescribeObject`，反射展开真字段 |
| P2-2 收尾日志与 `IncidentBundle.Capture` 的顺序 | 并入任务一 B：日志提前 + 额外写 `queue-summary.txt` 进包 |
| P2-3 `_shutdownPending` 到期/外部撤销后按钮失真 | 并入任务二：`_shutdownPendingUntil` + `ExpireShutdownPendingIfStale()` |
| P2-4 GDI 返回值检查与多显示器元数据 | 并入任务六：`CaptureEx()` 全链返回值检查 + `Scope` 元数据 |

---

## 八 验证

### 编译

- `dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release` → **0 个警告 0 个错误**
- `dotnet build "tests\CoordinatorProbe\CoordinatorProbe.csproj" -c Release` → **0 个警告 0 个错误**

### 探针（24 组全绿）

`schedule / resume / bundle / explain / retry / api / weekly / hotkey / progress / command / dag /
groups / bgiupdate / harvest / mcp / uirules / pager / morning / recap / toolgrid / logpattern /
migrate / afterrun / signals` → `ALL GREEN (24 groups)`

新增/改写的断言：

| 组 | 新增 | 说明 |
|---|---|---|
| `signals` | 8 条 | 真构造 `RunCoordinator` + `FakeSource` 喂信号，断言终态 |
| `afterrun` | 11 条 | `ShouldRun` 六参（全跳过/依赖中止/停止优先）+ 关机状态三条 |
| `api` | 22 条 | `WebhookListener.Parse` 状态码全表 + 绑定结果口径 |
| `bundle` | 约 30 条 | 脱敏纯函数、结构化输出、采集结果、截图元数据 |

### 顺手修的一处测试假绿

`morning` 组断言写死 `5400 + 6120` 秒，但 `DurationLedger` 是跨组共享的 `durations.json`，
前面几组跑完各记一笔，导致合计漂成 11524 → 失败。**这不是代码回归，是夹具不隔离**。
改用 `DurationLedger.PathOverride` 指向临时文件，跑完还原并删文件；注释写明这条断言问的是
「界面和推送数出同一个数字」，不是「今天这台机器一共跑了多少秒」。

### CI 新增三组闸门（`.github/workflows/build.yml`）

```
signals  = "v1.43 信号仲裁（崩溃不许当成功）"
afterrun = "v1.43 收尾授权（全跳过/依赖中止不许关机）"
migrate  = "v1.43 勾选单源化迁移"
```

理由写在注释里：这三组治的都是「会自己动手关机 / 把崩溃当成功」那类病，不能只靠人记得跑。

---

## 九 改动清单

**改（16 个文件，+1498 / −170）**

| 文件 | 变化 |
|---|---|
| `Dragon Butler/RunCoordinator.cs` | 249 → 351 行，信号仲裁重写 |
| `Dragon Butler/QueueService.cs` | 1625 → 1877 行，收尾事实 + 关机撤销睡眠 |
| `Dragon Butler/WebhookListener.cs` | 209 → 327 行，绑定结果 + 报文分类 |
| `Dragon Butler/WebhookHub.cs` | 339 → 367 行，实际绑定状态 |
| `Dragon Butler/NotifyService.cs` | 477 → 540 行，重试 + 留痕 + 真实结果 |
| `Dragon Butler/IncidentBundle.cs` | 293 → 552 行，整篇重写 |
| `Dragon Butler/ScreenshotMonitor.cs` | 300 → 358 行，`CaptureEx` |
| `Dragon Butler/App.xaml.cs` | 422 → 439 行，`ShowBalloon` 返回 bool + `StopPendingSends` |
| `Dragon Butler/MainWindow.xaml.cs` | +76 行，事件地址/局域网面板/取消关机按钮 |
| `Dragon Butler/Dragon Butler.csproj` | 版本 1.43.0 |
| `tests/CoordinatorProbe/Program.cs` | +299 行，四组新断言 |
| `.github/workflows/build.yml` | +5 行，三组进 CI |
| `README.md` / `README.en.md` / `RELEASE_NOTES.md` / `docs/使用说明.txt` | 文档同步 |

**新增**

- `Dragon Butler/AssemblyInfo.Probe.cs` —— `InternalsVisibleTo("CoordinatorProbe")`，
  让探针能直接调 `ScreenshotMonitor.CaptureEx`。主工程 csproj 是 GBK，不整体改写它。

---

## 十 待人工确认

1. **睡眠要不要也给 60 秒撤销窗口**（任务书二标「需产品确认」）。当前按原样立即执行，
   只修了结果真实性。
2. **要不要加端口编辑 UI**（任务书三标「需产品确认」）。当前只有只读地址框 + 复制按钮，
   端口占用时提示「换个端口再试」，但界面上没地方换。
3. **`IncidentBundle` 的 `Piece` 记录是公开类型**（`public sealed record`），如果不想暴露给外部，
   可以改 `internal`（探针已通过 `InternalsVisibleTo` 可见）。
4. **推送重试次数与间隔（1/3/7 秒）写死在业务层**，按任务书「不得新增重试次数、重试间隔等设置字段」的要求
   没有做成可配。若将来要做成可配，需另开任务。
