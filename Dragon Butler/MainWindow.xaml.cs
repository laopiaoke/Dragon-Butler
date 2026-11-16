using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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

    // v1.8：全局热键宿主（只在开启时建）与迷你浮条（只在开着时存在）
    private HotkeyService? _hotkeys;
    private MiniBarWindow? _miniBar;

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
            // v1.8：热键是系统级注册，窗口关掉必须还回去，否则组合键被永久占用
            try { _hotkeys?.Dispose(); } catch { }
        };

        _countdownTimer.Tick += (_, _) => { UpdateScheduleStatus(); RefreshShield(); };
        _groupScanTimer.Tick += (_, _) => RefreshDetectedGroups(force: true);
        _uiRefreshTimer.Tick += (_, _) => { RefreshToolCards(); UpdateQueueButtons(); RefreshLiveStrip(); RefreshHomeHero(); };

        // 全局按键音：所有可交互元素在"按下"这一瞬出声
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(AnyInteractivePress), true);

        LoadSettingsToUi();
        RebuildCustomToolCards();
        RenderQueue();
        RefreshToolCards();
        RefreshShield();
        RefreshHomeHero();
        RefreshPresetRow();

        // v1.8：热键与浮条要等控件树建完才能改它们的状态
        ApplyHotkeys();
        ApplyMiniBar();

        Loaded += (_, _) =>
        {
            AppState.RefreshStatus();
            if (SuppressAutoCenter) return;
            Dispatcher.BeginInvoke(new Action(CenterOnActiveMonitor), DispatcherPriority.Render);
            // 向导放到窗口真正显示之后再弹，避免主界面停留在"未显示"状态
            Dispatcher.BeginInvoke(new Action(TryShowFirstRunWizard), DispatcherPriority.Background);
            // v1.16：启动后安静地看一眼有没有新版。放这么晚是因为它要联网——
            // 界面先出来，网络慢不该让窗口空着等。
            StartBackgroundUpdateCheck();
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
        TgWeeklyReport.IsChecked = s.WeeklyReportEnabled;
        TgHarvest.IsChecked = s.HarvestEnabled;
        TgControlApi.IsChecked = s.ControlApiEnabled;
        TgLanPanel.IsChecked = s.LanPanelEnabled;
        RefreshApiRows(s);
        TgHotkey.IsChecked = s.HotkeyEnabled;
        TgMiniBar.IsChecked = s.MiniBarEnabled;
        TgSpeech.IsChecked = s.SpeechEnabled;
        RefreshHotkeyRow(s);
        TbNightMinutes.Text = Math.Clamp(s.NightScreenMinutes, 5, 120).ToString();
        TbNightMinutes.IsEnabled = s.NightScreenEnabled;
        TgVacation.IsChecked = s.VacationMode;
        SelectComboByTag(CbAfterRun, s.AfterRunAction);
        RefreshAfterRunAppRow();
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
            RefreshCatchUpText(s);
            RefreshScheduleQuick(s);
        }
        catch { }
    }

    /// <summary>
    /// v1.15 / L3：定时没开时，这张卡只给一句话 + 一个按钮。
    ///
    /// <para>
    /// 摊开的「重复日 + 大号时间 + 补跑档位」有八个可改项，但它们不是同一个决策：
    /// 新用户只有「要不要自动跑」这一个问题，答案是「要」的时候，8 点和每天就是最好的默认值。
    /// 所以先给结论，想改再点「我自己挑时间」——那条路和以前一样，一个选项都没少。
    /// </para>
    /// </summary>
    private void RefreshScheduleQuick(AppSettings s)
    {
        bool quick = !s.ScheduleEnabled;
        ScheduleQuick.Visibility = quick ? Visibility.Visible : Visibility.Collapsed;
        ScheduleDetail.Visibility = quick ? Visibility.Collapsed : Visibility.Visible;
        if (!quick) return;

        // 已经改过时间的人（不是默认的 8:00）看到的是自己定的时间，不能一律写「8 点」。
        string at = $"{Math.Clamp(s.ScheduleHour, 0, 23):00}:{Math.Clamp(s.ScheduleMinute, 0, 59):00}";
        bool everyDay = s.ScheduleWeekdays == 0x7F;
        TbQuickTitle.Text = everyDay ? $"每天 {at}，自己开跑" : $"下次 {at}，自己开跑";
    }

    /// <summary>「就这样，打开定时」：把默认值落实并打开，不再问第二个问题。</summary>
    private void BtnQuickAccept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = AppState.Settings;

            // 只在「真的还是默认值」时才写默认，避免把用户以前挑过的星期冲掉。
            if (s.ScheduleWeekdays == 0 || s.ScheduleWeekdays == 0x7F) s.ScheduleWeekdays = 0x7F;
            if (s.ScheduleHour < 0 || s.ScheduleHour > 23) s.ScheduleHour = 8;
            if (s.ScheduleMinute < 0 || s.ScheduleMinute > 59) s.ScheduleMinute = 0;

            s.ScheduleEnabled = true;
            AppState.SaveSettings();

            _suppressToggle = true;
            try { TgSchedule.IsChecked = true; }
            finally { _suppressToggle = false; }

            UpdateScheduleStatus();
            SyncWeekdayChips();
            RefreshHomeHero();

            string at = $"{s.ScheduleHour:00}:{s.ScheduleMinute:00}";
            AppState.PublishLog($"✓ 定时已打开：每天 {at} 自动开跑");
            LogService.LogEvent($"定时：一键打开（每天 {at}，掩码 {s.ScheduleWeekdays}）");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("一键开启定时失败：" + ex.Message);
        }
    }

    /// <summary>「我自己挑时间」：不打开定时，只把摊开的那一页露出来，让人自己看。</summary>
    private void BtnQuickPick_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 先落到详细页，再直接进时间选择器——点「我自己挑」的人多半就想改时间。
            ScheduleQuick.Visibility = Visibility.Collapsed;
            ScheduleDetail.Visibility = Visibility.Visible;
            ExpandPicker();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开时间选择失败：" + ex.Message);
        }
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
        return SchedulePlanner.NextDueAfter(s.ScheduleWeekdays, s.ScheduleHour, s.ScheduleMinute, DateTime.Now);
    }

    /// <summary>补跑窗口的档位（分钟）。0 表示关闭补跑，退回「必须整分钟命中」的老行为。</summary>
    private static readonly int[] CatchUpSteps = { 0, 60, 120, 240, 480, 1440 };

    /// <summary>点击「补跑」一行：在档位之间轮换，并存盘。</summary>
    private void BtnCatchUp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = AppState.Settings;
            int current = Math.Clamp(s.ScheduleCatchUpMinutes, 0, SchedulePlanner.MaxCatchUpMinutes);
            int idx = Array.IndexOf(CatchUpSteps, current);
            s.ScheduleCatchUpMinutes = CatchUpSteps[idx < 0 ? 2 : (idx + 1) % CatchUpSteps.Length];
            AppState.SaveSettings();
            LogService.LogEvent("定时：补跑窗口改为 " + SchedulePlanner.WindowText(s.ScheduleCatchUpMinutes));
            UpdateScheduleStatus();
        }
        catch { }
    }

    /// <summary>刷新「补跑」那一行的文案（随设置变化，读不到就退回默认窗口）。</summary>
    private void RefreshCatchUpText(AppSettings s)
    {
        try
        {
            BtnCatchUp.Visibility = s.ScheduleEnabled ? Visibility.Visible : Visibility.Collapsed;
            TbCatchUp.Text = "补跑：" + SchedulePlanner.WindowText(s.ScheduleCatchUpMinutes);
        }
        catch { }
    }

    /// <summary>每 30 秒检查一次：到点（或在补跑窗口内且该计划点没跑过）→ 触发队列（定时触发）。</summary>
    private void CheckSchedule()
    {
        try
        {
            var s = AppState.Settings;
            var decision = SchedulePlanner.Decide(
                s.ScheduleEnabled,
                s.VacationMode,
                QueueService.IsRunning,
                s.ScheduleHour,
                s.ScheduleMinute,
                s.ScheduleWeekdays,
                s.ScheduleCatchUpMinutes,
                ParseLastScheduleRun(s),
                DateTime.Now);

            if (!decision.ShouldRun) return;

            // 先记录，再启动——启动失败也认这个计划点，避免每 30 秒重试一次刷屏
            s.LastScheduleRunAt = (decision.DueAt ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm");
            _lastScheduleRun = DateTime.Now;
            AppState.SaveSettings();

            LogService.LogEvent("定时：" + decision.Reason + "，触发队列");
            _ = QueueService.StartAsync(QueueTrigger.Timer);
        }
        catch { }
    }

    /// <summary>读取「上次因定时而跑的计划点」；旧配置只有 LastRunAt 时退化为不记录（宁可补跑一次）。</summary>
    private static DateTime? ParseLastScheduleRun(AppSettings s)
    {
        if (DateTime.TryParse(s.LastScheduleRunAt, out var at)) return at;
        return null;
    }

    // ===== 队列卡 =====

    private void OnQueueStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenderQueue();
            RefreshToolCards();
            RefreshShield();
            RefreshProgress();
            RefreshHomeHero();
        }));
    }

    /// <summary>
    /// v1.8 / F10：把队列进度画到任务栏图标上。
    ///
    /// 为什么值得做：挂着一条龙的人多半在做别的事，全屏游戏时根本看不到主界面。
    /// 任务栏那条进度是 Windows 自带的能力，不需要任何第三方依赖，
    /// 用户抬眼扫一下任务栏就知道「跑到第几个了、卡没卡」。
    ///
    /// 进度分母取「今天计划要跑的项数」，分子取「今天已跑成的项数」，
    /// 与「接着跑」按钮共用同一份台账（RunJournal），两处数字永远一致。
    /// </summary>
    private void RefreshProgress()
    {
        try
        {
            bool running = QueueService.IsRunning;
            int done = QueueService.DoneToolIdsToday().Count;
            int total = Math.Max(done + QueueService.UnfinishedToday().Count, 0);

            if (TaskbarItemInfo == null) TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();

            if (!running || total <= 0)
            {
                TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                return;
            }

            TaskbarItemInfo.ProgressValue = QueueProgress.Fraction(done, total);
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
        }
        catch
        {
            // 任务栏集成失败（极老系统 / 远程桌面会话）不该影响队列本身
        }
    }

    // v1.13：跑起来时标题行那句状态是收起来的（实时条已经说了在跑哪个组），
    // 但「下一项将在 5 分钟后开始…」这类话只有这里会有，不能跟着一起吞掉——
    // 改存到 _queueStatusText，由 RefreshLiveStrip 挂到实时条第二行去。
    private string _queueStatusText = "";
    private DateTime _queueStatusStamp;

    private void OnQueueStatusText(string text)
    {
        _queueStatusText = text ?? "";
        _queueStatusStamp = QueueService.CurrentToolStartedAt;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TbQueueStatus.Text = _queueStatusText;
            RefreshLiveStrip();
        }));
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
            // v1.9：悬停能看到这一项等谁（没写依赖就是空白，不占视觉）
            ToolTip = BuildQueueTip(entry, result),
        };
        border.MouseLeftButtonDown += QueueRow_MouseDown;
        border.MouseLeftButtonUp += QueueRow_MouseUp;
        border.MouseEnter += (_, _) => border.Background = BrushOf("HoverBrush") ?? Brushes.Transparent;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    /// <summary>行悬停提示：依赖在前，上次结果在后（都没有就返回 null，不留空气泡）。</summary>
    private static string? BuildQueueTip(QueueEntry entry, QueueItemResult? result)
    {
        var parts = new List<string>();
        var deps = QueueGraph.DependenciesOf(entry);
        if (deps.Count > 0)
        {
            var names = deps.Select(id =>
            {
                var p = ProfileRegistry.Get(id);
                return string.IsNullOrWhiteSpace(p.DisplayName) ? id : p.DisplayName;
            });
            parts.Add("等 " + string.Join("、", names) + " 跑成"
                + (QueueGraph.ShouldAbortOnDependencyFail(entry) ? "（没成则整队停下）" : ""));
        }
        if (result != null && !string.IsNullOrWhiteSpace(result.Reason)) parts.Add(result.Reason);
        // v1.9：切片设过才提一句，没设就别占地方（默认就是全局勾选）
        if (!GroupSlicing.UsesGlobal(entry.Groups)) parts.Add(GroupSlicing.Describe(entry.Groups));
        return parts.Count == 0 ? null : string.Join("\n", parts);
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
        RefreshContinueButton();
        RefreshProgress();

        // v1.13 + v1.15：这一行只在「有话要说」时才出现。
        // 跑起来时下面那条实时条已经把组名、时长、预计结束都摆出来了；
        // 空闲时说「按顺序执行」也没人需要听——队列行上的字形已经说明了顺序。
        // 保留的就两种：度假中（队列被停）、项间等待（下面会写下一项几点开始）。
        bool waiting = !string.IsNullOrEmpty(_queueStatusText);
        TbQueueStatus.Visibility = running || waiting || AppState.Settings.VacationMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        TbQueueStatus.Text = AppState.Settings.VacationMode
            ? "度假中，定时已暂停"
            : (running ? "" : _queueStatusText);
    }

    /// <summary>
    /// v1.6 / F2「接着跑」按钮的显隐与文案。
    /// 只在「今天已经跑成过至少一项，且还有没跑成的」时出现——
    /// 一次都没跑过的日子显示它，等于给用户一个和「一键开跑」一样的按钮。
    /// </summary>
    private void RefreshContinueButton()
    {
        try
        {
            if (QueueService.IsRunning)
            {
                BtnQueueContinue.Visibility = Visibility.Collapsed;
                return;
            }
            var done = QueueService.DoneToolIdsToday();
            if (done.Count == 0)
            {
                BtnQueueContinue.Visibility = Visibility.Collapsed;
                return;
            }
            var left = QueueService.UnfinishedToday();
            BtnQueueContinue.Visibility = left.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (left.Count > 0)
                BtnQueueContinue.Content = $"接着跑（今天还剩 {left.Count} 项）";
        }
        catch { }
    }

    private void BtnQueueContinue_Click(object sender, RoutedEventArgs e)
    {
        if (QueueService.IsRunning) return;
        _ = QueueService.StartContinueAsync();
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

        // v1.19：开跑前体检。只有真挡路的（Block）才拦，其余一律放行——
        // 半夜点了开跑却被一句「你没开定时」挡住，比不检查还烦人。
        var report = SafePreflight();
        if (report != null && !report.CanRun)
        {
            AppState.PublishLog("✗ 开跑前还有没弄好的事，先看一眼");
            OpenPreflight();
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

    // ===== v1.9 依赖编排 =====

    private void BtnDepends_Click(object sender, RoutedEventArgs e) => OpenDepends();

    private void OpenDepends()
    {
        try
        {
            if ((AppState.Settings.Queue ?? new List<QueueEntry>()).Count == 0)
            {
                NotifyService.Show(NotifyLevel.Info, "龙管家", "队列里还没有工具：先在下方工具卡添加或勾选一个。");
                return;
            }
            var dlg = new DependsWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Saved)
            {
                RenderQueue();
                AppState.PublishLog("✓ 已保存依赖编排");
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开依赖编排失败：" + ex.Message);
        }
    }

    // ===== v1.9 配置组切片 =====

    private void BtnGroupSlice_Click(object sender, RoutedEventArgs e) => OpenGroupSlice();

    private void OpenGroupSlice()
    {
        try
        {
            if ((AppState.Settings.Queue ?? new List<QueueEntry>()).Count == 0)
            {
                NotifyService.Show(NotifyLevel.Info, "龙管家", "队列里还没有工具：先在下方工具卡添加或勾选一个。");
                return;
            }
            var dlg = new GroupsWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Saved)
            {
                RenderQueue();
                AppState.PublishLog("✓ 已保存配置组切片");
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开配置组切片失败：" + ex.Message);
        }
    }

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

    /// <summary>v1.8 / F13：打开挂机日历（只读，不改任何设置）。</summary>
    private void BtnCalendar_Click(object sender, RoutedEventArgs e) => OpenCalendar();

    private void OpenCalendar()
    {
        try
        {
            var dlg = new CalendarWindow { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开挂机日历失败：" + ex.Message);
        }
    }

    /// <summary>v1.13：打开「今日运行」——今天这一盘跑了什么、各花多久。</summary>
    private void BtnToday_Click(object sender, RoutedEventArgs e) => OpenToday();

    private void OpenToday()
    {
        try
        {
            var dlg = new TodayWindow { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开今日运行失败：" + ex.Message);
        }
    }

    /// <summary>v1.14：日志规则——规则本体在独立窗口，主界面只留一个入口。</summary>
    private void BtnLogRules_Click(object sender, RoutedEventArgs e) => OpenLogRules();

    private void OpenLogRules()
    {
        try
        {
            var dlg = new LogRulesWindow { Owner = this };
            dlg.ShowDialog();
            // 关掉窗口后立刻让新规则生效：LogRules.Observe 每次读 ActiveRules()，
            // 不需要重启任何东西，但把节流表留一份旧记录会让「刚加的规则」看起来没反应。
            LogRules.ResetThrottle();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开日志规则失败：" + ex.Message);
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
        RefreshAfterRunAppRow();
    }

    /// <summary>
    /// v1.15 / L5：只有选了「打开程序」才露出路径那一行。
    /// 平时不显示——一个平时不用的输入框，占了版面还让人以为必须填。
    /// </summary>
    private void RefreshAfterRunAppRow()
    {
        try
        {
            bool need = string.Equals(AppState.Settings.AfterRunAction, "OpenApp", StringComparison.OrdinalIgnoreCase);
            AfterRunAppRow.Visibility = need ? Visibility.Visible : Visibility.Collapsed;
            if (!need) return;

            string p = AppState.Settings.AfterRunAppPath ?? "";
            bool ok = p.Trim().Length > 0 && File.Exists(p);
            TbAfterRunApp.Text = p.Trim().Length == 0
                ? "还没选程序"
                : (ok ? Path.GetFileName(p) : $"找不到：{Path.GetFileName(p)}");
            TbAfterRunApp.Foreground = ok
                ? (BrushOf("TextSecondaryBrush") ?? DotGray)
                : (BrushOf("WarningBrush") ?? DotYellow);
            TbAfterRunApp.ToolTip = p.Trim().Length == 0 ? null : p;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新跑完程序行失败：" + ex.Message);
        }
    }

    private void BtnAfterRunApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择跑完之后要打开的程序",
                Filter = "可执行文件|*.exe|快捷方式|*.lnk|所有文件|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            AppState.Settings.AfterRunAppPath = dlg.FileName;
            AppState.SaveSettings();
            RefreshAfterRunAppRow();
            AppState.PublishLog($"✓ 跑完后将打开 {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("选择跑完程序失败：" + ex.Message);
        }
    }

    // ===== 配置组合（v1.15）=====

    /// <summary>
    /// 队列卡那一行：装了几套组合才显示。没建过的人看到的还是原来那块，
    /// 不用为「以后可能会用到」先占一行。
    /// </summary>
    private void RefreshPresetRow()
    {
        try
        {
            if (_suppressToggle) return;
            var presets = AppState.Settings.Presets ?? new List<ConfigPreset>();
            PresetRow.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (presets.Count == 0) return;

            var sorted = ConfigPresets.Sort(presets);
            _suppressToggle = true;
            try
            {
                CbPreset.Items.Clear();
                foreach (var p in sorted)
                {
                    var item = new ComboBoxItem
                    {
                        Content = p.Name,
                        Tag = p.Id,
                        ToolTip = ConfigPresets.Describe(p),
                    };
                    CbPreset.Items.Add(item);
                }

                var active = ConfigPresets.FindById(presets, AppState.Settings.ActivePresetId);
                if (active != null)
                {
                    var hit = CbPreset.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(i => string.Equals(i.Tag as string, active.Id, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) CbPreset.SelectedItem = hit;
                }
                else
                {
                    CbPreset.SelectedIndex = -1;
                }
            }
            finally { _suppressToggle = false; }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新配置组合行失败：" + ex.Message);
        }
    }

    private void CbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggle) return;
        try
        {
            if (CbPreset.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string id) return;
            if (string.Equals(AppState.Settings.ActivePresetId, id, StringComparison.OrdinalIgnoreCase)) return;

            var preset = ConfigPresets.FindById(AppState.Settings.Presets, id);
            if (preset == null) return;

            AppState.Settings.ActivePresetId = preset.Id;
            preset.LastUsedAt = ConfigPresets.StampUsed(DateTime.Now);
            AppState.SaveSettings();
            AppState.PublishLog($"✓ 这次用「{preset.Name}」：{ConfigPresets.Describe(preset)}");
            RefreshHomeHero();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("切换配置组合失败：" + ex.Message);
        }
    }

    private void BtnPresetSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = AppState.Settings;
            var next = ConfigPresets.Capture(
                s.Presets, s.DragonConfigName, s.SelectedGroups, "",
                () => Guid.NewGuid().ToString("N"));

            string? why = ConfigPresets.RefuseReason(next, s.Presets);
            if (why != null)
            {
                AppDialog.Show(why, null, AppDialog.Kind.Warning, "存为组合", owner: this);
                return;
            }

            // 主界面这一下是「快捷存」，名字自动编号；要自己命名就去窗口里存。
            s.Presets ??= new List<ConfigPreset>();
            s.Presets.Add(next);
            s.ActivePresetId = next.Id;
            next.LastUsedAt = ConfigPresets.StampUsed(DateTime.Now);
            AppState.SaveSettings();
            RefreshPresetRow();
            AppState.PublishLog($"✓ 已存为「{next.Name}」：{ConfigPresets.Describe(next)}");
            LogService.LogEvent($"保存配置组合（快捷）：{next.Name}");
            RefreshHomeHero();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("快捷保存配置组合失败：" + ex.Message);
        }
    }

    private void OpenPresets()
    {
        try
        {
            var w = new PresetsWindow { Owner = this };
            w.ShowDialog();
            if (w.Changed) RefreshPresetRow();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开配置组合失败：" + ex.Message);
        }
    }

    private void BtnPresetManage_Click(object sender, RoutedEventArgs e) => OpenPresets();

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

    /// <summary>
    /// v1.15 / L2：工具设置那一行。
    ///
    /// <para>
    /// 收起来不是把功能藏掉——主卡的「选择 BetterGI.exe」已经覆盖了最常见的那一次操作，
    /// 而「配好了几个」这件事仍然要能一眼看到，所以标题行右边挂一句摘要。
    /// </para>
    /// </summary>
    private void FoldTools_Click(object sender, RoutedEventArgs e)
    {
        bool expand = ToolCardsHost.Visibility != Visibility.Visible;
        ToolCardsHost.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ChevTools.RenderTransform = new RotateTransform(expand ? 180 : 0);
        if (expand) RefreshToolCards();
    }

    /// <summary>「已配 1 个 / 3 个都没配」——收起状态下唯一能看出配置状况的地方。</summary>
    private void RefreshToolsSummary()
    {
        try
        {
            int ready = 0;
            var missing = new List<string>();
            foreach (string id in new[] { "bettergi", "march7th", "ok-ww" })
            {
                var p = ProfileRegistry.Get(id);
                if (ToolRunner.HasExecutable(p)) ready++;
                else missing.Add(p.DisplayName);
            }
            foreach (string id in _customCards.Keys.ToList())
            {
                try
                {
                    var p = ProfileRegistry.Get(id);
                    if (ToolRunner.HasExecutable(p)) ready++;
                    else missing.Add(p.DisplayName);
                }
                catch { }
            }

            if (missing.Count == 0)
            {
                TbToolsSummary.Text = $"已配 {ready} 个";
                TbToolsSummary.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            }
            else
            {
                // 只报「还有几个没配」而不点名：点名会把一整排名字塞进 10.5 号字里，
                // 而且哪个没配点开就看得到。
                TbToolsSummary.Text = ready == 0
                    ? "还没配，点开填一下"
                    : $"已配 {ready} 个 · 还有 {missing.Count} 个没配";
                TbToolsSummary.Foreground = ready == 0
                    ? (BrushOf("WarningBrush") ?? DotYellow)
                    : (BrushOf("TextTertiaryBrush") ?? DotGray);
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("工具摘要刷新失败：" + ex.Message);
        }
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
        RefreshToolsSummary();
        // 路径或配置名刚变过时要重读；节流在 RefreshCompletionRow 里，20 秒最多读一次盘
        try { RefreshCompletionRow(); } catch { }
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
        RefreshCompletionRow();
    }

    // ===== 一条龙的「跑完后动作」（v1.17 / M1-B）=====

    /// <summary>上一次读到的文件内容与 mtime，写入前用来确认中间没人改过。</summary>
    private string _completionJson = "";
    private DateTime _completionMtime;
    private string _completionLoadedFor = "";
    private bool _completionRendering;
    private DateTime _completionRefreshedAt;

    /// <summary>
    /// 刷新「跑完后让 BetterGI 自己做什么」这一行。
    ///
    /// 候选值来自**用户自己的配置**，不是我们硬编码的枚举——ABGI 文档没列合法取值，
    /// 猜一个映射写进界面，猜错了用户会以为「选这个就是关机」而实际不是。
    /// 所以在下列表里只呈现事实：这个值长什么样、哪些配置用过。
    ///
    /// 没有任何配置 → 提示去 BetterGI 里先设一次；配置名留空 → 说明这份设置只管
    /// 全局默认，不知道 BGI 运行时选的是哪份。
    /// </summary>
    private void RefreshCompletionRow(bool force = false)
    {
        // 界面每秒刷一次，不能每次都读一遍目录里所有 json。
        // 只有「路径变了 / 刚写完 / 用户点了重新识别」才需要重读。
        if (!force && _completionRefreshedAt != default
            && (DateTime.Now - _completionRefreshedAt) < TimeSpan.FromSeconds(20)
            && _completionLoadedFor == (DragonCompletion.TargetJson(OneDragonDir(), AppState.Settings.DragonConfigName) ?? ""))
            return;
        _completionRefreshedAt = DateTime.Now;
        try
        {
            _completionRendering = true;
            CbCompletion.Items.Clear();

            string oneDragonDir = OneDragonDir();
            string configName = AppState.Settings.DragonConfigName;
            string target = DragonCompletion.TargetJson(oneDragonDir, configName) ?? "";

            if (oneDragonDir.Length == 0)
            {
                CbCompletion.IsEnabled = false;
                TbCompletionHint.Text = "先填 BetterGI 路径";
                TbCompletionHint.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
                return;
            }
            if (target.Length == 0)
            {
                CbCompletion.IsEnabled = false;
                TbCompletionHint.Text = "留空时不改，用 BGI 当前选中的那份";
                TbCompletionHint.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
                return;
            }

            var options = DragonCompletion.Scan(oneDragonDir, ReadAllTextOrNull, ListJson);
            bool fileExists = File.Exists(target);
            string current = "";
            if (fileExists)
            {
                _completionJson = ReadAllTextOrNull(target) ?? "";
                try { _completionMtime = File.GetLastWriteTime(target); } catch { _completionMtime = default; }
                _completionLoadedFor = target;
                current = DragonCompletion.CurrentValue(_completionJson);
            }
            else
            {
                _completionJson = "";
                _completionLoadedFor = target;
            }

            if (options.Count == 0)
            {
                CbCompletion.IsEnabled = false;
                TbCompletionHint.Text = fileExists ? "还没有配置设过这个值" : "这个配置文件已经不在了";
                TbCompletionHint.Foreground = BrushOf("WarningBrush") ?? DotYellow;
                return;
            }

            foreach (var o in options)
                CbCompletion.Items.Add(new ComboBoxItem { Content = DragonCompletion.OptionText(o), Tag = o.Value });

            // 当前值不在候选里（比如它是这份配置独有的），也要放进去，否则一打开就显示成别的值
            if (current.Length > 0 && !options.Any(o => DragonCompletion.IsSameValue(o.Value, current)))
                CbCompletion.Items.Add(new ComboBoxItem { Content = current + " · 只在这份里用", Tag = current });

            SelectCompletionByValue(current);
            CbCompletion.IsEnabled = true;
            TbCompletionHint.Text = current.Length == 0 ? "现在没设，跑完 BGI 自己决定" : "改这里的会写进配置文件";
            TbCompletionHint.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新跑完后动作失败：" + ex.Message);
            CbCompletion.IsEnabled = false;
            TbCompletionHint.Text = "读配置时出了点问题";
        }
        finally
        {
            _completionRendering = false;
        }
    }

    /// <summary>
    /// 用户从下拉里选了另一个值。这里做三件事：
    /// ① 选回原值就什么都不做（下拉的 SelectionChanged 在程序填列表时也会触发）；
    /// ② 写入前把「现在能不能写」全过一遍——BGI 在跑、文件没了、中间被人改过，任一成立就拒；
    /// ③ 先确认再写，写完把下拉重新刷一遍（从磁盘读回来，不是信任内存）。
    /// </summary>
    private void CbCompletion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_completionRendering) return;
        try
        {
            if (CbCompletion.SelectedItem is not ComboBoxItem item) return;
            string want = DragonCompletion.CleanValue(item.Tag as string);
            if (want.Length == 0) return;

            string target = _completionLoadedFor;
            if (target.Length == 0) { RefreshCompletionRow(); return; }

            string current = DragonCompletion.CurrentValue(_completionJson);
            if (DragonCompletion.IsSameValue(current, want)) return;

            string configName = AppState.Settings.DragonConfigName;
            bool running = ToolRunner.IsRunning(ProfileRegistry.Get("bettergi"));
            DateTime? now = null;
            try { if (File.Exists(target)) now = File.GetLastWriteTime(target); } catch { }

            string? refuse = BgiConfigWriter.RefuseReason(target, OneDragonDir(), running,
                File.Exists(target), _completionMtime == default ? null : _completionMtime, now);
            if (refuse != null)
            {
                _completionRendering = true;
                SelectCompletionByValue(current);
                _completionRendering = false;
                AppDialog.Show(refuse, null, AppDialog.Kind.Warning, "跑完后动作", owner: this);
                return;
            }

            if (!AppDialog.Show(DragonCompletion.ConfirmText(configName, current, want), Path.GetFileName(target),
                    AppDialog.Kind.Warning, "跑完后动作", "改", "算了", owner: this))
            {
                _completionRendering = true;
                SelectCompletionByValue(current);
                _completionRendering = false;
                return;
            }

            string? patched = DragonCompletion.Apply(_completionJson, want);
            if (patched == null)
            {
                _completionRendering = true;
                SelectCompletionByValue(current);
                _completionRendering = false;
                AppDialog.Show("这个文件的结构和我们认识的不一样，没敢改。去 BetterGI 自己的界面里改这一项。",
                    target, AppDialog.Kind.Warning, "跑完后动作", owner: this);
                return;
            }

            var (ok, msg) = BgiConfigWriter.Save(AppContext.BaseDirectory, target, patched, DateTime.Now);
            if (!ok)
            {
                _completionRendering = true;
                SelectCompletionByValue(current);
                _completionRendering = false;
                AppDialog.Show(msg, null, AppDialog.Kind.Warning, "跑完后动作", owner: this);
                AppState.PublishLog("✗ 改跑完后动作失败：" + msg);
                return;
            }

            LogService.LogEvent($"改了跑完后动作：{configName} {current} → {want}（{target}）");
            AppState.PublishLog($"✓ 跑完后动作已改为 {want}");
            RefreshCompletionRow();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("改跑完后动作失败：" + ex.Message);
            AppDialog.Show("改的时候出了点问题：" + ex.Message, null, AppDialog.Kind.Warning, "跑完后动作", owner: this);
        }
    }

    private void BtnOpenOneDragonDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = OneDragonDir();
            if (dir.Length == 0 || !Directory.Exists(dir))
            {
                AppState.PublishLog("✗ 还没有设置 BetterGI 路径");
                return;
            }
            RevealInExplorer(dir);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开一条龙目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 按值选中下拉项。**不能用 <see cref="SelectComboByTag"/>**：那个忽略大小写，
    /// 而这里的值 `Sleep` 和 `sleep` 在 JSON 里是两个不同的东西，选错了会让人以为
    /// 自己改的是另一个。找不到就保持未选中，不退回第一项——退回第一项等于悄悄换了个值。
    /// </summary>
    private void SelectCompletionByValue(string? value)
    {
        string v = value ?? "";
        foreach (var item in CbCompletion.Items)
        {
            if (item is ComboBoxItem ci && DragonCompletion.IsSameValue(ci.Tag as string, v))
            {
                CbCompletion.SelectedItem = ci;
                return;
            }
        }
        CbCompletion.SelectedIndex = -1;
    }

    /// <summary>一条龙配置目录；没设路径或路径非法时返回 ""。</summary>
    private static string OneDragonDir()
    {
        try
        {
            string probe = BgiConfigWriter.ConfigPath(BgiDiscovery.BgiRoot, "probe.json");
            if (probe.Length == 0) return "";
            return Path.GetDirectoryName(probe) ?? "";
        }
        catch { return ""; }
    }

    // ===== 开跑前体检（v1.19）=====
    //
    // 这一块只做一件事：把散在各处的原始事实收集起来，交给 Preflight.Build 出结论。
    // 判定逻辑一行都不写在这里——写在 Preflight 里才能被探针断言，也才不会
    // 因为界面控件改名而悄悄变味。

    /// <summary>收集当前事实。任何一步出错都退化成「不知道」，不抛。</summary>
    private static PreflightInput CollectPreflight()
    {
        var s = AppState.Settings;
        var bgi = ProfileRegistry.Get("bettergi");
        bool configured = bgi != null && !string.IsNullOrWhiteSpace(bgi.ExecutablePath);
        bool exists = configured && File.Exists(bgi!.ExecutablePath);
        bool running = false;
        try { running = bgi != null && ToolRunner.IsRunning(bgi); } catch { }

        var queue = s.Queue ?? new List<QueueEntry>();
        int enabled = queue.Count(q => q.Enabled);
        int ready = queue.Where(q => q.Enabled)
                         .Select(q => ProfileRegistry.Get(q.ToolId))
                         .Count(p => p != null && ToolRunner.HasExecutable(p));

        int groups = 0;
        var dragonConfigs = new List<string>();
        try
        {
            var scan = BgiDiscovery.Scan();
            groups = scan.Groups.Count;
            dragonConfigs = scan.DragonConfigs.ToList();
        }
        catch { }

        var preset = ConfigPresets.FindById(s.Presets, s.ActivePresetId);
        bool presetMissing = s.ActivePresetId.Length > 0 && preset == null;
        var missingGroups = preset == null || groups == 0
            ? new List<string>()
            : ConfigPresets.MissingGroups(preset, BgiDiscovery.Scan().Groups).ToList();

        string endpoint = Preflight.NotifyEndpointOf(s.NotifyMode, s);
        bool writable = true;
        try
        {
            string dir = Path.GetDirectoryName(AppState.SettingsPath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".dbtmp-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch { writable = false; }

        return new PreflightInput(
            BgiConfigured: configured,
            BgiExists: exists,
            BgiRunning: running,
            EnabledTools: enabled,
            ReadyTools: ready,
            GroupCount: groups,
            DragonConfigName: s.DragonConfigName,
            DragonConfigs: dragonConfigs,
            NotifyMode: s.NotifyMode,
            NotifyEndpoint: endpoint,
            MissingPresetGroups: missingGroups,
            PresetMissing: presetMissing,
            LastRunAt: s.LastRunAt,
            LastRunOk: s.LastRunOk,
            LastRunReason: null,
            ScheduleEnabled: s.ScheduleEnabled,
            Writable: writable);
    }

    /// <summary>体检结果；出错时返回 null（调用方一律当「没意见」处理）。</summary>
    private static PreflightReport? SafePreflight()
    {
        try { return Preflight.Build(CollectPreflight()); }
        catch (Exception ex)
        {
            LogService.LogEvent("体检收集失败：" + ex.Message);
            return null;
        }
    }

    private void BtnPreflight_Click(object sender, RoutedEventArgs e) => OpenPreflight();

    private void OpenPreflight()
    {
        try
        {
            var w = new PreflightWindow(SafePreflight, RunCommand) { Owner = this };
            w.ShowDialog();
            if (w.WentToFix)
            {
                RefreshHomeHero();
                RefreshToolCards();
                RefreshPresetRow();
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开体检失败：" + ex.Message);
        }
    }

    private static string? ReadAllTextOrNull(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    private static IEnumerable<string> ListJson(string dir)
    {
        try { return Directory.GetFiles(dir, "*.json"); } catch { return Array.Empty<string>(); }
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

        // v1.15 / L4：常跑的那几个排到最前面。只在显示时重排，
        // _groupPicks 本身不动——它承载的是 BGI 里的识别顺序与勾选序号。
        var favorites = AppState.Settings.FavoriteGroups ?? Array.Empty<string>();
        var ordered = GroupFavorites.Sort(_groupPicks.Select(g => g.Name).ToList(), favorites);
        var byName = _groupPicks.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

        var wrap = new WrapPanel();
        foreach (string name in ordered)
        {
            if (!byName.TryGetValue(name, out var item)) continue;
            bool fav = GroupFavorites.IsFavorite(favorites, item.Name);
            var cb = new CheckBox
            {
                Content = (fav ? "★ " : "") + (item.Order > 0 ? $"{item.Order}. {item.Name}" : item.Name),
                IsChecked = item.IsChecked,
                Style = TryFindResource("GroupCheck") as Style,
                Margin = new Thickness(0, 4, 8, 0),
                Tag = item.Name,
                ToolTip = fav ? "右键取消常用" : "右键设为常用（排到最前面）",
            };
            cb.Checked += GroupPickCheck_Click;
            cb.Unchecked += GroupPickCheck_Click;
            cb.MouseRightButtonUp += GroupPickFavorite_Click;
            wrap.Children.Add(cb);
        }
        BgiGroupsHost.Children.Add(wrap);
    }

    /// <summary>
    /// v1.15 / L4：右键把配置组设为常用 / 取消常用。
    ///
    /// <para>
    /// 用右键而不是加一颗星按钮：勾选框本身已经是「点一下改状态」，
    /// 再挂一个按钮会让 8 个组的区域变成 16 个可点目标。右键不占版面，
    /// 悬停提示里写着，需要的人一次就学会。
    /// </para>
    /// </summary>
    private void GroupPickFavorite_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not CheckBox cb || cb.Tag is not string name) return;
            e.Handled = true;

            AppState.Settings.FavoriteGroups = GroupFavorites.Toggle(AppState.Settings.FavoriteGroups, name);
            AppState.SaveSettings();
            RenderGroupPicker();

            bool now = GroupFavorites.IsFavorite(AppState.Settings.FavoriteGroups, name);
            AppState.PublishLog(now ? $"★ 已把「{name}」设为常用" : $"已把「{name}」移出常用");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("切换常用配置组失败：" + ex.Message);
        }
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
        RefreshCompletionRow(force: true);
        AppState.PublishLog("✓ 已重新识别 BetterGI 配置组");
    }

    // ===== 备份与恢复（v1.17）=====

    /// <summary>
    /// 备份：先问要不要连一条龙配置一起带（它可能含账号路线，不是人人都想发出去），
    /// 再打包。包落在 exe 旁「备份」目录，打完立刻把路径显示出来并选中——
    /// 用户下一步多半是把它发给自己或拷到 U 盘，「打好了但不知道在哪」等于没打。
    /// </summary>
    private void BtnBackupNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string cfgDir = Path.GetDirectoryName(AppState.SettingsPath) ?? "";
            if (cfgDir.Length == 0)
            {
                AppDialog.Show("找不到配置目录，没法备份。", null, AppDialog.Kind.Warning, "备份", owner: this);
                return;
            }

            var oneDragon = BgiConfigWriter.ConfigPath(BgiDiscovery.BgiRoot, "probe.json");
            string? oneDragonDir = oneDragon == null ? null : Path.GetDirectoryName(oneDragon);
            int bgiCount = 0;
            if (oneDragonDir != null && Directory.Exists(oneDragonDir))
                bgiCount = SettingsBackup.CollectBgiFiles(oneDragonDir, File.Exists, Directory.GetFiles).Count;

            bool withBgi = false;
            if (bgiCount > 0)
            {
                withBgi = AppDialog.Show(
                    $"要不要把 {bgiCount} 个一条龙配置也打进包里？",
                    "带上：换台机器也能直接用同一套路线。\n不带：包更小，也不含你自己写的路线内容。",
                    AppDialog.Kind.Info, "备份设置", "带上", "只备份设置", owner: this);
            }

            var files = new List<KeyValuePair<string, string>>();
            foreach (var name in SettingsBackup.CollectFiles(cfgDir, File.Exists))
                files.Add(new(name, Path.Combine(cfgDir, name)));
            if (withBgi && oneDragonDir != null)
            {
                foreach (var name in SettingsBackup.CollectBgiFiles(oneDragonDir, File.Exists, Directory.GetFiles))
                    files.Add(new(SettingsBackup.BgiEntry + name, Path.Combine(oneDragonDir, name)));
            }

            string zip = Path.Combine(SettingsBackup.Dir, SettingsBackup.FileName(DateTime.Now));
            var (ok, msg) = SettingsBackup.WritePackage(zip, files, DateTime.Now);
            if (!ok)
            {
                AppDialog.Show(msg, null, AppDialog.Kind.Warning, "备份设置", owner: this);
                AppState.PublishLog("✗ 备份没成功：" + msg);
                return;
            }

            LogService.LogEvent("已备份设置：" + zip);
            AppState.PublishLog("✓ 已备份到 " + Path.GetFileName(zip));
            AppDialog.Show("备份好了。", zip, AppDialog.Kind.Info, "备份设置", "知道了", "打开所在文件夹",
                () => RevealInExplorer(zip), this, zip);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("备份失败：" + ex.Message);
            AppDialog.Show("备份时出了点问题：" + ex.Message, null, AppDialog.Kind.Warning, "备份设置", owner: this);
        }
    }

    /// <summary>
    /// 恢复：这是全程序唯一一个「点一下就把现有东西换掉」的操作，所以分三步——
    /// 先说会覆盖什么 → 先把当前这份自动备份一次 → 再解包。
    /// **自动备份失败就整个不做**：宁可这次恢复不成功，也不能让用户两头落空。
    /// </summary>
    private void BtnBackupRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string cfgDir = Path.GetDirectoryName(AppState.SettingsPath) ?? "";
            if (cfgDir.Length == 0)
            {
                AppDialog.Show("找不到配置目录，没法恢复。", null, AppDialog.Kind.Warning, "恢复设置", owner: this);
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选一个之前备份的包",
                Filter = "一条龙管家备份|*.zip|所有文件|*.*",
                InitialDirectory = Directory.Exists(SettingsBackup.Dir) ? SettingsBackup.Dir : cfgDir,
            };
            if (dlg.ShowDialog(this) != true) return;

            var manifest = SettingsBackup.ReadManifest(dlg.FileName, out string reason);
            if (manifest == null)
            {
                AppDialog.Show(reason, null, AppDialog.Kind.Warning, "恢复设置", owner: this);
                return;
            }

            bool hasBgi = manifest.BgiFiles.Count > 0;
            string extra = hasBgi ? $"\n包里还有 {manifest.BgiFiles.Count} 个一条龙配置。" : "";
            if (!AppDialog.Show(SettingsBackup.RestorePrompt(manifest, dlg.FileName) + extra,
                    Path.GetFileName(dlg.FileName), AppDialog.Kind.Warning, "恢复设置", "恢复", "算了", owner: this))
                return;

            // 先给现在这份留底
            var current = new List<KeyValuePair<string, string>>();
            foreach (var name in SettingsBackup.CollectFiles(cfgDir, File.Exists))
                current.Add(new(name, Path.Combine(cfgDir, name)));
            string safety = Path.Combine(SettingsBackup.Dir, "恢复前-" + SettingsBackup.FileName(DateTime.Now));
            var (bOk, bMsg) = SettingsBackup.WritePackage(safety, current, DateTime.Now);
            if (!bOk)
            {
                AppDialog.Show("没能先备份现在这份，为了不丢东西这次没有恢复。", bMsg,
                    AppDialog.Kind.Warning, "恢复设置", owner: this);
                AppState.PublishLog("✗ 恢复中止（自动备份失败）：" + bMsg);
                return;
            }

            string? oneDragonDir = null;
            if (hasBgi)
            {
                var probe = BgiConfigWriter.ConfigPath(BgiDiscovery.BgiRoot, "probe.json");
                if (probe != null) oneDragonDir = Path.GetDirectoryName(probe);
                if (oneDragonDir == null || !Directory.Exists(oneDragonDir))
                {
                    AppDialog.Show("包里有一条龙配置，但这台机器还没填 BetterGI 路径，这次只恢复设置。",
                        null, AppDialog.Kind.Info, "恢复设置", owner: this);
                    oneDragonDir = null;
                }
            }

            var (restored, skipped, msg) = SettingsBackup.Extract(dlg.FileName, cfgDir, oneDragonDir, oneDragonDir != null);
            if (restored == 0)
            {
                AppDialog.Show(msg, null, AppDialog.Kind.Warning, "恢复设置", owner: this);
                AppState.PublishLog("✗ 恢复没成功：" + msg);
                return;
            }

            LogService.LogEvent($"已从 {Path.GetFileName(dlg.FileName)} 恢复 {restored} 个文件，恢复前那份在 {safety}");
            AppState.PublishLog($"✓ 已恢复 {restored} 个文件");

            string detail = skipped.Count > 0
                ? $"{msg}跳过 {skipped.Count} 个不认识的条目。\n恢复前那份在：{safety}"
                : $"恢复前那份在：{safety}";
            AppDialog.Show("恢复完了。程序需要重开一次才会用上新设置。", detail,
                AppDialog.Kind.Info, "恢复设置", "重开程序", "稍后", RestartSelf, this);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("恢复失败：" + ex.Message);
            AppDialog.Show("恢复时出了点问题：" + ex.Message, null, AppDialog.Kind.Warning, "恢复设置", owner: this);
        }
    }

    private void BtnBackupOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsBackup.Dir);
            RevealInExplorer(SettingsBackup.Dir);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开备份目录失败：" + ex.Message);
        }
    }

    /// <summary>在资源管理器里定位到某个文件或目录（选中它，而不是只打开一层）</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开资源管理器失败：" + ex.Message);
        }
    }

    /// <summary>重启自己：把当前 exe 路径交给系统再拉一次，然后关掉这个进程</summary>
    private void RestartSelf()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                });
            }
            LogService.LogEvent("恢复后重启程序");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("重启失败，请手动重开：" + ex.Message);
            AppDialog.Show("没能自动重开，请手动关掉再打开一次。", null, AppDialog.Kind.Warning, "恢复设置", owner: this);
        }
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
            // v1.15 / L4：常用表也要跟着收敛，否则删掉的组会一直挂在设置里，
            // 下次有人新建同名组时莫名其妙变成常用。
            var favBefore = AppState.Settings.FavoriteGroups ?? Array.Empty<string>();
            var favAfter = GroupFavorites.Prune(favBefore, groups);
            if (favAfter.Length != favBefore.Length)
            {
                AppState.Settings.FavoriteGroups = favAfter;
                AppState.SaveSettings();
            }

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

    private void TgWeeklyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgWeeklyReport.IsChecked == true;
        AppState.Settings.WeeklyReportEnabled = on;
        AppState.SaveSettings();
        LogService.LogEvent("周报：" + (on ? "开启" : "关闭"));
        if (on) AppState.PublishLog("周报已开启：每周首次打开时汇报近七天的队列情况");
    }

    // ===== v1.10：收益账本 =====

    private void TgHarvest_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgHarvest.IsChecked == true;
        AppState.Settings.HarvestEnabled = on;
        AppState.SaveSettings();
        LogService.LogEvent("收益账本：" + (on ? "开启" : "关闭"));
        if (on)
        {
            // 开的时候先把本月已有的账报一遍：让用户立刻判断解析对不对，
            // 而不是等到下次跑完队列才知道它有没有在工作。
            var month = HarvestLedger.GetMonth();
            string mine = HarvestLedger.Describe(month, "本月已记");
            AppState.PublishLog(mine.Length > 0
                ? "收益账本已开启：" + mine
                : "收益账本已开启：还没有记录，下次队列结束时会报一句");
        }
    }

    // ===== v1.7：控制 API 与局域网面板 =====

    private void TgControlApi_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgControlApi.IsChecked == true;
        AppState.Settings.ControlApiEnabled = on;
        if (on) AppState.EnsureControlToken();
        AppState.SaveSettings();
        RefreshApiRows(AppState.Settings);
        LogService.LogEvent("控制 API：" + (on ? "开启" : "关闭"));
        if (on)
        {
            AppState.PublishLog("控制 API 已开启：令牌在上面的框里，脚本用 " + ControlApi.TokenHeader + " 头带上即可");
            NotifyService.Show(NotifyLevel.Info, "龙管家", "控制 API 已开启。它只监听本机回环地址，局域网里的其他设备连不上。");
        }
    }

    private void TgLanPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgLanPanel.IsChecked == true;
        AppState.Settings.LanPanelEnabled = on;
        AppState.SaveSettings();
        RefreshApiRows(AppState.Settings);
        LogService.LogEvent("局域网面板：" + (on ? "开启" : "关闭"));

        // 监听前缀在启动时就定了（回环 / 所有网卡），改开关要重启监听才生效
        WebhookHub.Start(AppState.Settings.BgiWebhookPort, on);
        if (on)
        {
            string addr = WebhookHub.PanelAddress();
            AppState.PublishLog("局域网面板已开启：" + addr);
            NotifyService.Show(NotifyLevel.Info, "龙管家", "局域网面板已开启，手机浏览器打开：\n" + addr);
            TbLanPanelHint.Text = "手机连同一个 Wi-Fi，浏览器打开：" + addr;
        }
        else
        {
            TbLanPanelHint.Text = "打开后同一 Wi-Fi 下的手机浏览器可以看进度和最近一条事件，但没有任何按钮——只能看，不能指挥。";
        }
    }

    private void BtnCopyToken_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardHelper.TrySetText(TbApiToken.Text))
        {
            AppState.PublishLog("令牌已复制到剪贴板");
        }
        else
        {
            ClipboardHelper.ShowManualCopyFallback(TbApiToken.Text, this, "复制令牌");
        }
    }

    /// <summary>控制 API 打开时才露令牌行；局域网面板打开时把地址写到提示里。</summary>
    private void RefreshApiRows(AppSettings s)
    {
        ApiTokenRow.Visibility = s.ControlApiEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (s.ControlApiEnabled) TbApiToken.Text = AppState.EnsureControlToken();
        if (s.LanPanelEnabled)
        {
            string addr = WebhookHub.PanelAddress();
            if (addr.Length > 0) TbLanPanelHint.Text = "手机连同一个 Wi-Fi，浏览器打开：" + addr;
        }
    }

    // ===== v1.8：全局热键 / 迷你浮条 / 跑完说一句话 =====

    /// <summary>
    /// 按当前设置注册或注销热键。开关切换、改键、启动时都走这里，
    /// 保证「设置里显示的组合」和「系统里真注册的组合」永远一致。
    /// </summary>
    private void ApplyHotkeys()
    {
        try
        {
            var s = AppState.Settings;
            if (!s.HotkeyEnabled)
            {
                _hotkeys?.UnregisterAll();
                LogService.LogEvent("热键：已注销（开关关闭）");
                return;
            }

            _hotkeys ??= CreateHotkeyService();

            // 两个组合写成一样时只注册第一个——否则后注册的会顶掉前者，用户会以为「停止键没反应」
            var list = new List<(string Action, string Text)>
            {
                ("start", s.HotkeyStart),
                ("stop", s.HotkeyStop),
            };
            if (string.Equals(HotkeyService.Normalize(s.HotkeyStart), HotkeyService.Normalize(s.HotkeyStop),
                    StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(1);
                LogService.LogEvent("热键：开跑与停止写的是同一个组合，只保留开跑");
            }

            bool ok = _hotkeys.Register(list.ToArray());
            AppState.PublishLog(ok
                ? "热键已就绪：" + HotkeyService.Normalize(s.HotkeyStart) + " 开跑 / " + HotkeyService.Normalize(s.HotkeyStop) + " 停止"
                : "热键注册失败：组合可能已被其它程序占用（详见日志）");
            RefreshHotkeyRow(s);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("热键：应用失败 " + ex.Message);
        }
    }

    private HotkeyService CreateHotkeyService()
    {
        var svc = new HotkeyService();
        svc.Pressed += action =>
        {
            // WM_HOTKEY 来自隐藏窗口的消息循环，已经在 UI 线程，但仍用 Dispatcher 兜一层
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SoundService.Play("click.wav");
                if (action == "start")
                {
                    if (QueueService.IsRunning) return;
                    AppState.PublishLog("热键：开跑");
                    _ = QueueService.StartAsync(QueueTrigger.Manual);
                }
                else if (action == "stop")
                {
                    if (!QueueService.IsRunning) return;
                    AppState.PublishLog("热键：停止");
                    QueueService.Stop();
                }
            }));
        };
        return svc;
    }

    private void RefreshHotkeyRow(AppSettings s)
    {
        HotkeyRow.Visibility = s.HotkeyEnabled ? Visibility.Visible : Visibility.Collapsed;
        TgHotkey.IsChecked = s.HotkeyEnabled;
        TbHotkeyStart.Text = s.HotkeyStart;
        TbHotkeyStop.Text = s.HotkeyStop;
    }

    private void TgHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgHotkey.IsChecked == true;
        AppState.Settings.HotkeyEnabled = on;
        AppState.SaveSettings();
        RefreshHotkeyRow(AppState.Settings);
        ApplyHotkeys();
        LogService.LogEvent("热键：" + (on ? "开启" : "关闭"));
        if (on)
            NotifyService.Show(NotifyLevel.Info, "龙管家",
                "热键已开启。如果某个组合在游戏里没反应，多半被别的程序占了，换一个即可。");
    }

    private void TbHotkeyStart_LostFocus(object sender, RoutedEventArgs e) => CommitHotkey(TbHotkeyStart, isStart: true);
    private void TbHotkeyStop_LostFocus(object sender, RoutedEventArgs e) => CommitHotkey(TbHotkeyStop, isStart: false);

    /// <summary>输入框失焦时校验并保存：写法不合法就退回原值，不做无声的吞掉。</summary>
    private void CommitHotkey(TextBox box, bool isStart)
    {
        if (_suppressToggle) return;
        var s = AppState.Settings;
        string raw = box.Text ?? "";
        string normalized = HotkeyService.Normalize(raw);

        if (HotkeyService.Parse(raw) == null)
        {
            box.Text = isStart ? s.HotkeyStart : s.HotkeyStop;
            AppState.PublishLog("热键：写法要像 Ctrl+Alt+R（至少一个修饰键 + 一个主键）");
            return;
        }

        box.Text = normalized;
        if (isStart) s.HotkeyStart = normalized;
        else s.HotkeyStop = normalized;
        AppState.SaveSettings();
        if (s.HotkeyEnabled) ApplyHotkeys();
    }

    private void TgMiniBar_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgMiniBar.IsChecked == true;
        AppState.Settings.MiniBarEnabled = on;
        AppState.SaveSettings();
        ApplyMiniBar();
        LogService.LogEvent("浮条：" + (on ? "开启" : "关闭"));
    }

    /// <summary>开就建一个、关就关掉。浮条本身不持有主窗，避免循环引用。</summary>
    private void ApplyMiniBar()
    {
        try
        {
            bool on = AppState.Settings.MiniBarEnabled;
            if (on)
            {
                if (_miniBar == null)
                {
                    _miniBar = new MiniBarWindow();
                    _miniBar.Closed += (_, _) => _miniBar = null;
                    _miniBar.Show();
                }
                else
                {
                    _miniBar.Show();
                    _miniBar.Activate();
                }
            }
            else
            {
                _miniBar?.Close();
                _miniBar = null;
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("浮条：显示失败 " + ex.Message);
        }
    }

    private void TgSpeech_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgSpeech.IsChecked == true;
        AppState.Settings.SpeechEnabled = on;
        AppState.SaveSettings();
        LogService.LogEvent("语音：" + (on ? "开启" : "关闭"));

        if (on)
        {
            // 立刻试一句：本机没有语音引擎时，用户当场就知道，而不是等到跑完才发现没声音
            if (SpeechService.Available())
            {
                SpeechService.Speak("语音已开启。", 0);
                AppState.PublishLog("语音已开启：本机有可用的语音引擎");
            }
            else
            {
                TgSpeech.IsChecked = false;
                AppState.Settings.SpeechEnabled = false;
                AppState.SaveSettings();
                AppState.PublishLog("语音：本机没有可用的语音引擎（精简版 Windows 常见），已保持关闭");
            }
        }
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
            // v1.13：组开始/结束直接影响队列卡顶部那条实时条。
            if (kind is SentinelEventKind.GroupStarted or SentinelEventKind.GroupEnded
                or SentinelEventKind.DragonEnded or SentinelEventKind.BgiExited)
                RefreshLiveStrip();
            // v1.15：主卡要跟着「开跑 / 停下 / 出岔子」换脸，次秒级延迟就够，
            // 但事件来了先刷一次，免得 1 秒定时器正好错过这一刻。
            RefreshHomeHero();
        }));
    }

    // ===== 实时条（v1.13）=====

    /// <summary>
    /// 队列卡顶部那条「现在在跑哪个组 / 还要多久」。
    ///
    /// <para>
    /// 数据全部来自已有零件：<see cref="QueueService.CurrentToolId"/> 与
    /// <see cref="QueueService.CurrentToolStartedAt"/>（工具）、
    /// <see cref="SentinelService.HealthSnapshot"/> 的 CurrentGroup / CurrentGroupSince（组）、
    /// <see cref="GroupArchive.MedianMinutes"/>（历史中位数）。不新增采集。
    /// </para>
    /// <para>
    /// 不跑的时候整条 Collapsed：这个位置原本放的就是「一眼看清在干嘛」的那句话，
    /// 空闲时没有话可说，就不该占一行。
    /// </para>
    /// </summary>
    private void RefreshLiveStrip()
    {
        try
        {
            var h = SentinelService.HealthSnapshot();
            var snap = LiveStatus.Build(
                QueueService.IsRunning,
                ProfileName(QueueService.CurrentToolId),
                h.CurrentGroup,
                QueueService.CurrentToolStartedAt == default ? null : QueueService.CurrentToolStartedAt,
                h.CurrentGroupSince == default ? null : h.CurrentGroupSince,
                MedianFor(h.CurrentGroup),
                DateTime.Now);

            if (!snap.Running)
            {
                LiveStrip.Visibility = Visibility.Collapsed;
                return;
            }

            LiveStrip.Visibility = Visibility.Visible;

            // 项与项之间的那段等待：这时上一项已经跑完、下一项还没开始，
            // 再报「某某组 · 已跑 12 分」就是在说过去的事。改成「间隔中 + 还有多久」。
            bool waiting = !string.IsNullOrEmpty(_queueStatusText)
                           && QueueService.CurrentToolStartedAt == _queueStatusStamp;

            TbLiveGroup.Text = waiting ? "间隔中" : snap.Headline;
            TbLiveSince.Text = waiting ? _queueStatusText : snap.Detail;

            int done = QueueService.DoneToolIdsToday().Count;
            int total = done + QueueService.UnfinishedToday().Count;

            // 右侧固定挂队列进度：中间那两句会变（组名、剩余时间），
            // 只有这个数字是不动的位置，扫一眼就知道整晚跑到第几项了。
            TbLiveEta.Text = total > 0 ? $"{done}/{total}" : "";
            LiveStrip.ToolTip = LiveStatus.Tooltip(snap, done, total);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("实时条刷新失败：" + ex.Message);
        }
    }

    /// <summary>工具 id → 显示名；找不到就返回空串（界面上会退化成「正在执行」）。</summary>
    private static string ProfileName(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return "";
        try { return ProfileRegistry.Get(toolId)?.DisplayName ?? ""; }
        catch { return ""; }
    }

    // 中位数要扫 14 天的 JSON，1 秒一次扛不住；按 (组名, 日期) 缓存，
    // 跨天自动失效。同一个组跑一整天也只算一次。
    private static string _medianKey = "";
    private static double? _medianValue;

    /// <summary>拖拽悬停时是否已把首屏主卡点亮（避免每帧重设 BorderBrush）。</summary>
    private bool _dropLit;

    private static double? MedianFor(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        string key = group + "|" + DateTime.Today.ToString("yyyy-MM-dd");
        if (string.Equals(key, _medianKey, StringComparison.Ordinal)) return _medianValue;
        double? value = null;
        try { value = GroupArchive.MedianMinutes(group); }
        catch (Exception ex) { LogService.LogEvent("耗时中位数计算失败：" + ex.Message); }
        _medianKey = key;
        _medianValue = value;
        return value;
    }

    // ===== 首屏主卡（v1.15）=====

    /// <summary>
    /// 首屏那张会变的主卡。
    ///
    /// <para>
    /// 它要回答的问题只有一个：<b>现在该点哪</b>。所以每种状态只给一个主按钮，
    /// 其余操作降级成次按钮；「什么都没配」时次按钮也只是「我先看看」。
    /// </para>
    /// <para>
    /// 判定逻辑全在 <see cref="HomeState.Build"/> 里（纯函数、可断言），
    /// 这里只负责把结果贴到界面上、不掺判断——否则以后加一个状态要改两处，总有一处会漏。
    /// </para>
    /// </summary>
    private void RefreshHomeHero()
    {
        try
        {
            var s = AppState.Settings;
            var h = SentinelService.HealthSnapshot();

            int done = QueueService.DoneToolIdsToday().Count;
            int total = done + QueueService.UnfinishedToday().Count;

            // 「配好了」＝ 至少有一个内置工具能找到 exe。
            // 工具卡折叠进队列之后，这里是唯一还知道「有没有东西可跑」的地方。
            bool anyReady = false;
            foreach (string id in new[] { "bettergi", "march7th", "ok-ww" })
            {
                try
                {
                    if (ToolRunner.HasExecutable(ProfileRegistry.Get(id))) { anyReady = true; break; }
                }
                catch { }
            }

            // v1.16：自定义工具也算。以前只认三个内置工具，
            // 只配了自定义工具的人会一直看到「先告诉我在哪」，而他的工具明明已经能跑。
            if (!anyReady)
            {
                try
                {
                    anyReady = (s.Queue ?? new List<QueueEntry>())
                        .Select(q => q.ToolId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Any(id => ToolRunner.HasExecutable(ProfileRegistry.Get(id)));
                }
                catch { }
            }

            // 「刚才出过事」只认 30 分钟内的告警：昨天半夜卡过一次，
            // 今天中午打开软件不该还在说「刚才有点不对劲」。
            bool trouble = s.ShieldAlertAt.HasValue
                           && DateTime.Now - s.ShieldAlertAt.Value <= TimeSpan.FromMinutes(30);

            var view = HomeState.Build(
                anyReady,
                QueueService.IsRunning,
                ProfileName(QueueService.CurrentToolId),
                h.CurrentGroup,
                QueueService.CurrentToolStartedAt == default
                    ? TimeSpan.Zero
                    : DateTime.Now - QueueService.CurrentToolStartedAt,
                h.CurrentGroupSince == default ? null : DateTime.Now - h.CurrentGroupSince,
                MedianFor(h.CurrentGroup),
                done,
                total,
                s.ScheduleEnabled ? NextScheduleTime() : null,
                s.LastRunOk,
                ParseLastRunAt(s),
                trouble,
                DateTime.Now);

            TbHeroTitle.Text = view.Title;
            TbHeroDetail.Text = view.Detail;
            BtnHeroPrimary.Content = view.PrimaryText;
            BtnHeroPrimary.Tag = view.PrimaryCommand;
            BtnHeroSecondary.Content = view.SecondaryText;
            BtnHeroSecondary.Tag = view.SecondaryCommand;

            // 次按钮多余时收起：主卡已经说了「停下」，旁边再挂一个「看今天」只会分走注意力。
            BtnHeroSecondary.Visibility = string.IsNullOrEmpty(view.SecondaryText)
                ? Visibility.Collapsed : Visibility.Visible;

            RenderHomeSteps(view.ShowSteps);

            // 出错时整张卡换描边色：这一条比任何文字都快。
            HomeHero.BorderBrush = trouble
                ? (BrushOf("WarningBrush") ?? DotYellow)
                : (BrushOf("BorderBrush") ?? DotGray);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("首屏主卡刷新失败：" + ex.Message);
        }
    }

    /// <summary>引导三步。只在「什么都没配」时出现——配好之后这块是空的，不留坑。</summary>
    private void RenderHomeSteps(bool show)
    {
        HomeHeroSteps.Children.Clear();
        HomeHeroSteps.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        HomeHeroSteps.Children.Add(HomeStep("1", "选 BetterGI 的 exe", "点右边按钮，或者把 exe 直接拖进窗口", true));
        HomeHeroSteps.Children.Add(HomeStep("2", "确认每天几点跑", "默认早上 8 点，之后随时能改", false));
        HomeHeroSteps.Children.Add(HomeStep("3", "点开跑，然后去睡觉", "跑完会给你一句话", false));
    }

    private UIElement HomeStep(string no, string title, string hint, bool active)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Background = active ? (BrushOf("BrandBrush") ?? DotGreen) : (BrushOf("InsetBrush") ?? DotGray),
            BorderBrush = BrushOf("BorderBrush"),
            BorderThickness = active ? new Thickness(0) : new Thickness(1),
        };
        badge.Child = new TextBlock
        {
            Text = no, FontSize = 10.5,
            Foreground = active ? (BrushOf("BrandOnBrush") ?? DotGreen) : (BrushOf("TextTertiaryBrush") ?? DotGray),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        // 两列（序号 / 正文）而不是三列：标题和说明之间如果插一个 * 列，
        // 渲染出来会空出两百多像素，看着像两张表。让它们挨着。
        var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12.5,
            Foreground = active ? (BrushOf("TextPrimaryBrush") ?? DotGray) : (BrushOf("TextSecondaryBrush") ?? DotGray),
        });
        text.Children.Add(new TextBlock
        {
            Text = hint, FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0),
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private void BtnHeroPrimary_Click(object sender, RoutedEventArgs e) => RunHeroCommand(BtnHeroPrimary);

    private void BtnHeroSecondary_Click(object sender, RoutedEventArgs e) => RunHeroCommand(BtnHeroSecondary);

    /// <summary>
    /// 主卡按钮不直接绑 Click 处理器，而是绑命令 id——这样加一个状态只需要在
    /// <see cref="HomeState"/> 里给出新 id，界面这边一行都不用改。
    /// </summary>
    private void RunHeroCommand(Button? btn)
    {
        string id = btn?.Tag as string ?? "";
        if (id.Length == 0) return;
        try
        {
            if (string.Equals(id, "pick.bettergi", StringComparison.Ordinal))
            {
                PickBetterGi();
                return;
            }
            RunCommand(id);
        }
        catch (Exception ex)
        {
            LogService.LogEvent($"首屏主卡执行 {id} 失败：{ex.Message}");
        }
    }

    /// <summary>「选择 BetterGI.exe」：跟工具卡里的浏览按钮走同一条路，只是不用先展开折叠体。</summary>
    private void PickBetterGi()
    {
        var profile = ProfileRegistry.Get("bettergi");
        var dlg = new OpenFileDialog
        {
            Title = "选择 BetterGI 的主程序",
            Filter = "可执行文件|*.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        ApplyToolPath("bettergi", dlg.FileName);
    }

    /// <summary>
    /// 把某个工具的路径定下来。选文件、拖进来都走这里——
    /// 两处各写一遍的话，早晚有一处忘了刷配置组。
    /// </summary>
    private void ApplyToolPath(string toolId, string path)
    {
        try
        {
            var profile = ProfileRegistry.Get(toolId);
            profile.ExecutablePath = path;
            if (string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase))
                AppState.Settings.BgiPath = path;
            ToolIconService.Invalidate(toolId);
            ProfileRegistry.Save(profile);
            AppState.PublishLog($"✓ 已设置 {profile.DisplayName} 路径");
            RefreshToolCard(toolId);
            FindHintOf(toolId).Text = "";
            if (string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase))
                RefreshDetectedGroups(force: true);
            RefreshHomeHero();
            RefreshToolsSummary();
        }
        catch (Exception ex)
        {
            LogService.LogEvent($"设置 {toolId} 路径失败：{ex.Message}");
        }
    }

    // ===== 拖进来一个 exe（v1.16）=====

    /// <summary>
    /// 拖放的目标是整张主卡。为什么不只在「工具设置」里接：v1.15 的三步引导
    /// 就写在主卡上（「把 exe 直接拖进窗口」），而那句话当时是空的。
    /// </summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        bool ok = false;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var items = e.Data.GetData(DataFormats.FileDrop) as string[];
                ok = items is { Length: 1 } && DropAssign.ToolOf(items[0]) != null;
                // 目录也可能命中，但拖拽悬停时不去列目录（会在慢盘上卡住整个 UI）
                if (!ok && items is { Length: 1 } && Directory.Exists(items[0])) ok = true;
            }
        }
        catch { ok = false; }

        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        // 能放的时候把首屏那张卡的描边点亮——光标形状之外再给一个「这里能松手」的信号。
        // 光标本身在 Win11 上并不总变，尤其是拖文件夹的时候。
        if (ok && !_dropLit)
        {
            _dropLit = true;
            HomeHero.BorderBrush = BrushOf("BrandBrush") ?? DotGreen;
        }
        else if (!ok && _dropLit)
        {
            _dropLit = false;
            RefreshHomeHero();
        }
    }

    /// <summary>拖出窗口时把高亮收回去。不改文字、不动布局——拖拽过程中界面跳动最劝退。</summary>
    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        if (!_dropLit) return;
        _dropLit = false;
        RefreshHomeHero();
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        _dropLit = false;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] items || items.Length == 0) return;
            if (items.Length > 1)
            {
                AppDialog.Show("一次拖一个就好。", "拖多了我分不清哪个该给哪个工具，请逐个拖。",
                    AppDialog.Kind.Info, "拖放", owner: this);
                return;
            }

            string path = items[0];
            bool isDir = Directory.Exists(path);
            var hit = DropAssign.Resolve(
                path,
                File.Exists,
                isDir,
                dir => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.exe") : Array.Empty<string>());

            if (hit == null)
            {
                AppDialog.Show(DropAssign.Refusal(path), null, AppDialog.Kind.Warning, "拖放", owner: this);
                return;
            }

            var (toolId, target) = hit.Value;
            string name = DropAssign.DisplayName(toolId);
            bool already = string.Equals(ProfileRegistry.Get(toolId).ExecutablePath, target, StringComparison.OrdinalIgnoreCase);
            if (already)
            {
                AppState.PublishLog($"{name} 的路径没变，还是 {target}");
                return;
            }

            ApplyToolPath(toolId, target);

            // 一次拖放就改配置，得让人看见改成了什么——日志里一行不够显眼。
            string preview = Path.GetFileName(target);
            AppState.PublishLog($"拖放识别为 {name}：{preview}");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("拖放失败：" + ex.Message);
        }
    }

    /// <summary>上次那一轮开始的时刻。LastRunAt 存的是「yyyy-MM-dd HH:mm」，解析不出来就当没有。</summary>
    private static DateTime? ParseLastRunAt(AppSettings s)
    {
        if (DateTime.TryParse(s.LastRunAt, out var at)) return at;
        return null;
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
        TbQueueTemplate.Text = s.NotifyQueueTemplate;
        SyncNotifyChips();
        ApplyNotifyMode();
        RefreshQueueTemplateHint();
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

    /// <summary>
    /// v1.15 / L6：跑完通知的正文模板。
    /// 空着就用内置正文——占位提示里写清楚这件事，否则用户会以为必须填点什么。
    /// </summary>
    private void TbQueueTemplate_LostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = NotifyTemplate.OneLine(TbQueueTemplate.Text);
            AppState.Settings.NotifyQueueTemplate = text;
            AppState.SaveSettings();
            RefreshQueueTemplateHint();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存通知模板失败：" + ex.Message);
        }
    }

    private void RefreshQueueTemplateHint()
    {
        string saved = AppState.Settings.NotifyQueueTemplate;
        TbQueueTemplateWatermark.Visibility = string.IsNullOrEmpty(TbQueueTemplate.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(saved))
        {
            TbQueueTemplateHint.Text = "例如「" + NotifyTemplate.Sample + "」\n" + NotifyTemplate.Hint;
            return;
        }

        var demo = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["结果"] = "成功",
            ["成功"] = "3",
            ["总数"] = "3",
            ["耗时"] = "2 小时 12 分",
            ["原因"] = "三月七助手 退出码 1",
            ["现场"] = @"全部日志\incidents\20261115-090000",
            ["收益"] = "摩拉 12.3 万",
        };
        string preview = NotifyTemplate.Render(saved, demo);
        TbQueueTemplateHint.Text = "发出去会是这样：" + preview;
    }

    private void TbQueueTemplate_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TbQueueTemplateWatermark == null) return;
        TbQueueTemplateWatermark.Visibility = string.IsNullOrEmpty(TbQueueTemplate.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    // ===== v1.8 / F15：命令面板（Ctrl+K）=====

    /// <summary>
    /// Ctrl+K 打开命令面板。
    /// 只在主窗拥有焦点时响应——游戏里按 Ctrl+K 不该把管家叫出来，
    /// 那件事交给全局热键（F9），两套机制职责分开。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenCommandPalette();
        }
    }

    private void OpenCommandPalette()
    {
        try
        {
            // 「单跑某工具」是按当前配置临时生成的，配置文件里没有这些工具就不出现
            var toolCommands = CommandRegistry.ToolCommands(
                ProfileRegistry.All.Select(p => (p.ToolId, p.DisplayName)));

            var dlg = new CommandPalette(toolCommands) { Owner = this };
            dlg.Executed += id => Dispatcher.BeginInvoke(new Action(() => RunCommand(id)));
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("命令面板：打开失败 " + ex.Message);
        }
    }

    /// <summary>
    /// 命令分发。这里是唯一知道「某条命令到底怎么执行」的地方——
    /// 面板本身不认识队列、主题、托盘，坏了也不会连累它。
    /// </summary>
    private void RunCommand(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        SoundService.Play("click.wav");
        LogService.LogEvent("命令面板：" + id);

        try
        {
            if (id.StartsWith("run.tool:", StringComparison.Ordinal))
            {
                string toolId = id.Substring("run.tool:".Length);
                var profile = ProfileRegistry.Get(toolId);
                if (profile == null)
                {
                    NotifyService.Show(NotifyLevel.Warning, "龙管家", "找不到这个工具：" + toolId);
                    return;
                }
                if (QueueService.IsRunning)
                {
                    NotifyService.Show(NotifyLevel.Info, "龙管家", "队列正在跑，等它结束或先停止。");
                    return;
                }
                _ = QueueService.StartSingleAsync(profile, QueueTrigger.Manual);
                return;
            }

            switch (id)
            {
                case "run.queue": BtnQueueStart_Click(this, new RoutedEventArgs()); break;
                case "run.continue": _ = QueueService.StartContinueAsync(); break;
                case "run.stop": if (QueueService.IsRunning) QueueService.Stop(); break;
                case "run.single": RevealCard(CustomToolsHost); break;

                case "view.schedule": RevealCard(ScheduleCard); break;
                case "view.queue": RevealCard(QueueHost); break;
                case "view.tools": RevealCard(CustomToolsHost); break;
                case "view.advanced": RevealCard(AdvancedBody); break;
                case "view.log": RevealCard(TbLog); break;
                case "view.calendar": OpenCalendar(); break;
                case "view.today": OpenToday(); break;
                case "view.logrules": OpenLogRules(); break;
                case "view.depends": OpenDepends(); break;
                case "view.groups": OpenGroupSlice(); break;
                case "view.preflight": OpenPreflight(); break;
                case "view.presets": OpenPresets(); break;
                case "view.notify": RevealCard(NotifyCard); break;

                case "tool.copyToken": BtnCopyToken_Click(this, new RoutedEventArgs()); break;
                case "tool.apiToggle":
                    TgControlApi.IsChecked = !(TgControlApi.IsChecked == true);
                    TgControlApi_Click(this, new RoutedEventArgs());
                    break;
                case "tool.lanToggle":
                    TgLanPanel.IsChecked = !(TgLanPanel.IsChecked == true);
                    TgLanPanel_Click(this, new RoutedEventArgs());
                    break;
                case "tool.snapshot":
                    _ = Task.Run(() =>
                    {
                        string msg = SnapshotService.CaptureMessage("命令面板");
                        NotifyService.Show(NotifyLevel.Info, "龙管家", msg);
                    });
                    break;

                case "ui.miniBar":
                    TgMiniBar.IsChecked = !(TgMiniBar.IsChecked == true);
                    TgMiniBar_Click(this, new RoutedEventArgs());
                    break;
                case "ui.theme": ToggleTheme(); break;
                case "ui.hotkey": RevealCard(HotkeyRow); break;

                case "sys.openFolder": BtnOpenGroupDir_Click(this, new RoutedEventArgs()); break;
                case "sys.check":
                    _ = CheckUpdateManualAsync();
                    break;
                case "sys.about": OpenAbout(); break;
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("命令面板：执行 " + id + " 失败：" + ex.Message);
            NotifyService.Show(NotifyLevel.Warning, "龙管家", "这条命令没执行成功，详见日志。");
        }
    }

    /// <summary>把某个元素滚进视野，顺手把它所在的折叠卡展开——命令面板的「打开 X」要真的到得了。</summary>
    private void RevealCard(FrameworkElement target)
    {
        try
        {
            // 折叠卡里的元素是 Collapsed 的，先展开父级（只往上找一层已知的折叠体）
            foreach (var body in new FrameworkElement?[] { AdvancedBody, SystemBody, NotifyBody })
            {
                if (body == null) continue;
                if (body == target || body.IsAncestorOf(target))
                    body.Visibility = Visibility.Visible;
            }
            target.BringIntoView();
            ContentScroll.ScrollToVerticalOffset(ContentScroll.VerticalOffset);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("命令面板：定位失败 " + ex.Message);
        }
    }

    /// <summary>主题在两个模式间切换；自定义背景保持不动（那是另一套设置）。</summary>
    private void ToggleTheme()
    {
        string next = Theme.Mode == "Light" ? "Dark" : "Light";
        AppState.Settings.Theme = next;
        AppState.SaveSettings();
        Theme.Set(next);
        AppState.PublishLog("主题：" + (next == "Light" ? "浅色" : "深色"));
    }

    private static string ProductVersion() => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.8.0";

    // ===== 检查更新（v1.16）=====

    private static DateTime _lastUpdateCheck = DateTime.MinValue;
    private static bool _updateCheckRunning;

    /// <summary>
    /// 启动时静默查一次。查到了、且真有新版，才说话；没新版就完全不出声。
    ///
    /// <para>
    /// 六小时一次的门槛是给「同一天反复开关」的人准备的——每次启动都请求一遍
    /// GitHub 既没必要也会撞速率限制（匿名每小时 60 次，看着够，但公司网络是共用的）。
    /// </para>
    /// </summary>
    private void StartBackgroundUpdateCheck()
    {
        if (_updateCheckRunning) return;
        if (DateTime.Now - _lastUpdateCheck < UpdateCheck.MinGap) return;
        _ = CheckUpdateCore(quiet: true);
    }

    /// <summary>用户点了「检查更新」。这时无论结果都要说一句，否则像是没反应。</summary>
    private async Task CheckUpdateManualAsync()
    {
        var r = await CheckUpdateCore(quiet: false).ConfigureAwait(true);
        try
        {
            AppState.PublishLog((r.HasNewer ? "↑ " : "· ") + r.Message);
            if (r.HasNewer && !string.IsNullOrWhiteSpace(r.Detail))
                NotifyService.Show(NotifyLevel.Info, "龙管家有新版本", r.Message + " " + r.Detail);
        }
        catch { }
    }

    private async Task<UpdateCheck.Result> CheckUpdateCore(bool quiet)
    {
        if (_updateCheckRunning)
            return UpdateCheck.Describe(null, ProductVersion(), false, "正在检查");

        _updateCheckRunning = true;
        try
        {
            _lastUpdateCheck = DateTime.Now;
            var r = await UpdateCheck.CheckAsync(ProductVersion()).ConfigureAwait(true);

            if (r.HasNewer)
            {
                // 有新版才落日志：没新版也写一行，日志会被这些噪音撑满。
                LogService.LogEvent("发现新版本：" + r.LatestTag + "（当前 " + ProductVersion() + "）");
                ShowUpdateHint(r);
            }
            else if (!quiet)
            {
                LogService.LogEvent("检查更新：" + r.Message);
            }
            return r;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("检查更新出错：" + ex.Message);
            return UpdateCheck.Describe(null, ProductVersion(), false, "出错了");
        }
        finally { _updateCheckRunning = false; }
    }

    /// <summary>有新版本时在首屏主卡下面挂一条，点了开浏览器。用户不点就一直在，直到他真的更新。</summary>
    private void ShowUpdateHint(UpdateCheck.Result r)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TbUpdateHint.Visibility = Visibility.Visible;
                TbUpdateHint.Text = "↑ " + r.Message + (string.IsNullOrWhiteSpace(r.Detail) ? "" : " " + r.Detail);
                TbUpdateHint.Tag = r.Url;
            }), DispatcherPriority.Background);
        }
        catch { }
    }

    private void TbUpdateHint_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string? url = TbUpdateHint.Tag as string;
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { LogService.LogEvent("打开更新页面失败：" + ex.Message); }
    }

    private void OpenAbout()
    {
        var s = AppState.Settings;
        AppDialog.Show(
            "把你常用的游戏工具排成队列，按顺序启动、盯着完成、跑完通知你。",
            detail: "版本 " + ProductVersion() + "\n" +
                    "工具 " + ProfileRegistry.All.Count + " 个 · 队列 " + (s.Queue?.Count ?? 0) + " 项\n" +
                    "运行中 " + (QueueService.IsRunning ? "是" : "否") + "\n" +
                    "数据目录 " + AppContext.BaseDirectory,
            kind: AppDialog.Kind.Info,
            title: "关于 一条龙管家",
            primary: "知道了",
            owner: this);
    }
}
