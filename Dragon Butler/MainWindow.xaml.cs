using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>配置组勾选项（Order = 执行序号，0 表示未勾选；勾选顺序即执行顺序）</summary>
public sealed class GroupPickItem : INotifyPropertyChanged
{
    private string _name = "";
    private bool _isChecked;
    private int _order;

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Raise(nameof(Name)); }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; Raise(nameof(IsChecked)); }
    }

    public int Order
    {
        get => _order;
        set { if (_order == value) return; _order = value; Raise(nameof(Order)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 主界面（v1.3）：三层布局 —— 队列卡（Hero）+ 工具卡（默认折叠）+ 高级设置（默认折叠）。
/// 所有工具启停统一走 <see cref="QueueService"/>，界面不再直接持有进程。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Brush DotGray = Frozen(Color.FromRgb(0x6E, 0x70, 0x78));
    private static readonly Brush DotYellow = Frozen(Color.FromRgb(0xE0, 0xA3, 0x2E));
    private static readonly Brush DotGreen = Frozen(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush DotRed = Frozen(Color.FromRgb(0xE5, 0x53, 0x53));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>自动化验证用：为 true 时不自动居中（离屏渲染）</summary>
    public static bool SuppressAutoCenter { get; set; }

    private bool _suppressToggle;
    private bool _suppressChips;
    private bool _suppressNotifyChips;
    private bool _suppressQueueUi;
    private DateTime _lastScheduleRun = DateTime.MinValue;

    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusBarTimer = new();
    private readonly DispatcherTimer _groupScanTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _uiRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly Dictionary<string, CustomToolCard> _customCards = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CustomToolCard
    {
        public Border Card = null!;
        public Button Header = null!;
        public StackPanel Body = null!;
        public TextBlock State = null!;
        public TextBox Path = null!;
        public TextBlock Hint = null!;
        public Image Icon = null!;
        public TextBlock IconFallback = null!;
    }

    private readonly List<GroupPickItem> _groupPicks = new();
    private readonly List<string> _selectedOrder = new();
    private string _groupSignature = "\u0000";
    private int _dragFrom = -1;

    public MainWindow()
    {
        InitializeComponent();

        AppState.OnLog += OnLogMessage;
        QueueService.StateChanged += OnQueueStateChanged;
        QueueService.StatusChanged += OnQueueStatusText;
        SentinelService.OnEvent += OnSentinelEvent;
        Theme.Changed += OnAppThemeChanged;
        Closed += (_, _) =>
        {
            Theme.Changed -= OnAppThemeChanged;
            AppState.OnLog -= OnLogMessage;
            QueueService.StateChanged -= OnQueueStateChanged;
            QueueService.StatusChanged -= OnQueueStatusText;
            SentinelService.OnEvent -= OnSentinelEvent;
        };

        _countdownTimer.Tick += (_, _) => { UpdateScheduleStatus(); RefreshShield(); };
        _groupScanTimer.Tick += (_, _) => RefreshDetectedGroups(force: true);
        _uiRefreshTimer.Tick += (_, _) => { RefreshToolCards(); UpdateQueueButtons(); };

        // 全局按键音：所有可交互元素在"按下"这一瞬出声
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(AnyInteractivePress), true);

        LoadSettingsToUi();
        RebuildCustomToolCards();
        RenderQueue();
        RefreshToolCards();
        RefreshShield();

        Loaded += (_, _) =>
        {
            AppState.RefreshStatus();
            if (SuppressAutoCenter) return;
            Dispatcher.BeginInvoke(new Action(CenterOnActiveMonitor), DispatcherPriority.Render);
            // 向导放到窗口真正显示之后再弹，避免主界面停留在"未显示"状态
            Dispatcher.BeginInvoke(new Action(TryShowFirstRunWizard), DispatcherPriority.Background);
        };

        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();
        _countdownTimer.Start();
        _groupScanTimer.Start();
        _uiRefreshTimer.Start();

        try { MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 24); } catch { }
    }

    private static void CenterOnActiveMonitor()
    {
        // WindowStartupLocation 已在 XAML 里设 CenterScreen；这里不额外移动，避免多屏时跳到主屏
    }

    private void OnAppThemeChanged()
    {
        try
        {
            ApplyNotifyMode();
            RebuildCustomToolCards();
            RenderQueue();
            RefreshShield();
            RefreshToolCards();
        }
        catch { }
    }

    // ===== 全局按键音 =====

    private void AnyInteractivePress(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var target = SoundService.FindPressTarget(e.OriginalSource as DependencyObject);
            if (target == null) return;
            switch (SoundService.PressSoundOf(target))
            {
                case SoundService.PressSound.Toggle: SoundService.Play("toggle.wav"); break;
                case SoundService.PressSound.Confirm: SoundService.Play("confirm.wav"); break;
                case SoundService.PressSound.Cancel: SoundService.Play("cancel.wav"); break;
                case SoundService.PressSound.Tick: SoundService.Play("tick.wav"); break;
                case SoundService.PressSound.Click: SoundService.Play("click.wav"); break;
            }
        }
        catch { }
    }

    // ===== 工具 =====

    private static Brush? BrushOf(string key)
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res != null && res.Contains(key) && res[key] is Brush b) return b;
        }
        catch { }
        return null;
    }

    private bool _suppressThemeSeg;

    private void SyncThemeSeg()
    {
        _suppressThemeSeg = true;
        try
        {
            string m = Theme.Mode;
            SegDark.IsChecked = m == "Dark";
            SegLight.IsChecked = m == "Light";
            SegSystem.IsChecked = m == "System";
            SegCustom.IsChecked = m == "Custom";
        }
        finally { _suppressThemeSeg = false; }
    }

    // ===== 初始化 =====

    private void LoadSettingsToUi()
    {
        var s = AppState.Settings;
        TbConfigName.Text = s.DragonConfigName;
        TbEventUrl.Text = $"http://127.0.0.1:{s.BgiWebhookPort}/webhook";
        LoadNotifyFields();

        _suppressToggle = true;
        TgSchedule.IsChecked = s.ScheduleEnabled;
        TgAutoStart.IsChecked = AutoStartHelper.IsSet();
        TgAutoRun.IsChecked = s.AutoRunOnLaunch;
        TgStayTray.IsChecked = s.StayInTray;
        TgSound.IsChecked = s.SoundEnabled;
        TgSentinel.IsChecked = s.SentinelEnabled;
        TgAutoRestart.IsChecked = s.AutoRestartBgi;
        TgAutoRestart.IsEnabled = s.SentinelEnabled;
        TgScreenshot.IsChecked = s.ScreenshotCheckEnabled;
        TgIdleGuard.IsChecked = s.IdleGuardEnabled;
        TgAutoKill.IsChecked = s.AutoKillResidual;
        TgAfterRunOnFailure.IsChecked = s.AfterRunOnFailure;
        TgKeepAwake.IsChecked = s.KeepAwakeEnabled;
        TgNightScreen.IsChecked = s.NightScreenEnabled;
        TgStallAutoKill.IsChecked = s.StallAutoKill;
        TgMorningReport.IsChecked = s.MorningReportEnabled;
        TbNightMinutes.Text = Math.Clamp(s.NightScreenMinutes, 5, 120).ToString();
        TbNightMinutes.IsEnabled = s.NightScreenEnabled;
        TgVacation.IsChecked = s.VacationMode;
        SelectComboByTag(CbAfterRun, s.AfterRunAction);
        SelectComboByTag(CbFailStrategy, s.FailStrategy);
        SelectComboByTag(CbInterval, s.QueueIntervalMinutes.ToString());
        SyncThemeSeg();
        _suppressToggle = false;

        UpdateTimeDisplay();
        SyncWeekdayChips();
        UpdateScheduleStatus();
        VacationBadge.Visibility = s.VacationMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SelectComboByTag(ComboBox box, string? tag)
    {
        try
        {
            foreach (var item in box.Items)
            {
                if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = ci;
                    return;
                }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }
        catch { }
    }

    private static string TagOf(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void UpdateTimeDisplay()
    {
        var s = AppState.Settings;
        TbTimeDisplay.Text = $"{s.ScheduleHour:00}:{s.ScheduleMinute:00}";
    }

    // ===== 定时：星期多选 / 倒计时 / 上次执行 =====

    private void SyncWeekdayChips()
    {
        _suppressChips = true;
        try
        {
            int mask = AppState.Settings.ScheduleWeekdays & 0x7F;
            ChipEvery.IsChecked = mask == 0x7F;
            foreach (var (chip, bit) in WeekdayChipPairs())
                chip.IsChecked = (mask & (1 << bit)) != 0;
        }
        finally { _suppressChips = false; }
    }

    private (ToggleButton chip, int bit)[] WeekdayChipPairs() => new[]
    {
        (ChipMon, 0), (ChipTue, 1), (ChipWed, 2), (ChipThu, 3), (ChipFri, 4), (ChipSat, 5), (ChipSun, 6),
    };

    private void WeekdayChip_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressChips) return;
        try
        {
            var chip = sender as ToggleButton;
            int mask = AppState.Settings.ScheduleWeekdays & 0x7F;
            if ((chip?.Tag as string) == "All")
                mask = ChipEvery.IsChecked == true ? 0x7F : 0;
            else if (chip?.Tag is string t && int.TryParse(t, out int bit))
                mask ^= 1 << bit;
            AppState.Settings.ScheduleWeekdays = mask;
            AppState.SaveSettings();
            SyncWeekdayChips();
            UpdateScheduleStatus();
        }
        catch { }
    }

    private void UpdateScheduleStatus()
    {
        try
        {
            var s = AppState.Settings;
            ScheduleBar.Visibility = s.ScheduleEnabled ? Visibility.Visible : Visibility.Collapsed;
            NextDot.Fill = s.ScheduleEnabled ? (BrushOf("BrandBrush") ?? DotGray) : DotGray;

            if (!s.ScheduleEnabled) TbCountdown.Text = "定时未开启";
            else
            {
                var next = NextScheduleTime();
                if (next == null) TbCountdown.Text = "距下次执行 —";
                else
                {
                    var span = next.Value - DateTime.Now;
                    if (span < TimeSpan.Zero) span = TimeSpan.Zero;
                    TbCountdown.Text = $"距下次执行 {(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
                }
            }
            TbLastRun.Text = LastRunText(s);
        }
        catch { }
    }

    private static string LastRunText(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.LastRunAt)) return "上次执行：暂无记录";
        if (!DateTime.TryParse(s.LastRunAt, out var at)) return "上次执行：暂无记录";
        string when = at.Date == DateTime.Today ? "今天" : at.Date == DateTime.Today.AddDays(-1) ? "昨天" : at.ToString("MM-dd");
        string dur = s.LastRunSeconds >= 60 ? $"{s.LastRunSeconds / 60} 分 {s.LastRunSeconds % 60} 秒" : $"{s.LastRunSeconds} 秒";
        return $"上次执行 {when} {at:HH:mm} · {dur} · {(s.LastRunOk ? "成功" : "中止")}";
    }

    private DateTime? NextScheduleTime()
    {
        var s = AppState.Settings;
        int mask = s.ScheduleWeekdays & 0x7F;
        if (mask == 0) return null;
        var now = DateTime.Now;
        for (int i = 0; i < 8; i++)
        {
            var day = now.Date.AddDays(i);
            int bit = ((int)day.DayOfWeek + 6) % 7;
            if ((mask & (1 << bit)) == 0) continue;
            var t = day.AddHours(s.ScheduleHour).AddMinutes(s.ScheduleMinute);
            if (t > now) return t;
        }
        return null;
    }

    /// <summary>每 30 秒检查一次：到点且当天没跑过 → 触发队列（定时触发）。</summary>
    private void CheckSchedule()
    {
        try
        {
            var s = AppState.Settings;
            if (!s.ScheduleEnabled || QueueService.IsRunning) return;
            if (s.VacationMode) return;
            int mask = s.ScheduleWeekdays & 0x7F;
            if (mask == 0) return;
            var now = DateTime.Now;
            int bit = ((int)now.DayOfWeek + 6) % 7;
            if ((mask & (1 << bit)) == 0) return;
            if (now.Hour != s.ScheduleHour || now.Minute != s.ScheduleMinute) return;
            if (_lastScheduleRun.Date == now.Date) return;
            _lastScheduleRun = now;
            LogService.LogEvent("定时：到点，触发队列");
            _ = QueueService.StartAsync(QueueTrigger.Timer);
        }
        catch { }
    }

    // ===== 队列卡 =====

    private void OnQueueStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenderQueue();
            RefreshToolCards();
            RefreshShield();
        }));
    }

    private void OnQueueStatusText(string text)
    {
        Dispatcher.BeginInvoke(new Action(() => { TbQueueStatus.Text = text; }));
    }

    /// <summary>重建队列列表（顺序 = 设置里的顺序）。</summary>
    private void RenderQueue()
    {
        try
        {
            _suppressQueueUi = true;
            QueueHost.Children.Clear();

            var queue = AppState.Settings.Queue ?? new List<QueueEntry>();
            int interval = Math.Max(0, AppState.Settings.QueueIntervalMinutes);

            for (int i = 0; i < queue.Count; i++)
            {
                var entry = queue[i];
                var profile = ProfileRegistry.Get(entry.ToolId);
                QueueHost.Children.Add(BuildQueueRow(entry, profile, i));
                if (i < queue.Count - 1 && interval > 0)
                {
                    QueueHost.Children.Add(new TextBlock
                    {
                        Text = $"· 间隔 {interval} 分钟 ·",
                        FontSize = 10,
                        Margin = new Thickness(24, 2, 0, 2),
                        Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
                    });
                }
            }

            bool anyEnabled = queue.Any(e => e.Enabled);
            QueueEmptyHint.Visibility = anyEnabled ? Visibility.Collapsed : Visibility.Visible;
            _suppressQueueUi = false;
            RefreshPlanSummary();
            UpdateQueueButtons();
        }
        catch (Exception ex)
        {
            _suppressQueueUi = false;
            LogService.LogEvent("刷新队列失败：" + ex.Message);
        }
    }

    private Border BuildQueueRow(QueueEntry entry, ToolProfile profile, int index)
    {
        bool runningThis = QueueService.IsRunning &&
                           string.Equals(QueueService.CurrentToolId, entry.ToolId, StringComparison.OrdinalIgnoreCase);
        var result = QueueService.LastResults.FirstOrDefault(r => string.Equals(r.ToolId, entry.ToolId, StringComparison.OrdinalIgnoreCase));

        string glyph = "○";
        Brush color = BrushOf("TextTertiaryBrush") ?? DotGray;
        if (runningThis) { glyph = "▶"; color = BrushOf("BrandBrush") ?? DotGreen; }
        else if (result != null)
        {
            if (result.Skipped) { glyph = "‖"; color = BrushOf("TextTertiaryBrush") ?? DotGray; }
            else switch (result.State)
            {
                case ToolRunState.Done: glyph = "✓"; color = BrushOf("SuccessBrush") ?? DotGreen; break;
                case ToolRunState.Failed:
                case ToolRunState.Timeout: glyph = "✗"; color = BrushOf("DangerBrush") ?? DotRed; break;
                case ToolRunState.Halted:
                case ToolRunState.Stalled: glyph = "‖"; color = BrushOf("WarningBrush") ?? DotYellow; break;
            }
        }

        var grid = new Grid { Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

        var drag = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 11,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeNS,
            ToolTip = "拖动排序",
        };
        grid.Children.Add(drag);

        var check = new CheckBox
        {
            IsChecked = entry.Enabled,
            Tag = entry.ToolId,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        check.Checked += QueueCheck_Changed;
        check.Unchecked += QueueCheck_Changed;
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        var state = new TextBlock
        {
            Text = glyph,
            FontSize = 12,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(state, 2);
        grid.Children.Add(state);

        var name = new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = entry.Enabled ? (BrushOf("TextPrimaryBrush") ?? Brushes.Black) : (BrushOf("TextTertiaryBrush") ?? DotGray),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 3);
        grid.Children.Add(name);

        // v1.5：这一项每周跑几天（点一下打开按日计划矩阵）
        int planMask = entry.WeekdayMask & 0x7F;
        int planDays = 0;
        for (int d = 0; d < 7; d++) if ((planMask & (1 << d)) != 0) planDays++;
        var plan = new TextBlock
        {
            Text = planDays == 7 ? "每天" : $"{planDays}天",
            FontSize = 9.5,
            Tag = "plan",
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = "按日计划：" + WeeklyPlanWindow.MaskText(planMask) + "（点一下修改）",
            Foreground = planDays == 0
                ? (BrushOf("WarningBrush") ?? DotYellow)
                : (BrushOf("TextTertiaryBrush") ?? DotGray),
        };
        plan.MouseLeftButtonUp += (_, _) => OpenWeeklyPlan();
        Grid.SetColumn(plan, 4);
        grid.Children.Add(plan);

        var chev = new TextBlock
        {
            Text = "☰",
            FontSize = 10,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(chev, 5);
        grid.Children.Add(chev);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Child = grid,
            Tag = index,
            ToolTip = result?.Reason,
        };
        border.MouseLeftButtonDown += QueueRow_MouseDown;
        border.MouseLeftButtonUp += QueueRow_MouseUp;
        border.MouseEnter += (_, _) => border.Background = BrushOf("HoverBrush") ?? Brushes.Transparent;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    private void QueueCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressQueueUi) return;
        if (sender is not CheckBox cb || cb.Tag is not string toolId) return;
        var entry = (AppState.Settings.Queue ?? new List<QueueEntry>())
            .FirstOrDefault(q => string.Equals(q.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        entry.Enabled = cb.IsChecked == true;
        AppState.SaveSettings();
        RenderQueue();
        UpdateQueueButtons();
        AppState.PublishLog(entry.Enabled ? $"已加入队列：{ProfileRegistry.Get(toolId).DisplayName}" : $"已从队列移除：{ProfileRegistry.Get(toolId).DisplayName}");
        LogService.LogEvent($"队列勾选：{toolId}={entry.Enabled}");
    }

    private void QueueRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock { Tag: "plan" }) return;
        if (sender is Border b && b.Tag is int idx && e.OriginalSource is not CheckBox)
        {
            _dragFrom = idx;
            b.CaptureMouse();
        }
    }

    private void QueueRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border b) return;
            b.ReleaseMouseCapture();
            if (_dragFrom < 0) return;

            var queue = AppState.Settings.Queue ?? new List<QueueEntry>();
            int target = queue.Count - 1;
            for (int i = 0; i < QueueHost.Children.Count; i++)
            {
                if (QueueHost.Children[i] is not Border row) continue;
                if (row.Tag is not int rowIndex) continue;
                var pos = e.GetPosition(row);
                if (pos.Y < row.ActualHeight) { target = rowIndex; break; }
            }
            if (target != _dragFrom && _dragFrom < queue.Count && target < queue.Count)
            {
                var item = queue[_dragFrom];
                queue.RemoveAt(_dragFrom);
                queue.Insert(target, item);
                AppState.SaveSettings();
                LogService.LogEvent($"队列排序：{item.ToolId} {_dragFrom} → {target}");
                RenderQueue();
            }
            _dragFrom = -1;
        }
        catch { _dragFrom = -1; }
    }

    private void UpdateQueueButtons()
    {
        bool running = QueueService.IsRunning;
        BtnQueueStart.Content = running ? "停止" : "一键开跑";
        try
        {
            BtnQueueStart.Background = running
                ? (BrushOf("DangerBrush") ?? Brushes.IndianRed)
                : (BrushOf("BrandBrush") ?? Brushes.SeaGreen);
            BtnQueueStart.Foreground = running ? Brushes.White : (BrushOf("BrandOnBrush") ?? Brushes.Black);
        }
        catch { }

        BtnQueueIdleSkip.Visibility = QueueService.IsWaitingIdle ? Visibility.Visible : Visibility.Collapsed;
        BtnQueueCancelShutdown.Visibility = QueueService.IsShutdownPending ? Visibility.Visible : Visibility.Collapsed;
        TbQueueStatus.Text = running
            ? (string.IsNullOrEmpty(QueueService.CurrentToolId)
                ? "准备中…"
                : $"正在执行 {ProfileRegistry.Get(QueueService.CurrentToolId).DisplayName}")
            : (AppState.Settings.VacationMode ? "度假中，定时已暂停" : "按顺序执行");
    }

    private void BtnQueueStart_Click(object sender, RoutedEventArgs e)
    {
        if (QueueService.IsRunning)
        {
            QueueService.Stop();
            AppState.PublishLog("■ 已请求停止队列");
            return;
        }
        if ((AppState.Settings.Queue ?? new List<QueueEntry>()).Count(q => q.Enabled) == 0)
        {
            NotifyService.Show(NotifyLevel.Warning, "龙管家", "队列为空：请先在工具卡里勾选要运行的工具。");
            return;
        }
        _ = QueueService.StartAsync(QueueTrigger.Manual);
    }

    private void BtnQueueIdleSkip_Click(object sender, RoutedEventArgs e)
    {
        QueueService.SkipIdleWait();
        BtnQueueIdleSkip.Visibility = Visibility.Collapsed;
    }

    private void BtnQueueCancelShutdown_Click(object sender, RoutedEventArgs e)
    {
        QueueService.CancelShutdown();
        BtnQueueCancelShutdown.Visibility = Visibility.Collapsed;
    }

    // ===== v1.5 按日计划 =====

    private void BtnWeeklyPlan_Click(object sender, RoutedEventArgs e) => OpenWeeklyPlan();

    private void OpenWeeklyPlan()
    {
        try
        {
            if ((AppState.Settings.Queue ?? new List<QueueEntry>()).Count == 0)
            {
                NotifyService.Show(NotifyLevel.Info, "龙管家", "队列里还没有工具：先在下方工具卡添加或勾选一个。");
                return;
            }
            var dlg = new WeeklyPlanWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Saved)
            {
                RenderQueue();
                AppState.PublishLog("✓ 已保存按日计划");
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开按日计划失败：" + ex.Message);
        }
    }

    /// <summary>队列卡上的一句话摘要：今天按计划跑几项。</summary>
    private void RefreshPlanSummary()
    {
        try
        {
            var queue = AppState.Settings.Queue ?? new List<QueueEntry>();
            bool any = queue.Any(e => e.Enabled);
            TbPlanSummary.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (!any) return;

            var today = QueueService.PlanForToday(DateTime.Now);
            int bit = QueueService.WeekdayBit(DateTime.Now);
            string[] names = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

            if (!QueueService.HasCustomPlan())
            {
                TbPlanSummary.Text = $"按日计划：每天全选 · 今天 {today.Count} 项";
                TbPlanSummary.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            }
            else if (today.Count == 0)
            {
                TbPlanSummary.Text = $"按日计划：今天（{names[bit]}）为空，定时触发会跳过";
                TbPlanSummary.Foreground = BrushOf("WarningBrush") ?? DotYellow;
            }
            else
            {
                TbPlanSummary.Text = $"按日计划：今天（{names[bit]}）跑 {today.Count} 项 · "
                    + string.Join("、", today.Select(t => ProfileRegistry.Get(t.ToolId).DisplayName));
                TbPlanSummary.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            }
        }
        catch { }
    }

    private void CbAfterRun_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.AfterRunAction = TagOf(CbAfterRun);
        AppState.SaveSettings();
        LogService.LogEvent("跑完动作：" + AppState.Settings.AfterRunAction);
    }

    // ===== 工具卡 =====

    private StackPanel BodyOf(string toolId) => toolId switch
    {
        "bettergi" => Body_bettergi,
        "march7th" => Body_march7th,
        _ => Body_okww,
    };

    private TextBox PathBoxOf(string toolId) => toolId switch
    {
        "bettergi" => TbPath_bettergi,
        "march7th" => TbPath_march7th,
        _ => TbPath_okww,
    };

    private TextBlock FindHintOf(string toolId) => toolId switch
    {
        "bettergi" => TbFindHint_bettergi,
        "march7th" => TbFindHint_march7th,
        _ => TbFindHint_okww,
    };

    private TextBlock ToolStateOf(string toolId) => toolId switch
    {
        "bettergi" => TbToolState_bettergi,
        "march7th" => TbToolState_march7th,
        _ => TbToolState_okww,
    };

    private void ToolHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        var body = BodyOf(toolId);
        bool expand = body.Visibility != Visibility.Visible;
        body.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        if (expand) RefreshToolCard(toolId);
    }

    private void RefreshToolCards()
    {
        foreach (string id in new[] { "bettergi", "march7th", "ok-ww" })
        {
            try { RefreshToolCard(id); } catch { }
        }
        foreach (string id in _customCards.Keys.ToList())
        {
            try { RefreshCustomToolCard(id); } catch { }
        }
    }

    private void RefreshToolCard(string toolId)
    {
        if (!ProfileRegistry.IsBuiltIn(toolId))
        {
            RefreshCustomToolCard(toolId);
            return;
        }

        var profile = ProfileRegistry.Get(toolId);
        var box = PathBoxOf(toolId);
        box.Text = string.IsNullOrWhiteSpace(profile.ExecutablePath) ? "" : profile.ExecutablePath;

        var state = ToolStateOf(toolId);
        bool ok = ToolRunner.HasExecutable(profile);
        state.Text = ok ? "已就绪" : "未配置";
        state.Foreground = ok ? (BrushOf("SuccessBrush") ?? DotGreen) : (BrushOf("WarningBrush") ?? DotYellow);

        if (QueueService.IsRunning && string.Equals(QueueService.CurrentToolId, toolId, StringComparison.OrdinalIgnoreCase))
        {
            state.Text = "运行中";
            state.Foreground = BrushOf("BrandBrush") ?? DotGreen;
        }
        ApplyBuiltInIcon(toolId, profile);
    }

    /// <summary>内置工具卡图标：能提取就显示 exe 图标，否则回退首字母。</summary>
    private void ApplyBuiltInIcon(string toolId, ToolProfile profile)
    {
        Image? img = toolId switch
        {
            "bettergi" => IconImage_bettergi,
            "march7th" => IconImage_march7th,
            _ => IconImage_okww,
        };
        TextBlock? txt = toolId switch
        {
            "bettergi" => IconText_bettergi,
            "march7th" => IconText_march7th,
            _ => IconText_okww,
        };
        if (img == null || txt == null) return;
        var bmp = ToolIconService.GetBitmap(profile);
        if (bmp != null)
        {
            img.Source = bmp;
            img.Visibility = Visibility.Visible;
            txt.Visibility = Visibility.Collapsed;
        }
        else
        {
            img.Visibility = Visibility.Collapsed;
            txt.Visibility = Visibility.Visible;
        }
    }

    // ===== 自定义工具卡（v1.4）=====

    private void BtnAddCustomTool_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CustomToolWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        RebuildCustomToolCards();
        RenderQueue();
        RefreshToolCards();
        AppState.PublishLog($"✓ 已添加自定义工具：{dlg.Result.DisplayName}");
    }

    private void RebuildCustomToolCards()
    {
        try
        {
            CustomToolsHost.Children.Clear();
            _customCards.Clear();

            var custom = new List<ToolProfile>();
            foreach (var entry in AppState.Settings.Queue ?? new List<QueueEntry>())
            {
                var p = ProfileRegistry.All.FirstOrDefault(x =>
                    string.Equals(x.ToolId, entry.ToolId, StringComparison.OrdinalIgnoreCase));
                if (p != null && !ProfileRegistry.IsBuiltIn(p.ToolId) && !custom.Contains(p)) custom.Add(p);
            }
            foreach (var p in ProfileRegistry.All)
                if (!ProfileRegistry.IsBuiltIn(p.ToolId) && !custom.Contains(p)) custom.Add(p);

            foreach (var profile in custom)
                CustomToolsHost.Children.Add(BuildCustomToolCard(profile));
        }
        catch (Exception ex)
        {
            LogService.LogEvent("重建自定义工具卡失败：" + ex.Message);
        }
    }

    private Border BuildCustomToolCard(ToolProfile profile)
    {
        var fallback = new TextBlock
        {
            Text = string.IsNullOrEmpty(profile.DisplayName) ? "?" : profile.DisplayName[..1].ToUpperInvariant(),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2), Visibility = Visibility.Collapsed };
        var iconGrid = new Grid();
        iconGrid.Children.Add(fallback);
        iconGrid.Children.Add(icon);
        var iconBox = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(7),
            Background = BrushOf("InsetBrush") ?? Brushes.Transparent,
            BorderBrush = BrushOf("BorderBrush") ?? Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = iconGrid,
        };
        DockPanel.SetDock(iconBox, Dock.Left);

        var name = new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = BrushOf("TextPrimaryBrush") ?? Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(name, Dock.Left);

        var state = new TextBlock
        {
            Text = "未配置",
            FontSize = 11,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        DockPanel.SetDock(state, Dock.Left);

        var dock = new DockPanel { LastChildFill = false };
        dock.Children.Add(iconBox);
        dock.Children.Add(name);
        dock.Children.Add(state);

        var header = new Button
        {
            Style = TryFindResource("FoldHeader") as Style,
            Tag = profile.ToolId,
            Content = dock,
        };
        header.Click += CustomToolHeader_Click;

        var path = new TextBox
        {
            Style = TryFindResource("InputBox") as Style,
            IsReadOnly = true,
            Height = 32,
            Text = profile.ExecutablePath,
        };
        var edit = new Button
        {
            Content = "编辑",
            Style = TryFindResource("MiniAction") as Style,
            Margin = new Thickness(8, 0, 0, 0),
            Tag = profile.ToolId,
        };
        edit.Click += CustomToolEdit_Click;
        var test = new Button
        {
            Content = "测试启动",
            Style = TryFindResource("MiniAction") as Style,
            Margin = new Thickness(6, 0, 0, 0),
            Tag = profile.ToolId,
        };
        test.Click += CustomToolTest_Click;
        var iconBtn = new Button
        {
            Content = "换图标",
            Style = TryFindResource("MiniAction") as Style,
            Margin = new Thickness(6, 0, 0, 0),
            Tag = profile.ToolId,
            ToolTip = "换成你喜欢的图标（.ico / .exe / .png）",
        };
        iconBtn.Click += ToolIcon_Click;

        var pathGrid = new Grid();
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathGrid.Children.Add(path);
        Grid.SetColumn(edit, 1);
        pathGrid.Children.Add(edit);
        Grid.SetColumn(test, 2);
        pathGrid.Children.Add(test);
        Grid.SetColumn(iconBtn, 3);
        pathGrid.Children.Add(iconBtn);

        var hint = new TextBlock
        {
            FontSize = 10.5,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        };

        var body = new StackPanel
        {
            Margin = new Thickness(16, 0, 16, 14),
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(new TextBlock
        {
            Text = "程序位置",
            FontSize = 11,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        });
        body.Children.Add(pathGrid);
        body.Children.Add(hint);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);

        var card = new Border
        {
            Style = TryFindResource("Card") as Style,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(0),
            Child = stack,
        };

        _customCards[profile.ToolId] = new CustomToolCard
        {
            Card = card,
            Header = header,
            Body = body,
            State = state,
            Path = path,
            Hint = hint,
            Icon = icon,
            IconFallback = fallback,
        };
        return card;
    }

    private void CustomToolHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        if (!_customCards.TryGetValue(toolId, out var card)) return;
        bool expand = card.Body.Visibility != Visibility.Visible;
        card.Body.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        if (expand) RefreshCustomToolCard(toolId);
    }

    private void RefreshCustomToolCard(string toolId)
    {
        if (!_customCards.TryGetValue(toolId, out var card)) return;
        var profile = ProfileRegistry.Get(toolId);
        card.Path.Text = profile.ExecutablePath;

        bool ok = ToolRunner.HasExecutable(profile);
        card.State.Text = ok ? "已就绪" : "未配置";
        card.State.Foreground = ok ? (BrushOf("SuccessBrush") ?? DotGreen) : (BrushOf("WarningBrush") ?? DotYellow);
        if (QueueService.IsRunning && string.Equals(QueueService.CurrentToolId, toolId, StringComparison.OrdinalIgnoreCase))
        {
            card.State.Text = "运行中";
            card.State.Foreground = BrushOf("BrandBrush") ?? DotGreen;
        }

        string done = profile.PrimarySignal switch
        {
            CompletionSignalKind.Duration =>
                $"到达 {profile.CustomData.GetValueOrDefault("durationMinutes", "60")} 分钟",
            CompletionSignalKind.CustomSignal => "自定义信号 · " + CustomSignalSummary(profile),
            _ => "进程退出（退出码 0）",
        };
        card.Hint.Text = $"完成方式：{done} · 最大 {profile.MaxDurationMinutes} 分钟";

        var bmp = ToolIconService.GetBitmap(profile);
        if (bmp != null)
        {
            card.Icon.Source = bmp;
            card.Icon.Visibility = Visibility.Visible;
            card.IconFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            card.Icon.Visibility = Visibility.Collapsed;
            card.IconFallback.Visibility = Visibility.Visible;
        }
    }

    // ===== v1.5 图标更换 =====

    private void ToolIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        var profile = ProfileRegistry.Get(toolId);
        bool has = ToolIconService.HasOverride(toolId);
        bool pick = AppDialog.Show(
            $"给「{profile.DisplayName}」换一个图标？",
            has ? "当前用的是你手动设置的图标；也可以恢复成程序自带的。"
                : "支持 .ico / .png / .jpg，也可以直接选另一个 exe 借它的图标。",
            AppDialog.Kind.Info,
            "更换图标",
            "选择图片…",
            has ? "恢复默认" : null,
            has ? () => ResetToolIcon(toolId) : null,
            this);
        if (!pick) return;
        ChangeToolIcon(toolId);
    }

    private void ChangeToolIcon(string toolId)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择图标",
            Filter = "图标或图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        string? err = ToolIconService.SetOverride(toolId, dlg.FileName);
        if (err != null)
        {
            AppDialog.Show(err, null, AppDialog.Kind.Warning, "更换图标", "知道了", null, null, this);
            return;
        }
        RefreshToolCards();
        AppState.PublishLog($"✓ 已更换图标：{ProfileRegistry.Get(toolId).DisplayName}");
    }

    private void ResetToolIcon(string toolId)
    {
        ToolIconService.ClearOverride(toolId);
        RefreshToolCards();
        AppState.PublishLog($"已恢复默认图标：{ProfileRegistry.Get(toolId).DisplayName}");
    }

    /// <summary>自定义信号的一句话摘要（卡片提示用）。</summary>
    private static string CustomSignalSummary(ToolProfile profile)
    {
        var parts = new List<string>();
        var (texts, file, title) = CustomSignalSource.ReadConfig(profile);
        if (texts.Length > 0) parts.Add($"日志 {texts.Length} 条");
        if (file.Length > 0) parts.Add("等文件");
        if (title.Length > 0) parts.Add("等窗口标题");
        return parts.Count > 0 ? string.Join(" / ", parts) : "未配置";
    }

    private void CustomToolEdit_Click(object sender, RoutedEventArgs e)    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        var profile = ProfileRegistry.Get(toolId);
        var dlg = new CustomToolWindow(profile) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        RebuildCustomToolCards();
        RenderQueue();
        RefreshToolCards();
        AppState.PublishLog(dlg.Deleted ? $"已删除工具：{profile.DisplayName}" : $"✓ 已更新工具：{profile.DisplayName}");
    }

    private void CustomToolTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        if (!_customCards.TryGetValue(toolId, out var card)) return;
        var profile = ProfileRegistry.Get(toolId);
        if (!ToolRunner.HasExecutable(profile))
        {
            card.Hint.Text = "还没有可用路径，点「编辑」重新选择。";
            card.Hint.Foreground = BrushOf("WarningBrush") ?? DotYellow;
            return;
        }
        try
        {
            string args = ToolRunner.BuildArguments(profile, null);
            ToolRunner.StartProcess(profile, args);
            card.Hint.Text = $"已启动（测试）：{args}";
            card.Hint.Foreground = BrushOf("SuccessBrush") ?? DotGreen;
            AppState.PublishLog($"✓ 已启动 {profile.DisplayName}（测试）");
            LogService.LogEvent($"测试启动（自定义）：{toolId} 参数：{args}");
        }
        catch (Exception ex)
        {
            card.Hint.Text = "启动失败：请确认路径正确、并允许管理员权限。";
            card.Hint.Foreground = BrushOf("DangerBrush") ?? DotRed;
            LogService.LogEvent("测试启动失败：" + ex.Message);
        }
    }

    private void ToolBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        var profile = ProfileRegistry.Get(toolId);
        var dlg = new OpenFileDialog
        {
            Title = $"选择 {profile.DisplayName} 的主程序",
            Filter = "可执行文件|*.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        profile.ExecutablePath = dlg.FileName;
        if (string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase))
            AppState.Settings.BgiPath = dlg.FileName;
        ToolIconService.Invalidate(toolId);
        ProfileRegistry.Save(profile);
        RefreshToolCard(toolId);
        FindHintOf(toolId).Text = "";
        AppState.PublishLog($"✓ 已设置 {profile.DisplayName} 路径");
        if (string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase))
            RefreshDetectedGroups(force: true);
    }

    private void ToolAutoFind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        var profile = ProfileRegistry.Get(toolId);
        var hint = FindHintOf(toolId);

        var found = ToolDiscovery.TryAutoDiscover(profile, out string source);
        if (string.IsNullOrWhiteSpace(found))
        {
            hint.Text = "未自动找到，请手动选择文件夹。";
            hint.Foreground = BrushOf("WarningBrush") ?? DotYellow;
            LogService.LogEvent($"自动寻找：{toolId} 未命中（running/startmenu/candidate/registry 全部落空）");
            return;
        }

        // 预填但不立即生效：点「使用此路径」确认后才写入设置
        PathBoxOf(toolId).Text = found;
        hint.Text = $"已找到：{found}（来源：{SourceName(source)}）—— 点「使用此路径」生效";
        hint.Foreground = BrushOf("SuccessBrush") ?? DotGreen;

        var body = BodyOf(toolId);
        Button? confirm = body.Children.OfType<Button>().FirstOrDefault(b => (b.Tag as string) == "confirm-" + toolId);
        if (confirm == null)
        {
            confirm = new Button
            {
                Content = "使用此路径",
                Style = TryFindResource("MiniAction") as Style,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                Tag = "confirm-" + toolId,
            };
            confirm.Click += ToolConfirmPath_Click;
            int hintIndex = body.Children.IndexOf(hint);
            body.Children.Insert(Math.Min(hintIndex + 1, body.Children.Count), confirm);
        }
        confirm.Visibility = Visibility.Visible;
        confirm.Tag = "confirm-" + toolId + "|" + found;
    }

    private static string SourceName(string source) => source switch
    {
        "running" => "正在运行的进程",
        "startmenu" => "开始菜单快捷方式",
        "candidate" => "候选路径",
        "registry" => "注册表卸载信息",
        "已配置" => "原有配置",
        _ => source,
    };

    private void ToolConfirmPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        string[] parts = tag.Split('|', 2);
        if (parts.Length < 2) return;
        string toolId = parts[0].Replace("confirm-", "");
        string path = parts[1];

        var profile = ProfileRegistry.Get(toolId);
        profile.ExecutablePath = path;
        if (string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase))
            AppState.Settings.BgiPath = path;
        ToolIconService.Invalidate(toolId);
        ProfileRegistry.Save(profile);

        btn.Visibility = Visibility.Collapsed;
        FindHintOf(toolId).Text = $"已启用：{path}";
        FindHintOf(toolId).Foreground = BrushOf("SuccessBrush") ?? DotGreen;
        RefreshToolCard(toolId);
        AppState.PublishLog($"✓ 已启用 {profile.DisplayName}：{path}");
        if (string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase))
            RefreshDetectedGroups(force: true);
    }

    private void ToolTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string toolId) return;
        var profile = ProfileRegistry.Get(toolId);
        if (!ToolRunner.HasExecutable(profile))
        {
            FindHintOf(toolId).Text = "还没有可用路径，先点「自动寻找」或「浏览」。";
            FindHintOf(toolId).Foreground = BrushOf("WarningBrush") ?? DotYellow;
            return;
        }
        try
        {
            string args = ToolRunner.BuildArguments(profile, null);
            ToolRunner.StartProcess(profile, args);
            AppState.PublishLog($"✓ 已启动 {profile.DisplayName}（测试）");
            LogService.LogEvent($"测试启动：{toolId} 参数：{args}");
        }
        catch (Exception ex)
        {
            FindHintOf(toolId).Text = "启动失败：请确认路径正确、并允许管理员权限。";
            FindHintOf(toolId).Foreground = BrushOf("DangerBrush") ?? DotRed;
            LogService.LogEvent("测试启动失败：" + ex.Message);
        }
    }

    private void TbConfigName_LostFocus(object sender, RoutedEventArgs e)
    {
        string v = TbConfigName.Text.Trim();
        if (v == AppState.Settings.DragonConfigName) return;
        AppState.Settings.DragonConfigName = v;
        AppState.SaveSettings();
    }

    // ===== 配置组（BGI 工具卡内）=====

    private void RefreshGroupPicker()
    {
        try
        {
            var scan = BgiDiscovery.Scan();
            if (scan.Ok) SanitizeSelectedGroups(scan.Groups);

            var names = scan.Groups;
            for (int i = _groupPicks.Count - 1; i >= 0; i--)
            {
                if (!names.Any(n => string.Equals(n, _groupPicks[i].Name, StringComparison.OrdinalIgnoreCase)))
                    _groupPicks.RemoveAt(i);
            }
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                int idx = _groupPicks.FindIndex(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) _groupPicks.Insert(Math.Min(i, _groupPicks.Count), new GroupPickItem { Name = name });
                else if (idx != i) _groupPicks.Insert(Math.Min(i, _groupPicks.Count), _groupPicks[idx]) ;
            }
            // 去重（上面的插入式移动可能产生重复）
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _groupPicks.RemoveAll(g => !seen.Add(g.Name));

            SyncGroupSelection();
            RenderGroupPicker();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新配置组失败：" + ex.Message);
        }
    }

    private void RenderGroupPicker()
    {
        BgiGroupsHost.Children.Clear();
        if (_groupPicks.Count == 0)
        {
            BgiGroupsHost.Children.Add(new TextBlock
            {
                Text = "还没有识别到配置组；确认 BetterGI 路径后点「重新识别」。",
                FontSize = 10.5,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            });
            return;
        }

        var wrap = new WrapPanel();
        foreach (var item in _groupPicks)
        {
            var cb = new CheckBox
            {
                Content = item.Order > 0 ? $"{item.Order}. {item.Name}" : item.Name,
                IsChecked = item.IsChecked,
                Style = TryFindResource("GroupCheck") as Style,
                Margin = new Thickness(0, 4, 8, 0),
                Tag = item.Name,
            };
            cb.Checked += GroupPickCheck_Click;
            cb.Unchecked += GroupPickCheck_Click;
            wrap.Children.Add(cb);
        }
        BgiGroupsHost.Children.Add(wrap);
    }

    private void SyncGroupSelection()
    {
        var settings = AppState.Settings.SelectedGroups ?? Array.Empty<string>();
        bool same = settings.Length == _selectedOrder.Count;
        if (same)
        {
            for (int i = 0; i < settings.Length; i++)
                if (!string.Equals(settings[i], _selectedOrder[i], StringComparison.OrdinalIgnoreCase)) { same = false; break; }
        }
        if (!same)
        {
            _selectedOrder.Clear();
            _selectedOrder.AddRange(settings);
        }

        var valid = _groupPicks.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int before = _selectedOrder.Count;
        _selectedOrder.RemoveAll(n => !valid.Contains(n));
        if (_selectedOrder.Count != before)
        {
            AppState.Settings.SelectedGroups = _selectedOrder.ToArray();
            AppState.SaveSettings();
        }

        foreach (var item in _groupPicks)
        {
            int idx = _selectedOrder.FindIndex(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase));
            item.IsChecked = idx >= 0;
            item.Order = idx + 1;
        }
        UpdateGroupSummary();
    }

    private void UpdateGroupSummary()
    {
        try
        {
            int n = _selectedOrder.Count;
            if (n > 0)
            {
                TbGroupSummary.Text = $"将执行 {n} 个配置组 · 按勾选顺序";
                TbGroupSummary.Foreground = BrushOf("BrandBrush") ?? DotGray;
            }
            else
            {
                TbGroupSummary.Text = _groupPicks.Count > 0
                    ? $"共 {_groupPicks.Count} 个可选 · 未选时用配置名"
                    : "等待识别";
                TbGroupSummary.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            }
        }
        catch { }
    }

    private void BtnOpenGroupDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = BgiDiscovery.BgiRoot;
            if (string.IsNullOrEmpty(root))
            {
                AppState.PublishLog("✗ 还没有设置 BetterGI 路径");
                return;
            }
            string dir = Path.Combine(root, "User", "ScriptGroup");
            if (!Directory.Exists(dir)) dir = root;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开配置目录失败：" + ex.Message);
        }
    }

    private void BtnRescanGroups_Click(object sender, RoutedEventArgs e)
    {
        RefreshDetectedGroups(force: true);
        AppState.PublishLog("✓ 已重新识别 BetterGI 配置组");
    }

    private void RefreshDetectedGroups(bool force = false)
    {
        try
        {
            var scan = BgiDiscovery.Scan(force);
            string sig = string.Join("\u0001", scan.Groups) + "\u0002" + scan.Ok + "\u0002" + scan.Error;
            if (!force && sig == _groupSignature) return;
            _groupSignature = sig;
            if (scan.Ok) SanitizeSelectedGroups(scan.Groups);
            RefreshGroupPicker();
        }
        catch { }
    }

    private void SanitizeSelectedGroups(List<string> groups)
    {
        try
        {
            var selected = AppState.Settings.SelectedGroups ?? Array.Empty<string>();
            if (selected.Length == 0) return;
            var valid = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = selected.Where(valid.Contains).ToArray();
            if (kept.Length == selected.Length) return;
            AppState.Settings.SelectedGroups = kept;
            AppState.SaveSettings();
            AppState.PublishLog(kept.Length == 0
                ? "⚠ 原勾选的配置组在 BetterGI 中已不存在，已自动取消勾选"
                : $"⚠ 已移除 {selected.Length - kept.Length} 个在 BetterGI 中已不存在的配置组");
            LogService.LogEvent($"配置组同步：移除失效勾选 {selected.Length - kept.Length} 个");
        }
        catch { }
    }

    private void GroupPickCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not CheckBox cb || cb.Tag is not string name) return;
            bool on = cb.IsChecked == true;

            _selectedOrder.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (on) _selectedOrder.Add(name);

            foreach (var g in _groupPicks)
            {
                int idx = _selectedOrder.FindIndex(n => string.Equals(n, g.Name, StringComparison.OrdinalIgnoreCase));
                g.IsChecked = idx >= 0;
                g.Order = idx + 1;
            }

            AppState.Settings.SelectedGroups = _selectedOrder.ToArray();
            AppState.SaveSettings();
            UpdateGroupSummary();
            RenderGroupPicker();
            AppState.PublishLog(_selectedOrder.Count > 0
                ? $"✓ 将执行 {_selectedOrder.Count} 个配置组：{string.Join(" → ", _selectedOrder)}"
                : "已取消全部配置组勾选，启动时使用一条龙配置名");
        }
        catch { }
    }

        // ===== 定时卡：概览 ↔ 编辑 双页切换（手机式推入/推出，两拍：先布局后动画）=====
    //
    // 卡顿/闪现根因与对策：
    //  1) 页面高度不同 → SizeToContent 在动画期间反复重算窗口高度（每帧重排，肉眼即卡顿）。
    //     对策：展开前先让编辑页完成布局并量出高度，把 SchedulePageHost.MinHeight 一次性锁死，
    //     动画期间高度恒定 → 零重排。锁定只增不减，之后来回切换高度完全不变。
    //  2) 切换瞬间设置初始值又立刻启动动画 → 中间可能先渲染一帧"最终态"再跳回起点（闪现）。
    //     对策：FLIP 两拍——先写初始 Transform/Opacity 并 UpdateLayout，下一渲染帧再 BeginAnimation。
    //  3) 动画只作用于 RenderTransform / Opacity（不碰 Width/Height/Margin），不触发布局。
    //  4) ScrollViewer 滚动偏移在布局拍前后保持，避免"画面先跳一下"。

    private bool _scheduleEditorOpen;
    private int _pageSeq;               // 切换序号：新的切换会作废上一拍的动画回调（支持快速连点/打断）
    private double _editorPageHeight;   // 编辑页实测高度（首次展开测得，之后固定）

    private void BtnTimeDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduleEditorOpen) CollapsePicker();
        else ExpandPicker();
    }

    private void BtnScheduleBack_Click(object sender, RoutedEventArgs e)
    {
        CollapsePicker();
    }

    /// <summary>切到编辑页：布局拍（可见+锁高）→ 渲染拍（纯 Transform/Opacity 动画）</summary>
    private void ExpandPicker()
    {
        if (_scheduleEditorOpen) return;
        _scheduleEditorOpen = true;
        int seq = ++_pageSeq;
        try
        {
            TimePicker.InitFrom(AppState.Settings.ScheduleHour, AppState.Settings.ScheduleMinute);
            TgSchedule.Visibility = Visibility.Collapsed;
            BtnScheduleBack.Visibility = Visibility.Visible;
            ScheduleTitle.Text = "修改定时";

            ClearPageAnimations();
            ScheduleOverview.Visibility = Visibility.Visible;
            ScheduleOverview.Opacity = 1;
            TimePickerPanel.Visibility = Visibility.Visible;
            TimePickerPanel.Opacity = 0;
            EditorScale.ScaleX = EditorScale.ScaleY = 0.94;
            EditorShift.X = 26;

            double keepOffset = ContentScroll.VerticalOffset;

            // 布局拍：先清掉上一次收起动画留下的显式高度，再量出编辑页高度并锁定
            ResetHostHeight();
            SchedulePageHost.UpdateLayout();
            if (TimePickerPanel.ActualHeight > 0)
                _editorPageHeight = Math.Max(_editorPageHeight, TimePickerPanel.ActualHeight);
            if (_editorPageHeight > 0)
            {
                SchedulePageHost.MinHeight = _editorPageHeight;
                SchedulePageHost.UpdateLayout();
            }
            ContentScroll.ScrollToVerticalOffset(keepOffset);

            // 渲染拍：下一帧再启动动画（此时初始值已经渲染过一帧，不会"先跳后动"）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (seq != _pageSeq || !_scheduleEditorOpen) return; // 已被更快的切换取代
                try
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                    var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
                    fadeOut.Completed += (_, _) =>
                    {
                        if (_scheduleEditorOpen) ScheduleOverview.Visibility = Visibility.Collapsed;
                    };
                    ScheduleOverview.BeginAnimation(OpacityProperty, fadeOut);
                    OverviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.965, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
                    OverviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.965, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });
                    OverviewShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease });

                    var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease };
                    TimePickerPanel.BeginAnimation(OpacityProperty, fadeIn);
                    EditorScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                    EditorScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                    EditorShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                }
                catch
                {
                    ScheduleOverview.Visibility = Visibility.Collapsed;
                    TimePickerPanel.Opacity = 1;
                    EditorScale.ScaleX = EditorScale.ScaleY = 1;
                    EditorShift.X = 0;
                }
            }), DispatcherPriority.Render);
        }
        catch
        {
            // 动效异常静默降级为直切，功能不受影响
            ScheduleOverview.Visibility = Visibility.Collapsed;
            TimePickerPanel.Visibility = Visibility.Visible;
            TimePickerPanel.Opacity = 1;
            EditorScale.ScaleX = EditorScale.ScaleY = 1;
            EditorShift.X = 0;
        }
    }

    /// <summary>
    /// 回到概览页：点下返回的那一帧就把卡片交还给概览页的正确尺寸，随后只做淡入。
    ///
    /// 旧实现把编辑页高度锁到动画结束（约 0.3~0.5s），概览在这段时间里仍按"编辑页高度"排版：
    /// 文字被星号行拉开、位置整体下移，最后突然跳回合身高度——即用户看到的"错位/延迟"。
    /// 现在改成：①同一帧内收起编辑页、解除高度锁定 → 卡片/窗口立刻是该有的高度；
    /// ②下一帧只对概览页做 Opacity/Scale/Translate 动画，不再改任何布局，
    /// 因此"尺寸"与"内容"从第一帧起就是同步的。
    /// </summary>
    private void CollapsePicker()
    {
        if (!_scheduleEditorOpen) return;
        _scheduleEditorOpen = false;
        int seq = ++_pageSeq;
        try
        {
            TgSchedule.Visibility = Visibility.Visible;
            BtnScheduleBack.Visibility = Visibility.Collapsed;
            ScheduleTitle.Text = "每天自动跑一条龙";

            ClearPageAnimations();
            double keepOffset = ContentScroll.VerticalOffset;

            // 布局拍：立刻回到概览页与概览高度（编辑页直接收起，避免留下被裁一半的残影）
            ResetHostHeight();
            TimePickerPanel.Visibility = Visibility.Collapsed;
            TimePickerPanel.BeginAnimation(OpacityProperty, null);
            ScheduleOverview.Visibility = Visibility.Visible;
            // 不从 0 开始淡入：编辑页是"立刻消失"的，若概览也从全透明起，会有一帧空卡。
            // 从 0.55 起淡入 + 轻微放大，既无空窗，又保留一点"落位"的手感。
            ScheduleOverview.Opacity = 0.55;
            OverviewScale.ScaleX = OverviewScale.ScaleY = 0.99;
            OverviewShift.X = -8;
            SchedulePageHost.UpdateLayout();
            ContentScroll.ScrollToVerticalOffset(keepOffset);

            // 渲染拍：下一帧只做淡入（纯 RenderTransform / Opacity，不参与布局）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (seq != _pageSeq || _scheduleEditorOpen) return; // 已被更快的切换取代
                try
                {
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    ScheduleOverview.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
                    OverviewScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                    OverviewScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                    OverviewShift.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                }
                catch
                {
                    ScheduleOverview.Opacity = 1;
                    OverviewScale.ScaleX = OverviewScale.ScaleY = 1;
                    OverviewShift.X = 0;
                }
            }), DispatcherPriority.Render);
        }
        catch
        {
            ResetHostHeight();
            TimePickerPanel.Visibility = Visibility.Collapsed;
            ScheduleOverview.Visibility = Visibility.Visible;
            ScheduleOverview.Opacity = 1;
            OverviewScale.ScaleX = OverviewScale.ScaleY = 1;
            OverviewShift.X = 0;
        }
    }

    /// <summary>把页面容器高度交还给"自动"（清掉动画与显式高度，卡片随内容收缩）</summary>
    private void ResetHostHeight()
    {
        try
        {
            SchedulePageHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            SchedulePageHost.Height = double.NaN;
            SchedulePageHost.MinHeight = 0;
        }
        catch { }
    }

    private void ClearPageAnimations()
    {
        try
        {
            ScheduleOverview.BeginAnimation(OpacityProperty, null);
            OverviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            OverviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            OverviewShift.BeginAnimation(TranslateTransform.XProperty, null);
            TimePickerPanel.BeginAnimation(OpacityProperty, null);
            EditorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            EditorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            EditorShift.BeginAnimation(TranslateTransform.XProperty, null);
        }
        catch { }
    }

    private void TimePicker_Confirmed(int hour, int minute)
    {
        AppState.Settings.ScheduleHour = hour;
        AppState.Settings.ScheduleMinute = minute;
        AppState.SaveSettings();
        UpdateTimeDisplay();
        UpdateScheduleStatus();
        CollapsePicker();
        AppState.PublishLog($"✓ 定时已设置为 {hour:00}:{minute:00}");
        LogService.LogEvent($"定时设置为 {hour:00}:{minute:00}（星期掩码 {AppState.Settings.ScheduleWeekdays}）");
    }

    private void TimePicker_Cancelled()
    {
        CollapsePicker();
    }

    // ===== 高级设置 / 系统开关 =====

    private void TgSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.ScheduleEnabled = TgSchedule.IsChecked == true;
        AppState.SaveSettings();
        UpdateScheduleStatus();
    }

    private void TgAutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgAutoStart.IsChecked == true;
        AutoStartHelper.Set(on);
        AppState.PublishLog(on ? "✓ 已开启开机自动启动（当前用户）" : "已关闭开机自动启动（当前用户）");
        LogService.LogEvent("开机自启（当前用户）" + (on ? "已开启" : "已关闭"));
    }

    private void TgAutoRun_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.AutoRunOnLaunch = TgAutoRun.IsChecked == true;
        AppState.SaveSettings();
    }

    private void TgStayTray_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.StayInTray = TgStayTray.IsChecked == true;
        AppState.SaveSettings();
    }

    private void TgSound_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.SoundEnabled = TgSound.IsChecked == true;
        AppState.SaveSettings();
        if (AppState.Settings.SoundEnabled) SoundService.Play("toggle.wav");
    }

    private void TgVacation_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgVacation.IsChecked == true;
        AppState.Settings.VacationMode = on;
        AppState.SaveSettings();
        VacationBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateQueueButtons();
        if (on)
        {
            NotifyService.Show(NotifyLevel.Info, "龙管家", "度假模式已开启，定时任务已全部暂停（手动「一键开跑」不受影响）。");
            LogService.LogEvent("度假模式：已开启");
        }
        else
        {
            AppState.PublishLog("度假模式已关闭，定时恢复");
            LogService.LogEvent("度假模式：已关闭");
        }
    }

    private void TgSentinel_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        ApplySentinel(TgSentinel.IsChecked == true);
    }

    private void ApplySentinel(bool on)
    {
        AppState.Settings.SentinelEnabled = on;
        AppState.SaveSettings();
        _suppressToggle = true;
        TgSentinel.IsChecked = on;
        TgAutoRestart.IsEnabled = on;
        _suppressToggle = false;
        if (on) SentinelService.Start(); else SentinelService.Stop();
        RefreshShield();
        AppState.PublishLog(on ? "✓ 已开启守护" : "守护已关闭");
        LogService.LogEvent("守护" + (on ? "已开启" : "已关闭"));
    }

    private void TgAutoRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.AutoRestartBgi = TgAutoRestart.IsChecked == true;
        AppState.SaveSettings();
    }

    private void TgScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.ScreenshotCheckEnabled = TgScreenshot.IsChecked == true;
        AppState.SaveSettings();
        LogService.LogEvent("截图冻结检测：" + AppState.Settings.ScreenshotCheckEnabled);
    }

    private void TgIdleGuard_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.IdleGuardEnabled = TgIdleGuard.IsChecked == true;
        AppState.SaveSettings();
        LogService.LogEvent("防打扰：" + AppState.Settings.IdleGuardEnabled);
    }

    private void TgAutoKill_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.AutoKillResidual = TgAutoKill.IsChecked == true;
        AppState.SaveSettings();
        LogService.LogEvent("定时前清理残留：" + AppState.Settings.AutoKillResidual);
    }

    private void TgAfterRunOnFailure_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.AfterRunOnFailure = TgAfterRunOnFailure.IsChecked == true;
        AppState.SaveSettings();
    }

    private void TgKeepAwake_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgKeepAwake.IsChecked == true;
        AppState.Settings.KeepAwakeEnabled = on;
        AppState.SaveSettings();
        if (!on && KeepAwakeService.IsActive) KeepAwakeService.Release();
        else if (on && QueueService.IsRunning) KeepAwakeService.Acquire();
        LogService.LogEvent("挂机护航：" + (on ? "开启" : "关闭"));
        AppState.PublishLog(on ? "✓ 已开启挂机护航" : "挂机护航已关闭");
        RefreshShield();
    }

    private void TgNightScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgNightScreen.IsChecked == true;
        AppState.Settings.NightScreenEnabled = on;
        AppState.SaveSettings();
        TbNightMinutes.IsEnabled = on;
        KeepAwakeService.RefreshNightScreen();
        LogService.LogEvent("夜间息屏：" + (on ? $"开启（{AppState.Settings.NightScreenMinutes} 分钟）" : "关闭"));
        RefreshShield();
    }

    private void TbNightMinutes_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        if (!int.TryParse(TbNightMinutes.Text.Trim(), out int minutes)) minutes = 30;
        minutes = Math.Clamp(minutes, 5, 120);
        TbNightMinutes.Text = minutes.ToString();
        if (AppState.Settings.NightScreenMinutes == minutes) return;
        AppState.Settings.NightScreenMinutes = minutes;
        AppState.SaveSettings();
        KeepAwakeService.RefreshNightScreen();
        LogService.LogEvent($"夜间息屏：延时改为 {minutes} 分钟");
    }

    private void TgStallAutoKill_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.StallAutoKill = TgStallAutoKill.IsChecked == true;
        AppState.SaveSettings();
        LogService.LogEvent("疑似挂起自动终止：" + (AppState.Settings.StallAutoKill ? "开启" : "关闭"));
    }

    private void TgMorningReport_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgMorningReport.IsChecked == true;
        AppState.Settings.MorningReportEnabled = on;
        AppState.SaveSettings();
        LogService.LogEvent("晨报：" + (on ? "开启" : "关闭"));
        if (on) AppState.PublishLog("晨报已开启：每天首次打开时用一句话汇报昨天的情况");
    }

    private void CbFailStrategy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.FailStrategy = TagOf(CbFailStrategy);
        AppState.SaveSettings();
        LogService.LogEvent("失败策略：" + AppState.Settings.FailStrategy);
    }

    private void CbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle) return;
        if (int.TryParse(TagOf(CbInterval), out int minutes))
        {
            AppState.Settings.QueueIntervalMinutes = minutes;
            AppState.SaveSettings();
            RenderQueue();
        }
    }

    // ===== 底部盾牌 =====

    private void RefreshShield()
    {
        try
        {
            bool enabled = AppState.Settings.SentinelEnabled;
            bool alerted = AppState.Settings.ShieldAlertAt is DateTime t && (DateTime.Now - t).TotalMinutes < 30;
            bool escort = QueueService.IsRunning && KeepAwakeService.IsActive;

            ShieldDot.Fill = escort ? DotYellow : !enabled ? DotGray : alerted ? DotYellow : DotGreen;
            ShieldText.Text = escort ? "护航中" : !enabled ? "守护已关闭"
                : alerted ? "守护中 · 近期有告警" : "守护中";

            var h = SentinelService.HealthSnapshot();
            var escortItems = KeepAwakeService.ActiveSummary();
            ShieldTipText.Text =
                $"守护：{(enabled ? "开启" : "关闭")}\n" +
                $"心跳：{h.TickCount} 次 · 最近 {h.LastTick:HH:mm:ss}\n" +
                $"内存：{h.MemUsedPercent:F0}% · 工具工作集 {h.BgiWorkingSetMb}MB\n" +
                $"CPU：{h.ToolCpuPercent:F1}% · 挂起观察 {(h.StallSince == DateTime.MinValue ? "未触发" : h.StallSince.ToString("HH:mm:ss"))}\n" +
                $"截图检测：{(AppState.Settings.ScreenshotCheckEnabled ? "开" : "关")} · 冻结 {h.FrozenCount} 次\n" +
                $"Webhook：{(SentinelService.WebhookRunning ? "监听中" : "未监听")} · 端口 {AppState.Settings.BgiWebhookPort}\n" +
                $"自动重启：{(AppState.Settings.AutoRestartBgi ? "开" : "关")} · 已重启 {h.RestartCount} 次\n" +
                $"护航：{(AppState.Settings.KeepAwakeEnabled ? "开" : "关")}" +
                (escortItems.Count > 0 ? " · " + string.Join(" / ", escortItems) : " · 未生效");
        }
        catch { }
    }

    private void Shield_MouseEnter(object sender, MouseEventArgs e) => ShieldTip.Visibility = Visibility.Visible;
    private void Shield_MouseLeave(object sender, MouseEventArgs e) => ShieldTip.Visibility = Visibility.Collapsed;

    private void OnSentinelEvent(SentinelEventKind kind, string payload)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (kind is SentinelEventKind.ScreenFrozen or SentinelEventKind.MemoryHigh
                or SentinelEventKind.GameLost or SentinelEventKind.GroupError)
            {
                AppState.Settings.ShieldAlertAt = DateTime.Now;
                AppState.SaveSettings();
            }
            RefreshShield();
        }));
    }

    // ===== 通知设置卡 =====

    private void SyncNotifyChips()
    {
        _suppressNotifyChips = true;
        try
        {
            var ch = NotifyService.ParseChannel(AppState.Settings.NotifyMode);
            ChipToast.IsChecked = ch == PushChannel.Toast;
            ChipBalloon.IsChecked = ch == PushChannel.Balloon;
            ChipWecom.IsChecked = ch == PushChannel.Wecom;
            ChipFeishu.IsChecked = ch == PushChannel.Feishu;
            ChipDing.IsChecked = ch == PushChannel.DingTalk;
            ChipTelegram.IsChecked = ch == PushChannel.Telegram;
            ChipBark.IsChecked = ch == PushChannel.Bark;
            ChipEmail.IsChecked = ch == PushChannel.Email;
            ChipXxtui.IsChecked = ch == PushChannel.Xxtui;
            ChipWebhook.IsChecked = ch == PushChannel.Webhook;
            ChipWebSocket.IsChecked = ch == PushChannel.WebSocket;
        }
        finally { _suppressNotifyChips = false; }
    }

    private void NotifyChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNotifyChips) return;
        AppState.Settings.NotifyMode = (sender as FrameworkElement)?.Tag as string ?? "Toast";
        AppState.SaveSettings();
        ApplyNotifyMode();
    }

    private void ApplyNotifyMode()
    {
        try
        {
            var ch = NotifyService.ParseChannel(AppState.Settings.NotifyMode);
            bool local = ch is PushChannel.Toast or PushChannel.Balloon;
            NpLocal.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            NpWecom.Visibility = ch == PushChannel.Wecom ? Visibility.Visible : Visibility.Collapsed;
            NpFeishu.Visibility = ch == PushChannel.Feishu ? Visibility.Visible : Visibility.Collapsed;
            NpDing.Visibility = ch == PushChannel.DingTalk ? Visibility.Visible : Visibility.Collapsed;
            NpTelegram.Visibility = ch == PushChannel.Telegram ? Visibility.Visible : Visibility.Collapsed;
            NpBark.Visibility = ch == PushChannel.Bark ? Visibility.Visible : Visibility.Collapsed;
            NpEmail.Visibility = ch == PushChannel.Email ? Visibility.Visible : Visibility.Collapsed;
            NpXxtui.Visibility = ch == PushChannel.Xxtui ? Visibility.Visible : Visibility.Collapsed;
            NpWebhook.Visibility = ch == PushChannel.Webhook ? Visibility.Visible : Visibility.Collapsed;
            NpWebSocket.Visibility = ch == PushChannel.WebSocket ? Visibility.Visible : Visibility.Collapsed;

            TbNotifyHint.Text = ch switch
            {
                PushChannel.Toast => "Windows 系统通知（右下角弹出 + 操作中心）；发送失败自动降级为托盘气泡",
                PushChannel.Balloon => "只弹托盘气泡，不占用屏幕；适合已关闭 Windows 通知的系统",
                PushChannel.Wecom => "推送到企业微信群机器人，手机可收（参数只保存在本机 config\\settings.json）",
                PushChannel.Feishu => "推送到飞书群自定义机器人；开启签名校验时填密钥",
                PushChannel.DingTalk => "推送到钉钉群自定义机器人；安全设置选「加签」时填密钥",
                PushChannel.Telegram => "通过 Telegram Bot 推送到指定会话；国内网络可能需要代理",
                PushChannel.Bark => "推送到 iPhone（Bark App），iPhone 通知中心即时弹出",
                PushChannel.Email => "通过 SMTP 发送邮件，支持多个收件人（分号分隔）",
                PushChannel.Xxtui => "xxtui 聚合推送：一次请求可分发到微信/钉钉/Bark 等渠道",
                PushChannel.Webhook => "POST JSON 到你的服务，便于对接自建机器人或自动化平台",
                PushChannel.WebSocket => "连接后发送一条 JSON 即关闭；适合自建机器人（如 NoneBot/OneBot）",
                _ => "",
            };
            TbLocalHint.Text = ch == PushChannel.Toast
                ? "本机通知：不需要任何配置，发送测试即可验证系统通知是否正常。"
                : "本机提示：只弹托盘气泡，不写入操作中心。";

            bool configured = NotifyChannelConfigured(ch);
            TbNotifyChannelState.Text = local ? "本机" : configured ? "已配置" : "待配置";
            TbNotifyChannelState.Foreground = configured
                ? (BrushOf("SuccessBrush") ?? DotGray)
                : (BrushOf("TextTertiaryBrush") ?? DotGray);
            BtnTestWebhook.Content = local ? "发送测试通知" : "发送测试消息";
            TbNotifyTestResult.Text = "";
        }
        catch { }
    }

    private bool NotifyChannelConfigured(PushChannel ch)
    {
        var s = AppState.Settings;
        return ch switch
        {
            PushChannel.Toast or PushChannel.Balloon => true,
            PushChannel.Wecom => !string.IsNullOrWhiteSpace(s.NotifyWecomUrl),
            PushChannel.Feishu => !string.IsNullOrWhiteSpace(s.NotifyFeishuUrl),
            PushChannel.DingTalk => !string.IsNullOrWhiteSpace(s.NotifyDingUrl),
            PushChannel.Telegram => !string.IsNullOrWhiteSpace(s.NotifyTelegramToken) && !string.IsNullOrWhiteSpace(s.NotifyTelegramChat),
            PushChannel.Bark => !string.IsNullOrWhiteSpace(s.NotifyBarkUrl),
            PushChannel.Email => !string.IsNullOrWhiteSpace(s.NotifyEmailHost) && !string.IsNullOrWhiteSpace(s.NotifyEmailTo),
            PushChannel.Xxtui => !string.IsNullOrWhiteSpace(s.NotifyXxtuiUrl),
            PushChannel.Webhook => !string.IsNullOrWhiteSpace(s.NotifyWebhookUrl),
            PushChannel.WebSocket => !string.IsNullOrWhiteSpace(s.NotifyWebSocketUrl),
            _ => false,
        };
    }

    private void LoadNotifyFields()
    {
        var s = AppState.Settings;
        TbNotifyWecom.Text = s.NotifyWecomUrl;
        TbNotifyFeishu.Text = s.NotifyFeishuUrl;
        TbNotifyFeishuSecret.Text = s.NotifyFeishuSecret;
        TbNotifyDing.Text = s.NotifyDingUrl;
        TbNotifyDingSecret.Text = s.NotifyDingSecret;
        TbNotifyTelegramToken.Text = s.NotifyTelegramToken;
        TbNotifyTelegramChat.Text = s.NotifyTelegramChat;
        TbNotifyBark.Text = s.NotifyBarkUrl;
        TbEmailHost.Text = s.NotifyEmailHost;
        TbEmailPort.Text = s.NotifyEmailPort.ToString();
        TbEmailUser.Text = s.NotifyEmailUser;
        TbEmailPass.Text = s.NotifyEmailPass;
        TbEmailFrom.Text = s.NotifyEmailFrom;
        TbEmailTo.Text = s.NotifyEmailTo;
        TbNotifyXxtui.Text = s.NotifyXxtuiUrl;
        TbNotifyWebhook.Text = s.NotifyWebhookUrl;
        TbNotifyWebSocket.Text = s.NotifyWebSocketUrl;
        TgNotifyOnlyImportant.IsChecked = s.NotifyOnlyImportant;
        SyncNotifyChips();
        ApplyNotifyMode();
    }

    private void NotifyField_LostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not TextBox tb || tb.Tag is not string prop) return;
            var pi = typeof(AppSettings).GetProperty(prop);
            if (pi == null) return;
            if (pi.PropertyType == typeof(int))
                pi.SetValue(AppState.Settings, int.TryParse(tb.Text.Trim(), out int n) && n > 0 ? n : 465);
            else
                pi.SetValue(AppState.Settings, tb.Text.Trim());
            AppState.SaveSettings();
            ApplyNotifyMode();
        }
        catch { }
    }

    private void TgNotifyOnlyImportant_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.NotifyOnlyImportant = TgNotifyOnlyImportant.IsChecked == true;
        AppState.SaveSettings();
    }

    private async void BtnTestWebhook_Click(object sender, RoutedEventArgs e)
    {
        var ch = NotifyService.ParseChannel(AppState.Settings.NotifyMode);
        BtnTestWebhook.IsEnabled = false;
        TbNotifyTestResult.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
        TbNotifyTestResult.Text = "正在发送…";
        try
        {
            var (ok, err) = await NotifyService.SendTestAsync(ch);
            TbNotifyTestResult.Text = ok ? "✓ 发送成功，请查看接收端" : "✗ " + err;
            TbNotifyTestResult.Foreground = ok
                ? (BrushOf("SuccessBrush") ?? DotGray)
                : (BrushOf("DangerBrush") ?? DotGray);
            AppState.PublishLog(ok
                ? $"✓ 测试消息已通过「{NotifyService.NameOf(ch)}」发送"
                : $"✗ {NotifyService.NameOf(ch)} 测试失败：{err}");
        }
        finally { BtnTestWebhook.IsEnabled = true; }
    }

    private void BtnCopyWebhook_Click(object sender, RoutedEventArgs e)
    {
        string url = $"http://127.0.0.1:{AppState.Settings.BgiWebhookPort}/webhook";
        if (ClipboardHelper.TrySetText(url))
            AppState.PublishLog("✓ 已复制事件推送地址");
        else
            ClipboardHelper.ShowManualCopyFallback(url, this);
    }

    // ===== 折叠卡通用 =====

    private void FoldHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        FrameworkElement? body = btn.Name switch
        {
            "BtnFoldSystem" => SystemBody,
            "BtnFoldNotify" => NotifyBody,
            "BtnFoldAdvanced" => AdvancedBody,
            _ => null,
        };
        if (body == null) return;
        body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===== 主题（四段式分段控件）=====

    private void SegTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeSeg) return;
        string name = (sender as FrameworkElement)?.Tag as string ?? "System";
        if (name == "Custom")
        {
            OpenCustomThemeEditor();
            return;
        }
        AppState.Settings.Theme = name;
        AppState.SaveSettings();
        Theme.Set(name);
    }

    private void OpenCustomThemeEditor()
    {
        try
        {
            var dlg = new CustomThemeWindow { Owner = this };
            bool ok = dlg.ShowDialog() == true;
            SyncThemeSeg();
            if (ok) { RenderQueue(); RefreshShield(); }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开自定义主题失败：" + ex.Message);
            SyncThemeSeg();
        }
    }

    // ===== 首启向导 =====

    private void TryShowFirstRunWizard()
    {
        try
        {
            if (AppState.Settings.FirstRunDone) return;
            var wiz = new FirstRunWindow { Owner = this };
            wiz.ShowDialog();
            AppState.Settings.FirstRunDone = true;
            AppState.SaveSettings();
            RenderQueue();
            RefreshToolCards();
            RefreshDetectedGroups(force: true);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("首启向导失败：" + ex.Message);
            AppState.Settings.FirstRunDone = true;
            AppState.SaveSettings();
        }
    }

    // ===== 日志 / 横幅 =====

    private void OnLogMessage(string msg)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TbLog.Text = msg;
            if (msg.StartsWith("✗", StringComparison.Ordinal)) FlashStatusError();
        }));
    }

    private void FlashStatusError()
    {
        try
        {
            StatusBarBorder.Background = BrushOf("DangerBrush");
            TbLog.Foreground = Brushes.White;
            _statusBarTimer.Stop();
            _statusBarTimer.Interval = TimeSpan.FromSeconds(30);
            _statusBarTimer.Tick -= StatusBarTimer_Tick;
            _statusBarTimer.Tick += StatusBarTimer_Tick;
            _statusBarTimer.Start();
        }
        catch { }
    }

    private void StatusBarTimer_Tick(object? sender, EventArgs e)
    {
        _statusBarTimer.Stop();
        StatusBarBorder.Background = BrushOf("InsetBrush");
        TbLog.Foreground = BrushOf("TextSecondaryBrush");
    }

    // ===== 标题栏 / 关闭 =====

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void BtnMinWin_Click(object sender, RoutedEventArgs e)
    {
        try { WindowState = WindowState.Minimized; } catch { }
    }

    private void BtnCloseWin_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (App.ForceExit || !AppState.Settings.StayInTray) return;
        e.Cancel = true;
        Hide();
        App.Instance?.ShowBalloon(NotifyLevel.Info, "一条龙管家",
            "一条龙管家 仍在后台运行，双击托盘图标可打开。");
    }
}
