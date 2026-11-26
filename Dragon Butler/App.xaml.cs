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
    private wf.NotifyIcon? _tray;
    private wf.ToolStripMenuItem? _miOpen, _miStart, _miContinue, _miSnapshot, _miExit;
    private MainWindow? _win;
    private bool _trimming;
    internal bool IsTrimming => _trimming;
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private wf.Timer? _trimTimer;    // 收进托盘后那次延迟销毁

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

        _win = new MainWindow();
        _win.Show();
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
        // 开机自启时要不要把窗口收进托盘，由 StartMinimized 决定（见 BuildTray 之后）。
        if (AppState.Settings.StartMinimized && AppState.Settings.StayInTray)
        {
            LogService.LogEvent("启动时收进托盘（StartMinimized=开）");
            _win.Hide();
        }
    }

    private void BuildTray()
    {
        try
        {
            _tray = new wf.NotifyIcon { Text = "一条龙管家", Visible = true };

            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            }
            catch { }
            if (_tray.Icon == null)
            {
                try { _tray.Icon = System.Drawing.SystemIcons.Application; }
                catch { }
            }

            var menu = new wf.ContextMenuStrip();
            // 深色菜单渲染器：跟随主题的配色表，消除默认白条/白底
            menu.Renderer = new wf.ToolStripProfessionalRenderer(new ThemedMenuColors());

            _miOpen = new wf.ToolStripMenuItem("打开主界面", null, (_, _) =>
            {
                SoundService.Play("click.wav");
                ShowMainWindow();
            });
            _miStart = new wf.ToolStripMenuItem("⚡ 一键开跑", null, (_, _) =>
            {
                SoundService.Play("click.wav");   // 与主界面统一：托盘菜单也是普通点击音
                _ = QueueService.StartAsync(QueueTrigger.Manual);
            });
            // v1.6 / F2：只在「今天跑成过至少一项，且还有没跑成的」时出现
            _miContinue = new wf.ToolStripMenuItem("接着跑", null, (_, _) =>
            {
                SoundService.Play("click.wav");
                _ = QueueService.StartContinueAsync();
            });
            _miContinue.Visible = false;
            // v1.8 / F12：随时抓一张当前画面——人不在电脑前时，这是唯一能"看一眼"的入口
            _miSnapshot = new wf.ToolStripMenuItem("现在就到（抓一张当前画面）", null, (_, _) =>
            {
                SoundService.Play("click.wav");
                _ = Task.Run(() =>
                {
                    string msg = SnapshotService.CaptureMessage("托盘菜单");
                    NotifyService.Show(NotifyLevel.Info, "龙管家", msg);
                });
            });
            _miExit = new wf.ToolStripMenuItem("退出", null, (_, _) =>
            {
                SoundService.Play("click.wav");
                ForceExit = true;
                Shutdown();
            });
            menu.Items.Add(_miOpen);
            menu.Items.Add(_miStart);
            menu.Items.Add(_miContinue);
            menu.Items.Add(_miSnapshot);
            menu.Items.Add(new wf.ToolStripSeparator());
            menu.Items.Add(_miExit);
            menu.Opening += (_, _) => UpdateTrayMenu(); // 展开前刷新状态
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowMainWindow();
            RecolorMenu();
            UpdateTrayMenu();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("托盘初始化失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 刷新托盘菜单的运行状态：BGI 运行中时"启动一条龙"变为"一条龙运行中…"并禁用（防止误操作）
    /// </summary>
    private void UpdateTrayMenu()
    {
        try
        {
            if (_miStart == null) return;
            bool running = QueueService.IsRunning;
            _miStart.Enabled = !running;
            _miStart.Text = running ? "队列运行中…（请先在主界面停止）" : "⚡ 一键开跑";

            // 分子分母共用一份台账（RunJournal），与任务栏进度、接着跑按钮三处同源
            int doneToday = QueueService.DoneToolIdsToday().Count;
            // 跑动中也要算剩余数，否则进度分母退化成分子、进度条永远显示满
            int leftToday = QueueService.UnfinishedToday().Count;

            if (_miContinue != null)
            {
                // 菜单项只在「没在跑 + 今天跑成过至少一项 + 还有没跑成的」时出现
                bool showContinue = !running && doneToday > 0 && leftToday > 0;
                _miContinue.Visible = showContinue;
                _miContinue.Enabled = showContinue;
                _miContinue.Text = leftToday > 0 ? $"接着跑（今天还剩 {leftToday} 项）" : "接着跑";
            }

            // v1.8 / F10：托盘悬停文字带上进度。不新增菜单项——
            // 托盘菜单是「偶尔点一下」的地方，塞满反而难找；
            // 进度属于「扫一眼」，悬停提示才是它的正确位置。
            if (_tray != null)
            {
                _tray.Text = running
                    ? QueueProgress.TrayText(true, doneToday, doneToday + leftToday, CurrentToolName())
                    : "一条龙管家";
            }
        }
        catch { }
    }

    /// <summary>当前正在跑的工具显示名（没在跑就返回空串）。</summary>
    private static string CurrentToolName()
    {
        try
        {
            string id = QueueService.CurrentToolId;
            if (string.IsNullOrEmpty(id)) return "";
            return ProfileRegistry.Get(id).DisplayName;
        }
        catch { return ""; }
    }

    /// <summary>主题切换后重新着色托盘菜单（深浅主题即时跟随）</summary>
    public void RecolorMenu()
    {
        try
        {
            if (_tray?.ContextMenuStrip == null) return;
            var back = Theme.C("WindowBg");
            var fore = Theme.C("TextPrimary");
            _tray.ContextMenuStrip.BackColor = back;
            _tray.ContextMenuStrip.ForeColor = fore;
            foreach (wf.ToolStripItem item in _tray.ContextMenuStrip.Items)
            {
                item.BackColor = back;
                item.ForeColor = fore;
            }
        }
        catch { }
    }

    /// <summary>
    /// 刷新消息中心红点（v1.28）。消息中心在各处写消息时会回调这里；
    /// 主窗可能还没建好、也可能已经被收进托盘销毁了，两种情况都要安静地跳过。
    /// </summary>
    public void RefreshNoticeBadge()
    {
        try
        {
            if (_win == null) return;
            _win.RefreshNoticeBadge();
        }
        catch { }
    }

    /// <summary>
    /// 弹托盘气泡（自动切回 UI 线程）。
    ///
    /// <para>
    /// v1.43 / 任务五：返回「到底弹出来了没有」。
    /// 原来这个方法返回 void，「气泡通道」永远只能报成功——托盘图标还没建好、
    /// 系统把通知关掉了，用户一条也没看见，测试却显示通过。
    /// </para>
    /// </summary>
    /// <returns>true = 已交给托盘；false = 托盘没建好、跨线程调度失败或系统拒收。</returns>
    public bool ShowBalloon(NotifyLevel level, string title, string text)
    {
        try
        {
            var d = Dispatcher;
            if (!d.CheckAccess())
            {
                // Invoke 而不是 BeginInvoke：调用方要拿到结果，排队发出去就没法回话了。
                // 这里不会跟 UI 线程互相等待——UI 线程不会同步等一个后台推送任务。
                return (bool)d.Invoke(new Func<bool>(() => ShowBalloon(level, title, text)));
            }
            if (_tray == null) return false;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = level switch
            {
                NotifyLevel.Warning => wf.ToolTipIcon.Warning,
                NotifyLevel.Critical => wf.ToolTipIcon.Error,
                _ => wf.ToolTipIcon.Info,
            };
            _tray.ShowBalloonTip(4000);
            return true;
        }
        catch (Exception ex)
        {
            try { LogService.LogEvent("托盘气泡没弹出来：" + ex.Message); } catch { }
            return false;
        }
    }

    private void ShowMainWindow()
    {
        try
        {
            CancelTrim();
            // v1.21 / 内存方案 B：主窗可能已经被销毁了，这时重建一个（约 1 秒）。
            if (_win == null) _win = new MainWindow();
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

    /// <summary>
    /// 托盘菜单配色表（SPBGI）：全部取主题十色键，渲染时实时查询，
    /// 深浅主题切换即时生效，消除默认渲染器的白底/白条。
    /// </summary>
    private sealed class ThemedMenuColors : wf.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => Theme.C("CardBg");
        public override System.Drawing.Color MenuBorder => Theme.C("Border");
        public override System.Drawing.Color MenuItemBorder => Theme.C("Brand");
        public override System.Drawing.Color MenuItemSelected => Theme.C("Hover");
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Theme.C("Hover");
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Theme.C("Hover");
        public override System.Drawing.Color MenuItemPressedGradientBegin => Theme.C("Hover");
        public override System.Drawing.Color MenuItemPressedGradientEnd => Theme.C("Hover");
        public override System.Drawing.Color SeparatorDark => Theme.C("Border");
        public override System.Drawing.Color SeparatorLight => Theme.C("CardBg");
        public override System.Drawing.Color ToolStripBorder => Theme.C("Border");
        public override System.Drawing.Color ImageMarginGradientBegin => Theme.C("CardBg");
        public override System.Drawing.Color ImageMarginGradientMiddle => Theme.C("CardBg");
        public override System.Drawing.Color ImageMarginGradientEnd => Theme.C("CardBg");
    }
}
