using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace DragonButler;

/// <summary>
/// 哨兵统一事件类型。
/// </summary>
public enum SentinelEventKind
{
    BgiStarted, BgiExited, BgiRestarted,      // 进程生命周期
    DragonStarted, DragonEnded,               // 一条龙（仅作事件广播，不参与完成判定）
    GroupStarted, GroupEnded, GroupError,     // 配置组（组归档数据源）
    GameLost,                                  // 关联游戏进程消失
    MemoryHigh,                                // 内存阈值
    ScreenFrozen,                              // 截图比对疑似卡死
    LogErrorLine                               // 日志错误关键字命中
}

/// <summary>哨兵状态机：Idle（未跑）/ Running（队列执行中）/ Unknown（启动时工具已在跑但无佐证）</summary>
public enum SentinelState { Idle, Running, Unknown }

/// <summary>
/// 哨兵实时健康数据（供界面/诊断使用）。
/// </summary>
public sealed class SentinelHealth
{
    public DateTime StartedAt = DateTime.Now;
    public long TickCount;
    public DateTime LastTick;
    public DateTime LastProcessCheck;
    public DateTime LastMemoryCheck;
    public DateTime LastScreenCheck;
    public DateTime LastWebhookEvent;
    public double LastScreenScore;
    public double MemUsedPercent;
    public long BgiWorkingSetMb;
    public double ToolCpuPercent;
    public DateTime StallSince;
    public int RestartCount;
    public int FrozenCount;
    public DateTime LastFrozenAt;
    public int WebhookEvents;
    public string CurrentGroup = "";
    public string LastEventText = "";
    public DateTime LastEventAt;
}

/// <summary>
/// 守护总控（v1.3）：只保留"进程存活 / 内存 / 截图冻结 / 会话"四类监测。
/// **不再承担任何完成判定**——完成判定统一由 QueueService → RunCoordinator 负责；
/// 截图冻结只作为告警，不接入完成信号。
/// </summary>
public static class SentinelService
{
    /// <summary>统一事件出口（已切回 UI 线程）。</summary>
    public static event Action<SentinelEventKind, string>? OnEvent;

    /// <summary>当前状态。</summary>
    public static SentinelState State { get; private set; } = SentinelState.Idle;

    private static readonly SentinelHealth Health = new();
    private static readonly object HealthGate = new();

    /// <summary>健康数据快照。</summary>
    public static SentinelHealth HealthSnapshot()
    {
        lock (HealthGate)
        {
            return new SentinelHealth
            {
                StartedAt = Health.StartedAt,
                TickCount = Health.TickCount,
                LastTick = Health.LastTick,
                LastProcessCheck = Health.LastProcessCheck,
                LastMemoryCheck = Health.LastMemoryCheck,
                LastScreenCheck = Health.LastScreenCheck,
                LastWebhookEvent = Health.LastWebhookEvent,
                LastScreenScore = Health.LastScreenScore,
                MemUsedPercent = Health.MemUsedPercent,
                BgiWorkingSetMb = Health.BgiWorkingSetMb,
                ToolCpuPercent = Health.ToolCpuPercent,
                StallSince = Health.StallSince,
                RestartCount = Health.RestartCount,
                FrozenCount = Health.FrozenCount,
                WebhookEvents = Health.WebhookEvents,
                CurrentGroup = Health.CurrentGroup,
                LastEventText = Health.LastEventText,
                LastEventAt = Health.LastEventAt,
            };
        }
    }

    private static void MutateHealth(Action<SentinelHealth> action)
    {
        try { lock (HealthGate) action(Health); } catch { }
    }

    /// <summary>Webhook 监听是否在跑（底部盾牌详情用）。</summary>
    public static bool WebhookRunning => WebhookHub.IsRunning;

    private static readonly ScreenshotMonitor ScreenshotInstance = new();

    private static Timer? _timer;
    private static bool _running;
    private static int _missCount;
    private static DateTime _lastRestart = DateTime.MinValue;
    private static bool _lastGameAlive = true;
    private static DateTime _lastMemNotify = DateTime.MinValue;
    private static bool _sessionWarned;
    private static int _activeSession = -1;
    private static ToolProfile? _runProfile;

    // ===== v1.4：STALLED 观测（复用 5 秒轮询，不另起线程）=====
    /// <summary>判定疑似挂起所需的持续时长（测试可调小）。</summary>
    public static TimeSpan StallThreshold { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>CPU 低于该百分比视为"无活动"。</summary>
    public static double StallCpuPercent { get; set; } = 5;

    private static DateTime _lastCpuAt = DateTime.MinValue;
    private static double _lastCpuTotalMs;
    private static long _lastLogSize = -1;
    private static DateTime _stallSince = DateTime.MinValue;
    private static bool _stallNotified;

    /// <summary>启动（幂等）。</summary>
    public static void Start()
    {
        if (_running) return;
        _running = true;
        try
        {
            ScreenshotInstance.OnEvent += OnSourceEvent;
            if (ProfileRegistry.BetterGi is { } bgi && ToolRunner.IsRunning(bgi))
                State = SentinelState.Unknown;
            _timer = new Timer(Tick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
            LogService.LogEvent("守护已启动（5 秒心跳，仅存活/内存/截图/会话）");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("守护启动失败：" + ex.Message);
        }
    }

    /// <summary>停止。</summary>
    public static void Stop()
    {
        _running = false;
        try
        {
            _timer?.Dispose();
            _timer = null;
            ScreenshotInstance.OnEvent -= OnSourceEvent;
            State = SentinelState.Idle;
            ResetStallWatch();
            LogService.LogEvent("守护已停止");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("守护停止异常：" + ex.Message);
        }
    }

    /// <summary>队列开始执行某工具：进入 Running（崩溃重启判定的前提）。</summary>
    public static void NotifyRunStarted(ToolProfile profile)
    {
        _runProfile = profile;
        State = SentinelState.Running;
        _missCount = 0;
        ResetStallWatch();
    }

    /// <summary>队列该项结束：回到 Idle。</summary>
    public static void NotifyRunEnded(ToolProfile profile)
    {
        if (_runProfile != null && !string.Equals(_runProfile.ToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase))
            return;
        State = SentinelState.Idle;
        _missCount = 0;
        ResetStallWatch();
    }

    private static void OnSourceEvent(SentinelEvent ev) => HandleEvent(ev.Kind, ev.Payload, ev.Source);

    private static void Tick(object? _)
    {
        MutateHealth(h => { h.TickCount++; h.LastTick = DateTime.Now; });
        try { CheckProcesses(); } catch (Exception ex) { LogService.LogEvent("守护-进程检测异常：" + ex.Message); }
        MutateHealth(h => h.LastProcessCheck = DateTime.Now);
        try { CheckStall(); } catch (Exception ex) { LogService.LogEvent("守护-挂起检测异常：" + ex.Message); }
        try { CheckMemory(); } catch (Exception ex) { LogService.LogEvent("守护-内存检测异常：" + ex.Message); }
        try { ScreenshotInstance.Heartbeat(State == SentinelState.Running); } catch (Exception ex) { LogService.LogEvent("守护-截图驱动异常：" + ex.Message); }
        try { CheckSession(); } catch (Exception ex) { LogService.LogEvent("守护-会话检测异常：" + ex.Message); }

        MutateHealth(h =>
        {
            h.LastScreenCheck = ScreenshotInstance.LastCheckAt;
            h.LastScreenScore = ScreenshotInstance.LastScore;
            h.FrozenCount = ScreenshotInstance.FrozenCount;
            h.LastFrozenAt = ScreenshotInstance.LastFrozenAt;
            h.WebhookEvents = WebhookHub.EventCount;
            h.LastWebhookEvent = WebhookHub.LastEventAt;
        });
    }

    /// <summary>进程检测：工具丢失计数（5 秒×6=30 秒判死）+ 游戏进程消失提醒。</summary>
    private static void CheckProcesses()
    {
        var profile = _runProfile ?? ProfileRegistry.BetterGi;
        bool tool = ToolRunner.IsRunning(profile);
        bool game = profile.GameProcessNames.Any(n => ProcessTools.CountSessionProcesses(n) > 0);

        if (tool)
        {
            _missCount = 0;
            if (State == SentinelState.Idle) State = SentinelState.Unknown;
        }
        else
        {
            _missCount++;
            if (_missCount >= 6)
            {
                if (State == SentinelState.Running)
                {
                    HandleCrash(profile);
                }
                else if (State == SentinelState.Unknown)
                {
                    _missCount = 0;
                    State = SentinelState.Idle;
                    Emit(SentinelEventKind.BgiExited, profile.DisplayName + " 已退出");
                    LogService.LogEvent("守护：进程退出（Unknown → Idle）");
                }
            }
        }

        if (!game && _lastGameAlive && tool)
            HandleEvent(SentinelEventKind.GameLost, "游戏进程消失", "proc");
        _lastGameAlive = game;
    }

    /// <summary>
    /// 崩溃处置：仍走"自动重启"老逻辑，但重启目标读 profile，且队列正在跑时不重复启动
    /// （避免与协调器抢同一个进程）。
    /// </summary>
    private static void HandleCrash(ToolProfile profile)
    {
        _missCount = 0;
        bool auto = AppState.Settings.AutoRestartBgi;
        bool cooling = (DateTime.Now - _lastRestart).TotalSeconds < 60;

        if (QueueService.IsRunning)
        {
            State = SentinelState.Idle;
            LogService.LogEvent("守护：检测到进程消失，但队列正在运行，交由队列判定处理");
            return;
        }

        if (auto && !cooling)
        {
            _lastRestart = DateTime.Now;
            State = SentinelState.Idle;
            MutateHealth(h => h.RestartCount++);
            Emit(SentinelEventKind.BgiRestarted, "工具崩溃，自动重启");
            LogService.LogEvent($"守护：判定崩溃（30 秒无进程），自动重启 {profile.DisplayName}");
            NotifyService.Show(NotifyLevel.Critical, "龙管家",
                $"检测到 {profile.DisplayName} 异常退出，已尝试自动重启。");
            _ = QueueService.StartSingleAsync(profile, QueueTrigger.Sentinel);
        }
        else if (auto && cooling)
        {
            State = SentinelState.Idle;
            LogService.LogEvent("守护：判定崩溃，但处于 60 秒重启冷却期，只通知不重启");
            NotifyService.Show(NotifyLevel.Warning, "龙管家", "检测到工具异常退出，处于重启冷却期，暂不自动重启。");
        }
        else
        {
            State = SentinelState.Idle;
            LogService.LogEvent("守护：判定崩溃，未开启自动重启，只通知");
            NotifyService.Show(NotifyLevel.Warning, "龙管家", "检测到工具意外退出，未开启自动重启。");
        }
    }

    /// <summary>重置挂起观测（每次运行开始/结束时调用）。</summary>
    private static void ResetStallWatch()
    {
        _lastCpuAt = DateTime.MinValue;
        _lastCpuTotalMs = 0;
        _lastLogSize = -1;
        _stallSince = DateTime.MinValue;
        _stallNotified = false;
        MutateHealth(h => { h.ToolCpuPercent = 0; h.StallSince = DateTime.MinValue; });
    }

    /// <summary>
    /// 疑似挂起：目标进程存活 + CPU &lt;5% 持续 3 分钟 + 关联日志大小无增长。
    /// 观测复用本服务的 5 秒轮询；无日志文件的工具退化为"仅看 CPU"（见交付说明待确认项）。
    /// </summary>
    private static void CheckStall()
    {
        if (State != SentinelState.Running || _runProfile == null || !QueueService.IsRunning) return;

        var profile = _runProfile;
        var procs = ToolRunner.GetSessionProcesses(profile);
        if (procs.Length == 0) { ResetStallWatch(); return; }

        double cpu = SampleCpu(procs);
        long logSize = GetLogSize(profile, out bool hasLog);
        bool cpuLow = cpu >= 0 && cpu < StallCpuPercent;
        bool logFlat = !hasLog || logSize == _lastLogSize;

        MutateHealth(h => h.ToolCpuPercent = cpu);
        _lastLogSize = logSize;

        if (!(cpuLow && logFlat))
        {
            _stallSince = DateTime.MinValue;
            MutateHealth(h => h.StallSince = DateTime.MinValue);
            return;
        }

        if (_stallSince == DateTime.MinValue)
        {
            _stallSince = DateTime.Now;
            MutateHealth(h => h.StallSince = _stallSince);
            LogService.LogEvent($"守护：{profile.DisplayName} 进入挂起观察（CPU {cpu:F1}%，日志不增长）");
        }

        if (_stallNotified) return;
        if (DateTime.Now - _stallSince < StallThreshold) return;

        _stallNotified = true;
        string detail = hasLog
            ? $"CPU {cpu:F1}%，日志 {StallThreshold.TotalMinutes:F0} 分钟无增长"
            : $"CPU {cpu:F1}%，{StallThreshold.TotalMinutes:F0} 分钟无活动";
        LogService.LogEvent($"守护：{profile.DisplayName} 疑似挂起（{detail}）");
        QueueService.HandleStall(profile, "疑似挂起（" + detail + "）");
    }

    /// <summary>本会话内该工具进程的总 CPU 占比（首次采样返回 -1）。</summary>
    private static double SampleCpu(System.Diagnostics.Process[] procs)
    {
        try
        {
            double total = 0;
            foreach (var p in procs)
            {
                try { total += p.TotalProcessorTime.TotalMilliseconds; }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            DateTime now = DateTime.Now;
            if (_lastCpuAt == DateTime.MinValue)
            {
                _lastCpuAt = now;
                _lastCpuTotalMs = total;
                return -1;
            }
            double wallMs = (now - _lastCpuAt).TotalMilliseconds;
            double delta = total - _lastCpuTotalMs;
            _lastCpuAt = now;
            _lastCpuTotalMs = total;
            if (wallMs <= 0) return -1;
            double pct = delta / (wallMs * Math.Max(1, Environment.ProcessorCount)) * 100.0;
            return Math.Clamp(pct, 0, 100);
        }
        catch { return -1; }
    }

    /// <summary>关联日志目录的总大小；无目录/无日志返回 -1。</summary>
    private static long GetLogSize(ToolProfile profile, out bool hasLog)
    {
        hasLog = false;
        try
        {
            string? dir = null;
            if (profile.CustomData.TryGetValue("logDir", out string? custom) && !string.IsNullOrWhiteSpace(custom))
                dir = custom;
            if (string.IsNullOrWhiteSpace(dir))
            {
                string? exeDir = Path.GetDirectoryName(profile.ExecutablePath);
                if (!string.IsNullOrEmpty(exeDir)) dir = Path.Combine(exeDir, "log");
            }
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return -1;

            var files = Directory.GetFiles(dir, "*.log");
            if (files.Length == 0) return -1;
            hasLog = true;
            long total = 0;
            foreach (string f in files)
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }
        catch { return -1; }
    }

    /// <summary>内存检测：系统已用 &gt;85% 或工具工作集 &gt;4096MB → 只通知。</summary>
    private static void CheckMemory()
    {
        var (totalMb, availMb) = MemoryInfo();
        double usedPct = totalMb > 0 ? (totalMb - availMb) * 100.0 / totalMb : 0;
        long ws = 0;
        var profile = _runProfile ?? ProfileRegistry.BetterGi;
        foreach (var p in ToolRunner.GetSessionProcesses(profile))
        {
            try { ws = Math.Max(ws, p.WorkingSet64); }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        MutateHealth(h =>
        {
            h.LastMemoryCheck = DateTime.Now;
            h.MemUsedPercent = usedPct;
            h.BgiWorkingSetMb = ws / 1024 / 1024;
        });
        bool high = usedPct > 85 || ws > 4096L * 1024 * 1024;
        if (high && (DateTime.Now - _lastMemNotify).TotalMinutes >= 5)
        {
            _lastMemNotify = DateTime.Now;
            HandleEvent(SentinelEventKind.MemoryHigh,
                $"系统内存使用 {usedPct:F0}%，{profile.DisplayName} 工作集 {ws / 1024 / 1024}MB（只通知）",
                "proc");
        }
    }

    private static void CheckSession()
    {
        int active = GetActiveConsoleSessionId();
        if (active == -1 || active == _activeSession) return;
        bool changed = _activeSession != -1;
        _activeSession = active;
        if (!changed) return;
        int me = Process.GetCurrentProcess().SessionId;
        if (me != active)
        {
            if (!_sessionWarned)
            {
                _sessionWarned = true;
                NotifyService.Show(NotifyLevel.Warning, "龙管家",
                    "当前会话可能已切走（RDP/多用户切换），键鼠模拟可能失效。");
                LogService.LogEvent("会话：检测到前台会话变化，本会话可能已切走");
            }
        }
        else
        {
            _sessionWarned = false;
        }
    }

    /// <summary>告警事件决策：只负责提醒，不参与完成判定。</summary>
    private static void HandleEvent(SentinelEventKind kind, string payload, string source)
    {
        try
        {
            MutateHealth(h =>
            {
                h.LastEventAt = DateTime.Now;
                h.LastEventText = kind switch
                {
                    SentinelEventKind.BgiRestarted => "工具自动重启",
                    SentinelEventKind.BgiExited => "工具已退出",
                    SentinelEventKind.ScreenFrozen => "画面疑似冻结",
                    SentinelEventKind.MemoryHigh => "内存占用过高",
                    SentinelEventKind.GameLost => "游戏进程消失",
                    SentinelEventKind.GroupError => "任务异常",
                    _ => payload,
                };
            });

            switch (kind)
            {
                case SentinelEventKind.GameLost:
                    LogService.LogEvent("游戏进程消失（仅通知）");
                    NotifyService.Show(NotifyLevel.Warning, "龙管家", "检测到游戏进程已退出，工具通常会自行结束。");
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.MemoryHigh:
                    LogService.LogEvent("内存过高：" + payload);
                    NotifyService.Show(NotifyLevel.Warning, "龙管家", payload);
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.ScreenFrozen:
                    LogService.LogEvent("画面疑似卡死：" + payload);
                    NotifyService.Show(NotifyLevel.Warning, "龙管家",
                        $"连续两次截图差异极低（{payload}），请查看游戏画面（只通知，不自动重启）。");
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.GroupError:
                    LogService.LogEvent("任务异常：" + payload);
                    NotifyService.Show(NotifyLevel.Warning, "龙管家", payload);
                    Emit(kind, payload);
                    break;

                default:
                    Emit(kind, payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("守护事件处理异常：" + ex.Message);
        }
    }

    private static void Emit(SentinelEventKind kind, string payload)
    {
        var h = OnEvent;
        if (h == null) return;
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.BeginInvoke(() => h(kind, payload));
        else h(kind, payload);
    }

    // ===== 系统 API =====

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private static (double total, double avail) MemoryInfo()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m))
                return (m.ullTotalPhys / 1024.0 / 1024.0, m.ullAvailPhys / 1024.0 / 1024.0);
        }
        catch { }
        return (0, 0);
    }

    [DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    private static int GetActiveConsoleSessionId()
    {
        try { return WTSGetActiveConsoleSessionId(); }
        catch { return -1; }
    }
}
