using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using wf = System.Windows.Forms;

namespace DragonButler;

/// <summary>
/// App 的托盘部分（v1.46.1 按功能拆出）：托盘图标、右键菜单、气泡通知，
/// 以及「收进托盘」那次延迟销毁用的定时器。
///
/// 拆出来的理由：这些代码全都要靠 System.Windows.Forms 的类型，与启动流程（WPF 的窗口和调度）
/// 混在一个文件里，读的人得在两套 UI 体系之间来回切。拆开之后 App.xaml.cs 只剩「启动做什么」，
/// 这里只剩「托盘上有什么」。同属 partial class App，行为与调用点一个都没变。
///
/// v1.46.2 / 任务书 #18：右键菜单从 WinForms 的托盘菜单控件换成 WPF 的 ContextMenu。
/// 图标本体（NotifyIcon）仍然是 WinForms 的——托盘区本来就是它的地盘，换掉等于重写一遍托盘；
/// 但**菜单**是画出来的东西，必须跟主界面同一套视觉语言，所以只换菜单这一层。
/// 换掉之后左侧那条空白自然消失：WinForms 那边它叫 ImageMargin（默认留一列图标位，项目从没用过），
/// 而 WPF 的模板里压根没有这一列。样式见 Themes/TrayMenuStyle.xaml。
/// </summary>
public partial class App
{
    private wf.NotifyIcon? _tray;
    private ContextMenu? _menu;
    private MenuItem? _miOpen, _miStart, _miContinue, _miSnapshot, _miExit;
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

            EnsureTrayMenuStyles();
            _menu = BuildTrayMenu();

            // 右键才弹菜单；左键保持原样（双击开主界面，见下）。
            // NotifyIcon 没有 WPF 的 ContextMenu 属性，只能自己接 MouseUp 再摆位置。
            _tray.MouseUp += (_, e) =>
            {
                if (e.Button == wf.MouseButtons.Right) ShowTrayMenu();
            };
            _tray.DoubleClick += (_, _) => ShowMainWindow();
            UpdateTrayMenu();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("托盘初始化失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把 Themes/TrayMenuStyle.xaml 并进应用资源。只在第一次进来时并一次。
    ///
    /// <para>
    /// 为什么并到应用级：弹出层是独立的可视树，挂在它上面的 DynamicResource 要能一路找到
    /// Theme.Apply 写进去的那些刷子。并进 App.Resources 是最短的一条路。
    /// 那个文件里只准有 TrayMenu.* 前缀的键（任务书资源策略第 4 条），并进来不会盖掉任何既有键。
    /// </para>
    /// </summary>
    private static void EnsureTrayMenuStyles()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        foreach (ResourceDictionary d in res.MergedDictionaries)
        {
            if (d.Source != null && d.Source.OriginalString.Contains("TrayMenuStyle", StringComparison.OrdinalIgnoreCase))
                return;
        }
        res.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/TrayMenuStyle.xaml", UriKind.Relative),
        });
    }

    /// <summary>建一遍菜单。四个菜单项的处理函数与迁移前逐个对应，没有第二套实现。</summary>
    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu
        {
            Style = Res<Style>("TrayMenu.ContextMenuStyle"),
            StaysOpen = false,
        };

        _miOpen = MakeItem("打开主界面", () => ShowMainWindow());
        _miStart = MakeItem("⚡ 一键开跑", () => _ = QueueService.StartAsync(QueueTrigger.Manual));
        // v1.6 / F2：只在「今天跑成过至少一项，且还有没跑成的」时出现
        _miContinue = MakeItem("接着跑", () => _ = QueueService.StartContinueAsync());
        _miContinue.Visibility = Visibility.Collapsed;
        // v1.8 / F12：随时抓一张当前画面——人不在电脑前时，这是唯一能「看一眼」的入口
        _miSnapshot = MakeItem("现在就到（抓一张当前画面）", () =>
            _ = Task.Run(() =>
            {
                string msg = SnapshotService.CaptureMessage("托盘菜单");
                NotifyService.Show(NotifyLevel.Info, "龙管家", msg);
            }));
        _miExit = MakeItem("退出", () =>
        {
            ForceExit = true;
            Shutdown();
        });

        menu.Items.Add(_miOpen);
        menu.Items.Add(_miStart);
        menu.Items.Add(_miContinue);
        menu.Items.Add(_miSnapshot);
        menu.Items.Add(new Separator { Style = Res<Style>("TrayMenu.SeparatorStyle") });
        menu.Items.Add(_miExit);
        return menu;
    }

    /// <summary>
    /// 造一个菜单项。点击先收菜单再干活——收尾动作可能是关机或退出进程，
    /// 让一个还开着的弹出层挡在前面，轻则关不掉，重则进程退不干净。
    /// 音效也在这里统一放，四个项都是同一个点击音（与主界面统一）。
    /// </summary>
    private MenuItem MakeItem(string header, Action act)
    {
        var mi = new MenuItem
        {
            Header = header,
            Style = Res<Style>("TrayMenu.ItemStyle"),
        };
        mi.Click += (_, _) =>
        {
            CloseTrayMenu();
            SoundService.Play("click.wav");
            // Click 是在弹出层自己的嵌套消息循环里跑的：这个循环要等处理器返回才收尾。
            // 在里面同步 Shutdown()，OnExit 会照常跑完、日志也写了「正常退出」，
            // 但那个循环还压在栈上，进程就留在原地不走了（v1.46.2 实测踩到）。
            // 排到下一轮消息泵再执行，弹层先收干净，退出/关机才真的开始。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { act(); }
                catch (Exception ex) { LogService.LogEvent("托盘菜单动作失败：" + ex.Message); }
            }), DispatcherPriority.Background);
        };
        return mi;
    }

    private static T? Res<T>(string key) where T : class
    {
        try { return Application.Current?.TryFindResource(key) as T; }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void ShowTrayMenu()
    {
        try
        {
            if (_menu == null) return;
            UpdateTrayMenu();
            // 菜单没有宿主窗口，用鼠标位置定位；这是 WPF 托盘菜单的常规做法。
            _menu.Placement = PlacementMode.MousePoint;
            _menu.IsOpen = true;
            // 弹出这一刻还处在 WinForms 的托盘消息里：键盘焦点还在任务栏（实测前台窗口是
            // Shell_TrayWnd），菜单看得见，但按 Esc 没反应、点到别处也不收——那两件事都靠键盘焦点
            // 与窗口激活。所以弹出后把焦点交给菜单自己的弹出窗口，WPF 的默认行为才真的生效。
            // 这不是自造关闭逻辑：Esc 与失焦仍然是 ContextMenu 自己的处理。
            // 弹出这一刻还处在 WinForms 的托盘消息里：键盘焦点还在任务栏（实测前台窗口是
            // Shell_TrayWnd），菜单看得见，但按 Esc 没反应、点到别处也不收——那两件事都靠键盘焦点
            // 与窗口激活。所以弹出后把焦点交给菜单自己的弹出窗口，WPF 的默认行为才真的生效。
            // 这不是自造关闭逻辑：Esc 与失焦仍然是 ContextMenu 自己的处理。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (PresentationSource.FromVisual(_menu) is System.Windows.Interop.HwndSource src)
                        SetForegroundWindow(src.Handle);
                    if (_menu.Items.Count > 0 && _menu.Items[0] is MenuItem first)
                    {
                        first.Focus();
                        Keyboard.Focus(first);
                    }
                }
                catch { }
            }), DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("托盘菜单弹出失败：" + ex.Message);
        }
    }

    private void CloseTrayMenu()
    {
        try { if (_menu != null) _menu.IsOpen = false; }
        catch { }
    }

    /// <summary>
    /// 刷新托盘菜单的运行状态：队列在跑时「一键开跑」变成说明文字并禁用（防止误操作）。
    /// </summary>
    private void UpdateTrayMenu()
    {
        try
        {
            if (_miStart == null) return;
            bool running = QueueService.IsRunning;
            _miStart.IsEnabled = !running;
            _miStart.Header = running ? "队列运行中…（请先在主界面停止）" : "⚡ 一键开跑";

            // 分子分母共用一份台账（RunJournal），与任务栏进度、接着跑按钮三处同源
            int doneToday = QueueService.DoneToolIdsToday().Count;
            // 跑动中也要算剩余数，否则进度分母退化成分子、进度条永远显示满
            int leftToday = QueueService.UnfinishedToday().Count;

            if (_miContinue != null)
            {
                // 菜单项只在「没在跑 + 今天跑成过至少一项 + 还有没跑成的」时出现
                bool showContinue = !running && doneToday > 0 && leftToday > 0;
                _miContinue.Visibility = showContinue ? Visibility.Visible : Visibility.Collapsed;
                _miContinue.IsEnabled = showContinue;
                _miContinue.Header = leftToday > 0 ? $"接着跑（今天还剩 {leftToday} 项）" : "接着跑";
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

    /// <summary>
    /// 主题切换的挂钩（Theme.Apply 每次换色都会叫一声）。
    ///
    /// <para>
    /// v1.46.2 起这里是空的：菜单的颜色全部走 DynamicResource，Theme.Apply 把新刷子写进应用资源
    /// 的那一刻，菜单下次弹出就已经是新颜色，不需要再逐项刷一遍。
    /// 方法留着是因为调用点在 Theme.cs，删掉它等于为了这点小事去改主题服务。
    /// </para>
    /// </summary>
    public void RecolorMenu()
    {
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
}
