using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 挂机护航（v1.4）：队列运行期间临时抑制系统睡眠、更新重启与屏保，并在结束后恢复。
/// 只在队列真正开始执行时 <see cref="Acquire"/>，队列结束/停止/悬置时 <see cref="Release"/>。
/// 正常时完全静默，不弹任何用户可见通知；每个动作只写日志。
/// </summary>
public static class KeepAwakeService
{
    private const uint ES_CONTINUOUS = 0x80000000u;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001u;

    private const uint SPI_GETSCREENSAVEACTIVE = 0x0010u;
    private const uint SPI_SETSCREENSAVEACTIVE = 0x0011u;

    private const uint WM_SYSCOMMAND = 0x0112u;
    private const int SC_MONITORPOWER = 0xF170;
    private const int HWND_BROADCAST = 0xFFFF;

    private static readonly object Gate = new();
    private static bool _active;
    private static Thread? _worker;
    private static ManualResetEventSlim? _ready;
    private static Dispatcher? _workerDispatcher;
    private static IntPtr _blockWindow;
    private static Timer? _nightTimer;

    /// <summary>是否处于护航状态（队列运行且总开关开启）。</summary>
    public static bool IsActive { get { lock (Gate) return _active; } }

    /// <summary>睡眠抑制请求是否已生效。</summary>
    public static bool SleepBlocked { get; private set; }

    /// <summary>重启阻断是否已注册。</summary>
    public static bool RestartBlocked { get; private set; }

    /// <summary>屏保是否被临时关闭。</summary>
    public static bool ScreenSaverSuppressed { get; private set; }

    /// <summary>夜间息屏是否已排定。</summary>
    public static bool NightScreenScheduled => _nightTimer != null;

    /// <summary>夜间息屏预计触发时间（未排定=null）。</summary>
    public static DateTime? NightScreenAt { get; private set; }

    /// <summary>测试用：覆盖夜间息屏延时（null=按设置分钟数）。</summary>
    public static TimeSpan? NightScreenDelayOverride { get; set; }

    /// <summary>测试用：只写日志，不真正发送关闭显示器指令。</summary>
    public static bool NightScreenDryRun { get; set; }

    /// <summary>
    /// 启动时补偿：上次进程异常退出可能留下被关闭的屏保，这里按 settings 里记录的原值恢复。
    /// </summary>
    public static void CompensateOnStartup()
    {
        try
        {
            int original = AppState.Settings.KeepAwakeScreenSaverOriginal;
            if (original < 0) return;
            if (SetScreenSaverActive(original != 0))
            {
                LogService.LogEvent("护航补偿：已恢复上次异常退出遗留的屏保设置");
                AppState.Settings.KeepAwakeScreenSaverOriginal = -1;
                AppState.SaveSettings();
            }
            else
            {
                LogService.LogEvent("WARNING 护航补偿：屏保设置恢复失败，将在下次启动继续尝试");
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("护航补偿异常：" + ex.Message);
        }
    }

    /// <summary>队列开始执行时调用；重复调用幂等。</summary>
    public static void Acquire()
    {
        if (!AppState.Settings.KeepAwakeEnabled)
        {
            LogService.LogEvent("护航：总开关关闭，跳过");
            return;
        }

        lock (Gate)
        {
            if (_active) return;
            _active = true;
            SleepBlocked = false;
            RestartBlocked = false;
            ScreenSaverSuppressed = false;
            NightScreenAt = null;
        }

        LogService.LogEvent("护航：开始（防空闲睡眠 / 防更新重启 / 防屏保）");

        // 1) 屏保：记录原值后临时关闭；失败只告警，不影响队列
        try
        {
            if (GetScreenSaverActive(out bool original))
            {
                if (!original)
                {
                    LogService.LogEvent("护航：屏保原本未启用，无需修改");
                }
                else if (SetScreenSaverActive(false))
                {
                    // 只有真的改过才记录原值，异常退出后下次启动补偿
                    AppState.Settings.KeepAwakeScreenSaverOriginal = 1;
                    AppState.SaveSettings();
                    ScreenSaverSuppressed = true;
                    LogService.LogEvent("护航：屏保已临时关闭（原值=开启）");
                }
                else
                {
                    LogService.LogEvent("WARNING 护航：屏保关闭失败（可能被策略锁定）");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("WARNING 护航：屏保处理异常 " + ex.Message);
        }

        // 2) 睡眠抑制 + 重启阻断：专用 STA 线程持有请求，避免调用线程结束后失效
        try
        {
            _ready = new ManualResetEventSlim(false);
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "DragonButler-KeepAwake",
            };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
            _ready.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            LogService.LogEvent("WARNING 护航：系统请求线程启动失败 " + ex.Message);
        }

        // 3) 夜间息屏
        ScheduleNightScreen();
    }

    /// <summary>队列结束/停止/悬置时调用；重复调用幂等。</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (!_active) return;
            _active = false;
        }

        try { _nightTimer?.Dispose(); } catch { }
        _nightTimer = null;
        NightScreenAt = null;

        try
        {
            if (_workerDispatcher != null)
                _workerDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_blockWindow != IntPtr.Zero)
                        {
                            ShutdownBlockReasonDestroy(_blockWindow);
                            LogService.LogEvent("护航：重启阻断已注销");
                        }
                    }
                    catch { }
                    try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }));
        }
        catch { }

        try { _worker?.Join(1500); } catch { }
        _worker = null;
        _workerDispatcher = null;
        _blockWindow = IntPtr.Zero;
        SleepBlocked = false;
        RestartBlocked = false;

        // 3) 恢复屏保
        try
        {
            int original = AppState.Settings.KeepAwakeScreenSaverOriginal;
            if (original == 1)
            {
                if (SetScreenSaverActive(true))
                {
                    AppState.Settings.KeepAwakeScreenSaverOriginal = -1;
                    AppState.SaveSettings();
                    ScreenSaverSuppressed = false;
                    LogService.LogEvent("护航：屏保已恢复（原值=开启）");
                }
                else
                {
                    LogService.LogEvent("WARNING 护航：屏保恢复失败，将在下次启动补偿");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("WARNING 护航：屏保恢复异常 " + ex.Message);
        }

        LogService.LogEvent("护航：结束，系统设置已归位");
    }

    /// <summary>夜间息屏设置变化时重排（队列未运行则忽略）。</summary>
    public static void RefreshNightScreen()
    {
        if (!IsActive) return;
        try { _nightTimer?.Dispose(); } catch { }
        _nightTimer = null;
        NightScreenAt = null;
        ScheduleNightScreen();
    }

    /// <summary>给盾牌悬停用的当前生效项。</summary>
    public static List<string> ActiveSummary()
    {
        var list = new List<string>();
        if (SleepBlocked) list.Add("已抑制空闲睡眠");
        if (RestartBlocked) list.Add("已注册重启阻断");
        if (ScreenSaverSuppressed) list.Add("屏保已临时关闭");
        if (NightScreenAt is DateTime at) list.Add($"夜间息屏 {at:HH:mm}");
        return list;
    }

    private static void ScheduleNightScreen()
    {
        if (!AppState.Settings.NightScreenEnabled) return;
        int minutes = Math.Clamp(AppState.Settings.NightScreenMinutes, 5, 120);
        TimeSpan delay = NightScreenDelayOverride ?? TimeSpan.FromMinutes(minutes);
        if (delay <= TimeSpan.Zero) return;
        NightScreenAt = DateTime.Now + delay;
        _nightTimer = new Timer(_ => TurnOffMonitor(minutes), null, delay, Timeout.InfiniteTimeSpan);
        LogService.LogEvent($"护航：夜间息屏已排定（{minutes} 分钟后关闭显示器）");
    }

    private static void TurnOffMonitor(int minutes)
    {
        try
        {
            if (NightScreenDryRun)
            {
                LogService.LogEvent($"DRYRUN 夜间息屏：将关闭显示器（已运行 {(NightScreenDelayOverride ?? TimeSpan.FromMinutes(minutes)).TotalSeconds:F0} 秒）");
                return;
            }
            SendMessage(new IntPtr(HWND_BROADCAST), WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
            LogService.LogEvent("护航：夜间息屏，已发送关闭显示器指令（队列继续运行）");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("WARNING 护航：关闭显示器失败 " + ex.Message);
        }
    }

    private static void WorkerLoop()
    {
        try
        {
            var src = new HwndSource(new HwndSourceParameters("DragonButlerKeepAwake")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP，隐藏窗口
                Width = 0,
                Height = 0,
            });
            _workerDispatcher = src.Dispatcher;
            _blockWindow = src.Handle;

            uint prev = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            SleepBlocked = prev != 0;
            if (SleepBlocked)
                LogService.LogEvent("护航：已请求系统保持唤醒（SetThreadExecutionState）");
            else
                LogService.LogEvent("WARNING 护航：保持唤醒请求失败");

            RestartBlocked = ShutdownBlockReasonCreate(src.Handle, "龙管家队列运行中，完成后自动恢复");
            LogService.LogEvent(RestartBlocked ? "护航：已注册重启阻断" : "WARNING 护航：重启阻断注册失败");

            _ready?.Set();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("WARNING 护航：系统请求线程异常 " + ex.Message);
            _ready?.Set();
        }
    }

    private static bool GetScreenSaverActive(out bool active)
    {
        active = false;
        // 优先读注册表：这是用户实际设置的"原值"，SPI 在部分环境会报告未启用
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? raw = key?.GetValue("ScreenSaveActive")?.ToString();
            if (raw != null)
            {
                active = raw.Trim() == "1";
                return true;
            }
        }
        catch { }

        int value = 0;
        if (!SystemParametersInfo(SPI_GETSCREENSAVEACTIVE, 0, ref value, 0)) return false;
        active = value != 0;
        return true;
    }

    private static bool SetScreenSaverActive(bool active)
    {
        int value = active ? 1 : 0;
        bool ok = SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, 0, ref value, 0);
        try
        {
            // 写注册表作为兜底（SPI 在个别环境会返回失败），原值已在 Acquire 记录，Release 会恢复
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            key?.SetValue("ScreenSaveActive", active ? "1" : "0", RegistryValueKind.String);
            return true;
        }
        catch { return ok; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
