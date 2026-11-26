using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DragonButler;

/// <summary>队列触发来源。</summary>
public enum QueueTrigger
{
    /// <summary>用户点「一键开跑」/ 托盘启动。</summary>
    Manual = 0,

    /// <summary>定时计划触发（受度假模式与防打扰约束）。</summary>
    Timer = 1,

    /// <summary>哨兵崩溃后自动重启（不等待空闲）。</summary>
    Sentinel = 2,

    /// <summary>「接着跑」：只跑今天还没跑成的项（v1.6 / F2）。</summary>
    Continue = 3,
}

/// <summary>单个队列项的执行结果。</summary>
public sealed class QueueItemResult
{
    /// <summary>工具标识。</summary>
    public string ToolId { get; init; } = "";

    /// <summary>工具显示名。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>终态。</summary>
    public ToolRunState State { get; init; }

    /// <summary>给用户看的一句话原因（超时/异常退出…）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>实际尝试次数（含重试）。</summary>
    public int Attempts { get; init; }

    /// <summary>耗时。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>是否跳过（前置检查未通过，不计入成功/失败）。</summary>
    public bool Skipped { get; init; }
}

/// <summary>
/// 队列引擎：按勾选顺序依次执行工具，单工具经 <see cref="RunCoordinator"/> 仲裁完成。
/// 铁律：状态机无法确定时只允许 Halted + 通知，绝不猜测性推进。
/// </summary>
public static class QueueService
{
    private static readonly object Gate = new();
    private static bool _running;
    private static bool _stopRequested;
    private static RunCoordinator? _currentCoordinator;
    private static Process? _currentProcess;
    private static CancellationTokenSource? _cts;
    private static bool _shutdownPending;

    /// <summary>
    /// v1.43 / 任务二：待关机状态的有效期。
    ///
    /// <para>
    /// 只有布尔值的话，状态没法自己过期：用户在外面用 shutdown /a 撤了、
    /// 或者干脆让电脑关掉了，这个 true 会一直留着，界面上「取消关机」按钮永不消失——
    /// 点下去还是「已取消关机」，而其实早就没事了。
    /// </para>
    /// </summary>
    private static DateTime _shutdownPendingUntil;

    /// <summary>队列状态变化（开始/结束/换项），UI 刷新用。</summary>
    public static event Action? StateChanged;

    /// <summary>需要给用户的即时提示（已在 UI 线程）。</summary>
    public static event Action<string>? StatusChanged;

    /// <summary>是否正在跑队列。</summary>
    public static bool IsRunning { get { lock (Gate) return _running; } }

    /// <summary>当前正在执行的工具（空=无）。</summary>
    public static string CurrentToolId { get; private set; } = "";

    /// <summary>
    /// 当前工具的开始时刻（v1.13）。<see cref="CurrentToolId"/> 为空时为 default。
    /// 只给实时面板算「已跑多久」用，不参与任何判定。
    /// </summary>
    public static DateTime CurrentToolStartedAt { get; private set; }

    /// <summary>是否处于"等待电脑空闲"状态。</summary>
    public static bool IsWaitingIdle { get; internal set; }

    /// <summary>是否已排定关机（60 秒倒计时内）。</summary>
    public static bool IsShutdownPending
    {
        get
        {
            lock (Gate) return _shutdownPending && DateTime.Now <= _shutdownPendingUntil;
        }
    }

    /// <summary>最近一次队列结果（UI 展示用）。</summary>
    public static List<QueueItemResult> LastResults { get; } = new();

    /// <summary>等待电脑空闲时，用户点「立即开始」；返回 true 表示已请求。</summary>
    public static void SkipIdleWait() => IdleGuard.RequestSkip();

    /// <summary>
    /// 取消已排定的关机（关机倒计时内可用）。
    ///
    /// <para>
    /// v1.43 / 任务二：返回「取消到底成没成」，而且**先确认成功再清状态**。
    /// 原来的写法是先把 _shutdownPending 抹掉再去叫 shutdown /a，失败了也不回头，
    /// 于是界面上按钮消失、用户以为取消了，60 秒后电脑照关——这比不做还糟。
    /// </para>
    /// <para>
    /// shutdown /a 在没有倒计时时会返回非 0（「没有进行中的关机」），这也是真实结果，
    /// 但它说明「此刻确实没有待办关机」——所以这种情况照样清状态，只是文案不同。
    /// </para>
    /// </summary>
    /// <returns>取消命令被系统接受（或本来就没有待办关机）返回 true。</returns>
    public static bool CancelShutdown()
    {
        if (!_shutdownPending)
        {
            // 状态本来就是干净的，不用叫命令，也不用提示。
            return false;
        }

        bool aborted;
        string detail;
        try
        {
            var psi = new ProcessStartInfo("shutdown", "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using Process? proc = Process.Start(psi);
            if (proc == null)
            {
                detail = "系统没有返回进程";
                aborted = false;
            }
            else
            {
                // 有超时，否则一个卡住的 shutdown 会把 UI 线程拖死。
                if (!proc.WaitForExit(5000))
                {
                    detail = "shutdown /a 5 秒没有返回";
                    aborted = false;
                }
                else
                {
                    aborted = proc.ExitCode == 0;
                    detail = aborted ? "" : $"shutdown /a 退出码 {proc.ExitCode}";
                }
            }
        }
        catch (Exception ex)
        {
            aborted = false;
            detail = ex.Message;
        }

        if (aborted)
        {
            lock (Gate) _shutdownPending = false;
            LogService.LogEvent("已取消关机倒计时（shutdown /a）");
            NotifyService.Show(NotifyLevel.Info, "龙管家", "已取消关机，电脑继续运行。");
            NotifyShutdownStateChanged();
            return true;
        }

        // 失败：状态和按钮都留着，让人还能再点一次、还能手动处理。
        LogService.LogEvent($"取消关机失败：{detail}");
        NotifyService.Show(NotifyLevel.Warning, "龙管家", $"没能取消关机（{detail}）。倒计时可能还在，请再点一次或手动运行 shutdown /a。");
        return false;
    }

    /// <summary>测试用：覆盖工具间隔（null=按设置）。</summary>
    public static TimeSpan? IntervalOverride { get; set; }

    // ===== v1.41 / M3-7：「跳过这次」 =====

    /// <summary>
    /// 这一次不想跑的工具有哪些（工具 id）。只活到这一趟队列结束——
    /// 它和 <see cref="QueueEntry.Enabled"/> 是两件事：后者是把这项从队列里关掉，
    /// 得人手动开回来；这里只是「这一趟先别跑它」，下一趟定时照旧。
    /// </summary>
    private static readonly HashSet<string> _skipOnce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个工具是否被标成「这次不跑」。</summary>
    public static bool IsSkippedOnce(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return false;
        lock (Gate) return _skipOnce.Contains(toolId);
    }

    /// <summary>翻转「这次不跑」。返回翻转之后是不是「不跑」。</summary>
    public static bool ToggleSkipOnce(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return false;
        lock (Gate)
        {
            if (_skipOnce.Contains(toolId)) { _skipOnce.Remove(toolId); return false; }
            _skipOnce.Add(toolId);
            return true;
        }
    }

    /// <summary>队列跑完就把「这次不跑」清空——它是给这一趟用的，不跨趟。</summary>
    private static void ClearSkipOnce()
    {
        lock (Gate) _skipOnce.Clear();
    }

    // 先占住队列，再撤掉上轮的收尾；撤销失败时不能让工具进程开始跑。
    private static bool TryBeginRun()
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _stopRequested = false;
            CurrentToolId = "";
            CurrentToolStartedAt = default;
            _cts = new CancellationTokenSource();
        }

        bool started = false;
        try
        {
            if (!WrapUpCountdown.TryCancelForNewRun() || _cts.IsCancellationRequested) return false;
            LastResults.Clear();
            RaiseState();
            started = true;
            return true;
        }
        finally
        {
            if (!started) EndRun();
        }
    }

    private static void EndRun()
    {
        lock (Gate)
        {
            _running = false;
            _stopRequested = false;
            _currentCoordinator = null;
            _currentProcess = null;
            CurrentToolId = "";
            CurrentToolStartedAt = default;
            IsWaitingIdle = false;
            _cts?.Dispose();
            _cts = null;
        }
        RaiseState();
        AppState.RefreshStatus();
    }

    /// <summary>启动整条队列。true 表示队列结束（含被停止），false 表示未开跑或执行链异常。</summary>
    public static Task<bool> StartAsync(QueueTrigger trigger) => StartCoreAsync(trigger, isAutomaticRerun: false);

    internal static Task<bool> StartRerunAsync() => StartCoreAsync(QueueTrigger.Manual, isAutomaticRerun: true);

    private static async Task<bool> StartCoreAsync(QueueTrigger trigger, bool isAutomaticRerun)
    {
        if (!TryBeginRun()) return false;

        try
        {
            // v1.38：这里原来是「度假模式生效，定时触发一律不跑」。那个开关删了——
            // 想临时不跑，把定时关掉就是了；多一个名字说同一件事，
            // 只会让人在「我明明关了定时」和「它怎么又不跑」之间来回找原因。

            var items = BuildItems(trigger);
            if (items.Count == 0)
            {
                if (trigger == QueueTrigger.Timer)
                {
                    bool hasEnabled = (AppState.Settings.Queue ?? new List<QueueEntry>()).Any(e => e.Enabled);
                    if (hasEnabled)
                    {
                        // v1.5 按日计划：勾了工具但今天这一天空着 → 不算异常，跳过并说明
                        LogService.LogEvent("队列：今日计划为空，已跳过定时触发");
                        NotifyService.Show(NotifyLevel.Warning, "龙管家", "今日计划为空，已跳过定时触发。");
                    }
                    else
                    {
                        LogService.LogEvent("队列：定时触发但队列为空");
                        NotifyService.Show(NotifyLevel.Warning, "龙管家", "定时触发但队列为空，请先勾选要运行的工具。");
                    }
                }
                else
                {
                    NotifyService.Show(NotifyLevel.Warning, "龙管家", "队列为空：请先勾选要运行的工具。");
                }
                return false;
            }

            // v1.9 / P2：开跑前看一眼 BGI 版本有没有变过——变了就先说一句，别让用户去猜配置
            AnnounceBgiVersion();

            await RunListAsync(items, trigger, _cts!.Token, isAutomaticRerun).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("队列异常：" + ex);
            // v1.28：整条队列炸掉以前只落在日志里，用户第二天只会觉得「今晚没跑」。
            // 这条必须进消息中心——它是「今晚白等了」最直接的原因。
            NoticeCenter.AddCrash("队列没能跑完", ex.ToString(), title: "队列整个中断了");
            return false;
        }
        finally { EndRun(); }
    }

    /// <summary>
    /// 用一个显式的 profile 列表跑队列（测试入口：不读 settings 的勾选）。
    /// 返回实际执行（含跳过）的项数。
    /// </summary>
    public static async Task<int> RunProfilesAsync(IReadOnlyList<ToolProfile> profiles, QueueTrigger trigger)
    {
        if (!TryBeginRun()) return 0;
        try
        {
            return await RunListAsync(profiles.ToList(), trigger, _cts!.Token).ConfigureAwait(false);
        }
        finally { EndRun(); }
    }

    // ===== v1.6 / F2「接着跑」 =====

    /// <summary>
    /// 今天「已经跑成的队列项」——注意只看今天、只看 Done。
    /// 数据源是 <see cref="RunJournal"/>（按 ToolId 记），不是 GroupArchive（按 BGI 组名记），
    /// 因为「接着跑」要判断的是队列项，不是组。
    /// </summary>
    public static HashSet<string> DoneToolIdsToday() => RunJournal.DoneToolsToday();

    /// <summary>今天还没跑成的队列项（保持队列顺序，已停用的不算）。</summary>
    public static List<ToolProfile> UnfinishedToday()
    {
        var list = new List<ToolProfile>();
        foreach (var entry in AppState.Settings.Queue ?? new List<QueueEntry>())
        {
            if (!entry.Enabled) continue;
            var p = ProfileRegistry.Get(entry.ToolId);
            if (string.Equals(p.ToolId, entry.ToolId, StringComparison.OrdinalIgnoreCase))
                list.Add(p);
        }
        return FilterUnfinished(list);
    }

    /// <summary>
    /// 从给定列表里挑出「今天还没跑成的」——纯函数，不读 settings，
    /// 这样 probe 能拿任意列表断言，不用改动用户真实队列配置。
    /// </summary>
    public static List<ToolProfile> FilterUnfinished(IEnumerable<ToolProfile> items)
    {
        var done = DoneToolIdsToday();
        var list = new List<ToolProfile>();
        foreach (var p in items)
        {
            if (done.Contains(p.ToolId)) continue;
            list.Add(p);
        }
        return list;
    }

    /// <summary>今天是否有可「接着跑」的项（界面与托盘用它决定按钮/菜单可见）。</summary>
    public static bool HasUnfinishedToday() => UnfinishedToday().Count > 0;

    /// <summary>
    /// 接着跑（v1.6 / F2）：只跑今天还没跑成的项，已跑成的不重复跑。
    /// 与 <see cref="StartAsync"/> 的区别只有「挑哪些项」，执行链路完全复用它，
    /// 所以悬置/挂起/失败策略/间隔/防打扰的行为都不会跑偏。
    /// </summary>
    public static async Task<bool> StartContinueAsync()
    {
        if (!TryBeginRun()) return false;

        try
        {
            var items = UnfinishedToday();
            if (items.Count == 0)
            {
                LogService.LogEvent("队列：接着跑——今天已经没有未完成的项");
                NotifyService.Show(NotifyLevel.Info, "龙管家", "今天要跑的都已经跑完了，没有可接着跑的项目。");
                return false;
            }

            LogService.LogEvent($"队列：接着跑，今天还有 {items.Count} 项没完成（{string.Join("、", items.Select(p => p.DisplayName))}）");
            await RunListAsync(items, QueueTrigger.Continue, _cts!.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("队列异常（接着跑）：" + ex);
            return false;
        }
        finally { EndRun(); }
    }

    /// <summary>队列主体循环（StartAsync / RunProfilesAsync 共用）。</summary>
    private static async Task<int> RunListAsync(List<ToolProfile> items, QueueTrigger trigger, CancellationToken ct,
        bool isAutomaticRerun = false)
    {
        KeepAwakeService.Acquire();
        try
        {
            // v1.20 / 奇思妙想 #7：开跑前记一次窗口位置，跑完才好挪回去。
            // 记的是「跑之前在哪」，所以必须在任何工具启动之前。
            try
            {
                if (AppState.Settings.TidyEnabled && AppState.Settings.TidyRestoreWindows)
                {
                    foreach (string name in TidyService.GameProcessNames)
                    {
                        var spot = TidyService.Capture(name);
                        if (spot != null) TidyService.Remember(spot.ProcessName, spot.Title,
                            spot.Left, spot.Top, spot.Width, spot.Height);
                    }
                    var bgiSpot = TidyService.Capture("BetterGenshinImpact");
                    if (bgiSpot != null) TidyService.Remember(bgiSpot.ProcessName, bgiSpot.Title,
                        bgiSpot.Left, bgiSpot.Top, bgiSpot.Width, bgiSpot.Height);
                }
            }
            catch { }

            int finished = 0;
            // v1.43 / 任务一：「依赖没跑成 → 整队中止」也是一种「这一趟没跑完」的事实。
            // 原来这里只 break，什么都不留，收尾那边看到的是「跑过的都成功了」，
            // 于是 AnyFailed=false → 关机照做。事实必须带出去。
            string? dependencyAbort = null;
            // v1.9 / P2：这一轮里成没成，依赖门只认本轮结果 + 今天台账（跨轮次也能接着跑）
            var doneThisRun = new HashSet<string>(RunJournal.DoneToolsToday(), StringComparer.OrdinalIgnoreCase);
            var failedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i++)
            {
                if (_stopRequested) break;
                var profile = items[i];

                // v1.41 / M3-7：右键点了「跳过这次」。放在最前面判断，
                // 连 CurrentToolId 都不改——界面上不该闪一下「运行中」再说跳过。
                // 也不写台账：跳过不算跑过，下次接着跑仍要尝试（同 RunJournal 的立场）。
                if (IsSkippedOnce(profile.ToolId))
                {
                    LogService.LogEvent($"队列：{profile.DisplayName} 被标为「这次不跑」，跳过");
                    DecisionLog.Add(DecisionSource.Queue, $"跳过 {profile.DisplayName}", "你在桌面上点了「这次不跑」");
                    LastResults.Add(new QueueItemResult
                    {
                        ToolId = profile.ToolId,
                        DisplayName = profile.DisplayName,
                        State = ToolRunState.Idle,
                        Reason = "这次不跑",
                        Skipped = true,
                    });
                    RaiseState();
                    continue;
                }

                CurrentToolId = profile.ToolId;
                CurrentToolStartedAt = DateTime.Now;
                RaiseState();

                // v1.9 / P2：依赖门。排在前面的依赖没跑成 → 这一项不跑，说清等谁。
                var entry = FindEntry(profile.ToolId);
                var gate = QueueGraph.Gate(entry, doneThisRun, failedThisRun);
                if (gate.Verdict != GateVerdict.Ready)
                {
                    bool abort = gate.Verdict == GateVerdict.Blocked
                                 && QueueGraph.ShouldAbortOnDependencyFail(entry);
                    LogService.LogEvent($"队列：{profile.DisplayName} 依赖未满足（{gate.Reason}）"
                        + (abort ? "，按设置中止整队" : "，跳过这一项"));
                    // v1.20：这句话同时进「为什么」台账——用户看不到日志，但会问「它怎么没跑」。
                    DecisionLog.Add(DecisionSource.Queue,
                        abort ? $"跳过 {profile.DisplayName}，整队停下" : $"跳过 {profile.DisplayName}",
                        gate.Reason);
                    LastResults.Add(new QueueItemResult
                    {
                        ToolId = profile.ToolId,
                        DisplayName = profile.DisplayName,
                        State = ToolRunState.Idle,
                        Reason = gate.Reason,
                        Skipped = true,
                    });
                    RaiseState();
                    if (abort)
                    {
                        // 中止原因要能在收尾时说清：谁、等谁、没等到。
                        dependencyAbort = $"{profile.DisplayName} 的依赖没跑成（{gate.Reason}）";
                        break;
                    }
                    continue;
                }

                // 防打扰只约束定时触发；接着跑等同手动，不等空闲
                if (trigger == QueueTrigger.Timer)
                {
                    bool canStart = await IdleGuard.WaitAsync(ct).ConfigureAwait(false);
                    if (!canStart || _stopRequested) break;
                }

                var pre = PreCheck(profile, trigger);
                if (pre.Skip)
                {
                    DecisionLog.Add(DecisionSource.Queue, $"跳过 {profile.DisplayName}", pre.Message);
                    LastResults.Add(new QueueItemResult
                    {
                        ToolId = profile.ToolId,
                        DisplayName = profile.DisplayName,
                        State = ToolRunState.Idle,
                        Reason = pre.Message,
                        Skipped = true,
                    });
                    // v1.9 / P2：本轮被前置检查挡掉＝本轮不会再有结果，依赖它的一项不该干等
                    failedThisRun.Add(profile.ToolId);
                    RaiseState();
                    continue;
                }

                var result = await RunItemAsync(profile, trigger, ct).ConfigureAwait(false);
                LastResults.Add(result);
                finished++;
                RaiseState();

                // v1.9 / P2：把本轮结果记进依赖门的账
                if (result.State == ToolRunState.Done) doneThisRun.Add(profile.ToolId);
                else if (result.State is ToolRunState.Failed or ToolRunState.Timeout
                         or ToolRunState.Stalled or ToolRunState.Halted)
                    failedThisRun.Add(profile.ToolId);

                if (result.State == ToolRunState.Halted)
                {
                    // 悬置：队列立刻停止，等用户处理，绝不自动推进
                    LogService.LogEvent($"队列：{profile.DisplayName} 悬置，队列停止");
                    DecisionLog.Add(DecisionSource.Queue, "整队停下",
                        $"{profile.DisplayName} 状态不明（悬置），不敢替你继续往下跑");
                    break;
                }

                if (result.State == ToolRunState.Stalled)
                    LogService.LogEvent($"队列：疑似挂起判定（自动终止={(AppState.Settings.StallAutoKill ? "开" : "关")}）");
                if (result.State == ToolRunState.Stalled && !AppState.Settings.StallAutoKill)
                {
                    // 疑似挂起：未开自动终止时队列停止，进程保留等用户处理。
                    LogService.LogEvent($"队列：{profile.DisplayName} 疑似挂起，队列停止");
                    DecisionLog.Add(DecisionSource.Queue, "整队停下",
                        $"{profile.DisplayName} 疑似卡住，而「自动终止」没开，留给你处理");
                    break;
                }

                bool failed = result.State is ToolRunState.Failed or ToolRunState.Timeout
                    or ToolRunState.Stalled;
                if (failed && string.Equals(AppState.Settings.FailStrategy, "Abort", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.LogEvent($"队列：失败策略=中止，剩余 {items.Count - i - 1} 项不再执行");
                    DecisionLog.Add(DecisionSource.Queue, "后面的不跑了",
                        $"{profile.DisplayName} 没跑成，而你选的是「一项失败就整队停」");
                    break;
                }

                if (i < items.Count - 1 && !_stopRequested)
                {
                    var wait = IntervalOverride ?? TimeSpan.FromMinutes(Math.Max(0, AppState.Settings.QueueIntervalMinutes));
                    if (wait > TimeSpan.Zero)
                    {
                        SetStatus($"下一项将在 {FormatMinutes(wait)} 后开始…");
                        try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }

            FinishQueue(trigger, finished, dependencyAbort, isAutomaticRerun);
            return finished;
        }
        finally
        {
            KeepAwakeService.Release();
        }
    }

    /// <summary>
    /// 只跑一个工具（哨兵崩溃重启用）：不排队、不等待间隔，但同样走前置检查与状态机。
    /// </summary>
    public static async Task<bool> StartSingleAsync(ToolProfile profile, QueueTrigger trigger)
    {
        if (!TryBeginRun()) return false;

        try
        {
            var pre = PreCheck(profile, trigger);
            if (pre.Skip) return false;

            CurrentToolId = profile.ToolId;
            CurrentToolStartedAt = DateTime.Now;
            RaiseState();
            var result = await RunItemAsync(profile, trigger, _cts!.Token).ConfigureAwait(false);
            LastResults.Add(result);
            return result.State == ToolRunState.Done;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("单工具执行异常：" + ex);
            return false;
        }
        finally { EndRun(); }
    }

    /// <summary>
    /// 哨兵检测到疑似挂起时调用：状态机转 Stalled；按设置决定是否结束进程。
    /// 队列由 RunListAsync 看到 Stalled 后停止。
    /// </summary>
    internal static void HandleStall(ToolProfile profile, string reason)
    {
        RunCoordinator? coord;
        Process? proc;
        lock (Gate)
        {
            coord = _currentCoordinator;
            proc = _currentProcess;
        }
        if (coord == null) return;
        if (!string.Equals(QueueService.CurrentToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase)) return;

        bool autoKill = AppState.Settings.StallAutoKill;
        LogService.LogEvent($"队列：{profile.DisplayName} {reason}（自动终止={(autoKill ? "开" : "关")}）");

        // v1.6 / F3：挂起正是最需要现场的场合——进程还活着，但什么都不做。
        // 这份包要在杀进程之前抓，否则截到的是桌面而不是卡住的那个画面。
        try { IncidentBundle.Capture($"队列挂起：{profile.DisplayName}（{reason}）", profile); }
        catch { }

        coord.MarkStalled(reason);

        if (autoKill)
        {
            try
            {
                if (proc is { HasExited: false })
                {
                    int pid = SafePid(proc);
                    proc.Kill();
                    proc.WaitForExit(3000);
                    LogService.LogEvent($"队列：疑似挂起，已结束 {profile.ManagedProcessName} pid={pid}");
                }
            }
            catch (Exception ex)
            {
                LogService.LogEvent("队列：结束疑似挂起进程失败 " + ex.Message);
            }
        }
    }

    /// <summary>
    /// v1.44 / 修复 #4（D19）：把关机请求交给系统，并确认它真的被接受了。
    ///
    /// <para>
    /// 必须在发出去之后等它退出、按退出码判断——<c>Process.Start</c> 返回非空只说明
    /// 进程起来了，不代表系统接受了这次请求（被策略拦、被别的程序占着都会失败）。
    /// 没接受就压根没有倒计时，这时候弹「60 秒后关机」就是骗人。
    /// </para>
    /// </summary>
    /// <param name="detail">失败原因（成功时为空串）。</param>
    internal static bool RequestSystemShutdown(int seconds, out string detail)
    {
        try
        {
            var psi = new ProcessStartInfo("shutdown", $"/s /t {seconds}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? proc = Process.Start(psi);
            if (proc == null)
            {
                detail = "系统没有返回进程";
                return false;
            }
            if (!proc.WaitForExit(5000))
            {
                detail = "shutdown 5 秒没有返回";
                return false;
            }
            if (proc.ExitCode != 0)
            {
                detail = $"shutdown 退出码 {proc.ExitCode}";
                return false;
            }
            detail = "";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>关机已排定（供取消按钮判断）。</summary>
    internal static void MarkShutdownPending(int seconds)
    {
        lock (Gate)
        {
            _shutdownPending = true;
            // 倒计时结束后系统还有少量时间启动关机；复用原来的 5 秒余量。
            _shutdownPendingUntil = DateTime.Now.AddSeconds(seconds + 5);
        }
        NotifyShutdownStateChanged();
    }

    /// <summary>
    /// v1.43 / 任务二 / P2-3：把过期的待关机状态抹掉（界面每次刷新时顺手调一次）。
    ///
    /// <para>
    /// 外部程序执行了 shutdown /a、或者倒计时走完了，这里都读不出来——
    /// 但两种情况都能从「时间过了」推出来。到点就清，按钮不会永远留在那儿。
    /// </para>
    /// </summary>
    /// <returns>状态是否发生了变化。</returns>
    public static bool ExpireShutdownPendingIfStale()
    {
        lock (Gate)
        {
            if (!_shutdownPending || DateTime.Now <= _shutdownPendingUntil) return false;
            _shutdownPending = false;
        }
        LogService.LogEvent("待关机状态已过期（倒计时结束或被外部取消），界面复位");
        NotifyShutdownStateChanged();
        return true;
    }

    /// <summary>
    /// v1.43 / 任务二：待关机状态变了就通知界面重算一次。
    ///
    /// <para>
    /// 「取消关机」按钮的可见性完全跟着 IsShutdownPending 走。不主动通知的话，
    /// 关机排上之后按钮要等到下一次别的状态刷新才出现——那可能是几十秒后，
    /// 而倒计时只有 60 秒。
    /// </para>
    /// </summary>
    internal static void NotifyShutdownStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    /// <summary>请求停止当前队列（运行中工具按"用户中止"处理并结束进程）。</summary>
    public static void Stop()
    {
        if (!IsRunning) return;
        _stopRequested = true;
        try { _cts?.Cancel(); } catch { }
        try { _currentCoordinator?.Abort("用户停止队列"); } catch { }
        try
        {
            if (_currentProcess is { HasExited: false })
            {
                _currentProcess.Kill();
                LogService.LogEvent($"队列：用户停止，已结束进程 pid={SafePid(_currentProcess)}");
            }
        }
        catch { }
        LogService.LogEvent("队列：收到停止请求");
    }

    /// <summary>
    /// 队列中的条目 → 已配置可执行的 profile 列表（保持顺序）。
    /// v1.5：定时触发只取"今天这一位为 1"的条目；手动触发不看按日计划（手动永远跑当前勾选）。
    /// </summary>
    private static List<ToolProfile> BuildItems(QueueTrigger trigger)
    {
        int todayBit = WeekdayBit(DateTime.Now);
        var list = new List<ToolProfile>();
        int skippedByPlan = 0;

        foreach (var entry in AppState.Settings.Queue ?? new List<QueueEntry>())
        {
            if (!entry.Enabled) continue;
            if (trigger == QueueTrigger.Timer && (entry.WeekdayMask & (1 << todayBit)) == 0)
            {
                skippedByPlan++;
                continue;
            }
            var p = ProfileRegistry.Get(entry.ToolId);
            if (string.Equals(p.ToolId, entry.ToolId, StringComparison.OrdinalIgnoreCase))
                list.Add(p);
        }

        if (trigger == QueueTrigger.Timer && skippedByPlan > 0)
            LogService.LogEvent($"队列：今日计划命中 {list.Count} 项，按计划跳过 {skippedByPlan} 项");

        // v1.9 / P2：有人写了依赖就按拓扑序重排——被依赖的必须先跑，同层保持用户拖出来的顺序。
        var entries = AppState.Settings.Queue ?? new List<QueueEntry>();
        if (QueueGraph.HasDependencies(entries))
        {
            var orderedIds = QueueGraph.Order(entries)
                .Select(e => e.ToolId)
                .ToList();
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < orderedIds.Count; i++) rank.TryAdd(orderedIds[i], i);
            list = list
                .OrderBy(p => rank.TryGetValue(p.ToolId, out int r) ? r : int.MaxValue)
                .ToList();
            LogService.LogEvent($"队列：按依赖排序 → {string.Join(" → ", list.Select(p => p.DisplayName))}");
        }

        return list;
    }

    /// <summary>今天星期对应 <see cref="QueueEntry.WeekdayMask"/> 的位号：周一=0 … 周日=6。</summary>
    public static int WeekdayBit(DateTime day) => ((int)day.DayOfWeek + 6) % 7;

    /// <summary>按工具 ID 找队列项（依赖门要读它的声明）；找不到返回 null。</summary>
    public static QueueEntry? FindEntry(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return null;
        return (AppState.Settings.Queue ?? new List<QueueEntry>())
            .FirstOrDefault(e => string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>今天按计划会执行哪些工具（UI 摘要用；停用的不算）。</summary>
    public static List<QueueEntry> PlanForToday(DateTime day)
    {
        int bit = WeekdayBit(day);
        return (AppState.Settings.Queue ?? new List<QueueEntry>())
            .Where(e => e.Enabled && (e.WeekdayMask & (1 << bit)) != 0)
            .ToList();
    }

    /// <summary>按日计划是否被改过（不是"每天全选"）。</summary>
    public static bool HasCustomPlan()
        => (AppState.Settings.Queue ?? new List<QueueEntry>()).Any(e => (e.WeekdayMask & 0x7F) != 0x7F);

    /// <summary>单个工具执行（含一次重试）。</summary>
    public static async Task<QueueItemResult> RunItemAsync(ToolProfile profile, QueueTrigger trigger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // v1.40：记下开始的时刻，耗时账本靠它去 GroupArchive 里挑「这一趟开过哪些配置组」。
        // 放在秒表旁边是因为两者必须是同一个瞬间——差几秒就会把上一项的组算到这一项头上。
        DateTime startedAt = DateTime.Now;
        int attempts = 0;
        int maxAttempts = 1 + Math.Max(0, profile.RetryCount);
        QueueItemResult last = new()
        {
            ToolId = profile.ToolId,
            DisplayName = profile.DisplayName,
            State = ToolRunState.Failed,
            Reason = "未执行",
        };

        while (attempts < maxAttempts)
        {
            attempts++;
            var result = await RunOnceAsync(profile, trigger, attempts, ct).ConfigureAwait(false);
            last = new QueueItemResult
            {
                ToolId = profile.ToolId,
                DisplayName = profile.DisplayName,
                State = result.State,
                Reason = result.Reason,
                Attempts = attempts,
                Elapsed = sw.Elapsed,
                Skipped = false,
            };

            if (result.State is ToolRunState.Done or ToolRunState.Halted or ToolRunState.Stalled) break;
            if (ct.IsCancellationRequested) break;

            if (attempts < maxAttempts)
            {
                // v1.6 / F5：等多久由档案决定，并可逐次翻倍——固定 3 秒硬撞对慢机器没意义
                TimeSpan wait = RetryDelayFor(profile, attempts);
                LogService.LogEvent($"队列：{profile.DisplayName} 第 {attempts} 次失败（{result.Reason}），{DescribeWait(wait)}后重试");
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        if (last.State is ToolRunState.Failed or ToolRunState.Timeout)
        {
            NotifyService.Show(NotifyLevel.Warning, "龙管家", $"{profile.DisplayName} 未完成：{last.Reason}");
            // v1.28：跑失败这件事以前只有一句气泡，飘过去就没了。留在消息中心，
            // 第二天早上打开就能看见是哪一项没成、为什么。
            string whyBody = last.State switch
            {
                ToolRunState.Timeout => "等它跑完的时间超过了档案里写的上限，管家中途收手了。"
                                        + "如果是第一次这样，多半是那一局比平时长；连着这样就要调一下上限。",
                _ => "它自己退出了，但没有给出「我跑完了」的信号——可能是崩了，也可能是被别的程序关掉了。",
            };
            NoticeCenter.AddError(
                $"{profile.DisplayName} 没跑成",
                (string.IsNullOrWhiteSpace(last.Reason) ? whyBody : last.Reason + "\n" + whyBody),
                $"state={last.State} attempts={last.Attempts} elapsed={last.Elapsed:mm\\:ss} reason={last.Reason}");
        }

        // v1.6 / F2：把终态记进台账，「接着跑」靠它判断今天哪些项还没成
        RunJournal.Record(last);

        // v1.40：同一时刻给耗时账本记一笔。两台账各答各的问题——
        // RunJournal 记「最后成没成」，DurationLedger 记「花了多久、哪个配置组最费时间」。
        // 放在 RunJournal 之后而不是之前：那两行先落盘，即使这里抛异常也还有结果可查。
        try
        {
            DurationLedger.Record(profile.ToolId, profile.DisplayName, last.Elapsed, startedAt);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("记耗时失败（不影响这次运行）：" + ex.Message);
        }

        // v1.9 / P2：只有真跑成了才更新版本基线——更新坏掉的那一版不该被当成"正常"
        if (last.State == ToolRunState.Done
            && string.Equals(profile.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase)
            && BgiVersionGuard.Remember(BgiVersionGuard.ReadVersion(AppState.Settings.BgiPath)))
        {
            AppState.SaveSettings();
            LogService.LogEvent("BGI 版本：已记录 " + AppState.Settings.LastBgiVersion);
        }

        return last;
    }

    /// <summary>
    /// v1.6 / F5：重试等多久。基线由档案给，开着退避就逐次翻倍；
    /// 封顶 300 秒——再长就不如让用户自己决定要不要等，硬拖着反而像卡死。
    /// </summary>
    internal static TimeSpan RetryDelayFor(ToolProfile profile, int attempts)
    {
        return RetryDelayCore(profile.RetryDelaySeconds, profile.RetryBackoff, attempts);
    }

    /// <summary>
    /// v1.6 / F5：纯函数版本，探针直接断言这一层（不起进程、不读配置）。
    /// </summary>
    public static TimeSpan RetryDelayCore(int baseSeconds, bool backoff, int attempts)
    {
        baseSeconds = Math.Max(0, baseSeconds);
        if (!backoff) return TimeSpan.FromSeconds(baseSeconds);
        int doublings = Math.Max(0, attempts - 1);
        // 先封顶指数再乘，避免 attempts 很大时溢出
        long seconds = baseSeconds;
        for (int i = 0; i < doublings && seconds < MaxRetryDelaySeconds; i++) seconds *= 2;
        if (seconds > MaxRetryDelaySeconds) seconds = MaxRetryDelaySeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    public const int MaxRetryDelaySeconds = 300;

    /// <summary>v1.6 / F5：把等待时长说成人话，写进日志和通知。</summary>
    public static string DescribeWait(TimeSpan t)
    {        if (t.TotalSeconds <= 0) return "立刻";
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} 秒";
        double minutes = t.TotalMinutes;
        return minutes % 1 == 0 ? $"{(int)minutes} 分钟" : $"{minutes:F1} 分钟";
    }

    private sealed record SingleOutcome(ToolRunState State, string Reason);

    /// <summary>执行一次（不重试）：起进程 → 状态机 → 等终态。</summary>
    private static async Task<SingleOutcome> RunOnceAsync(ToolProfile profile, QueueTrigger trigger, int attempt, CancellationToken ct)
    {
        string args = BuildLaunchArguments(profile);
        // v1.6 / F4：每一轮都从干净的证据开始，别把上一轮的错误行带过来
        RunEvidence.Reset();
        var coord = ToolRunner.CreateCoordinator(profile, msg =>
            NotifyService.Show(NotifyLevel.Warning, "龙管家", msg));

        bool anchorHit = false;
        coord.Transition += (from, to, reason) =>
        {
            if (to == ToolRunState.Resolving && reason.Contains("LogPattern", StringComparison.Ordinal)) anchorHit = true;
        };

        lock (Gate) _currentCoordinator = coord;
        try
        {
            coord.BeginLaunch();
            Process? proc;
            try
            {
                proc = ToolRunner.StartProcess(profile, args);
            }
            catch (Exception ex)
            {
                coord.LaunchFailed(ex.Message);
                return new SingleOutcome(ToolRunState.Failed, "启动失败（请确认路径正确、已允许管理员权限）");
            }

            if (proc == null)
            {
                coord.LaunchFailed("Process.Start 返回 null");
                return new SingleOutcome(ToolRunState.Failed, "启动失败（系统未返回进程）");
            }

            lock (Gate) _currentProcess = proc;
            LogService.LogEvent($"队列：启动 {profile.DisplayName}（第 {attempt} 次，来源 {trigger}）参数：{args}");
            coord.LaunchSucceeded(proc, new ToolRunContext { Profile = profile, Process = proc, Arguments = args });
            SentinelService.NotifyRunStarted(profile);

            while (!coord.IsFinished)
            {
                if (ct.IsCancellationRequested || _stopRequested)
                {
                    coord.Abort("用户停止队列");
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    break;
                }
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { coord.Abort("用户停止队列"); break; }
            }

            var state = coord.State;
            int? exitCode = null;
            try { if (proc.HasExited) exitCode = proc.ExitCode; } catch { }

            string reason = state switch
            {
                ToolRunState.Done => "已完成",
                ToolRunState.Failed => "异常退出",
                ToolRunState.Timeout => $"超时 {profile.MaxDurationMinutes} 分钟未完成",
                ToolRunState.Halted => "报告完成但进程未退出",
                ToolRunState.Stalled => $"疑似挂起（{SentinelService.StallThreshold.TotalMinutes:F0} 分钟无任何活动）",
                _ => "未完成",
            };

            // v1.6 / F4：把「为什么」攒进 reason——退出码与最后一条错误日志行。
            // Done 不改（成功通知里塞错误行只会让人以为出了问题）。
            if (state != ToolRunState.Done)
                reason = RunEvidence.Describe(reason, exitCode, RunEvidence.LastErrorLine);

            if (state == ToolRunState.Timeout)
            {
                // §2.2 防漂移：超时且全程没有命中完成锚点 → 只写 WARNING，不打扰用户
                if (!anchorHit && profile.PrimarySignal == CompletionSignalKind.LogPattern)
                {
                    LogService.LogEvent("WARNING 完成锚点未命中，请检查 BGI 版本是否更新");
                }
                KillCurrent(profile, "超时清理");
            }
            else if (state == ToolRunState.Failed)
            {
                KillCurrent(profile, "失败清理");
            }
            else if (state == ToolRunState.Done && profile.PrimarySignal == CompletionSignalKind.Duration)
            {
                // “到达时长”是到时收尾：进程通常还活着，主动结束，避免占着下一个工具
                KillCurrent(profile, "到达时长清理");
            }

            return new SingleOutcome(state, reason);
        }
        finally
        {
            SentinelService.NotifyRunEnded(profile);
            try { coord.Dispose(); } catch { }
            lock (Gate)
            {
                _currentCoordinator = null;
                _currentProcess = null;
            }
        }
    }

    /// <summary>启动参数：BGI 勾选组优先走 --startGroups，其余用模板。</summary>
    private static string BuildLaunchArguments(ToolProfile profile)
    {
        if (string.Equals(profile.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase))
        {
            // v1.9 / P2：本项自己写了组就跑那几个，没写才用全局勾选（空＝交给配置名/默认流程）
            // v1.15：中间的兜底从「全局勾选」改成「配置组合勾选，没有组合再用全局勾选」。
            // 优先级 = 本项切片 > 配置组合 > 全局勾选；越具体越优先，和用户改动的顺序一致。
            // v1.42 / M9-2（D16 单点化）：勾选只有一份事实源——本项自己的 QueueEntry.Groups。
            // 配置组合是「记下来的一套」，仍然优先；再往下就是「不指定，交给配置名」。
            // 不再回落到旧的全局 SelectedGroups：那份已 deprecated，没有其他存活读取点。
            var own = FindEntry(profile.ToolId)?.Groups;
            var presetGroups = ActivePresetGroups();
            var groups = GroupSlicing.Effective(own, presetGroups, SafeKnownGroups());
            if (groups.Count > 0)
                return GroupSlicing.StartGroupsArgument(groups);
            return ToolRunner.BuildArguments(profile, ActiveDragonConfigName());
        }
        return ToolRunner.BuildArguments(profile, null);
    }

    /// <summary>
    /// v1.42 / M9-2：这个工具今晚实际会跑哪几组配置组。
    ///
    /// <para>
    /// 存在的理由只有一个：「今晚要跑」那一行必须和真正传出去的命令行同源。
    /// 各算一遍迟早会分叉——摘要说 3 组、实际只传了 2 组，这种错没人能一眼看出来。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> EffectiveGroupsFor(string toolId)
    {
        try
        {
            if (!string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();
            var own = FindEntry(toolId)?.Groups;
            return GroupSlicing.Effective(own, ActivePresetGroups(), SafeKnownGroups());
        }
        catch { return Array.Empty<string>(); }
    }
    /// <summary>
    /// v1.15：这次开跑用哪个一条龙配置名。
    /// 装了配置组合就跟着组合走，没装或组合已被删就退回全局设置——
    /// 退回是静默的（只记日志），因为让「组合被删了」变成一次开跑失败毫无意义。
    /// </summary>
    private static string ActiveDragonConfigName()
    {
        try
        {
            string id = AppState.Settings.ActivePresetId ?? "";
            if (string.IsNullOrWhiteSpace(id)) return AppState.Settings.DragonConfigName;

            var preset = ConfigPresets.FindById(AppState.Settings.Presets, id);
            if (preset == null)
            {
                LogService.LogEvent("配置组合已不存在，本次回退到全局设置：" + id);
                return AppState.Settings.DragonConfigName;
            }
            return ConfigPresets.Expand(preset).DragonConfigName;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("读取配置组合失败，回退到全局设置：" + ex.Message);
            return AppState.Settings.DragonConfigName;
        }
    }

    /// <summary>
    /// v1.15：这次开跑用哪几个配置组。组合里没勾组时返回空——空的意思是「不指定，交给 BGI 默认流程」，
    /// 而不是「一个组都不跑」。
    /// </summary>
    private static List<string> ActivePresetGroups()
    {
        try
        {
            var preset = ConfigPresets.FindById(AppState.Settings.Presets, AppState.Settings.ActivePresetId);
            return preset == null
                ? new List<string>()
                : ConfigPresets.Expand(preset).Groups.ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 当前 BGI 里真实存在的配置组名；没识别到就返回空集合
    /// （空集合＝不做过滤，别把用户写的组名当错的丢掉）。
    /// </summary>
    private static IReadOnlyList<string> SafeKnownGroups()
    {
        try
        {
            var scan = BgiDiscovery.Scan();
            return scan.Ok ? scan.Groups : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// v1.9 / P2：开跑前报告 BGI 版本变化（只报一次，不重复刷屏）。
    /// 读不到版本时保持沉默——不知道就别乱报，这是这条功能唯一的失败模式。
    /// </summary>
    private static void AnnounceBgiVersion()
    {
        try
        {
            var (verdict, _, message) = BgiVersionGuard.Preflight();
            if (verdict is not (VersionVerdict.Changed or VersionVerdict.FirstSeen)) return;
            if (message.Length == 0) return;
            LogService.LogEvent("BGI 版本：" + message);
            // 只有「变了」才值得打断用户；第一次记录只写日志
            if (verdict == VersionVerdict.Changed)
                NotifyService.Show(NotifyLevel.Info, "龙管家 · 版本变化", message);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("BGI 版本检查失败：" + ex.Message);
        }
    }

    private static void KillCurrent(ToolProfile profile, string why)
    {
        try
        {
            foreach (var p in ToolRunner.GetSessionProcesses(profile))
            {
                try
                {
                    int pid = SafePid(p);
                    bool exited = p.HasExited;
                    if (!exited)
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    LogService.LogEvent($"队列：{why} {profile.ManagedProcessName} pid={pid} exited={exited}");
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    /// <summary>前置检查：游戏冲突 / 残留进程。</summary>
    private static (bool Skip, string Message) PreCheck(ToolProfile profile, QueueTrigger trigger)
    {
        // 1) 关联游戏在跑：一律不杀，提示后跳过
        foreach (string game in profile.GameProcessNames ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(game)) continue;
            if (ProcessTools.CountSessionProcesses(game) > 0)
            {
                string msg = "检测到游戏客户端正在运行，与队列冲突，请先关闭";
                LogService.LogEvent($"队列：{profile.DisplayName} 前置检查未通过（游戏进程 {game} 在运行）");
                NotifyService.Show(NotifyLevel.Warning, "龙管家", msg);
                return (true, msg);
            }
        }

        // 2) 工具自身残留进程
        if (ToolRunner.IsRunning(profile))
        {
            bool autoKill = trigger != QueueTrigger.Manual && AppState.Settings.AutoKillResidual;
            if (autoKill)
            {
                foreach (var p in ToolRunner.GetSessionProcesses(profile))
                {
                    try
                    {
                        int pid = SafePid(p);
                        if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); }
                        LogService.LogEvent($"队列：清理残留 {profile.ManagedProcessName} pid={pid}");
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                NotifyService.Show(NotifyLevel.Info, "龙管家", "已清理上次残留进程");
            }
            else
            {
                string msg = $"检测到 {profile.DisplayName} 已在运行，本次已跳过";
                LogService.LogEvent("队列：" + msg);
                NotifyService.Show(NotifyLevel.Warning, "龙管家", msg);
                return (true, msg);
            }
        }

        return (false, "");
    }

    /// <summary>
    /// 收尾：汇总通知 + 跑完动作。
    ///
    /// <para>
    /// v1.43 / 任务一：收尾要拿到的不是「跑过的那几项怎么样」，而是「这一趟到底怎么样」。
    /// 原来只看 ran（= 非跳过项），于是两种真实情况被抹平了：
    /// 全部跳过会变成 total=0 / 没有失败，依赖门中止会变成「跑过的都成功了」。
    /// 两者都会让睡眠/关机的授权判定以为「一切正常」。
    /// </para>
    /// </summary>
    /// <param name="dependencyAbort">依赖没跑成导致整队中止时的原因；没有中止为 null。</param>
    private static void FinishQueue(QueueTrigger trigger, int finished, string? dependencyAbort, bool isAutomaticRerun)
    {
        var ran = LastResults.Where(r => !r.Skipped).ToList();
        int total = ran.Count;
        int ok = ran.Count(r => r.State == ToolRunState.Done);
        bool anyHalted = ran.Any(r => r.State == ToolRunState.Halted);
        bool anyStalled = ran.Any(r => r.State == ToolRunState.Stalled);
        // v1.43 / 任务一：依赖门中止也是失败——它意味着「该跑的没跑」。
        bool dependencyFailed = dependencyAbort != null;
        bool anyFailed = dependencyFailed || ran.Any(r => r.State is ToolRunState.Failed or ToolRunState.Timeout
            or ToolRunState.Halted or ToolRunState.Stalled);
        // 一个工具都没实际执行：全是「这次不跑」或被前置检查挡掉。
        bool nothingRan = total == 0 && !dependencyFailed;
        TimeSpan elapsed = TimeSpan.FromSeconds(ran.Sum(r => r.Elapsed.TotalSeconds));

        // v1.43 / P2-2：总结先落日志，再打现场包。
        // 原来的顺序是先 Capture 再写「队列结束」，于是最该看的那一行永远进不了包。
        string summaryLine = $"队列结束：{ok}/{total} 成功（实际执行 {finished} 项），耗时 {FormatDuration(elapsed)}，触发 {trigger}，停止={_stopRequested}"
            + (dependencyFailed ? $"，依赖中止：{dependencyAbort}"
                : nothingRan ? "，没有可执行项" : "");
        LogService.LogEvent(summaryLine);

        // v1.6 / F3：有事才留现场——成功时打一份包只是往硬盘扔垃圾。
        // 先打包再发通知，这样通知正文里能直接给出文件夹路径。
        string bundleNote = "";
        if (anyFailed)
        {
            var blamed = ran.FirstOrDefault(r => r.State is ToolRunState.Failed or ToolRunState.Timeout
                or ToolRunState.Halted or ToolRunState.Stalled);
            ToolProfile? blamedProfile = null;
            try
            {
                if (blamed != null) blamedProfile = ProfileRegistry.Get(blamed.ToolId);
            }
            catch { }
            // 依赖中止时 blamed 是 null（中止那一项本身记为 Skipped），原因在 dependencyAbort 里。
            string why = dependencyAbort ?? (blamed == null ? "队列失败" : $"{blamed.DisplayName} {blamed.State}（{blamed.Reason}）");
            string? dir = IncidentBundle.Capture("队列结束：" + why, blamedProfile);
            if (!string.IsNullOrEmpty(dir))
            {
                bundleNote = " 现场：" + dir;
                // P2-2：把最终队列总结作为独立证据写进包。
                // 只靠 log.txt 的尾巴不够——收尾这几行常常刚好被后来的日志挤出去。
                try { File.WriteAllText(Path.Combine(dir, "queue-summary.txt"), summaryLine); } catch { }
            }
        }

        string text;
        NotifyLevel level = NotifyLevel.Warning;
        if (anyHalted)
        {
            var h = ran.First(r => r.State == ToolRunState.Halted);
            text = $"【龙管家】「{h.DisplayName}」这次没法确认结果，队列已暂停等你处理。原因：{h.Reason}";
        }
        else if (anyStalled && !AppState.Settings.StallAutoKill)
        {
            var st = ran.First(r => r.State == ToolRunState.Stalled);
            text = $"【龙管家】{st.DisplayName} 疑似挂起（{SentinelService.StallThreshold.TotalMinutes:F0} 分钟无任何活动），队列已暂停等你处理。原因：{st.Reason}";
        }
        else if (anyStalled && AppState.Settings.StallAutoKill)
        {
            var st = ran.First(r => r.State == ToolRunState.Stalled);
            text = $"【龙管家】队列结束：{ok}/{total} 成功。⚠ {st.DisplayName} 疑似挂起，已自动终止并继续。原因：{st.Reason}";
        }
        else if (dependencyFailed)
        {
            // 中止不是「跑完了」：说清停在哪、为什么停、还差什么。
            text = total == 0
                ? $"【龙管家】队列停在中途：{dependencyAbort}。一项都没跑成，今晚不算完成。"
                : $"【龙管家】队列停在中途：{dependencyAbort}。前面 {ok}/{total} 项成功，剩下的没跑，今晚不算完成。";
        }
        else if (nothingRan)
        {
            // 全部被跳过（这次不跑 / 前置检查挡掉）：一项都没执行，不是「跑完了」。
            text = "【龙管家】队列结束：没有可执行项，今晚一个都没跑。";
        }
        else if (ok == total)
        {
            text = $"【龙管家】今晚队列完成：{ok}/{total} 项成功，总耗时 {FormatDuration(elapsed)}。晚安。";
            level = NotifyLevel.Info;
        }
        else if (ok == 0)
        {
            var first = ran.First(r => r.State != ToolRunState.Done);
            text = total == 1
                ? $"【龙管家】{first.DisplayName} 未完成。原因：{first.Reason}"
                : $"【龙管家】队列结束：0/{total} 成功。⚠ 全部未完成。第一项 {first.DisplayName} 的原因：{first.Reason}";
        }
        else
        {
            var f = ran.First(r => r.State != ToolRunState.Done);
            text = $"【龙管家】队列结束：{ok}/{total} 成功。⚠ {f.DisplayName}未完成（{f.Reason}），已跳过。";
        }

        // v1.10 / G1：收益账本。Take() 会把本轮累计清零——
        // 不清零的话下一条队列会把这一趟的收益再报一遍。
        string harvestNote = "";
        try
        {
            var harvest = HarvestLedger.Take();
            string mine = HarvestLedger.Describe(harvest);
            if (mine.Length > 0)
            {
                harvestNote = " " + mine + "。";
                var month = HarvestLedger.GetMonth();
                if (month.Mora > harvest.Mora)
                    harvestNote += $"本月累计 {HarvestLedger.FormatAmount(month.Mora)}摩拉。";
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("收益账本汇总失败：" + ex.Message);
        }

        // 失败类通知带上现场包路径：人在外面看到这句话，才知道该去哪看
        if (bundleNote.Length > 0) text += bundleNote;
        text += harvestNote;

        // v1.15 / L6：用户自己写了模板就用他的，没写就用上面这段内置正文。
        // 只替换一句正文，标题、级别、跑完动作都不动——改正文不该牵出别的事。
        string custom = NotifyTemplate.Render(
            NotifyTemplate.OneLine(AppState.Settings.NotifyQueueTemplate),
            BuildTemplateValues(ok, total, elapsed, ran, bundleNote, harvestNote, dependencyAbort));
        if (custom.Length > 0) text = custom;

        NotifyService.Show(level, "龙管家", text);

        // v1.8 / F14：跑完说一句话。念的是给人听的一句口语，不是通知正文——
        // 通知里那些【】、路径、退出码念出来没法听。默认关，见 AppSettings.SpeechEnabled。
        try
        {
            if (nothingRan) SpeechService.Speak("队列结束了，今晚没有可执行项。");
            else if (dependencyFailed) SpeechService.Speak("队列停在中途，有一项依赖没跑成。", 30);
            else if (ok == total && total > 0)
                SpeechService.Speak($"一条龙跑完了，{total} 项全部成功，用时 {FormatDuration(elapsed)}。");
            else if (total > 0)
                SpeechService.Speak($"一条龙跑完了，{total} 项里成功 {ok} 项，有失败。", 30);
        }
        catch { }

        // 记录到设置（界面"上次执行"用）
        try
        {
            AppState.Settings.LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            AppState.Settings.LastRunSeconds = (int)elapsed.TotalSeconds;
            // 依赖中止/一项没跑都不算「上次跑成功了」。
            AppState.Settings.LastRunOk = ok == total && total > 0 && !dependencyFailed;
            AppState.SaveSettings();
        }
        catch { }

        // v1.20 / 奇思妙想 #7：收拾必须在跑完动作**之前**——那条链最后可能是关机，
        // 关机之后才收拾等于什么都没做。任何失败都不冒泡（收拾是收尾动作）。
        try
        {
            var tidy = TidyService.PlanFromSettings(anyFailed);
            string did = TidyService.Execute(tidy);
            if (did.Length > 0) SetStatus(did);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("收拾失败：" + ex.Message);
        }

        AfterRunActionService.Execute(new AfterRunContext
        {
            Total = total,
            Success = ok,
            HasFailure = anyFailed,
            Stopped = _stopRequested,
            NothingRan = nothingRan,
            DependencyAbortReason = dependencyAbort,
            IsAutomaticRerun = isAutomaticRerun,
        });

        // v1.41 / M3-7：「这次不跑」只活一趟，队列结束就清空。
        ClearSkipOnce();
    }

    /// <summary>
    /// v1.15 / L6：给通知模板填值。
    /// 「原因」取第一项没跑成的原因，「现场」「收益」沿用内置正文那两段（带前导空格，
    /// 用户把它们放在句子中间读起来也不会粘在一起）。
    ///
    /// <para>
    /// v1.43 / 任务一：「结果」不再只分成功/有失败——依赖中止、一项没跑都是单独一类，
    /// 用户模板里那句「{结果}」得能看出这一趟到底怎么了。
    /// </para>
    /// </summary>
    private static Dictionary<string, string> BuildTemplateValues(
        int ok, int total, TimeSpan elapsed, List<QueueItemResult> ran, string bundleNote, string harvestNote,
        string? dependencyAbort)
    {
        string reason = dependencyAbort ?? "";
        if (reason.Length == 0)
        {
            var bad = ran.FirstOrDefault(r => r.State != ToolRunState.Done);
            if (bad != null) reason = bad.DisplayName + " " + bad.Reason;
        }

        string result;
        if (dependencyAbort != null) result = "中止";
        else if (total == 0) result = "没跑";
        else result = ok == total ? "成功" : "有失败";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["结果"] = result,
            ["成功"] = ok.ToString(),
            ["总数"] = total.ToString(),
            ["耗时"] = FormatDuration(elapsed),
            ["原因"] = reason,
            ["现场"] = bundleNote.Trim(),
            ["收益"] = harvestNote.Trim(),
        };
    }

    // tidy：耗时文案与 MorningReportService.FormatDuration 是同一条规则，这里不再自留一份。
    private static string FormatDuration(TimeSpan t) => MorningReportService.FormatDuration(t);

    private static string FormatMinutes(TimeSpan t)
        => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分钟" : $"{t.TotalSeconds:F0} 秒";

    private static void SetStatus(string text)
    {
        try { StatusChanged?.Invoke(text); } catch { }
    }

    private static void RaiseState()
    {
        try { StateChanged?.Invoke(); } catch { }
        try { AppState.RefreshStatus(); } catch { }
    }
}

/// <summary>
/// 防打扰：定时触发时若用户正在用电脑，则推迟到空闲后再开始。
/// 判定用 GetLastInputInfo（键鼠最后一次输入距今 &lt; 5 分钟 = 有人在用）。
/// </summary>
public static class IdleGuard
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>超过这个时长没有任何键鼠输入 → 视为空闲。</summary>
    public static TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>每次推迟的时长（测试可调小）。</summary>
    public static TimeSpan PostponeDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>累计推迟到该时长后发一次升级提醒。</summary>
    public static TimeSpan EscalateAfter { get; set; } = TimeSpan.FromHours(2);

    /// <summary>测试注入：非 null 时直接使用该空闲时长（跳过真实键鼠检测）。</summary>
    public static TimeSpan? IdleOverride { get; set; }

    private static volatile bool _skipRequested;

    /// <summary>距离最后一次键鼠输入的时长。</summary>
    public static TimeSpan IdleFor
    {
        get
        {
            if (IdleOverride.HasValue) return IdleOverride.Value;
            try
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
                uint now = (uint)Environment.TickCount;
                uint idleMs = unchecked(now - info.dwTime);
                return TimeSpan.FromMilliseconds(idleMs);
            }
            catch { return TimeSpan.Zero; }
        }
    }

    /// <summary>用户点「立即开始」：跳过本轮等待。</summary>
    public static void RequestSkip() => _skipRequested = true;

    /// <summary>
    /// 等到电脑空闲。返回 false 表示被取消（用户停止 / 设置关闭后不等待）。
    /// </summary>
    public static async Task<bool> WaitAsync(CancellationToken ct)
    {
        _skipRequested = false;
        if (!AppState.Settings.IdleGuardEnabled) return true;
        if (IdleFor >= IdleThreshold) return true;

        LogService.LogEvent($"防打扰：检测到用户正在用电脑（最近输入 {IdleFor.TotalSeconds:F0} 秒前），推迟 {PostponeDelay.TotalMinutes:F0} 分钟");
        DecisionLog.Add(DecisionSource.Schedule, "先不开始",
            $"你 {IdleFor.TotalSeconds:F0} 秒前还在动键鼠，等 {PostponeDelay.TotalMinutes:F0} 分钟再看");
        QueueService.IsWaitingIdle = true;
        NotifyService.Show(NotifyLevel.Info, "龙管家",
            "定时任务等待电脑空闲中，空闲后自动开始（可在队列卡点「立即开始」跳过等待）。");

        var waited = TimeSpan.Zero;
        bool escalated = false;
        while (true)
        {
            if (ct.IsCancellationRequested) { QueueService.IsWaitingIdle = false; return false; }
            if (_skipRequested)
            {
                LogService.LogEvent("防打扰：用户选择立即开始");
                QueueService.IsWaitingIdle = false;
                return true;
            }

            try { await Task.Delay(PostponeDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { QueueService.IsWaitingIdle = false; return false; }

            waited += PostponeDelay;
            if (IdleFor >= IdleThreshold)
            {
                QueueService.IsWaitingIdle = false;
                LogService.LogEvent($"防打扰：已空闲 {IdleFor.TotalMinutes:F1} 分钟，开始执行");
                return true;
            }
            if (!escalated && waited >= EscalateAfter)
            {
                escalated = true;
                NotifyService.Show(NotifyLevel.Warning, "龙管家",
                    "定时任务已等待超过 2 小时，电脑一直有人使用；仍会继续等待，也可以点「立即开始」直接跑。");
            }
        }
    }
}

/// <summary>跑完动作的输入参数。</summary>
public sealed class AfterRunContext
{
    /// <summary>实际执行项数。</summary>
    public int Total { get; init; }

    /// <summary>成功项数。</summary>
    public int Success { get; init; }

    /// <summary>是否有失败/超时/悬置。</summary>
    public bool HasFailure { get; init; }

    /// <summary>是否被用户停止。</summary>
    public bool Stopped { get; init; }

    /// <summary>
    /// v1.43 / 任务一：这一趟一个工具都没实际执行（全被跳过）。
    ///
    /// <para>
    /// 光看 Total=0 分不出「今晚没事可做」和「整队被依赖门中止」——后者是失败。
    /// 两种要分开，否则没跑会被当成跑完了。
    /// </para>
    /// </summary>
    public bool NothingRan { get; init; }

    /// <summary>依赖没跑成导致整队中止时的原因；没中止则为 null。</summary>
    public string? DependencyAbortReason { get; init; }

    /// <summary>由「再跑一轮」发起的这一轮，不再触发下一轮。</summary>
    internal bool IsAutomaticRerun { get; init; }
}

/// <summary>
/// 跑完动作：仅通知（默认）/ 睡眠 / 关机。
/// 安全默认：队列有失败项时不执行破坏性动作（除非设置里显式勾选"失败也执行"）；
/// 验收期 DRYRUN 只写日志。
/// </summary>
public static class AfterRunActionService
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>
    /// v1.15 / L5：跑完能不能做这件事。
    ///
    /// <para>
    /// 关机、睡眠是破坏性动作，队列有失败就一律不做；但「再跑一轮」和「打开某个程序」
    /// 不是破坏性动作，失败时也可以做——比如跑挂了之后把日志工具拉起来，正是最需要的时候。
    /// </para>
    /// <para>纯函数，便于断言。</para>
    /// </summary>
    public static bool ShouldRun(string? action, bool hasFailure, bool stopped, bool runOnFailure)
        => ShouldRun(action, hasFailure, stopped, runOnFailure, nothingRan: false, dependencyAbort: false);

    /// <summary>
    /// v1.43 / 任务一：把「这一趟到底跑没跑成」带进授权判定。
    ///
    /// <para>
    /// 多两个参数，是因为原来只有「有没有失败」一维，判不出下面两种：
    /// 全部被跳过（一个都没跑）、依赖门中止（跑了前面、后面没跑）。
    /// 前者不该关机，后者还该算失败。
    /// </para>
    /// </summary>
    public static bool ShouldRun(string? action, bool hasFailure, bool stopped, bool runOnFailure,
        bool nothingRan, bool dependencyAbort)
    {
        string a = action ?? "Notify";
        if (string.Equals(a, "Notify", StringComparison.OrdinalIgnoreCase)) return false;
        if (stopped) return false;
        // v1.43 / 任务一：一个工具都没实际跑过 → 打开程序、再跑一轮照做，睡眠/关机一律不做。
        // 「今晚没事可做」不是关机理由，更不在「失败后仍执行」的授权范围里。
        if (nothingRan && IsDestructive(a)) return false;
        if (dependencyAbort) hasFailure = true;
        if (IsDestructive(a) && hasFailure && !runOnFailure) return false;
        return true;
    }

    /// <summary>会造成「东西被关掉」的动作，失败时要让用户自己决定等不等。纯函数。</summary>
    public static bool IsDestructive(string? action)
        => string.Equals(action, "Sleep", StringComparison.OrdinalIgnoreCase)
           || string.Equals(action, "Shutdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// v1.44 / 修复 #4：尝试进入睡眠，并把系统给的失败原因说出来。
    ///
    /// <para>
    /// 单独抽出来是为了让倒计时执行体（<see cref="WrapUpCountdown"/>）能复用同一段判定，
    /// 而不是在那边再抄一份 <c>SetSuspendState</c> 的调用与错误处理。
    /// </para>
    /// </summary>
    /// <param name="detail">失败原因（成功时为空串）。</param>
    /// <returns>系统是否真的进入了睡眠。</returns>
    internal static bool TrySleep(out string detail)
    {
        try
        {
            if (SetSuspendState(false, false, false))
            {
                detail = "";
                return true;
            }
            int err = Marshal.GetLastWin32Error();
            detail = err != 0 ? $"系统拒绝（Win32 {err}）" : "系统没有进入睡眠";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>执行跑完动作。</summary>
    public static void Execute(AfterRunContext ctx)
    {
        string action = AppState.Settings.AfterRunAction ?? "Notify";
        if (!ShouldRun(action, ctx.HasFailure, ctx.Stopped, AppState.Settings.AfterRunOnFailure,
            ctx.NothingRan, ctx.DependencyAbortReason != null))
        {
            if (!string.Equals(action, "Notify", StringComparison.OrdinalIgnoreCase))
            {
                string reason = ctx.Stopped ? "队列被用户停止"
                    : ctx.NothingRan ? "队列没有执行任何工具" : "队列有失败项";
                LogService.LogEvent($"跑完动作：{reason}，跳过 {action}");
            }
            return;
        }

        if (string.Equals(action, "OpenApp", StringComparison.OrdinalIgnoreCase))
        {
            OpenApp(AppState.Settings.AfterRunAppPath);
            return;
        }

        if (string.Equals(action, "RunAgain", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.IsAutomaticRerun) return;

            // v1.15 / L5：再跑一轮。只在「上一轮一个都没失败」时才做，
            // 因为失败的组再跑一次大概率还是一样地失败，白白多烧一小时。
            if (ctx.HasFailure)
            {
                LogService.LogEvent("跑完动作：上一轮有失败项，不自动再跑一轮");
                return;
            }
            try
            {
                LogService.LogEvent("跑完动作：再跑一轮");
                NotifyService.Show(NotifyLevel.Info, "龙管家", "队列跑完了，接着再来一轮。");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    try { await QueueService.StartRerunAsync().ConfigureAwait(false); }
                    catch (Exception ex) { LogService.LogEvent("再跑一轮失败：" + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                LogService.LogEvent("再跑一轮失败：" + ex.Message);
            }
            return;
        }

        bool dryRun = AppState.Settings.AfterRunDryRun;
        if (string.Equals(action, "Sleep", StringComparison.OrdinalIgnoreCase))
        {
            if (dryRun)
            {
                LogService.LogEvent("DRYRUN: 将执行睡眠");
                return;
            }
            // v1.44 / 修复 #4（D19）：不再立刻睡。60 秒倒计时，人能反悔。
            WrapUpCountdown.Start(WrapUpKind.Sleep);
            return;
        }

        if (string.Equals(action, "Shutdown", StringComparison.OrdinalIgnoreCase))
        {
            if (dryRun)
            {
                LogService.LogEvent("DRYRUN: 将执行关机");
                return;
            }
            // v1.44 / 修复 #4（D19）：60 秒窗口。关机走系统级倒计时（进程被杀也照样关），
            // 睡眠走进程内倒计时（Windows 没有「延迟睡眠 + 可中止」的命令）。
            WrapUpCountdown.Start(WrapUpKind.Shutdown);
        }
    }

    /// <summary>
    /// v1.15 / L5：跑完后顺手把另一个程序拉起来（典型用法：跑完打开游戏的启动器、或打开日志工具）。
    ///
    /// <para>
    /// 只接受已存在的绝对路径——按名字找程序在 Windows 上没有可靠做法，猜错了就是打开了别的东西。
    /// 路径不存在就只写日志，不去猜、也不弹窗打断。
    /// </para>
    /// </summary>
    private static void OpenApp(string? path)
    {
        string p = (path ?? "").Trim();
        if (p.Length == 0)
        {
            LogService.LogEvent("跑完动作：OpenApp 但没填路径，跳过");
            return;
        }
        if (!File.Exists(p))
        {
            LogService.LogEvent($"跑完动作：要打开的程序不存在，跳过（{p}）");
            return;
        }
        try
        {
            LogService.LogEvent($"跑完动作：打开程序 {p}");
            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开程序失败：" + ex.Message);
        }
    }
}
