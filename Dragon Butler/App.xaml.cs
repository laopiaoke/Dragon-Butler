using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using wf = System.Windows.Forms;

namespace DragonButler;

/// <summary>
/// 应用入口（SPBGI）：单实例（会话级 Mutex）、全局异常兜底、托盘常驻、
/// 主窗创建、哨兵启动、音效初始化。ShutdownMode=OnExplicitShutdown，主窗由代码创建。
/// </summary>
public partial class App : Application
{
    /// <summary>全局实例（供主题重着色托盘等使用）</summary>
    public static App? Instance { get; private set; }

    /// <summary>托盘退出标志：为 true 时关窗不再驻留托盘，直接放行退出</summary>
    public static bool ForceExit { get; set; }

    /// <summary>产品版本（取自 csproj 的 Version，日志/README 保持一致）</summary>
    public static string ProductVersion =>
        typeof(App).Assembly.GetName().Version is { } v ? v.ToString(3) : "1.0.0";

    private static Mutex? _mutex;
    private MainWindow? _win;
    private bool _trimming;
    internal bool IsTrimming => _trimming;
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    protected override void OnStartup(StartupEventArgs e)
    {
        Instance = this;

        // 单实例 Mutex 是会话级：多用户场景 A、B 会话各自可跑一个实例
        int session = Process.GetCurrentProcess().SessionId;
        _mutex = new Mutex(true, "DragonButler_SI_" + session, out bool createdNew);
        if (!createdNew)
        {
            // 统一走应用内弹窗（主题色/字体/图标与主界面一致），不再用系统 MessageBox
            AppDialog.Show(
                "一条龙管家已在当前用户会话运行，请看右下角托盘。",
                kind: AppDialog.Kind.Info,
                title: "一条龙管家",
                primary: "知道了");
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 全局异常兜底（含上次异常退出检测）
        CrashGuard.Install();
        LogService.LogEvent($"启动：Dragon Butler v{ProductVersion}");

        // 音效初始化：程序化生成 WAV，失败仅记日志，功能层零影响
        try { SoundService.EnsureSounds(); }
        catch (Exception ex) { LogService.LogEvent("音效初始化失败：" + ex.Message); }

        AppState.Init();
        KeepAwakeService.CompensateOnStartup();

        // v1.38：开机自启对账。用户上次勾了「开机自启」，但那个值可能已经失效——
        // 便携版文件夹被挪了（值还指着旧路径）、被任务管理器的「启动」页禁用过
        // （StartupApproved 里留了 12 字节禁用标记）、或者被系统清理工具删了。
        // 这三种情况下勾子都还是亮的，用户以为开着，实际开机什么都不会发生。
        // 所以每次启动对一次账：只要用户要的是「开」，值不对就补写回去。
        try
        {
            string? healNote = AutoStartHelper.Heal(AppState.Settings.AutoStartOnBoot);
            if (healNote != null) LogService.LogEvent(healNote);
        }
        catch (Exception ex) { LogService.LogEvent("开机自启对账失败：" + ex.Message); }

        AppState.OnStatusChanged += _ => UpdateTrayMenu(); // 托盘菜单跟随运行状态
        Theme.ApplyFromSettings();

        // 全库唯一 Webhook 监听 + 组归档接驳（与完成判定解耦）
        try
        {
            // v1.7：局域网面板打开时同时监听所有网卡（失败自动退回回环，见 WebhookListener.Start）
            AppState.EnsureControlToken();
            WebhookHub.Start(AppState.Settings.BgiWebhookPort, AppState.Settings.LanPanelEnabled);
            GroupArchiveWatcher.Start();
        }
        catch (Exception ex) { LogService.LogEvent("事件总线启动失败：" + ex.Message); }

        // v1.46.1：主窗怎么上屏，在 Show() 之前就要定下来。
        //
        // 以前是 Show() 之后一路刷红点、播两张报告、建托盘、起调度定时器，
        // 最后才 Hide()——中间那几毫秒足够首帧画到屏幕上，用户看到的是「闪一下才消失」。
        // 现在「要不要藏」先算好，四个属性全部在 Show() 之前摆完，见 StartupPresentation。
        bool hiddenStart = StartupPresentation.ShouldStartHidden(
            AppState.Settings.StartMinimized, AppState.Settings.StayInTray);
        // 首启向导还没走完就别藏：那个向导本身是个窗口，藏起来启动等于把「第一次怎么用」
        // 也一起藏了。实际走不到这一步（StartMinimized 只能在设置页或向导里打开，
        // 那两处都会先把 FirstRunDone 落成 true），留着是为了别让组合意外把用户关在门外。
        if (hiddenStart && !AppState.Settings.FirstRunDone)
        {
            hiddenStart = false;
            LogService.LogEvent("启动时收进托盘：首启向导还没走完，本次先正常显示");
        }

        _win = new MainWindow();
        // 手动建窗就必须手动登记：ShutdownMode=OnExplicitShutdown 之下这条不算退出判定，
        // 但 AppDialog 之类会去读 Application.Current.MainWindow 定父窗，漏了会静默拿不到主人。
        MainWindow = _win;
        if (hiddenStart) StartupPresentation.PrepareHidden(_win);
        _win.Show();
        if (hiddenStart)
        {
            // 与 Show() 同一次调用里收掉：消息泵还没转过一圈，画不出任何一帧。
            // 仍要 Hide() 而不是只靠 Minimized——界面定时器看的是 IsVisible，
            // 一直「可见但最小化」等于后台空转四个 DispatcherTimer。
            _win.Hide();
            LogService.LogEvent("启动时收进托盘（StartMinimized=开，首帧前置隐藏）");
        }
        // v1.28：主窗建好之后才刷红点——之前 CrashGuard 记的「上次没收好尾」
        // 就是这时候才亮起来的，早于这一刻控件还不存在。
        RefreshNoticeBadge();

        // v1.5 晨报：每天首次启动播报一句（默认关）
        MorningReportService.MaybeShow();

        // v1.7 周报：每周首次启动播报一句（默认关）
        WeeklyReportService.MaybeShow();

        BuildTray();
        _scheduleTimer.Tick += (_, _) => ScheduleTrigger.Check(DateTime.Now);
        _scheduleTimer.Start();

        if (AppState.Settings.SentinelEnabled) SentinelService.Start();

        // v1.38：「软件启动后自动跑」删了。
        //
        // 它和定时一条龙是两套独立的触发：开着定时又开着这个，早上定时跑完，
        // 晚上一开机再跑一遍，两次之间还会互相盖掉「上次跑过」的记录。
        // 想开机就跑，正确组合是「开机自启 + 定时」——同一件事，只算一次。
        //
        // 开机自启时要不要把窗口收进托盘，已在前面建窗时按 StartMinimized 决定（见 StartupPresentation）。
        // 放到这里再 Hide() 会有两个问题：中间那几行已经把首帧放出去了；而且这一段的
        // 判据只覆盖「本来显示过又藏回去」，藏不掉的痕迹（任务栏按钮一闪）会留下。
    }

    private void ShowMainWindow()
    {
        try
        {
            CancelTrim();
            // v1.21 / 内存方案 B：主窗可能已经被销毁了，这时重建一个（约 1 秒）。
            if (_win == null) _win = new MainWindow();
            // v1.46.1：藏起来启动时改过 ShowInTaskbar / ShowActivated / Visibility，
            // 这里按相反方向还原——只 Show() 不还原，会得到「出现了但没任务栏按钮」的怪窗口。
            StartupPresentation.RestoreShown(_win);
            _win.Show();
            if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
            _win.Activate();
        }
        catch { }
    }

    /// <summary>
    /// v1.21 / 内存方案 B：主窗收进托盘后，把窗口本身销毁，只留托盘图标。
    ///
    /// 实测数据（docs/plans/内存调研-计划.md）：Show 过的窗口比隐藏状态多占 3.4 MB 私有内存，
    /// 而这台机器上从「空进程」到「主窗画出来」一共涨了 28 MB——那 28 MB 里绝大部分
    /// 是渲染管线、字体光栅和纹理，不是控件对象。控件摘掉只省 84 KB，窗口销毁才省得下来。
    ///
    /// 代价是下次打开要重建：实测 1049 ms。所以这里只做「延迟一拍」，
    /// 期间用户要是又点开了（双击托盘 / 浮条 / 命令面板），就整个放弃这次销毁。
    /// 队列正在跑的时候不销毁——那会有半秒左右的托盘菜单迟钝，还不值得。
    /// </summary>
    public void TrimToTray()
    {
        try
        {
            if (_win == null || _trimming) return;
            if (!AppState.Settings.TrimToTrayEnabled) return;   // 默认关：实测只省 3.7 MB，不值得 1.4 秒
            _trimTimer?.Dispose();
            _trimTimer = new wf.Timer { Interval = 900 };
            _trimTimer.Tick += (_, _) =>
            {
                _trimTimer?.Stop();
                _trimTimer?.Dispose();
                _trimTimer = null;

                var w = _win;
                if (w == null || w.IsVisible || QueueService.IsRunning) return;

                _trimming = true;
                try
                {
                    w.PrepareForTrim();      // 必须先停 timer，否则闭包抓着窗口，销毁也回收不掉
                    w.Close();
                }
                catch (Exception ex) { LogService.LogEvent("销毁主窗失败：" + ex.Message); }
                finally
                {
                    _trimming = false;
                    if (ReferenceEquals(_win, w)) _win = null;
                }
            };
            _trimTimer.Start();
        }
        catch (Exception ex) { LogService.LogEvent("安排收进托盘失败：" + ex.Message); }
    }

    /// <summary>有人要打开主窗了，撤销这次销毁。</summary>
    private void CancelTrim()
    {
        try
        {
            if (_trimTimer == null) return;
            _trimTimer.Stop();
            _trimTimer.Dispose();
            _trimTimer = null;
        }
        catch { }
    }

    /// <summary>供浮条双击调用（浮条是独立窗口，自己不持有主窗引用）。</summary>
    public void ShowMainWindowPublic() => ShowMainWindow();

    protected override void OnExit(ExitEventArgs e)
    {
        _scheduleTimer.Stop();
        try
        {
            KeepAwakeService.Release();
            SentinelService.Stop();
            GroupArchiveWatcher.Stop();
            WebhookHub.Stop();
            CrashGuard.Shutdown();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        // v1.43 / 任务五：通知重试是后台任务，退出时要能喊停。
        // 不喊停的话，用户在「重试等待」那几秒里关程序，进程会多挂一会儿等 Task.Delay 走完。
        NotifyService.StopPendingSends();
        base.OnExit(e);
    }

}
