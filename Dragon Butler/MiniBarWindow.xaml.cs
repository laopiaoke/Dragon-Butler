using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DragonButler;

/// <summary>
/// 迷你浮条（v1.8 / F11）。
///
/// 为什么需要它：主界面是一个 980×790 的大窗。挂着跑一整晚的时候，
/// 用户要的只是「现在到哪一步了」——为此把整个主界面摊在游戏旁边不值当。
/// 浮条把它压成 180×44 的一角，抬眼就能看见，不挡鼠标、不抢焦点、不进任务栏。
///
/// 设计约束（来自路线图第九节）：
/// · 只用 CardBg / Border / TextPrimary / Brand 四个色键，不做新配色，深浅主题自动跟随
/// · 圆角 14、字号 12.5 / 11，与主界面同一标尺，放在主界面旁边不违和
/// · Topmost 但 ShowActivated=false 的等效做法：不抢焦点（不设 Focusable、不 Activate）
/// </summary>
public partial class MiniBarWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _dragging;
    private Point _dragStart;
    private double _startLeft, _startTop;

    public MiniBarWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            RestorePosition();
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>把浮条放回上次的位置；从没放过就贴屏幕右上角。</summary>
    private void RestorePosition()
    {
        var s = AppState.Settings;
        double left = s.MiniBarLeft;
        double top = s.MiniBarTop;

        if (left < 0 || top < 0)
        {
            var area = SystemParameters.WorkArea;
            left = area.Right - Width - 24;
            top = area.Top + 24;
        }

        // 显示器被拔掉后旧坐标会落在屏幕外，夹回可见区域
        left = Math.Max(0, Math.Min(left, SystemParameters.VirtualScreenWidth - Width));
        top = Math.Max(0, Math.Min(top, SystemParameters.VirtualScreenHeight - 40));
        Left = left;
        Top = top;
    }

    private void Refresh()
    {
        try
        {
            bool running = QueueService.IsRunning;
            int done = QueueService.DoneToolIdsToday().Count;
            int left = QueueService.UnfinishedToday().Count;
            int total = done + left;

            string toolName = "";
            if (running && !string.IsNullOrEmpty(QueueService.CurrentToolId))
            {
                try { toolName = ProfileRegistry.Get(QueueService.CurrentToolId).DisplayName; }
                catch { }
            }

            // v1.13：浮条不再只报「3/5 · BetterGI」。
            // 那个数字在真正挂机的时候几乎不动，看久了等于没信息；
            // 用户抬眼想知道的是「现在在刷哪个组、刷了多久、还要多久」。
            if (running)
            {
                var h = SentinelService.HealthSnapshot();
                var snap = LiveStatus.Build(
                    true,
                    toolName,
                    h.CurrentGroup,
                    QueueService.CurrentToolStartedAt == default ? null : QueueService.CurrentToolStartedAt,
                    h.CurrentGroupSince == default ? null : h.CurrentGroupSince,
                    h.CurrentGroup.Length > 0 ? GroupArchive.MedianMinutes(h.CurrentGroup) : null,
                    DateTime.Now);

                // 主行：组名（没有组就是工具名）。队列进度挪到次行，信息密度反而更高。
                TbTitle.Text = snap.Headline;
                TbDetail.Text = BuildDetail(snap, done, total, running);
                ToolTip = LiveStatus.Tooltip(snap, done, total);
            }
            else
            {
                TbTitle.Text = "一条龙管家 · 空闲";
                TbDetail.Text = done > 0 ? $"今天已完成 {done} 项" : "右键托盘或点这里打开";
                ToolTip = null;
            }

            BtnClose.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

            if (running && total > 0)
            {
                BarHost.Visibility = Visibility.Visible;
                // 用 ActualWidth 会滞后一帧，直接按窗口宽减内边距算
                double track = Math.Max(ActualWidth - 22, 0);
                BarFill.Width = Math.Max(track * QueueProgress.Fraction(done, total), 0);
            }
            else
            {
                BarHost.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // 浮条出错绝不能拖累队列
        }
    }

    /// <summary>
    /// 次行：能说「还要多久」就说，说不了就退回到队列进度。
    /// 浮条只有 180 宽，一行放不下两件事——必须先挑最有用的一件。
    /// 而且不能直接用 LiveStatus.Detail：那句是给主窗实时条写的完整句
    /// （「还要 2 分 · 预计 23:03 结束」），在 180 里会被截掉后半截，
    /// 所以走 Brief 的短版措辞。
    /// </summary>
    private static string BuildDetail(LiveStatus.Snapshot snap, int done, int total, bool running)
    {
        if (!running) return "";
        string brief = LiveStatus.Brief(snap, DateTime.Now);
        if (brief.Length > 0) return brief;
        if (total > 0) return $"队列 {done}/{total} · 还剩 {Math.Max(total - done, 0)} 项";
        return "准备中…";
    }

    /// <summary>按住拖动；松手后记住位置（下次启动还在原地）。</summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            App.Instance?.ShowMainWindowPublic();
            return;
        }

        _dragging = true;
        _dragStart = PointToScreen(e.GetPosition(this));
        _startLeft = Left;
        _startTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var now = PointToScreen(e.GetPosition(this));
        Left = _startLeft + (now.X - _dragStart.X);
        Top = _startTop + (now.Y - _dragStart.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        AppState.Settings.MiniBarLeft = Left;
        AppState.Settings.MiniBarTop = Top;
        AppState.SaveSettings();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.MiniBarEnabled = false;
        AppState.SaveSettings();
        LogService.LogEvent("浮条：已关闭");
        Close();
    }
}
