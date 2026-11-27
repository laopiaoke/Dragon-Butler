using System;
using wf = System.Windows.Forms;

namespace DragonButler;

/// <summary>
/// App 的托盘部分（v1.46.1 按功能拆出）：托盘图标、右键菜单、菜单配色、气泡通知，
/// 以及「收进托盘」那次延迟销毁用的定时器。
///
/// 拆出来的理由：这些代码全都要靠 System.Windows.Forms 的类型，与启动流程（WPF 的窗口和调度）
/// 混在一个文件里，读的人得在两套 UI 体系之间来回切。拆开之后 App.xaml.cs 只剩「启动做什么」，
/// 这里只剩「托盘上有什么」。同属 partial class App，行为与调用点一个都没变。
/// </summary>
public partial class App
{
    private wf.NotifyIcon? _tray;
    private wf.ToolStripMenuItem? _miOpen, _miStart, _miContinue, _miSnapshot, _miExit;
    private wf.Timer? _trimTimer;    // 收进托盘后那次延迟销毁

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