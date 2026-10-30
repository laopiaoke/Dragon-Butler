using System;
using System.Diagnostics;
using System.Windows;
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
    private wf.ToolStripMenuItem? _miOpen, _miStart, _miExit;
    private MainWindow? _win;

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
        AppState.OnStatusChanged += _ => UpdateTrayMenu(); // 托盘菜单跟随运行状态
        Theme.ApplyFromSettings();

        // 全库唯一 Webhook 监听 + 组归档接驳（与完成判定解耦）
        try
        {
            WebhookHub.Start(AppState.Settings.BgiWebhookPort);
            GroupArchiveWatcher.Start();
        }
        catch (Exception ex) { LogService.LogEvent("事件总线启动失败：" + ex.Message); }

        _win = new MainWindow();
        _win.Show();

        // v1.5 晨报：每天首次启动播报一句（默认关）
        MorningReportService.MaybeShow();

        BuildTray();

        if (AppState.Settings.SentinelEnabled) SentinelService.Start();

        // 软件启动后自动跑一条龙
        if (AppState.Settings.AutoRunOnLaunch)
        {
            LogService.LogEvent("开机选项：软件启动后自动开跑队列");
            _ = QueueService.StartAsync(QueueTrigger.Manual);
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
            _miExit = new wf.ToolStripMenuItem("退出", null, (_, _) =>
            {
                SoundService.Play("click.wav");
                ForceExit = true;
                Shutdown();
            });
            menu.Items.Add(_miOpen);
            menu.Items.Add(_miStart);
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
        }
        catch { }
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

    /// <summary>弹托盘气泡（自动切回 UI 线程）</summary>
    public void ShowBalloon(NotifyLevel level, string title, string text)
    {
        try
        {
            var d = Dispatcher;
            if (!d.CheckAccess())
            {
                d.BeginInvoke(new Action(() => ShowBalloon(level, title, text)));
                return;
            }
            if (_tray == null) return;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = level switch
            {
                NotifyLevel.Warning => wf.ToolTipIcon.Warning,
                NotifyLevel.Critical => wf.ToolTipIcon.Error,
                _ => wf.ToolTipIcon.Info,
            };
            _tray.ShowBalloonTip(4000);
        }
        catch { }
    }

    private void ShowMainWindow()
    {
        try
        {
            if (_win == null) return;
            _win.Show();
            if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
            _win.Activate();
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
