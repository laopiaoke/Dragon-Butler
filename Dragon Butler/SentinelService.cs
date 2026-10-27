using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace DragonButler;

/// <summary>
/// 哨兵统一事件类型（SPBGI）
/// </summary>
public enum SentinelEventKind
{
    BgiStarted, BgiExited, BgiRestarted,      // 进程生命周期
    DragonStarted, DragonEnded,               // 一条龙
    GroupStarted, GroupEnded, GroupError,     // 配置组
    GameLost,                                  // 原神进程消失
    MemoryHigh,                                // 内存阈值
    ScreenFrozen,                              // 截图比对疑似卡死
    LogErrorLine                               // 日志错误关键字命中
}

/// <summary>哨兵状态机：Idle（未跑）/ Running（一条龙中）/ Unknown（软件启动时 BGI 已在跑但无佐证）</summary>
public enum SentinelState { Idle, Running, Unknown }

/// <summary>
/// 哨兵实时健康数据（供界面展示"它真的在工作"）：
/// 心跳次数、各项检查的最后时间、系统/BGI 内存、重启与冻结计数、当前配置组。
/// 写入在计时器线程，读取用 HealthSnapshot() 复制，避免撕裂。
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
    public int RestartCount;
    public int FrozenCount;
    public DateTime LastFrozenAt;
    public int WebhookEvents;
    public string CurrentGroup = "";
    public string LastEventText = "";
    public DateTime LastEventAt;
}

/// <summary>
/// 哨兵总控（SPBGI）：单个 5 秒心跳 Timer 统一调度全部监测源。
/// 防误杀三红线：①截图冻结绝不自动重启 ②原神消失绝不自动重启
/// ③只有"BGI进程消失+一条龙进行中"满足全部四条件才自动重启。
/// 任何单点失败全部 try-catch，绝不把软件自己拖死。
/// </summary>
public static class SentinelService
{
    /// <summary>统一事件出口（已切回 UI 线程）</summary>
    public static event Action<SentinelEventKind, string>? OnEvent;

    public static SentinelState State { get; private set; } = SentinelState.Idle;

    private static readonly SentinelHealth Health = new();
    private static readonly object HealthGate = new();

    /// <summary>健康数据快照（界面每秒取一次）</summary>
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

    /// <summary>BGI Webhook 监听是否正在运行（供界面显示进度识别通道状态）</summary>
    public static bool WebhookRunning => WebhookInstance.IsRunning;

    private static readonly LogWatcher LogWatcherInstance = new();
    private static readonly ScreenshotMonitor ScreenshotInstance = new();
    private static readonly WebhookListener WebhookInstance = new();

    private static System.Threading.Timer? _timer;
    private static bool _running;
    private static int _missCount;
    private static DateTime _lastRestart = DateTime.MinValue;
    private static bool _lastGameAlive = true;
    private static DateTime _lastMemNotify = DateTime.MinValue;
    private static bool _sessionWarned;
    private static int _activeSession = -1;

    /// <summary>启动哨兵（幂等）</summary>
    public static void Start()
    {
        if (_running) return;
        _running = true;
        try
        {
            GroupArchive.Load();
            LogWatcherInstance.OnEvent += OnSourceEvent;
            ScreenshotInstance.OnEvent += OnSourceEvent;
            LogWatcherInstance.Start();
            WebhookInstance.Start(BgiService.Settings.BgiWebhookPort);
            if (BgiService.IsRunning) State = SentinelState.Unknown;
            _timer = new System.Threading.Timer(Tick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
            LogService.LogEvent("哨兵已启动（5 秒心跳）");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("哨兵启动失败：" + ex.Message);
        }
    }

    /// <summary>停止哨兵</summary>
    public static void Stop()
    {
        _running = false;
        try
        {
            _timer?.Dispose();
            _timer = null;
            LogWatcherInstance.OnEvent -= OnSourceEvent;
            ScreenshotInstance.OnEvent -= OnSourceEvent;
            WebhookInstance.Stop();
            State = SentinelState.Idle;
            LogService.LogEvent("哨兵已停止");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("哨兵停止异常：" + ex.Message);
        }
    }

    /// <summary>监测源事件入口（可能在计时器线程同步调用）</summary>
    private static void OnSourceEvent(SentinelEvent ev)
    {
        HandleEvent(ev.Kind, ev.Payload, ev.Source);
    }

    // ===== 5 秒心跳 =====

    private static void Tick(object? _)
    {
        MutateHealth(h => { h.TickCount++; h.LastTick = DateTime.Now; });

        // 1. 进程检测
        try { CheckProcesses(); } catch (Exception ex) { LogService.LogEvent("哨兵-进程检测异常：" + ex.Message); }
        MutateHealth(h => h.LastProcessCheck = DateTime.Now);
        // 2. 内存检测
        try { CheckMemory(); } catch (Exception ex) { LogService.LogEvent("哨兵-内存检测异常：" + ex.Message); }
        // 3. 日志兜底驱动
        try { LogWatcherInstance.Pump(); } catch (Exception ex) { LogService.LogEvent("哨兵-日志驱动异常：" + ex.Message); }
        // 4. Webhook 队列消费
        try
        {
            foreach (var ev in WebhookInstance.Drain()) HandleEvent(ev.Kind, ev.Payload, ev.Source);
        }
        catch (Exception ex) { LogService.LogEvent("哨兵-Webhook 驱动异常：" + ex.Message); }
        // 5. 截图心跳
        try { ScreenshotInstance.Heartbeat(State == SentinelState.Running); } catch (Exception ex) { LogService.LogEvent("哨兵-截图驱动异常：" + ex.Message); }
        // 6. 会话前台检测
        try { CheckSession(); } catch (Exception ex) { LogService.LogEvent("哨兵-会话检测异常：" + ex.Message); }

        // 汇总各监测源的真实指标
        MutateHealth(h =>
        {
            h.LastScreenCheck = ScreenshotInstance.LastCheckAt;
            h.LastScreenScore = ScreenshotInstance.LastScore;
            h.FrozenCount = ScreenshotInstance.FrozenCount;
            h.LastFrozenAt = ScreenshotInstance.LastFrozenAt;
            h.WebhookEvents = WebhookInstance.EventCount;
            h.LastWebhookEvent = WebhookInstance.LastEventAt;
        });
    }

    /// <summary>1. 进程检测：BGI 丢失计数（5 秒×6 次=30 秒判死）+ 原神消失</summary>
    private static void CheckProcesses()
    {
        bool bgi = BgiService.IsRunning;
        bool game = BgiService.GetSessionGame().Length > 0;

        if (bgi)
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
                    // 判定 BGI 崩溃（一条龙进行中）
                    HandleBgiCrash();
                }
                else if (State == SentinelState.Unknown)
                {
                    // 启动时就在跑、后来没了：只记录退出，不自动重启（无进行中佐证）
                    _missCount = 0;
                    State = SentinelState.Idle;
                    Emit(SentinelEventKind.BgiExited, "BetterGI 已退出");
                    LogService.LogEvent("BGI：进程退出（Unknown → Idle）");
                }
            }
        }

        // 原神进程消失且 BGI 还在 → 只通知，不自动处理（原神死了 BGI 会自己结束）
        if (!game && _lastGameAlive && bgi)
        {
            HandleEvent(SentinelEventKind.GameLost, "原神进程消失", "proc");
        }
        _lastGameAlive = game;
    }

    /// <summary>BGI 崩溃处置：四条件全满足才自动重启（宁可漏报不可误杀）</summary>
    private static void HandleBgiCrash()
    {
        _missCount = 0;
        bool auto = BgiService.Settings.AutoRestartBgi;
        bool cooling = (DateTime.Now - _lastRestart).TotalSeconds < 60;

        if (auto && !cooling)
        {
            _lastRestart = DateTime.Now;
            State = SentinelState.Idle;
            MutateHealth(h => h.RestartCount++);
            Emit(SentinelEventKind.BgiRestarted, "BGI 崩溃，自动重启一条龙");
            LogService.LogEvent("BGI：判定崩溃（30 秒无进程），自动重启一条龙");
            NotifyService.Show(NotifyLevel.Critical, "BGI 已自动重启",
                "检测到 BetterGI 异常退出（一条龙进行中），已自动重启一条龙");
            _ = BgiService.StartDragonOrGroupsAsync("哨兵重启");
        }
        else if (auto && cooling)
        {
            State = SentinelState.Idle;
            LogService.LogEvent("BGI：判定崩溃，但处于 60 秒重启冷却期，只通知不重启");
            NotifyService.Show(NotifyLevel.Warning, "BGI 异常退出",
                "检测到 BetterGI 异常退出，处于 60 秒重启冷却期，暂不自动重启");
        }
        else
        {
            State = SentinelState.Idle;
            LogService.LogEvent("BGI：判定崩溃，未开启自动重启，只通知");
            NotifyService.Show(NotifyLevel.Warning, "BGI 意外退出",
                "检测到 BetterGI 意外退出，未开启自动重启");
        }
    }

    /// <summary>2. 内存检测：系统可用 &lt;15%（已用&gt;85%）或 BGI 工作集 &gt;4096MB → 只通知不重启</summary>
    private static void CheckMemory()
    {
        var (totalMb, availMb) = MemoryInfo();
        double usedPct = totalMb > 0 ? (totalMb - availMb) * 100.0 / totalMb : 0;
        long bgiWs = 0;
        foreach (var p in BgiService.GetSessionBgi())
        {
            try { bgiWs = Math.Max(bgiWs, p.WorkingSet64); }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        MutateHealth(h =>
        {
            h.LastMemoryCheck = DateTime.Now;
            h.MemUsedPercent = usedPct;
            h.BgiWorkingSetMb = bgiWs / 1024 / 1024;
        });
        bool high = usedPct > 85 || bgiWs > 4096L * 1024 * 1024;
        if (high && (DateTime.Now - _lastMemNotify).TotalMinutes >= 5)
        {
            _lastMemNotify = DateTime.Now;
            HandleEvent(SentinelEventKind.MemoryHigh,
                $"系统内存使用 {usedPct:F0}%，BetterGI 工作集 {bgiWs / 1024 / 1024}MB（只通知，不自动重启）",
                "proc");
        }
    }

    /// <summary>6. 会话前台检测：本会话不是当前活动控制台会话（RDP/多用户切换）→ 通知一次</summary>
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
                NotifyService.Show(NotifyLevel.Warning, "会话已切走",
                    "当前会话可能已切走（RDP/多用户切换），键鼠模拟可能失效");
                LogService.LogEvent("会话：检测到前台会话变化，本会话可能已切走");
            }
        }
        else
        {
            _sessionWarned = false;
        }
    }

    // ===== 统一事件决策 =====

    private static void HandleEvent(SentinelEventKind kind, string payload, string source)
    {
        try
        {
            MutateHealth(h =>
            {
                h.LastEventAt = DateTime.Now;
                h.LastEventText = kind switch
                {
                    SentinelEventKind.DragonStarted => "一条龙启动",
                    SentinelEventKind.DragonEnded => "一条龙结束",
                    SentinelEventKind.GroupStarted => "开始 " + payload,
                    SentinelEventKind.GroupEnded => payload + " 完成",
                    SentinelEventKind.BgiRestarted => "BGI 自动重启",
                    SentinelEventKind.BgiExited => "BGI 已退出",
                    SentinelEventKind.ScreenFrozen => "画面疑似冻结",
                    SentinelEventKind.MemoryHigh => "内存占用过高",
                    SentinelEventKind.GameLost => "原神进程消失",
                    SentinelEventKind.GroupError => "任务异常",
                    _ => payload,
                };
            });

            switch (kind)
            {
                case SentinelEventKind.DragonStarted:
                    State = SentinelState.Running;
                    MutateHealth(h => h.CurrentGroup = "");
                    LogService.LogEvent("一条龙：开始");
                    NotifyService.Show(NotifyLevel.Info, "一条龙已启动", "BetterGI 开始执行一条龙");
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.DragonEnded:
                    LogService.LogEvent("一条龙：结束");
                    MutateHealth(h => h.CurrentGroup = "");
                    CheckDragonComplete();
                    break;

                case SentinelEventKind.GroupStarted:
                    GroupArchive.RecordStart(payload, source);
                    MutateHealth(h => h.CurrentGroup = payload);
                    if (State != SentinelState.Running) State = SentinelState.Running;
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.GroupEnded:
                    GroupArchive.RecordEnd(payload, source);
                    MutateHealth(h => { if (h.CurrentGroup == payload) h.CurrentGroup = ""; });
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.GroupError:
                    LogService.LogEvent("任务异常：" + payload);
                    NotifyService.Show(NotifyLevel.Warning, "任务异常", payload);
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.GameLost:
                    LogService.LogEvent("原神进程消失（仅通知，不自动处理）");
                    NotifyService.Show(NotifyLevel.Warning, "原神进程消失",
                        "检测到原神进程已退出，BetterGI 通常会自行结束");
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.MemoryHigh:
                    LogService.LogEvent("内存过高：" + payload);
                    NotifyService.Show(NotifyLevel.Warning, "内存占用过高", payload);
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.ScreenFrozen:
                    LogService.LogEvent("画面疑似卡死：" + payload);
                    NotifyService.Show(NotifyLevel.Warning, "画面疑似卡死",
                        "连续两次截图差异极低（" + payload + "），请查看游戏画面（只通知，不自动重启）");
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.LogErrorLine:
                    LogService.LogEvent("BGI日志：" + payload);
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.BgiStarted:
                    Emit(kind, payload);
                    break;

                case SentinelEventKind.BgiExited:
                case SentinelEventKind.BgiRestarted:
                    Emit(kind, payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("哨兵事件处理异常：" + ex.Message);
        }
    }

    /// <summary>一条龙完成判定：最近一次 DragonEnded 之后无未完成组 → Idle + 完成通知</summary>
    private static void CheckDragonComplete()
    {
        var unfinished = GroupArchive.GetUnfinished();
        if (unfinished.Count == 0)
        {
            State = SentinelState.Idle;
            var today = GroupArchive.GetTodayGroups();
            int done = today.Count(g => g.End != null);
            string span = "未知";
            try
            {
                var firstStart = today.Where(g => !string.IsNullOrEmpty(g.Start))
                    .Select(g => TimeSpan.Parse(g.Start!)).OrderBy(t => t).FirstOrDefault();
                var lastEnd = today.Where(g => !string.IsNullOrEmpty(g.End))
                    .Select(g => TimeSpan.Parse(g.End!)).OrderByDescending(t => t).FirstOrDefault();
                if (lastEnd > firstStart)
                    span = (lastEnd - firstStart).ToString(@"h\小\时\ m\分\钟");
            }
            catch { }
            LogService.LogEvent($"一条龙完成：今日完成 {done} 组，总时长 {span}");
            NotifyService.Show(NotifyLevel.Info, "一条龙已完成",
                $"今日完成 {done} 个配置组，总时长 {span}");
        }
        else
        {
            NotifyService.Show(NotifyLevel.Warning, "一条龙结束",
                $"一条龙已结束，但仍有 {unfinished.Count} 个配置组未完成，可在主界面点击「继续跑未完成」");
        }
        Emit(SentinelEventKind.DragonEnded, string.Join(",", unfinished));
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

    /// <summary>返回 (总物理内存MB, 可用物理内存MB)</summary>
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
