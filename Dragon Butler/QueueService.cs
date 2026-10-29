using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>队列状态变化（开始/结束/换项），UI 刷新用。</summary>
    public static event Action? StateChanged;

    /// <summary>需要给用户的即时提示（已在 UI 线程）。</summary>
    public static event Action<string>? StatusChanged;

    /// <summary>是否正在跑队列。</summary>
    public static bool IsRunning { get { lock (Gate) return _running; } }

    /// <summary>当前正在执行的工具（空=无）。</summary>
    public static string CurrentToolId { get; private set; } = "";

    /// <summary>是否处于"等待电脑空闲"状态。</summary>
    public static bool IsWaitingIdle { get; internal set; }

    /// <summary>是否已排定关机（60 秒倒计时内）。</summary>
    public static bool IsShutdownPending { get { lock (Gate) return _shutdownPending; } }

    /// <summary>最近一次队列结果（UI 展示用）。</summary>
    public static List<QueueItemResult> LastResults { get; } = new();

    /// <summary>等待电脑空闲时，用户点「立即开始」；返回 true 表示已请求。</summary>
    public static void SkipIdleWait() => IdleGuard.RequestSkip();

    /// <summary>取消已排定的关机（关机倒计时内可用）。</summary>
    public static void CancelShutdown()
    {
        if (!_shutdownPending) return;
        _shutdownPending = false;
        try
        {
            var psi = new ProcessStartInfo("shutdown", "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            LogService.LogEvent("已取消关机倒计时（shutdown /a）");
            NotifyService.Show(NotifyLevel.Info, "龙管家", "已取消关机，电脑继续运行。");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("取消关机失败：" + ex.Message);
        }
    }

    /// <summary>测试用：覆盖工具间隔（null=按设置）。</summary>
    public static TimeSpan? IntervalOverride { get; set; }

    /// <summary>
    /// 启动整条队列。返回 true 表示跑完（含被停止），false 表示没有可执行项。
    /// </summary>
    public static async Task<bool> StartAsync(QueueTrigger trigger)
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _stopRequested = false;
            CurrentToolId = "";
            _cts = new CancellationTokenSource();
        }
        LastResults.Clear();
        RaiseState();

        try
        {
            // 度假模式：定时触发一律不跑（手动不受限）
            if (trigger == QueueTrigger.Timer && AppState.Settings.VacationMode)
            {
                LogService.LogEvent("队列：度假模式生效，定时触发已跳过");
                return false;
            }

            var items = BuildItems();
            if (items.Count == 0)
            {
                if (trigger == QueueTrigger.Timer)
                {
                    LogService.LogEvent("队列：定时触发但队列为空");
                    NotifyService.Show(NotifyLevel.Warning, "龙管家", "定时触发但队列为空，请先勾选要运行的工具。");
                }
                else
                {
                    NotifyService.Show(NotifyLevel.Warning, "龙管家", "队列为空：请先勾选要运行的工具。");
                }
                return false;
            }

            int finished = await RunListAsync(items, trigger, _cts!.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("队列异常：" + ex);
            return false;
        }
        finally
        {
            lock (Gate)
            {
                _running = false;
                _stopRequested = false;
                _currentCoordinator = null;
                _currentProcess = null;
                CurrentToolId = "";
                IsWaitingIdle = false;
                _cts?.Dispose();
                _cts = null;
            }
            RaiseState();
            AppState.RefreshStatus();
        }
    }

    /// <summary>
    /// 用一个显式的 profile 列表跑队列（测试入口：不读 settings 的勾选）。
    /// 返回实际执行（含跳过）的项数。
    /// </summary>
    public static async Task<int> RunProfilesAsync(IReadOnlyList<ToolProfile> profiles, QueueTrigger trigger)
    {
        lock (Gate)
        {
            if (_running) return 0;
            _running = true;
            _stopRequested = false;
            _cts = new CancellationTokenSource();
        }
        LastResults.Clear();
        RaiseState();
        try
        {
            return await RunListAsync(profiles.ToList(), trigger, _cts!.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (Gate)
            {
                _running = false;
                _stopRequested = false;
                _currentCoordinator = null;
                _currentProcess = null;
                CurrentToolId = "";
                IsWaitingIdle = false;
                _cts?.Dispose();
                _cts = null;
            }
            RaiseState();
            AppState.RefreshStatus();
        }
    }

    /// <summary>队列主体循环（StartAsync / RunProfilesAsync 共用）。</summary>
    private static async Task<int> RunListAsync(List<ToolProfile> items, QueueTrigger trigger, CancellationToken ct)
    {
        int finished = 0;
        for (int i = 0; i < items.Count; i++)
            {
                if (_stopRequested) break;
                var profile = items[i];
                CurrentToolId = profile.ToolId;
                RaiseState();

                // 防打扰只约束定时触发
                if (trigger == QueueTrigger.Timer)
                {
                    bool canStart = await IdleGuard.WaitAsync(ct).ConfigureAwait(false);
                    if (!canStart || _stopRequested) break;
                }

                var pre = PreCheck(profile, trigger);
                if (pre.Skip)
                {
                    LastResults.Add(new QueueItemResult
                    {
                        ToolId = profile.ToolId,
                        DisplayName = profile.DisplayName,
                        State = ToolRunState.Idle,
                        Reason = pre.Message,
                        Skipped = true,
                    });
                    RaiseState();
                    continue;
                }

                var result = await RunItemAsync(profile, trigger, ct).ConfigureAwait(false);
                LastResults.Add(result);
                finished++;
                RaiseState();

                if (result.State == ToolRunState.Halted)
                {
                    // 悬置：队列立刻停止，等用户处理，绝不自动推进
                    LogService.LogEvent($"队列：{profile.DisplayName} 悬置，队列停止");
                    break;
                }

                bool failed = result.State is ToolRunState.Failed or ToolRunState.Timeout;
                if (failed && string.Equals(AppState.Settings.FailStrategy, "Abort", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.LogEvent($"队列：失败策略=中止，剩余 {items.Count - i - 1} 项不再执行");
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

        FinishQueue(trigger, finished);
        return finished;
    }

    /// <summary>
    /// 只跑一个工具（哨兵崩溃重启用）：不排队、不等待间隔，但同样走前置检查与状态机。
    /// </summary>
    public static async Task<bool> StartSingleAsync(ToolProfile profile, QueueTrigger trigger)
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _stopRequested = false;
            _cts = new CancellationTokenSource();
        }
        LastResults.Clear();
        RaiseState();

        try
        {
            var pre = PreCheck(profile, trigger);
            if (pre.Skip) return false;

            CurrentToolId = profile.ToolId;
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
        finally
        {
            lock (Gate)
            {
                _running = false;
                _stopRequested = false;
                _currentCoordinator = null;
                _currentProcess = null;
                CurrentToolId = "";
                IsWaitingIdle = false;
                _cts?.Dispose();
                _cts = null;
            }
            RaiseState();
            AppState.RefreshStatus();
        }
    }

    /// <summary>关机已排定（供取消按钮判断）。</summary>
    internal static void MarkShutdownPending() => _shutdownPending = true;

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

    /// <summary>队列中的条目 → 已配置可执行的 profile 列表（保持顺序）。</summary>
    private static List<ToolProfile> BuildItems()
    {
        var list = new List<ToolProfile>();
        foreach (var entry in AppState.Settings.Queue ?? new List<QueueEntry>())
        {
            if (!entry.Enabled) continue;
            var p = ProfileRegistry.Get(entry.ToolId);
            if (string.Equals(p.ToolId, entry.ToolId, StringComparison.OrdinalIgnoreCase))
                list.Add(p);
        }
        return list;
    }

    /// <summary>单个工具执行（含一次重试）。</summary>
    public static async Task<QueueItemResult> RunItemAsync(ToolProfile profile, QueueTrigger trigger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
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

            if (result.State == ToolRunState.Done || result.State == ToolRunState.Halted) break;
            if (ct.IsCancellationRequested) break;

            if (attempts < maxAttempts)
            {
                LogService.LogEvent($"队列：{profile.DisplayName} 第 {attempts} 次失败（{result.Reason}），重试一次");
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        if (last.State is ToolRunState.Failed or ToolRunState.Timeout)
            NotifyService.Show(NotifyLevel.Warning, "龙管家", $"{profile.DisplayName} 未完成：{last.Reason}");

        return last;
    }

    private sealed record SingleOutcome(ToolRunState State, string Reason);

    /// <summary>执行一次（不重试）：起进程 → 状态机 → 等终态。</summary>
    private static async Task<SingleOutcome> RunOnceAsync(ToolProfile profile, QueueTrigger trigger, int attempt, CancellationToken ct)
    {
        string args = BuildLaunchArguments(profile);
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
            string reason = state switch
            {
                ToolRunState.Done => "已完成",
                ToolRunState.Failed => "异常退出",
                ToolRunState.Timeout => $"超时 {profile.MaxDurationMinutes} 分钟未完成",
                ToolRunState.Halted => "报告完成但进程未退出",
                _ => "未完成",
            };

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
            var groups = AppState.Settings.SelectedGroups ?? Array.Empty<string>();
            if (groups.Length > 0)
                return "--startGroups " + string.Join(" ", groups.Select(g => "\"" + g + "\""));
            return ToolRunner.BuildArguments(profile, AppState.Settings.DragonConfigName);
        }
        return ToolRunner.BuildArguments(profile, null);
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

    /// <summary>收尾：汇总通知 + 跑完动作。</summary>
    private static void FinishQueue(QueueTrigger trigger, int finished)
    {
        var ran = LastResults.Where(r => !r.Skipped).ToList();
        int total = ran.Count;
        int ok = ran.Count(r => r.State == ToolRunState.Done);
        bool anyHalted = ran.Any(r => r.State == ToolRunState.Halted);
        bool anyFailed = ran.Any(r => r.State is ToolRunState.Failed or ToolRunState.Timeout or ToolRunState.Halted);
        TimeSpan elapsed = TimeSpan.FromSeconds(ran.Sum(r => r.Elapsed.TotalSeconds));

        string text;
        if (anyHalted)
        {
            var h = ran.First(r => r.State == ToolRunState.Halted);
            text = $"【龙管家】{h.DisplayName}报告完成但进程未退出，队列已暂停等你处理。";
            NotifyService.Show(NotifyLevel.Warning, "龙管家", text);
        }
        else if (total == 0)
        {
            text = "【龙管家】队列结束：没有可执行项。";
            NotifyService.Show(NotifyLevel.Warning, "龙管家", text);
        }
        else if (ok == total)
        {
            text = $"【龙管家】今晚队列完成：{ok}/{total} 项成功，总耗时 {FormatDuration(elapsed)}。晚安。";
            NotifyService.Show(NotifyLevel.Info, "龙管家", text);
        }
        else if (ok == 0)
        {
            text = $"【龙管家】队列结束：0/{total} 成功。⚠ 全部未完成。详情见软件日志。";
            NotifyService.Show(NotifyLevel.Warning, "龙管家", text);
        }
        else
        {
            var f = ran.First(r => r.State != ToolRunState.Done);
            text = $"【龙管家】队列结束：{ok}/{total} 成功。⚠ {f.DisplayName}失败（{f.Reason}），已跳过，详情见软件日志。";
            NotifyService.Show(NotifyLevel.Warning, "龙管家", text);
        }

        LogService.LogEvent($"队列结束：{ok}/{total} 成功，耗时 {FormatDuration(elapsed)}，触发 {trigger}，停止={_stopRequested}");

        // 记录到设置（界面"上次执行"用）
        try
        {
            AppState.Settings.LastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            AppState.Settings.LastRunSeconds = (int)elapsed.TotalSeconds;
            AppState.Settings.LastRunOk = ok == total && total > 0;
            AppState.SaveSettings();
        }
        catch { }

        AfterRunActionService.Execute(new AfterRunContext
        {
            Total = total,
            Success = ok,
            HasFailure = anyFailed,
            Stopped = _stopRequested,
        });
    }

    private static string FormatDuration(TimeSpan t)
    {
        int h = (int)t.TotalHours;
        int m = t.Minutes;
        if (h > 0) return $"{h}小时{m}分";
        if (m > 0) return $"{m}分";
        return "不到 1 分钟";
    }

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

    /// <summary>执行跑完动作。</summary>
    public static void Execute(AfterRunContext ctx)
    {
        string action = AppState.Settings.AfterRunAction ?? "Notify";
        if (string.Equals(action, "Notify", StringComparison.OrdinalIgnoreCase)) return;

        if (ctx.HasFailure && !AppState.Settings.AfterRunOnFailure)
        {
            LogService.LogEvent($"跑完动作：队列有失败项，跳过 {action}（仅通知）");
            return;
        }
        if (ctx.Stopped)
        {
            LogService.LogEvent($"跑完动作：队列被用户停止，跳过 {action}");
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
            try
            {
                LogService.LogEvent("跑完动作：执行睡眠");
                SetSuspendState(false, false, false);
            }
            catch (Exception ex)
            {
                LogService.LogEvent("睡眠失败：" + ex.Message);
            }
            return;
        }

        if (string.Equals(action, "Shutdown", StringComparison.OrdinalIgnoreCase))
        {
            if (dryRun)
            {
                LogService.LogEvent("DRYRUN: 将执行关机");
                return;
            }
            try
            {
                LogService.LogEvent("跑完动作：60 秒后关机");
                var psi = new ProcessStartInfo("shutdown", "/s /t 60")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
                QueueService.MarkShutdownPending();
                NotifyService.Show(NotifyLevel.Warning, "龙管家", "队列完成，60 秒后将关机，可在队列卡点「取消关机」。");
            }
            catch (Exception ex)
            {
                LogService.LogEvent("关机失败：" + ex.Message);
            }
        }
    }
}
