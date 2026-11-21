using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using DragonButler.Plugins;

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

        // v1.22：三页并排，每页宽度跟着可视区走。挂一次就够，窗口改大小也由它处理。
        PageClip.SizeChanged += OnPageClipSized;

        // 全局按键音：所有可交互元素在"按下"这一瞬出声
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(AnyInteractivePress), true);

        LoadSettingsToUi();
        BuildSettingsHome();   // v1.41 / M4-1：一级五张入口卡，构造时铺一次就够
        RebuildCustomToolCards();
        RefreshPlugins();
        // 汇报页只在切过去时刷一次（见 ShowPage），构造时不读盘：
        // DurationLedger 是懒加载的，主窗上屏那一瞬去读 60 天的账本纯属浪费。
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
            // v1.41 / M6-1：先摆回上次的位置，再谈居中——「上次在哪」比「默认在哪」优先。
            RestoreWindowBounds();
            // 向导放到窗口真正显示之后再弹，避免主界面停留在"未显示"状态
            Dispatcher.BeginInvoke(new Action(TryShowFirstRunWizard), DispatcherPriority.Background);
            // v1.41 / M4-2：等这一刻才收说明——在此之前控件还没量出行高，判断不了「超没超过一行」。
            // 之后再改窗口大小也不用管：收过的已经收好了，没超的本来就不该挂 ⓘ。
            HintFold.Apply(PageSettings);
            // v1.16：启动后安静地看一眼有没有新版。放这么晚是因为它要联网——
            // 界面先出来，网络慢不该让窗口空着等。
            StartBackgroundUpdateCheck();
        };

        _scheduleTimer.Tick += (_, _) => { CheckSchedule(); CheckAway(); };
        _scheduleTimer.Start();
        _countdownTimer.Start();
        _groupScanTimer.Start();
        _uiRefreshTimer.Start();

        try { MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 24); } catch { }
        // v1.41 / M6-1：最宽 = 可用宽 − 200。给两侧各留 100，是为了让「这不是全屏」一眼看得出来；
        // 贴着屏幕边缘的方窗口看起来像卡住了，而它其实只是被拖到最大。
        try { MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 200); } catch { }
    }

    private static void CenterOnActiveMonitor()
    {
        // WindowStartupLocation 已在 XAML 里设 CenterScreen；这里不额外移动，避免多屏时跳到主屏
    }

    // ===== v1.41 / M6-1：主窗位置与大小 =====

    /// <summary>
    /// 把窗口摆回上次关掉时的位置和大小。
    ///
    /// <para>
    /// 三种情况不动它：没记过（-1）、记的数不像话（宽高 <= 0）、
    /// 记的位置现在跑到屏幕外面去了（拔过外接屏、改过缩放）。最后一种最常见，
    /// 表现为「打开就是一片空白，任务栏有图标但窗口找不到」——所以必须检测，
    /// 不能只是把 Left/Top 赋回去。
    /// </para>
    /// </summary>
    private void RestoreWindowBounds()
    {
        try
        {
            var s = AppState.Settings;
            if (!LooksLikeValidBounds(s)) return;

            // 最大宽 = 屏幕可用宽 − 200：留出两侧，否则贴着边框的窗口看起来像全屏卡死。
            var work = SystemParameters.WorkArea;
            double maxW = Math.Max(MinWidth, work.Width - 200);
            double w = Math.Min(Math.Max(s.WindowWidth, MinWidth), maxW);
            double h = Math.Min(Math.Max(s.WindowHeight, MinHeight), Math.Max(MinHeight, work.Height));

            Width = w;
            Height = h;

            // 判定「这一块还有多少露在屏幕里」：只要标题栏能抓到就不算丢。
            // 拿整个矩形去比会把「右上角探出去一点」也算成丢，那样用户把它挪到边上
            // 下次打开就被拽回中间，比不记忆更烦人。
            var candidate = new Rect(s.WindowLeft, s.WindowTop, w, h);
            bool visible = candidate.Right > work.Left + 80
                           && candidate.Left < work.Right - 80
                           && candidate.Bottom > work.Top + 40
                           && candidate.Top < work.Bottom - 40;

            if (!visible)
            {
                // 位置失效但大小还有效：只把大小用上，位置交给 XAML 的 CenterScreen。
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = candidate.Left;
            Top = candidate.Top;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("恢复窗口位置失败：" + ex.Message);
        }
    }

    private static bool LooksLikeValidBounds(AppSettings s)
        => s.WindowLeft > -1 && s.WindowTop > -1 && s.WindowWidth > 0 && s.WindowHeight > 0;

    /// <summary>
    /// 关窗口（或收进托盘）时把当前位置记下来。
    /// 用 RestoreBounds 而不是 Left/Top：最小化/最大化之后 Left/Top 会变成
    /// (−32000, −32000)，记下那个下次启动窗口就在屏幕外面。
    /// </summary>
    private void SaveWindowBounds()
    {
        try
        {
            var s = AppState.Settings;
            Rect r = RestoreBounds;
            if (r.Width < MinWidth / 2 || r.Height < MinHeight / 2) return;
            s.WindowLeft = r.Left;
            s.WindowTop = r.Top;
            s.WindowWidth = r.Width;
            s.WindowHeight = r.Height;
            AppState.SaveSettings();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("记录窗口位置失败：" + ex.Message);
        }
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
            // v1.33：主题不再是三个 RadioButton，而是一块会滑的玻璃。
            // 「在哪一格」= 当前模式；超过三段的「自定义」搬到设置页去了。
            PlaceThemeThumb(ThemeModeIndex(), animate: false);
        }
        finally { _suppressThemeSeg = false; }
    }

    // ===== 两段式主题开关（v1.33 起，v1.34 收成两档）=====
    //
    // 做法照 BV15dVd6JEns 那期：一条轨道 + 一块玻璃滑块 + 两个图标。
    // 三个要点：
    //   ① 滑块比轨道高（32 vs 30）、上下各探出去 —— 视频里专门说过，
    //      缩在轨道里面玻璃效果根本看不出来，探出来才有「一块东西贴在玻璃上」的感觉。
    //      v1.34 把整体尺寸缩小了（174×36 -> 112×30）：原来那块压在标题栏里，
    //      面积一大，玻璃就只剩一片糊白，质感全变成脏。
    //   ② 切换时图标要分层错位再归位。视频里的说法是「位移距离不同 -> 速度不同 -> 视差」，
    //      到位时间一样（我们取 520ms），所以只要给不同元素不同距离，就有了速度差。
    //   ③ 只留深色和浅色两档。原来中间那一档是「跟随系统」——一个「替我做决定」的选项，
    //      可这块开关只有两个可见结果；第三档夹在中间，人点左边一下根本说不清到底切没切。
    private const double ThemeThumbWidth = 52;
    private const double ThemeTrackWidth = 112;
    private static readonly string[] ThemeModes = { "Dark", "Light" };

    private bool _themeDragging;
    private double _themeGrabOffset;
    private double _themeThumbX;

    private int ThemeModeIndex()
    {
        string m = Theme.Mode;
        int i = Array.FindIndex(ThemeModes, x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase));

        // 「跟随系统」和「自定义」都不在这两档里，但开关总得停在某一格上。
        // 判据用「现在这套配色实际是深还是浅」（Theme.LastSchemeLight），而不是去猜设置字符串——
        // 人看到的是屏幕上的深色，就让它停在深色那一格。
        if (i < 0) i = Theme.LastSchemeLight ? 1 : 0;
        return i;
    }

    /// <summary>两个位置：左 / 右。滑块宽 52，轨道宽 112，两端各贴边。</summary>
    private static double ThemeSlotX(int index) =>
        index <= 0 ? 0 : ThemeTrackWidth - ThemeThumbWidth;

    private int ThemeSlotAt(double x)
    {
        // 按滑块中心落在哪一半来判断，而不是按左上角——手是按在滑块中间拖的。
        double center = x + ThemeThumbWidth / 2.0;
        double slice = ThemeTrackWidth / 2.0;
        int i = (int)Math.Floor(center / slice);
        return Math.Clamp(i, 0, 1);
    }

    private void PlaceThemeThumb(int index, bool animate)
    {
        if (ThemeThumbShift == null) return;
        double target = ThemeSlotX(index);
        _themeThumbX = target;

        if (!animate)
        {
            ThemeThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            ThemeThumbShift.X = target;
        }
        else
        {
            // Smart Animate 那一档的手感：先略微冲过头再回来。
            // 260ms 太快（看不清视差），800ms 太黏（每天点好几次的东西不该等这么久）。
            // v1.35：回弹从 0.22 收到 0.12。原来那一版冲过头接近 11px，
            // 而轨道只有 112 宽、滑块 52 宽 —— 过头的那一下会探到轨道外面，
            // 看起来像「气泡把图标蹭歪了」。现在只留一点点惯性。
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 },
                FillBehavior = FillBehavior.HoldEnd,
            };
            ThemeThumbShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        PaintThemeIcons(index, animate);
    }

    /// <summary>
    /// 把两个图标按「当前模式」上色：选中的那档用主文字色，另一档退成三级灰。
    ///
    /// v1.36：这里只改颜色，不动位置。图标里每一条笔画（月亮、星星、太阳、光芒）
    /// 都是画在同一张画布上的图案，不是分层素材 —— 让其中几笔单独位移，
    /// 屏幕上读出来就是「图标自己在抖」，比整组飘更难懂。要做出「切换」的体感，
    /// 靠的是滑块滑过去 + 这一行的颜色淡变，而不是让图案动。
    ///
    /// 淡变 200ms 而不是瞬切：灰色变主色时如果是硬跳，眼睛会先注意到「亮了」，
    /// 再注意到「滑过去了」，两件事被打断成两拍；淡进去才是同一次动作的两个面。
    /// </summary>
    private void PaintThemeIcons(int index, bool animate = false)
    {
        var on = BrushOf("TextPrimaryBrush") ?? DotGray;
        var off = BrushOf("TextTertiaryBrush") ?? DotGray;
        var fade = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 必须用「自己的一份」画刷。XAML 里这些笔画绑的是 DynamicResource 指向的共享画刷，
        // 直接对它 BeginAnimation 会把全界面的三级灰文字一起改色 —— 那是另一个级别的 bug。
        // ReadLocalValue 能分清「还是 XAML 绑的」和「代码已经接手了」。
        void Tint(System.Windows.Shapes.Shape? shape, Brush target, bool isStroke)
        {
            if (shape == null || target is not SolidColorBrush to) return;
            DependencyProperty dp = isStroke
                ? System.Windows.Shapes.Shape.StrokeProperty
                : System.Windows.Shapes.Shape.FillProperty;

            SolidColorBrush brush;
            if (shape.ReadLocalValue(dp) is SolidColorBrush own)
            {
                brush = own;
            }
            else
            {
                var now = (isStroke ? shape.Stroke : shape.Fill) as SolidColorBrush;
                brush = new SolidColorBrush(now?.Color ?? to.Color);
                shape.SetValue(dp, brush);
            }

            if (!animate)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = to.Color;
                return;
            }
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to.Color, fade) { EasingFunction = ease });
        }

        try
        {
            Tint(MoonPath, index == 0 ? on : off, isStroke: true);
            Tint(Star1, index == 0 ? on : off, isStroke: false);
            Tint(Star2, index == 0 ? on : off, isStroke: false);

            Tint(SunCore, index == 1 ? on : off, isStroke: true);
            Tint(SunRays, index == 1 ? on : off, isStroke: true);
        }
        catch { }
    }
    private void ThemeSwitch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ThemeSwitchRoot == null || ThemeThumbShift == null) return;
        // v1.34：这一句是「点了没反应」的根治。
        // 标题栏那一层挂着 MouseLeftButtonDown -> DragMove()，按下会一路冒泡上去，
        // 窗口开始拖动，我们的 MouseUp 就再也收不到了（鼠标被 DragMove 接管）。
        // 所以在这一层吃掉事件：只有这一小块区域不拖窗口。
        e.Handled = true;
        _themeGrabOffset = e.GetPosition(ThemeSwitchRoot).X - _themeThumbX;
        _themeDragging = false;
        // 和导航栏同一个道理：按下先不抢鼠标，等真的拖起来才抢，
        // 否则「点一下图标切主题」会被这块 Grid 吃掉。
    }

    private void ThemeSwitch_MouseMove(object sender, MouseEventArgs e)
    {
        if (ThemeSwitchRoot == null || ThemeThumbShift == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        double x = e.GetPosition(ThemeSwitchRoot).X - _themeGrabOffset;

        if (!_themeDragging && Math.Abs(x - _themeThumbX) < 4) return;
        if (!_themeDragging)
        {
            _themeDragging = true;
            ThemeSwitchRoot.CaptureMouse();
        }

        double max = ThemeTrackWidth - ThemeThumbWidth;
        ThemeThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
        ThemeThumbShift.X = _themeThumbX = Math.Clamp(x, 0, max);
    }

    private void ThemeSwitch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ThemeSwitchRoot == null) return;
        // 按下和松开都要吃掉，否则事件冒泡到标题栏那一层，窗口跟着鼠标跑。
        e.Handled = true;
        bool wasDragging = _themeDragging;
        _themeDragging = false;
        if (ThemeSwitchRoot.IsMouseCaptured) ThemeSwitchRoot.ReleaseMouseCapture();

        int from = ThemeModeIndex();
        int to = wasDragging ? ThemeSlotAt(_themeThumbX) : ThemeSlotAt(e.GetPosition(ThemeSwitchRoot).X - ThemeThumbWidth / 2.0);

        // 注意判据是「这次点的模式跟现在真的模式一样吗」，不是「跟滑块所在那格一样吗」。
        // 跟随系统/自定义配色下开关会停在最近的两档之一（见 ThemeModeIndex），这时候点那一格
        // 必须真的切过去，否则人会发现「点了一下没反应」——和标签栏那个 bug 是同一种失望。
        bool sameAsNow = to == from
                         && string.Equals(Theme.Mode, ThemeModes[to], StringComparison.OrdinalIgnoreCase);
        if (sameAsNow)
        {
            // 拖了一点又放回原处，或者点到自己那一格：弹回去，不做任何切换。
            PlaceThemeThumb(from, animate: true);
            return;
        }
        ApplyTheme(ThemeModes[to], from);
    }

    private void ApplyTheme(string mode, int fromIndex = -1)
    {
        if (_suppressThemeSeg) return;
        int from = fromIndex >= 0 ? fromIndex : ThemeModeIndex();
        int to = Math.Max(0, Array.FindIndex(ThemeModes, x => string.Equals(x, mode, StringComparison.OrdinalIgnoreCase)));

        AppState.Settings.Theme = mode;
        AppState.SaveSettings();
        Theme.Set(mode);

        PlaceThemeThumb(to, animate: true);
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
        TgStayTray.IsChecked = s.StayInTray;
        TgStartMin.IsChecked = s.StartMinimized;
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
        TgTrimToTray.IsChecked = s.TrimToTrayEnabled;
        TbTrimHint.Text = s.TrimToTrayEnabled
            ? "收进托盘后会销毁主窗，省 3.7 MB；再打开要多等 1 秒多。队列在跑时不销毁。"
            : "实测只省 3.7 MB，但再打开一次要多等 1 秒多。想腾内存再开。";
        RefreshHotkeyRow(s);
        TbNightMinutes.Text = Math.Clamp(s.NightScreenMinutes, 5, 120).ToString();
        TbNightMinutes.IsEnabled = s.NightScreenEnabled;
        SelectComboByTag(CbAfterRun, s.AfterRunAction);
        RefreshAfterRunAppRow();
        SelectComboByTag(CbFailStrategy, s.FailStrategy);
        SelectComboByTag(CbInterval, s.QueueIntervalMinutes.ToString());
        SyncThemeSeg();
        RefreshAppearanceSummary();
        _suppressToggle = false;

        UpdateTimeDisplay();
        SyncWeekdayChips();
        UpdateScheduleStatus();
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
            bool anyTrigger = s.ScheduleEnabled || s.AwayStartEnabled;
            ScheduleBar.Visibility = anyTrigger ? Visibility.Visible : Visibility.Collapsed;
            NextDot.Fill = anyTrigger ? (BrushOf("BrandBrush") ?? DotGray) : DotGray;

            // v1.20：「你走开就跑」没有「下次几点」，所以它单独一句话，
            // 而不是在倒计时位置显示一个假的时间点。
            if (s.AwayStartEnabled && !s.ScheduleEnabled)
            {
                var snap = AwayWatcher.Snapshot;
                TbCountdown.Text = snap?.State == AwaySense.Presence.Away
                    ? "你不在，随时会开跑"
                    : "你走开 20 分钟后开跑";
            }
            else if (!s.ScheduleEnabled) TbCountdown.Text = "定时未开启";
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

        // v1.20：「你走开就跑」也停在简版——它没有「几点」可调，
        // 摊开时间与星期只会让人以为那些设置也生效。
        if (s.AwayStartEnabled)
        {
            TbQuickTitle.Text = "你走开，我就开跑";
            TbQuickDesc.Text = "盯着键鼠，连续安静 20 分钟才认账。\n中途碰一下鼠标就重新数。";
            BtnQuickAccept.Content = "改回按时间跑";
            BtnQuickAccept.Tag = "schedule";
            BtnQuickPick.Content = "现在就开跑";
            BtnQuickPick.Tag = "runnow";
            BtnQuickAway.Visibility = Visibility.Collapsed;
            return;
        }

        TbQuickTitle.Text = "每天 8 点，自己开跑";
        TbQuickDesc.Text = "到点自动拉起 BetterGI 跑一遍，跑完告诉你结果。\n你什么都不用做。";
        BtnQuickAccept.Content = "就这样，打开定时";
        BtnQuickAccept.Tag = null;
        BtnQuickPick.Content = "我自己挑时间";
        BtnQuickPick.Tag = null;
        BtnQuickAway.Visibility = Visibility.Visible;

        // 已经改过时间的人（不是默认的 8:00）看到的是自己定的时间，不能一律写「8 点」。
        string at = $"{Math.Clamp(s.ScheduleHour, 0, 23):00}:{Math.Clamp(s.ScheduleMinute, 0, 59):00}";
        bool everyDay = s.ScheduleWeekdays == 0x7F;
        TbQuickTitle.Text = everyDay ? $"每天 {at}，自己开跑" : $"下次 {at}，自己开跑";
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #2：「或者：我一走开就跑」。
    /// 与「就这样，打开定时」摆在一起，因为它回答的是同一个问题——什么时候开始，
    /// 只是答案从「几点」换成了「你在不在」。
    /// </summary>
    private void BtnQuickAway_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = AppState.Settings;
            s.AwayStartEnabled = true;
            s.ScheduleEnabled = false;      // 两种触发互斥：同时开会让「为什么现在跑」说不清
            AppState.SaveSettings();

            try { TgSchedule.IsChecked = false; } catch { }
            AwayWatcher.Reset();
            UpdateScheduleStatus();
            RefreshHomeHero();

            AppState.PublishLog("✓ 改成「你走开就跑」：盯着键鼠，连续安静 20 分钟才认账");
            LogService.LogEvent("触发器：切换为一走开就跑");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("切换到一走开就跑失败：" + ex.Message);
        }
    }

    /// <summary>「就这样，打开定时」：把默认值落实并打开，不再问第二个问题。</summary>
    private void BtnQuickAccept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = AppState.Settings;

            // v1.20：在「你走开就跑」状态下，这个按钮变成「改回按时间跑」——
            // 用户想换回来时不该还要去找别的地方。
            if (string.Equals(BtnQuickAccept.Tag as string, "schedule", StringComparison.Ordinal))
            {
                s.AwayStartEnabled = false;
                AppState.SaveSettings();
                AwayWatcher.Reset();
                RefreshScheduleQuick(s);
                UpdateScheduleStatus();
                RefreshHomeHero();
                AppState.PublishLog("✓ 改回按时间跑，挑个时间就行");
                return;
            }

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
            // v1.20：走开模式下这个按钮是「现在就开跑」，不是挑时间。
            if (string.Equals(BtnQuickPick.Tag as string, "runnow", StringComparison.Ordinal))
            {
                AwayWatcher.Reset();
                _ = QueueService.StartAsync(QueueTrigger.Manual);
                return;
            }

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

    /// <summary>
    /// v1.20 / 奇思妙想 #2：每 30 秒采一次样，看看你是不是走开了。
    /// <para>
    /// 判定逻辑全在 <see cref="AwaySense"/>（纯函数）；这里只管三件事：
    /// 要不要问、要不要开、以及开完把话说明白。判错的代价是「你还在它就开跑了」，
    /// 所以 <see cref="AwaySense.ConfirmTicks"/> 次连续判定才认账，中途一动立刻清零。
    /// </para>
    /// </summary>
    private void CheckAway()
    {
        try
        {
            var s = AppState.Settings;
            if (!s.AwayStartEnabled) return;

            // 定时优先：两个都开时按定时走，因为定时能说清「几点」，而它说不清「你在不在」。
            if (s.ScheduleEnabled) return;

            var snap = AwayWatcher.Sample();
            bool ranToday = QueueService.DoneToolIdsToday().Count > 0
                            || AppState.Settings.LastRunAt is { Length: > 0 } last
                               && DateTime.TryParse(last, out var at) && at.Date == DateTime.Today;

            if (AwaySense.ShouldStart(snap, QueueService.IsRunning, ranToday))
            {
                DecisionLog.Add(DecisionSource.Schedule, "你走开，那就现在跑",
                    $"连续 {AwaySense.ConfirmTicks} 次判定都没人动键鼠，已经安静 {AwaySense.Describe(snap).Split('，').Last()}");
                AppState.PublishLog("✓ 你好像走了，开始跑今天的队列");
                LogService.LogEvent("触发器：一走开就跑 —— " + AwaySense.Describe(snap));
                AwayWatcher.Reset();       // 跑过一次就清零，免得你回来坐下又触发
                _ = QueueService.StartAsync(QueueTrigger.Timer);
                UpdateScheduleStatus();
            }
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

    /// <summary>
    /// v1.26：队列不再画成「一行一个工具」的列表——那张表已经换成上面那盘格子了。
    ///
    /// <para>
    /// 这里保留下来是因为它是「队列变了」的唯一收口：勾选、拖动排序、增删工具、
    /// 按日计划改完，全项目都是调它。现在它做的三件事是：给出网格该摆什么（走
    /// <see cref="SyncToolGrid"/>）、更新空队列提示、刷新下面那行汇总。
    /// 原来往 <c>QueueHost</c> 里塞 34 高行的循环删掉了——容器现在藏着，
    /// 往里面画东西既不显示又要为每个工具解一次图标。
    /// </para>
    /// </summary>
    private void RenderQueue()
    {
        try
        {
            SyncToolRows();
            RefreshTonightSummary();
            UpdateQueueButtons();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新队列失败：" + ex.Message);
        }
    }

    /// <summary>
    /// v1.26：格子里那个工具的队列项（格子在不在 = 今晚跑不跑）。
    /// 返回 null 表示这个工具已经不在队列里了——渲染时会把它当作「有档案但没上桌」。
    /// </summary>
    private static QueueEntry? QueueEntryOf(string toolId)
        => (AppState.Settings.Queue ?? new List<QueueEntry>())
            .FirstOrDefault(e => string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// v1.26：把某个工具从「今晚跑」切到「今晚不跑」，或者反过来。
    ///
    /// <para>
    /// 原来这件事由队列行上的勾选框做。列表换成格子之后勾选框没了，
    /// 但这个能力必须留着——不然「今天不想跑它」就只能去删工具。
    /// 收进右键菜单：格子本身要留给「点一下改设置」和「按住拖」，塞不下第三个动作。
    /// </para>
    /// </summary>
    private void ToggleQueueEnabled(string toolId)
    {
        var entry = QueueEntryOf(toolId);
        if (entry == null) return;

        entry.Enabled = !entry.Enabled;
        AppState.SaveSettings();

        string name = ProfileRegistry.Get(toolId).DisplayName;
        if (string.IsNullOrWhiteSpace(name)) name = toolId;
        AppState.PublishLog(entry.Enabled ? $"已加入队列：{name}" : $"已从队列移除：{name}");
        LogService.LogEvent($"队列勾选：{toolId}={entry.Enabled}");

        RenderQueue();
    }

    /// <summary>格子悬停提示：依赖在前，上次结果在后（都没有就返回 null，不留空气泡）。</summary>
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
        // 保留的就一种：项间等待（下面会写下一项几点开始）。
        //
        // v1.38：原来还有第二种「度假中，定时已暂停」，随度假模式一起删了。
        bool waiting = !string.IsNullOrEmpty(_queueStatusText);
        TbQueueStatus.Visibility = running || waiting
            ? Visibility.Visible
            : Visibility.Collapsed;
        TbQueueStatus.Text = running ? "" : _queueStatusText;
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
        // v1.37：首次运行按钮是禁用的，正常点不到；键盘空格或自动化仍可能送进来。
        if (!BtnQueueStart.IsEnabled) return;

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

    // ===== v1.42.1 / M9-4：四个视图窗口连类一起删了 =====
    //
    // 「计划 / 日历 / 依赖 / 配置组」原来挂在任务清单卡标题行那个下拉里（V141 的 M3-3）。
    // M8-3 先撤了入口，M9-4 把四个窗口类也从仓库里删掉——留着入口是隐患，
    // 留着类更是：六个月后有人「顺手接回去」，被撤掉的视图系统就还魂了。
    //
    // 数据侧没跟着删，这几样仍被引擎读：
    //   · QueueEntry.DependsOn + QueueGraph（Gate / ShouldAbortOnDependencyFail / HasDependencies / Order）
    //     ——历史用户的依赖关系继续生效，只是没有编辑器能改了；
    //   · QueueEntry.Groups ——现在由 BetterGI 工具行的展开区读写。

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

    // ===== 消息中心（v1.28）=====

    /// <summary>
    /// 打开消息中心。**不阻塞**：用 Show 而不是 ShowDialog——它是一张可以随时放下的清单，
    /// 不是必须处理完才能继续的关卡。开着它的时候队列照跑。
    /// </summary>
    private void BtnNotice_Click(object sender, RoutedEventArgs e) => OpenNotices();

    private void OpenNotices()
    {
        try
        {
            // 已经开着一个就把它提到前面，别开第二份（两张一样的清单会让人以为漏看了什么）
            foreach (Window w in Application.Current.Windows)
            {
                if (w is NoticeWindow existing)
                {
                    existing.Activate();
                    return;
                }
            }

            var win = new NoticeWindow { Owner = this };
            win.Closed += (_, _) => RefreshNoticeBadge();
            win.Show();
            RefreshNoticeBadge();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开消息中心失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 刷新标题栏红点。没消息就把整块藏起来——不留一个空圈。
    /// 这个方法会被 <see cref="NoticeCenter"/> 在各处回调，所以要能容忍主窗还没建好的时刻。
    /// </summary>
    internal void RefreshNoticeBadge()
    {
        try
        {
            if (NoticeBadge == null || TbNoticeBadge == null) return;
            int unread = NoticeCenter.UnreadCount;
            if (unread <= 0)
            {
                NoticeBadge.Visibility = Visibility.Collapsed;
                if (BtnNotice != null) BtnNotice.ToolTip = "消息中心";
                return;
            }

            NoticeBadge.Visibility = Visibility.Visible;
            TbNoticeBadge.Text = unread > 99 ? "99+" : unread.ToString();
            if (BtnNotice != null)
            {
                BtnNotice.ToolTip = $"消息中心 · {unread} 条没看";
            }
        }
        catch { }
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

    /// <summary>
    /// v1.42 / M8-7：今晚这一趟跑什么、大概多久。
    ///
    /// <para>
    /// 原来这里是「按日计划：每天全选 · 今天 N 项」——说的是一份已经算好的结论，
    /// 既不告诉人具体哪几个组，也不告诉人大概要挂多久。现在是「今晚要跑：A + B · 2 项 · 约 47 分钟」，
    /// 每改一次勾选就重算一次，和上面那张卡说的是同一件事。
    /// </para>
    /// <para>
    /// 时长只在能算出来时才写：按 <see cref="GroupArchive.MedianMinutes"/> 取每个组过去两周的中位数，
    /// 少于两个样本的组不算。算不出就不写，不猜一个——猜出来的数比没有更糟。
    /// </para>
    /// </summary>
    private void RefreshTonightSummary()
    {
        try
        {
            var tonight = TonightGroups(out int toolCount);
            if (ToolRowHost == null) return;

            if (toolCount == 0)
            {
                TbTonightSummary.Text = "今晚还没有要跑的工具。";
                TbTonightSummary.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
                return;
            }

            // 组名太长就只报前两个，剩下的折成「等 N 个组」——一行放不下的时候，
            // 与其让省略号吃掉最后一个组名，不如自己说清有多少个。
            var shown = tonight.Take(2).ToList();
            string label = shown.Count == 0
                ? "用配置名跑"
                : string.Join(" + ", shown) + (tonight.Count > shown.Count ? $" 等 {tonight.Count} 个组" : "");

            int? minutes = TonightMinutes(tonight);
            string tail = minutes.HasValue ? $" · 约 {minutes.Value} 分钟" : "";
            string count = shown.Count == 0 ? $"{toolCount} 个工具" : $"{tonight.Count} 项";

            TbTonightSummary.Text = $"今晚要跑：{label} · {count}{tail}";
            TbTonightSummary.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("今晚摘要刷新失败：" + ex.Message);
        }
    }

    /// <summary>
    /// v1.42 / M8-7：今晚会被真正交给 BetterGI 的那些配置组，按执行顺序。
    ///
    /// <para>
    /// 口径与 <see cref="QueueService.BuildLaunchArguments"/> 完全一致（本项指定 &gt; 配置组合 &gt; 全局），
    /// 否则摘要上写的和实际跑的会是两拨组——那比不写更糟。
    /// </para>
    /// </summary>
    private static List<string> TonightGroups(out int toolCount)
    {
        toolCount = 0;
        var result = new List<string>();
        try
        {
            var known = BgiDiscovery.Scan().Groups;
            foreach (var entry in QueueService.PlanForToday(DateTime.Now))
            {
                toolCount++;
                var profile = ProfileRegistry.Get(entry.ToolId);
                var knownTool = ToolCatalog.Find(entry.ToolId);
                // 只有 BetterGI 认「配置组」这个概念，别的工具没有组可勾。
                if (!string.Equals(entry.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase)) continue;
                _ = knownTool;

                // v1.42 / M9-2：摘要和执行走同一个函数，别各算一遍。
                foreach (string g in QueueService.EffectiveGroupsFor(entry.ToolId))
                {
                    if (!result.Contains(g, StringComparer.OrdinalIgnoreCase)) result.Add(g);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("统计今晚配置组失败：" + ex.Message);
        }
        return result;
    }

    /// <summary>
    /// v1.42 / M8-7：今晚这些组的估计时长（分钟）。
    /// 取每个组过去 14 天的中位耗时相加；任何一个组没有足够样本就整体返回 null——
    /// 只加一部分再报个总数，会让人以为整趟跑完只要那么久。
    /// </summary>
    private static int? TonightMinutes(IReadOnlyList<string> groups)
    {
        if (groups.Count == 0) return null;
        double total = 0;
        foreach (string name in groups)
        {
            double? median = GroupArchive.MedianMinutes(name, days: 14, minSamples: 2);
            if (median == null) return null;
            total += median.Value;
        }
        return Math.Max(1, (int)Math.Round(total));
    }

    /// <summary>v1.42 / M8-2：标题行右侧那句「已配 N 个」。</summary>
    private void RefreshQueueCount()
    {
        try
        {
            if (TbQueueCount == null) return;
            int n = QueueService.PlanForToday(DateTime.Now).Count;
            TbQueueCount.Text = n == 0 ? "今晚没有工具" : $"已配 {n} 个";
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
                // v1.42 / M9-2：存组合抓的是「此刻真正会跑的那几个组」，
                // 不是某一份全局勾选的影子——否则存下来的组合跟实际跑的对不上。
                s.Presets, s.DragonConfigName, QueueService.EffectiveGroupsFor("bettergi"), "",
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

    /// <summary>上一次画出来的那排工具按键的签名（id + 有没有配好）。</summary>
    private string _toolButtonSignature = "";

    /// <summary>
    /// v1.32：「每个工具怎么跑」那一排小按键。
    ///
    /// <para>
    /// 原来是「工具设置」折叠区，里面每个工具再套一层折叠卡。用户想看 BetterGI，
    /// 得先展开整个区、再展开 BetterGI——两次点击都在回答同一个问题「它在哪」。
    /// 现在一个工具一个按键，点一下直接开它的配置窗口，跟托管桌面里「左键点图标」是同一个动作。
    /// </para>
    /// <para>
    /// 图标 20×20、按键高 30：比格子小一号，因为它们不承担「今晚跑不跑」的信息，
    /// 只承担「去哪改它」。排不下会自动换行（WrapPanel）。
    /// </para>
    /// </summary>
    /// <summary>
    /// 「我现在在用的工具」——小按键那一排和右边那句「已配 N 个」用的是同一份名单。
    /// 两处各自算一遍的话，迟早会出现「一排三颗按键，摘要写着还有五个没配」这种自相矛盾。
    /// </summary>
    private static List<string> ToolButtonIds()
    {
        var ids = new List<string>();
        foreach (var entry in AppState.Settings.Queue ?? new List<QueueEntry>())
        {
            if (string.IsNullOrWhiteSpace(entry.ToolId)) continue;

            // v1.33：只列「今晚真的要跑」的，不列「队列里躺着但关着」的。
            // 
            // 队列里有一堆 Enabled=false 的条目是历史遗留（引导页以前会把十六个工具全写进去）。
            // 这一排的意思是「这些是你现在在用的」，把用不上的也摆出来，等于每打开一次就提醒
            // 一次「你还差五个没配」——用户的原话是「显然我用不着这么多」。
            // 关着但已经填好路径的仍然列出来（那说明他配过、只是这轮不跑），见下面的补漏。
            if (!entry.Enabled) continue;
            if (!ids.Contains(entry.ToolId, StringComparer.OrdinalIgnoreCase)) ids.Add(entry.ToolId);
        }
        foreach (var p in ProfileRegistry.All)
        {
            if (string.IsNullOrWhiteSpace(p.ExecutablePath)) continue;
            if (!ids.Contains(p.ToolId, StringComparer.OrdinalIgnoreCase)) ids.Add(p.ToolId);
        }
        if (ids.Count == 0)
        {
            foreach (string id in new[] { "bettergi", "march7th", "ok-ww" }) ids.Add(id);
        }
        return ids;
    }

    private void RenderToolButtons()
    {
        if (ToolButtonHost == null) return;

        var ids = ToolButtonIds();

        // 每秒一次的界面轮询也会走到这里。签名没变就一个控件都不碰——
        // 重造一排按钮不只是浪费，还会把正按在上面的手指/鼠标弄丢（v1.23 桌面格子踩过同一条）。
        string signature = string.Join("|", ids.Select(id =>
            id + ":" + (ToolRunner.HasExecutable(ProfileRegistry.Get(id)) ? "1" : "0")));
        if (signature == _toolButtonSignature) return;
        _toolButtonSignature = signature;

        ToolButtonHost.Children.Clear();
        foreach (string id in ids) ToolButtonHost.Children.Add(BuildToolButton(id));
    }

    private Border BuildToolButton(string toolId)
    {
        var profile = ProfileRegistry.Get(toolId);
        bool ready = ToolRunner.HasExecutable(profile);

        var icon = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(6),
            Background = BrushOf("ControlBgBrush") ?? DotGray,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bmp = ToolIconService.GetBitmap(profile);
        if (bmp != null)
        {
            icon.Child = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(2) };
        }
        else
        {
            icon.Child = new TextBlock
            {
                Text = profile.DisplayName.Length > 0 ? profile.DisplayName[..1] : "?",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("TextSecondaryBrush") ?? DotGray,
            };
        }

        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 11.5,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextPrimaryBrush") ?? DotGray,
            MaxWidth = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var btn = new Border
        {
            Height = 30,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(8),
            Background = BrushOf("InsetBrush") ?? DotGray,
            BorderThickness = new Thickness(1),
            BorderBrush = BrushOf(ready ? "BorderBrush" : "WarningBrush") ?? DotGray,
            Child = stack,
            Cursor = Cursors.Hand,
            Tag = toolId,
            ToolTip = ready
                ? profile.DisplayName + "\n" + profile.ExecutablePath
                : profile.DisplayName + "\n还没找到主程序——点一下填上。",
        };
        btn.MouseEnter += (_, _) => btn.Background = BrushOf("HoverBrush") ?? DotGray;
        btn.MouseLeave += (_, _) => btn.Background = BrushOf("InsetBrush") ?? DotGray;
        btn.MouseLeftButtonDown += (_, _) => OpenToolLaunch(toolId);
        return btn;
    }

    /// <summary>「已配 1 个 / 3 个都没配」——收起状态下唯一能看出配置状况的地方。</summary>
    private void RefreshToolsSummary()
    {
        try
        {
            int ready = 0;
            var missing = new List<string>();
            // v1.33：跟小按键那一排用同一份名单。原来这里写死三个内置 + 自定义卡，
            // 于是「只勾了 BetterGI」的人会看到「已配 1 个·还有 2 个没配」——
            // 那两个他根本没勾，报出来只是在提醒他「你还不够」。
            foreach (string id in ToolButtonIds())
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
                // v1.37：只配了一部分时也是警示，不是普通状态——
                // 「一键开跑」会把没配好的那几个直接跳过，跑出来的结果和用户以为的不一样。
                // 原来这里跟「全部配好」一样用三级灰，等于把一句要紧的话写成了旁白。
                TbToolsSummary.Foreground = ready == 0
                    ? (BrushOf("WarningBrush") ?? DotYellow)
                    : (BrushOf("WarningBrush") ?? DotYellow);
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("工具摘要刷新失败：" + ex.Message);
        }
    }

    // ===== v1.42 / M8-2：横向工具行 =====

    /// <summary>每一行右侧那个状态徽章的 TextBlock（按 toolId 索引，供只改字不重画时用）。</summary>
    private readonly Dictionary<string, TextBlock> _rowBadges = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>点开着的工具行（展开区）。默认全收起——一打开就看到八行勾选框不是「任务清单」该有的样子。</summary>
    private readonly HashSet<string> _expandedRows = new(StringComparer.OrdinalIgnoreCase);
    private string _rowSignature = "";
    /// <summary>v1.42 / M8-2 ⑤：添加区是否展开（内联图标网格，不另开窗口）。</summary>
    private bool _addingTool;

    /// <summary>
    /// 外部状态变化时走这里：名单没变就只改徽章，变了才重画。
    /// 和原来的 <see cref="SyncToolGrid"/> 同一套思路——那条每秒一次的轮询不该打断展开区里的勾选。
    /// </summary>
    private void SyncToolRows()
    {
        if (ToolRowHost == null) return;
        string signature = string.Join("|", ToolButtonIds().Select(id =>
        {
            var p = ProfileRegistry.Get(id);
            var e = QueueEntryOf(id);
            return id + ":" + (ToolRunner.HasExecutable(p) ? "1" : "0") + (e == null || e.Enabled ? "1" : "0");
        }));
        if (signature != _rowSignature)
        {
            RenderToolRows();
            return;
        }
        RefreshToolRowStates();
    }

    private void RenderToolRows()
    {
        if (ToolRowHost == null) return;
        ToolRowHost.Children.Clear();
        _rowBadges.Clear();

        var ids = ToolButtonIds();
        for (int i = 0; i < ids.Count; i++)
        {
            try { ToolRowHost.Children.Add(BuildToolRow(ProfileRegistry.Get(ids[i]), i + 1)); }
            catch (Exception ex) { LogService.LogEvent("画工具行失败：" + ids[i] + " " + ex.Message); }
        }
        ToolRowHost.Children.Add(BuildAddRow());
        if (_addingTool)
        {
            try { ToolRowHost.Children.Add(BuildAddPanel()); }
            catch (Exception ex) { LogService.LogEvent("画添加区失败：" + ex.Message); }
        }

        _rowSignature = string.Join("|", ids.Select(id =>
        {
            var p = ProfileRegistry.Get(id);
            var e = QueueEntryOf(id);
            return id + ":" + (ToolRunner.HasExecutable(p) ? "1" : "0") + (e == null || e.Enabled ? "1" : "0");
        }));

        RefreshQueueCount();
        RefreshTonightSummary();
    }

    private void RefreshToolRowStates()
    {
        foreach (var kv in _rowBadges)
        {
            try
            {
                var profile = ProfileRegistry.Get(kv.Key);
                var entry = QueueEntryOf(kv.Key);
                bool onDuty = entry == null || entry.Enabled;
                bool ready = ToolRunner.HasExecutable(profile);
                bool running = QueueService.IsRunning &&
                               string.Equals(QueueService.CurrentToolId, kv.Key, StringComparison.OrdinalIgnoreCase);
                ApplyBadge(kv.Value, running, ready, onDuty);
            }
            catch { }
        }
    }

    private void ApplyBadge(TextBlock badge, bool running, bool ready, bool onDuty)
    {
        if (!onDuty)
        {
            badge.Text = "今晚不跑";
            badge.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            return;
        }
        if (running)
        {
            badge.Text = "运行中";
            badge.Foreground = BrushOf("BrandTextBrush") ?? DotGreen;
            return;
        }
        if (ready)
        {
            badge.Text = "✓ 已就绪";
            badge.Foreground = BrushOf("SuccessBrush") ?? DotGreen;
            return;
        }
        badge.Text = "未配置路径";
        badge.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
    }

    /// <summary>一行工具：≡ 手柄 · 图标 · 名字 · 徽章 · 摘要 · ⌄，展开时下面挂配置组面板。</summary>
    private UIElement BuildToolRow(ToolProfile profile, int order)
    {
        bool ready = ToolRunner.HasExecutable(profile);
        bool running = QueueService.IsRunning &&
                       string.Equals(QueueService.CurrentToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase);
        var entry = QueueEntryOf(profile.ToolId);
        bool onDuty = entry == null || entry.Enabled;
        bool expanded = _expandedRows.Contains(profile.ToolId);

        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        // v1.42.1 / M9-3（D17）：56 → 52。720 高度下收起态要零滚动，
        // 一行省 4px、三行省 12px，加上间距收紧正好把 32px 的赤字抹平；
        // 再矮下去 15 号的 ≡ 手柄和 30 的图标就会顶到边框，所以到 52 为止。
        var row = new Border
        {
            Height = 52,
            CornerRadius = new CornerRadius(8),
            Background = expanded ? (BrushOf("CardBg2Brush") ?? Brushes.Transparent) : Brushes.Transparent,
            BorderBrush = expanded ? (BrushOf("BorderBrush") ?? DotGray) : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = profile.ToolId,
            Opacity = onDuty ? 1.0 : 0.6,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 手柄
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 图标
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // ⌄

        // 拖拽手柄：抓这里才换顺序，抓别处是展开/双击跑
        var handle = new TextBlock
        {
            Text = "≡",
            FontSize = 15,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeAll,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            ToolTip = "按住往上/往下拖，换今晚跑的顺序",
        };
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        var iconBox = new Border
        {
            Width = 30, Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = BrushOf("ControlBgBrush") ?? Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };
        var bitmap = ToolIconService.GetBitmap(profile);
        iconBox.Child = bitmap != null
            ? new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(4) }
            : (UIElement)new TextBlock
            {
                Text = profile.DisplayName.Length > 0 ? profile.DisplayName[..1].ToUpperInvariant() : "?",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("TextSecondaryBrush") ?? DotGray,
            };
        Grid.SetColumn(iconBox, 1);
        grid.Children.Add(iconBox);

        // 中间：名字 + 徽章一行，摘要一行
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BrushOf("TextPrimaryBrush") ?? Brushes.White,
        });
        var badge = new TextBlock { FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ApplyBadge(badge, running, ready, onDuty);
        titleLine.Children.Add(badge);
        _rowBadges[profile.ToolId] = badge;
        mid.Children.Add(titleLine);
        mid.Children.Add(new TextBlock
        {
            Text = BuildToolTileSummary(profile.ToolId, order),
            FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        });
        Grid.SetColumn(mid, 2);
        grid.Children.Add(mid);

        var chev = new TextBlock
        {
            Text = expanded ? "⌃" : "⌄",
            FontSize = 12,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        };
        Grid.SetColumn(chev, 3);
        grid.Children.Add(chev);

        row.Child = grid;
        host.Children.Add(row);

        // 展开区：缩进对齐到图标，背景比卡片深一档
        if (expanded) host.Children.Add(BuildGroupPanel(profile));

        // 单击任意处（手柄除外）展开/收起；双击＝立即跑这一个
        row.MouseLeftButtonUp += (s2, e2) =>
        {
            if (e2.ChangedButton != MouseButton.Left) return;
            if (handle.IsMouseOver) return;
            if (e2.ClickCount >= 2) { RunToolNow(profile.ToolId); return; }
            if (_expandedRows.Contains(profile.ToolId)) _expandedRows.Remove(profile.ToolId);
            else _expandedRows.Add(profile.ToolId);
            RenderToolRows();
        };
        row.MouseRightButtonUp += ToolTile_RightClick;

        // 手柄拖拽：按住移动超过 8px 就开始换序
        int dragIndex = order - 1;
        Point from = default;
        bool armed = false;
        handle.MouseLeftButtonDown += (s3, e3) => { from = e3.GetPosition(ToolRowHost); armed = true; handle.CaptureMouse(); e3.Handled = true; };
        handle.MouseMove += (s3, e3) =>
        {
            if (!armed) return;
            Point now = e3.GetPosition(ToolRowHost);
            if (Math.Abs(now.Y - from.Y) < 24) return;
            armed = false;
            handle.ReleaseMouseCapture();
            int target = dragIndex + (now.Y > from.Y ? 1 : -1);
            MoveToolRow(profile.ToolId, target);
        };
        handle.MouseLeftButtonUp += (s3, e3) => { armed = false; handle.ReleaseMouseCapture(); };

        return host;
    }

    /// <summary>v1.42 / M8-2：把某个工具挪到指定下标（0 起），越界就夹到两端。</summary>
    private void MoveToolRow(string toolId, int targetIndex)
    {
        var order = CurrentToolOrder().ToList();
        int from = order.FindIndex(x => string.Equals(x, toolId, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, order.Count - 1);
        if (targetIndex == from) return;
        order.RemoveAt(from);
        order.Insert(targetIndex, toolId);
        SaveToolOrder(order);
        RenderToolRows();
        LogService.LogEvent($"工具顺序：{toolId} 第 {from + 1} → 第 {targetIndex + 1}");
    }

    /// <summary>
    /// v1.42 / M8-4：展开区。
    ///
    /// <para>
    /// 配置组只属于 BetterGI，所以只有它这一行会列出勾选框；别的工具展开后只有一句说明 + 「编辑…」。
    /// 勾选读写的是队列项自己的 <see cref="QueueEntry.Groups"/>——和原来的「配置组」视图同一份存储，
    /// 也是 <see cref="QueueService.BuildLaunchArguments"/> 里优先级最高的那一层。空列表＝沿用全局勾选。
    /// </para>
    /// </summary>
    private UIElement BuildGroupPanel(ToolProfile profile)
    {
        var panel = new Border
        {
            Margin = new Thickness(42, 2, 0, 8),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = BrushOf("ControlBgBrush") ?? Brushes.Transparent,
            BorderBrush = BrushOf("BorderBrush") ?? DotGray,
            BorderThickness = new Thickness(1),
        };
        var stack = new StackPanel();
        panel.Child = stack;

        bool isBgi = string.Equals(profile.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase);
        if (!isBgi)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var note = new TextBlock
            {
                Text = "这个工具没有配置组。程序位置和完成方式在「编辑…」里改。",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            };
            Grid.SetColumn(note, 0);
            line.Children.Add(note);
            var edit = new Button
            {
                Content = "编辑…", Style = TryFindResource("MiniAction") as Style,
                Height = 26, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(10, 0, 0, 0),
            };
            edit.Click += (_, _) => OpenToolLaunch(profile.ToolId);
            Grid.SetColumn(edit, 1);
            line.Children.Add(edit);
            stack.Children.Add(line);
            return panel;
        }

        var known = new List<string>();
        try { var scan = BgiDiscovery.Scan(); if (scan.Ok) known = scan.Groups; } catch { }
        if (known.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "还没识别到配置组。确认 BetterGI 的位置后点「重新识别」。",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            });
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var openDir = new Button { Content = "打开目录", Style = TryFindResource("MiniAction") as Style, Height = 26, Padding = new Thickness(10, 0, 10, 0) };
            openDir.Click += BtnOpenGroupDir_Click;
            var rescan = new Button { Content = "重新识别", Style = TryFindResource("MiniAction") as Style, Height = 26, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0) };
            rescan.Click += BtnRescanGroups_Click;
            row2.Children.Add(openDir);
            row2.Children.Add(rescan);
            stack.Children.Add(row2);
            return panel;
        }

        var entry = QueueEntryOf("bettergi");
        // 勾选顺序即执行顺序：先把本项自己写过的组按原序排好，再把全局勾选里剩下的补进来。
        var order2 = GroupSlicing.Sanitize(entry?.Groups, known);
        var favorites = AppState.Settings.FavoriteGroups ?? Array.Empty<string>();
        var shown = GroupFavorites.Sort(known, favorites);

        var wrap = new WrapPanel();
        var boxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var footerCount = new TextBlock
        {
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        };

        void Renumber()
        {
            for (int k = 0; k < order2.Count; k++)
            {
                if (boxes.TryGetValue(order2[k], out var cb2))
                {
                    string star = GroupFavorites.IsFavorite(favorites, order2[k]) ? "★ " : "";
                    cb2.Content = $"{star}{k + 1}. {order2[k]}";
                }
            }
            foreach (var kv in boxes)
            {
                if (order2.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) continue;
                string star = GroupFavorites.IsFavorite(favorites, kv.Key) ? "★ " : "";
                kv.Value.Content = star + kv.Key;
            }
            int n = order2.Count;
            footerCount.Text = n == 0
                ? "今晚 0 项 · 不勾＝按配置名跑"
                : $"今晚 {n} 项";
            footerCount.Foreground = n > 0
                ? (BrushOf("BrandTextBrush") ?? DotGreen)
                : (BrushOf("TextTertiaryBrush") ?? DotGray);
        }

        void Persist()
        {
            var target = QueueEntryOf("bettergi");
            if (target == null) return;
            target.Groups = order2.ToList();
            AppState.SaveSettings();
            Renumber();
            RefreshTonightSummary();
        }

        foreach (string name in shown)
        {
            var cb = new CheckBox
            {
                Style = TryFindResource("GroupCheck") as Style,
                Margin = new Thickness(0, 4, 8, 0),
                Tag = name,
                IsChecked = order2.Contains(name, StringComparer.OrdinalIgnoreCase),
            };
            string star = GroupFavorites.IsFavorite(favorites, name) ? "★ " : "";
            int at = order2.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            cb.Content = at >= 0 ? $"{star}{at + 1}. {name}" : star + name;
            boxes[name] = cb;
            cb.Checked += (_, _) =>
            {
                if (!order2.Contains(name, StringComparer.OrdinalIgnoreCase)) order2.Add(name);
                Persist();
            };
            cb.Unchecked += (_, _) =>
            {
                order2.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                Persist();
            };
            wrap.Children.Add(cb);
        }
        stack.Children.Add(wrap);

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var all = new Button { Content = "全选", Style = TryFindResource("LinkButton") as Style, FontSize = 11 };
        all.Click += (_, _) =>
        {
            foreach (string name in shown) if (!order2.Contains(name, StringComparer.OrdinalIgnoreCase)) order2.Add(name);
            foreach (var kv in boxes) kv.Value.IsChecked = true;
            Persist();
        };

        var none = new Button { Content = "全部取消", Style = TryFindResource("LinkButton") as Style, FontSize = 11, Margin = new Thickness(10, 0, 0, 0) };
        none.Click += (_, _) =>
        {
            order2.Clear();
            foreach (var kv in boxes) kv.Value.IsChecked = false;
            Persist();
        };

        // 先拼好左边这一组再一次性挂上去。逐个 Add 到一个已经进过父容器的控件上，
        // WPF 会报「元素已经是另一个元素的逻辑子元素」，整行跟着画不出来。
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(all);
        left.Children.Add(none);
        Grid.SetColumn(left, 0);
        footer.Children.Add(left);

        Grid.SetColumn(footerCount, 2);
        footer.Children.Add(footerCount);
        stack.Children.Add(footer);

        Renumber();
        return panel;
    }

    /// <summary>v1.42 / M8-2：列表末尾的虚线「+ 添加工具」。</summary>
    private UIElement BuildAddRow()
    {
        var row = new Border
        {
            Height = 40,
            Margin = new Thickness(0, 4, 0, 0),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            ToolTip = "从内置清单里挑一个，或者添加任意 exe",
        };
        var dash = new System.Windows.Shapes.Rectangle
        {
            Stroke = BrushOf("BorderStrongBrush") ?? DotGray,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            RadiusX = 8, RadiusY = 8,
            Fill = Brushes.Transparent,
        };
        var label = new TextBlock
        {
            Text = "＋ 添加工具",
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        };
        var layer = new Grid();
        layer.Children.Add(dash);
        layer.Children.Add(label);
        row.Child = layer;
        row.MouseLeftButtonUp += (_, _) => { _addingTool = !_addingTool; RenderToolRows(); };
        row.MouseEnter += (_, _) => label.Foreground = BrushOf("BrandTextBrush") ?? DotGreen;
        row.MouseLeave += (_, _) => label.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
        return row;
    }

    /// <summary>
    /// v1.42 / M8-2 ⑤：添加工具的图标网格——用的就是向导第 1 步那套格子。
    ///
    /// <para>
    /// 不另开窗口：这张卡本身就是「这台机器交给管家托管的那几样」，加一样东西在卡片里就地完成，
    /// 人才不会觉得「加完还得回去找」。格子在＝已经加过，点了直接跳它的路径设置。
    /// </para>
    /// </summary>
    private UIElement BuildAddPanel()
    {
        var panel = new Border
        {
            Margin = new Thickness(0, 6, 0, 4),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = BrushOf("ControlBgBrush") ?? Brushes.Transparent,
            BorderBrush = BrushOf("BorderBrush") ?? DotGray,
            BorderThickness = new Thickness(1),
        };
        var stack = new StackPanel();
        panel.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = "加一个工具。已经在清单里的会打勾——点它直接去改路径。",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        });

        var wrap = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        var existing = new HashSet<string>(ToolButtonIds(), StringComparer.OrdinalIgnoreCase);

        // 来源与向导一致：内置清单 + 已经有档案的自定义工具。
        var candidates = new List<ToolProfile>();
        foreach (var known in ToolCatalog.All)
        {
            try { candidates.Add(ProfileRegistry.Get(known.ToolId)); } catch { }
        }
        foreach (var p in ProfileRegistry.All)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.ToolId)) continue;
            if (candidates.Any(c => string.Equals(c.ToolId, p.ToolId, StringComparison.OrdinalIgnoreCase))) continue;
            candidates.Add(p);
        }

        foreach (var profile in candidates)
        {
            bool added = existing.Contains(profile.ToolId);
            var tile = new Border
            {
                Width = 84,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(4, 7, 4, 6),
                CornerRadius = new CornerRadius(8),
                Background = added ? (BrushOf("BrandSoftBrush") ?? Brushes.Transparent) : Brushes.Transparent,
                BorderBrush = added ? (BrushOf("BrandBrush") ?? DotGreen) : (BrushOf("BorderBrush") ?? DotGray),
                BorderThickness = new Thickness(1.2),
                Cursor = Cursors.Hand,
            };
            var known = ToolCatalog.Find(profile.ToolId);
            tile.ToolTip = added
                ? profile.DisplayName + "（已在清单里，点一下去改它的设置）"
                : (string.IsNullOrWhiteSpace(known?.Blurb) ? profile.DisplayName : profile.DisplayName + "\n" + known!.Blurb);

            var box = new StackPanel();
            var iconBox = new Border
            {
                Width = 38, Height = 38,
                CornerRadius = new CornerRadius(10),
                Background = BrushOf("CardBg2Brush") ?? Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var bmp = ToolIconService.GetBitmap(profile);
            iconBox.Child = bmp != null
                ? new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(5) }
                : (UIElement)new TextBlock
                {
                    Text = profile.DisplayName.Length > 0 ? profile.DisplayName[..1] : "?",
                    FontSize = 16, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = BrushOf("TextSecondaryBrush") ?? DotGray,
                };
            box.Children.Add(iconBox);
            box.Children.Add(new TextBlock
            {
                Text = (added ? "✓ " : "") + profile.DisplayName,
                FontSize = 10.5,
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 76, MaxHeight = 28,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = BrushOf("TextPrimaryBrush") ?? Brushes.White,
            });
            tile.Child = box;

            string id = profile.ToolId;
            tile.MouseLeftButtonUp += (_, _) =>
            {
                if (!existing.Contains(id))
                {
                    SaveToolOrder(ToolGrid.Append(CurrentToolOrder(), id));
                    var entry = QueueEntryOf(id);
                    if (entry != null) entry.Enabled = true;
                    AppState.SaveSettings();
                    AppState.PublishLog($"✓ 已加入清单：{profile.DisplayName}");
                    LogService.LogEvent($"添加工具：{id}");
                }
                _addingTool = false;
                RenderQueue();
                RenderToolRows();
                RefreshToolCards();
                OpenToolLaunch(id);
            };
            wrap.Children.Add(tile);
        }
        stack.Children.Add(wrap);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var custom = new Button { Content = "其它 exe…", Style = TryFindResource("MiniAction") as Style, Height = 28, Padding = new Thickness(11, 0, 11, 0) };
        custom.Click += (s2, e2) => { _addingTool = false; RenderToolRows(); BtnAddCustomTool_Click(null!, new RoutedEventArgs()); };
        buttons.Children.Add(custom);
        var cancel = new Button { Content = "收起", Style = TryFindResource("LinkButton") as Style, FontSize = 11, Margin = new Thickness(12, 0, 0, 0) };
        cancel.Click += (_, _) => { _addingTool = false; RenderToolRows(); };
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        return panel;
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
        try { RenderToolButtons(); } catch { }
        try { SyncToolGrid(); } catch { }
        // 路径或配置名刚变过时要重读；节流在 RefreshCompletionRow 里，20 秒最多读一次盘
        try { RefreshCompletionRow(); } catch { }
    }

    // ===== 桌面网格（v1.21）=====

    /// <summary>桌面上每一格的「状态小字」，按 ToolId 索引——刷新状态时改字不改元素。</summary>
    private readonly Dictionary<string, TextBlock> _tileStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>桌面上每一格的边框，按 ToolId 索引（悬停高亮与运行中配色都靠它）。</summary>
    private readonly Dictionary<string, Border> _tileBorders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上一次画出来的格子签名（ToolId 按顺序拼），用来判断「需不需要重画」。</summary>
    private string _gridSignature = "";

    /// <summary>
    /// 工具表的自然顺序 = 队列里的先后。
    ///
    /// <para>
    /// v1.26：原来是「<see cref="ProfileRegistry.All"/> 的顺序」。格子搬进启动页那张卡之后，
    /// 格子回答的问题变了——它不再只是「这台机器上有哪些工具」，而是「今晚按什么顺序跑」。
    /// 那个问题的答案只在队列里，所以这里改成读队列；不在队列里的工具不上桌面
    /// （要加它就去点「＋ 添加一个工具」，或者走第一次使用的引导）。
    /// </para>
    /// </summary>
    private static List<string> NaturalToolOrder()
    {
        var ids = new List<string>();
        try
        {
            foreach (var entry in AppState.Settings.Queue ?? new List<QueueEntry>())
            {
                if (string.IsNullOrWhiteSpace(entry.ToolId)) continue;
                if (!ids.Contains(entry.ToolId, StringComparer.OrdinalIgnoreCase)) ids.Add(entry.ToolId);
            }
        }
        catch { }
        return ids;
    }

    private static List<string> CurrentToolOrder()
        => ToolPages.Flatten(AppState.Settings.ToolPages);

    /// <summary>
    /// 存下桌面顺序。
    ///
    /// <para>
    /// v1.26：除了写 <see cref="AppSettings.ToolOrder"/>，还要把**队列本身**按这个顺序重排。
    /// 格子现在是「今晚按什么顺序跑」的那张图，如果只存偏好、不重排队列，
    /// 用户在格子上拖了半天，实际执行仍是老顺序——那就是骗人。
    /// 重排时按 ToolId 把原来那一项整个搬过去，勾选状态、按日计划、依赖、配置组都不动。
    /// </para>
    /// <para>
    /// v1.27：入参从「一条顺序表」变成「一条顺序表 + 现有页数」——
    /// 右键菜单那几项（前挪/后挪/移到最后/移除）都只关心先后，不关心摆在第几页，
    /// 所以这里按每页 6 个重新切一刀，页数尽量保持原样（多出来的页留空，不悄悄合并）。
    /// 拖拽走的是 <see cref="ReplacePages"/>，不经过这里，所以页内位置不会被打乱。
    /// </para>
    /// </summary>
    private static void SaveToolOrder(List<string> order)
    {
        int keepPages = AppState.Settings.ToolPages?.Count ?? 1;
        var repaged = new List<List<string>>();
        for (int i = 0; i < order.Count; i += ToolPages.PerPage)
            repaged.Add(order.Skip(i).Take(ToolPages.PerPage).ToList());
        while (repaged.Count < Math.Min(keepPages, ToolPages.MaxPages)) repaged.Add(new List<string>());
        if (repaged.Count == 0) repaged.Add(new List<string>());
        AppState.Settings.ToolPages = repaged;
        AppState.Settings.ToolOrder = order.ToArray();

        try
        {
            var queue = AppState.Settings.Queue;
            if (queue != null && queue.Count > 1)
            {
                var byId = new Dictionary<string, QueueEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in queue)
                    if (!string.IsNullOrWhiteSpace(e.ToolId)) byId[e.ToolId] = e;

                var rebuilt = new List<QueueEntry>();
                foreach (string id in order)
                    if (byId.TryGetValue(id, out var hit) && !rebuilt.Contains(hit)) rebuilt.Add(hit);
                // 队列里还有、但这次顺序表里没有的（比如超过 8 格排不进桌面的），原序补在后面
                foreach (var e in queue)
                    if (!rebuilt.Contains(e)) rebuilt.Add(e);

                AppState.Settings.Queue = rebuilt;
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("重排队列失败：" + ex.Message);
        }

        AppState.SaveSettings();
    }

    /// <summary>
    /// 画 2×4 桌面。
    ///
    /// <para>
    /// 为什么是格子而不是列表：用户要「像手机桌面」。列表回答「有哪些工具」，
    /// 格子回答「我把哪个放在第一格」——后者是他每天真的会看的那一眼。
    /// </para>
    /// </summary>
    /// <summary>
    /// v1.23：只刷新格子上那行小字，不重建格子。
    ///
    /// 为什么要有这么一个方法：每秒一次的状态轮询原来直接调 <see cref="RenderToolGrid"/>，
    /// 而那个方法会 <c>Children.Clear()</c> 之后把八格全部重新造一遍——正在被长按拖起来的
    /// 那一格因此会在一秒之内被换成一个新对象，<c>_dragTile</c> 指向的东西已经不在可视树里，
    /// 拖拽必然断在半路。顺带每秒重解码八个图标。
    ///
    /// 这里只按 ToolId 找回原来的 TextBlock 改字与颜色，元素本身不动，拖拽就断不了。
    /// </summary>
    private void RefreshToolGridStates()
    {
        foreach (var kv in _tileStates)
        {
            try
            {
                var profile = ProfileRegistry.Get(kv.Key);
                bool ready = ToolRunner.HasExecutable(profile);
                bool runningThis = QueueService.IsRunning
                    && string.Equals(QueueService.CurrentToolId, kv.Key, StringComparison.OrdinalIgnoreCase);

                kv.Value.Text = runningThis ? "运行中" : ready ? "已就绪" : "未配置";
                kv.Value.Foreground = runningThis
                    ? (BrushOf("BrandBrush") ?? DotGreen)
                    : ready ? (BrushOf("TextTertiaryBrush") ?? DotGray) : (BrushOf("WarningBrush") ?? DotYellow);

                if (_tileBorders.TryGetValue(kv.Key, out var border))
                {
                    // 边框只在「刚跑完/刚配好」这类状态真的变了时才碰它，
                    // 免得把鼠标正停在上面时的悬停高亮擦掉。
                    if (border.IsMouseOver) continue;
                    border.BorderBrush = runningThis
                        ? (BrushOf("BrandBrush") ?? DotGray)
                        : (BrushOf("BorderBrush") ?? DotGray);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// v1.23：外部状态变化时（可能是「工具集合变了」，也可能只是「某个工具装好了」）走这里。
    /// 桌面上该摆的东西没变就只改字，变了才重画——那条每秒一次的轮询因此不会再打断拖拽。
    /// </summary>
    private void SyncToolGrid()
    {
        if (ToolGridHost == null) return;
        string signature = string.Join("|", PageList().Select(pg => string.Join(",", pg)));
        if (signature != _gridSignature)
        {
            RenderToolGrid();
            return;
        }
        RefreshToolGridStates();
    }

    // ===== 可翻页桌面（v1.27）=====

    /// <summary>
    /// 当前停在第几页（0 起）。
    ///
    /// v1.29：<b>故意不存进设置</b>。上次停在第三页，这次打开就还在第三页——
    /// 那意味着用户每次启动都要先往左划两下才看得到「今晚跑什么」，
    /// 而他打开这个程序十次有九次就是为了看那一件事。
    /// 把它归零不需要记性，也不需要一次「重置」。
    /// </summary>
    private int _toolPage;

    /// <summary>每页一个 Grid（2×3），下标＝页码。翻页动画直接改它们的 TranslateTransform。</summary>
    private readonly Dictionary<int, Grid> _pageViews = new();

    /// <summary>上一次画完之后每个工具在桌面上的绝对位置（页 × 宽 + 列 × 格宽），用于重排动画。</summary>
    private readonly Dictionary<string, Point> _tilePositions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 桌面上「还算存在的工具」：档案表 ∪ 队列。
    /// <para>
    /// 不能只拿队列当真相：桌面能摆下不在队列里的工具（从没加进队列的），
    /// 只按队列剔除的话，那些工具会在下一次重画时集体消失。
    /// </para>
    /// </summary>
    private static List<string> KnownToolIds()
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in ProfileRegistry.All)
                if (!string.IsNullOrWhiteSpace(p.ToolId) && seen.Add(p.ToolId)) ids.Add(p.ToolId);
        }
        catch { }
        foreach (var e in AppState.Settings.Queue ?? new List<QueueEntry>())
            if (!string.IsNullOrWhiteSpace(e.ToolId) && seen.Add(e.ToolId)) ids.Add(e.ToolId);
        return ids;
    }

    /// <summary>当前页面结构（已经过校验：剔除已删工具、每页不超 6 格）。</summary>
    private List<List<string>> PageList()
        => ToolPages.Normalize(AppState.Settings.ToolPages, KnownToolIds());

    private void ReplacePages(List<List<string>> pages)
    {
        // v1.29：存之前先按「6 个一页」重切一遍——页数由数量决定，
        // 不再保留用户手动加的空白页（「＋ 新页」这个按钮已经删了）。
        var packed = ToolPages.Repack(pages);
        AppState.Settings.ToolPages = packed;
        // ToolOrder 是 v1.21 留下的字段（导出/备份还有它），同步一份，免得两处说法不一致
        AppState.Settings.ToolOrder = ToolPages.Flatten(packed).ToArray();
        AppState.SaveSettings();
    }

    /// <summary>
    /// 从头画一遍桌面。所有「工具集合变了」的地方都调它；
    /// 只是状态字变了走 <see cref="RefreshToolGridStates"/>，不要重画。
    /// </summary>
    private void RenderToolGrid(bool animate = false)
    {
        if (ToolGridHost == null) return;

        ToolGridHost.Children.Clear();
        _pageViews.Clear();
        _tileStates.Clear();
        _tileBorders.Clear();

        var before = new Dictionary<string, Point>(_tilePositions, StringComparer.OrdinalIgnoreCase);
        var pages = PageList();
        if (_toolPage >= pages.Count) _toolPage = Math.Max(0, pages.Count - 1);

        for (int p = 0; p < pages.Count; p++)
        {
            var view = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                RenderTransform = new TranslateTransform(0, 0),
            };
            // 星号列宽与内容无关，格子永远等宽；Auto 列会被最长的名字撑开（v1.21 撞过）
            view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < ToolPages.Rows; r++)
                view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var ids = pages[p];
            for (int slot = 0; slot < ToolPages.PerPage; slot++)
            {
                UIElement cell;
                if (slot < ids.Count)
                {
                    // v1.41 / M3-5：序号按格子的全局先后算，跨页连续——第 2 页第 1 格是 7，
                    // 不是 1。用户数的是「今晚第几个跑」，不是「这一页第几个」。
                    int order = p * ToolPages.PerPage + slot + 1;
                    cell = BuildToolTile(ProfileRegistry.Get(ids[slot]), order);
                }
                else if (p == _toolPage && slot == ids.Count)
                {
                    // 「＋」只出现在当前页——每页都放一个会让人以为每页都要单独加
                    cell = BuildAddTile();
                }
                else
                {
                    cell = BuildEmptyTile();
                }
                PlaceInSlot(cell, slot);
                view.Children.Add(cell);
            }

            view.IsHitTestVisible = p == _toolPage;
            ToolGridHost.Children.Add(view);
            _pageViews[p] = view;
        }

        PlaceToolPages(animate: false);
        RenderPageDots();

        // 记下这一轮的位置，下一次重排时用来算「从哪儿飞过来」
        _tilePositions.Clear();
        double w = PageWidth;
        if (w > 0 && ToolPagerClip.ActualHeight > 0)
        {
            double cellW = w / ToolPages.Columns;
            double cellH = ToolPagerClip.ActualHeight / ToolPages.Rows;
            for (int p = 0; p < pages.Count; p++)
                for (int s = 0; s < pages[p].Count; s++)
                    _tilePositions[pages[p][s]] = new Point(p * w + (s % ToolPages.Columns) * cellW,
                                                            (s / ToolPages.Columns) * cellH);
        }

        if (animate && before.Count > 0) AnimateTilesFrom(before);

        // 桌面能摆的格数＝所有页加起来
        int room = pages.Sum(pg => ToolPages.PerPage) - pages.Sum(pg => pg.Count);
        TbGridOverflow.Visibility = Visibility.Collapsed;
        if (pages.Count >= ToolPages.MaxPages && room <= 1)
        {
            TbGridOverflow.Text = $"桌面已经开满 {ToolPages.MaxPages} 页。想再放工具，先删掉一些不用的。";
            TbGridOverflow.Visibility = Visibility.Visible;
        }

        _gridSignature = string.Join("|", pages.Select(pg => string.Join(",", pg)));
    }

    /// <summary>
    /// 单页宽度（＝可视区宽度）。
    /// <para>
    /// 三级退路：裁切容器 → 页容器 → 窗口宽减内边距；都量不出来时给 600 这个保守值。
    /// **绝不能返回 0**：所有页都靠「第 n 页平移 n 个页宽」来分开，
    /// 页宽是 0 就等于所有页叠在同一处——两页的格子会在同一格里重合，
    /// 空格的「空」字会浮在真格子的图标上（v1.27 第一次渲染就是这样，
    /// 截图里 BetterGI 那格右边多了个淡淡的「空」）。宁可先按 600 摆歪，
    /// 等 <see cref="ToolPager_SizeChanged"/> 量出来再摆正。
    /// </para>
    /// </summary>
    private double PageWidth
    {
        get
        {
            double w = ToolPagerClip?.ActualWidth ?? 0;
            if (w <= 0) w = ToolGridHost?.ActualWidth ?? 0;
            if (w <= 0) w = ActualWidth - 60;
            return w > 0 ? w : 600;
        }
    }

    /// <summary>把各页摆到「当前页在正中间」的位置；animate=true 时滑过去而不是瞬移。</summary>
    private void PlaceToolPages(bool animate)
    {
        double w = PageWidth;
        if (w <= 0) return;
        foreach (var kv in _pageViews)
        {
            double target = (kv.Key - _toolPage) * w;
            if (kv.Value.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform(target, 0);
                kv.Value.RenderTransform = tt;
                continue;
            }
            if (!animate)
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = target;
                continue;
            }
            var anim = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
            };
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }

    /// <summary>翻到第 n 页（带滑动动画）。</summary>
    private void GoToolPage(int index, bool animate = true)
    {
        var pages = PageList();
        int target = Math.Clamp(index, 0, Math.Max(0, pages.Count - 1));
        if (target == _toolPage && _pageViews.Count > 0) { PlaceToolPages(animate: false); return; }
        _toolPage = target;

        // 「＋」格只在当前页，翻页要跟着搬，不然新页上一个添加入口都没有
        RenderToolGrid();
        PlaceToolPages(animate);
        foreach (var kv in _pageViews) kv.Value.IsHitTestVisible = kv.Key == _toolPage;
        UpdatePageDots();
    }

    /// <summary>重排之后让每一格从旧位置滑到新位置（ColorOS 那种「让开」的手感）。</summary>
    private void AnimateTilesFrom(Dictionary<string, Point> before)
    {
        double w = PageWidth;
        if (w <= 0 || ToolPagerClip == null) return;
        double cellW = w / ToolPages.Columns;
        double cellH = ToolPagerClip.ActualHeight / ToolPages.Rows;
        if (cellH <= 0) return;

        var pages = PageList();
        for (int p = 0; p < pages.Count; p++)
        {
            if (!_pageViews.TryGetValue(p, out var view)) continue;
            for (int s = 0; s < pages[p].Count; s++)
            {
                string id = pages[p][s];
                if (!before.TryGetValue(id, out var old)) continue;
                var now = new Point(p * w + (s % ToolPages.Columns) * cellW, (s / ToolPages.Columns) * cellH);
                double dx = old.X - now.X, dy = old.Y - now.Y;
                if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) continue;
                if (view.Children.Count <= s) continue;
                if (view.Children[s] is not FrameworkElement fe) continue;

                var tt = new TranslateTransform(dx, dy);
                fe.RenderTransform = tt;
                var ease = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                var ax = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(240))
                { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd };
                var ay = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(240))
                { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd };
                tt.BeginAnimation(TranslateTransform.XProperty, ax);
                tt.BeginAnimation(TranslateTransform.YProperty, ay);
            }
        }
    }

    /// <summary>第 n 个格子摆在第几行第几列（2 列 3 行）。</summary>
    private static void PlaceInSlot(UIElement element, int slot)
    {
        if (element is not FrameworkElement fe) return;
        int col = slot % ToolPages.Columns;
        int row = slot / ToolPages.Columns;
        Grid.SetRow(fe, Math.Min(row, ToolPages.Rows - 1));
        Grid.SetColumn(fe, Math.Min(col, ToolPages.Columns - 1));
    }

    // ===== 页码指示器 =====

    /// <summary>每个圆点对应当前第几页（长按弹删除确认时要用）。</summary>
    private readonly List<Border> _pageDots = new();

    private void RenderPageDots()
    {
        if (PageDotsHost == null) return;
        int count = PageList().Count;

        // v1.29：只有一页时整行不出现——一个点不传递任何信息，
        // 摆在那儿只会让人猜「是不是还有别的页我没翻到」。
        PageDotsHost.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (count <= 1)
        {
            PageDotsHost.Children.Clear();
            _pageDots.Clear();
            return;
        }

        if (_pageDots.Count == count)
        {
            UpdatePageDots();
            return;
        }

        PageDotsHost.Children.Clear();
        _pageDots.Clear();
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = BrushOf("BorderStrongBrush") ?? DotGray,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = index,
                ToolTip = $"第 {index + 1} 页（点一下过去，按住能删掉这一页）",
            };
            dot.MouseLeftButtonUp += PageDot_Click;
            dot.MouseLeftButtonDown += PageDot_PressDown;
            PageDotsHost.Children.Add(dot);
            _pageDots.Add(dot);
        }
        UpdatePageDots();
    }

    /// <summary>把当前页那个点拉长成胶囊，其余小圆点——比加个框更安静，也更像手机桌面。</summary>
    private void UpdatePageDots()
    {
        for (int i = 0; i < _pageDots.Count; i++)
        {
            bool here = i == _toolPage;
            var dot = _pageDots[i];
            var ease = new System.Windows.Media.Animation.CubicEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var w = new System.Windows.Media.Animation.DoubleAnimation(here ? 18 : 6, TimeSpan.FromMilliseconds(200))
            { EasingFunction = ease, FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd };
            dot.BeginAnimation(FrameworkElement.WidthProperty, w);
            dot.Background = here
                ? (BrushOf("BrandBrush") ?? DotGreen)
                : (BrushOf("BorderStrongBrush") ?? DotGray);
        }
    }

    private void PageDot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border dot || dot.Tag is not int index) return;
        if (_suppressDotClick) { _suppressDotClick = false; return; }
        e.Handled = true;
        GoToolPage(index);
    }

    private bool _suppressDotClick;
    private System.Windows.Threading.DispatcherTimer? _dotHold;

    /// <summary>按住页码点 600ms ＝ 想删掉这一页。松开时不当作点击。</summary>
    private void PageDot_PressDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border dot || dot.Tag is not int index) return;
        _suppressDotClick = false;
        _dotHold?.Stop();
        _dotHold = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _dotHold.Tick += (_, _) =>
        {
            _dotHold?.Stop();
            _dotHold = null;
            _suppressDotClick = true;
            AskDeletePage(index);
        };
        _dotHold.Start();
        dot.MouseLeave += (_, _) => { _dotHold?.Stop(); _dotHold = null; };
    }

    /// <summary>
    /// 删掉一整页。
    /// <para>
    /// 这个动作不可撤销——页里的工具会从桌面上消失（档案还在，可以再添回来）。
    /// 所以对话框要把「哪一页、几个工具、能不能反悔」三件事一次说清，
    /// 而不是只问一句「确定删除吗」。
    /// </para>
    /// </summary>
    private void AskDeletePage(int index)
    {
        var pages = PageList();
        if (pages.Count <= 1)
        {
            AppDialog.Show("这是唯一的一页，删掉就没地方放工具了。",
                kind: AppDialog.Kind.Info, title: "删除这一页", primary: "知道了", owner: this);
            return;
        }
        if (index < 0 || index >= pages.Count) return;

        var ids = pages[index];
        var names = ids.Select(id => ProfileRegistry.Get(id).DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        string detail = ids.Count == 0
            ? "这一页是空的，删掉不影响任何工具。"
            : $"这一页上有 {ids.Count} 个工具：{string.Join("、", names.Take(6))}{(names.Count > 6 ? " 等" : "")}。\n"
              + "删掉之后它们不会出现在桌面上，但档案和设置都还在——想放回来，右键任意一格「移到最后」，或者点「＋ 添加」。\n\n"
              + "这一步撤不回来。";

        bool ok = AppDialog.Show($"删掉第 {index + 1} 页？", detail: detail,
            kind: AppDialog.Kind.Question, title: "删除整页",
            primary: "删掉这一页", secondary: "算了", owner: this);
        if (!ok) return;

        ReplacePages(ToolPages.RemovePageAt(pages, index));
        if (_toolPage >= index && _toolPage > 0) _toolPage--;
        _toolPage = Math.Clamp(_toolPage, 0, Math.Max(0, PageList().Count - 1));
        RenderToolGrid();
        RenderQueue();
        AppState.PublishLog($"✓ 已删除第 {index + 1} 页桌面");
        LogService.LogEvent($"删除桌面页：第 {index + 1} 页，含 {ids.Count} 个工具");
    }

    // ===== 触摸横滑翻页 =====

    private void ToolPager_ManipulationStarting(object sender, System.Windows.Input.ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = ToolPagerClip;
        e.Mode = System.Windows.Input.ManipulationModes.TranslateX;
    }

    /// <summary>
    /// 可视区宽度变了就重新摆位。
    /// <para>
    /// 第一次量出来之前 <see cref="PageWidth"/> 只能用保守值（600），那时的摆位是错的；
    /// 这里补一次不动画的重摆，宽窗口下才不会被看成「第一页露了一半、第二页挤进来」。
    /// 这里不放动画：拖窗口边框时每帧都会触发，放动画会变成一路抖。
    /// </para>
    /// </summary>
    private void ToolPager_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (_pageViews.Count == 0) return;
        PlaceToolPages(animate: false);
    }

    private void ToolPager_ManipulationDelta(object sender, System.Windows.Input.ManipulationDeltaEventArgs e) => e.Handled = true;

    private void ToolPager_ManipulationCompleted(object sender, System.Windows.Input.ManipulationCompletedEventArgs e)
    {
        double dx = e.TotalManipulation.Translation.X;
        double w = PageWidth;
        if (w <= 0) return;
        double threshold = Math.Max(60, w * 0.18);
        if (Math.Abs(dx) < threshold) return;
        GoToolPage(_toolPage + (dx < 0 ? 1 : -1));
    }

    /// <summary>
    /// v1.41 / M3-5：格子再加一行「① · 间隔 2 分钟 · 跑完通知」。
    ///
    /// <para>
    /// 为什么放这儿：格子上原来只有名字和状态两行，回答的是「它配没配好、今晚跑不跑」。
    /// 但真正让人半夜爬起来看屏幕的问题是「它排第几个、中间歇多久、跑完之后机器会怎样」——
    /// 这三件事以前散在「任务清单」卡的下半部分和设置页里，要滚要翻。挪到格子上，
    /// 一眼就齐了。序号按队列顺序（也就是格子的先后），不在队列里的工具没有序号，显示「—」。
    /// </para>
    /// </summary>
    private Border BuildToolTile(ToolProfile profile, int order = 0)
    {
        bool ready = false;
        try { ready = ToolRunner.HasExecutable(profile); } catch { }
        bool runningThis = QueueService.IsRunning
            && string.Equals(QueueService.CurrentToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase);

        // v1.26：格子在不在桌上＝今晚跑不跑。原来这件事由队列行的勾选框表达，
        // 列表换成格子后勾选框没了，所以状态得直接画在格子上：
        // 关了的那一格压暗、状态字写「今晚不跑」，一眼能分出「今晚要跑的」和「装着但我先不用」。
        var queueEntry = QueueEntryOf(profile.ToolId);
        bool onDuty = queueEntry == null || queueEntry.Enabled;

        var icon = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(9),
            Background = BrushOf("InsetBrush") ?? DotGray,
            BorderBrush = BrushOf("BorderBrush") ?? DotGray,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var bmp = ToolIconService.GetBitmap(profile);
        if (bmp != null)
        {
            icon.Child = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(4) };
        }
        else
        {
            string letter = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "?"
                : profile.DisplayName.Trim()[..1].ToUpperInvariant();
            icon.Child = new TextBlock
            {
                Text = letter,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("TextSecondaryBrush") ?? DotGray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        string stateText = !onDuty ? "今晚不跑" : runningThis ? "运行中" : ready ? "已就绪" : "未配置";
        var stateBrush = !onDuty
            ? (BrushOf("TextTertiaryBrush") ?? DotGray)
            : runningThis ? (BrushOf("BrandBrush") ?? DotGreen)
            : ready ? (BrushOf("TextTertiaryBrush") ?? DotGray) : (BrushOf("WarningBrush") ?? DotYellow);

        // v1.26：格子从「图标在上、名字在下」改成横排（图标左、名字与状态右）。
        //
        // 原因是格子搬进启动页那张卡之后，一列只有半屏宽的一半——竖排里图标居中、
        // 名字字距被拉得很开，看着像八个大方块而不是八个工具；而且竖排一格要 96 高，
        // 四行下来 400 多像素，把下面「一键开跑」挤出屏幕。横排一格 54 高，四行刚好放得下。
        //
        // 用 Grid 而不是 StackPanel：StackPanel 给子元素的可用宽度是无限的，
        // TextBlock 拿到无限宽就不会触发 TextTrimming（v1.21 截了三次才发现）。
        var stack = new Grid();
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // 默认列是 Auto＝按内容量宽＝无限宽，TextTrimming 永远不会生效。
        // 必须显式给一个星号列，宽度才由格子决定。
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // v1.41 / M3-5：第三行留给「① · 间隔 2 分钟 · 跑完通知」。
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRowSpan(icon, 3);
        Grid.SetColumn(icon, 0);
        stack.Children.Add(icon);

        var nameText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.ToolId : profile.DisplayName,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(9, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BrushOf("TextPrimaryBrush") ?? DotGray,
        };
        Grid.SetRow(nameText, 0);
        Grid.SetColumn(nameText, 1);
        stack.Children.Add(nameText);

        var stateText_ = new TextBlock
        {
            Text = stateText,
            FontSize = 10.5,
            Margin = new Thickness(9, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = stateBrush,
        };
        Grid.SetRow(stateText_, 1);
        Grid.SetColumn(stateText_, 1);
        stack.Children.Add(stateText_);

        // v1.41 / M3-5：第三行——「① · 间隔 2 分钟 · 跑完通知」。
        // 用辅助色，压得比状态那行更淡：它是给「扫一眼」用的，不该抢状态的位置。
        var summaryText = new TextBlock
        {
            Text = BuildToolTileSummary(profile.ToolId, order),
            FontSize = 10.5,
            Margin = new Thickness(9, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        };
        Grid.SetRow(summaryText, 2);
        Grid.SetColumn(summaryText, 1);
        stack.Children.Add(summaryText);

        var tile = new Border
        {
            Margin = new Thickness(4),
            Padding = new Thickness(10, 9, 10, 9),
            MinHeight = 54,     // 名字一行与两行的格子要一样高，否则第二行会参差
            // 长名字在被裁掉之前会先画到格子外面去（窗口没 Show 过时 UniformGrid 量到的宽度是无限大）。
            // 裁一刀是唯一能保证「长名字不会顶穿格子」的办法，配合 TextTrimming 才有省略号。
            ClipToBounds = true,
            CornerRadius = new CornerRadius(10),
            Background = BrushOf("CardBg2Brush") ?? BrushOf("ControlBgBrush") ?? DotGray,
            BorderBrush = runningThis ? (BrushOf("BrandBrush") ?? DotGray) : (BrushOf("BorderBrush") ?? DotGray),
            BorderThickness = new Thickness(1),
            Child = stack,
            ToolTip = BuildToolTileTip(profile, ready, runningThis),
            Tag = profile.ToolId,
            Cursor = System.Windows.Input.Cursors.Hand,
            // v1.26：「今晚不跑」的那一格压暗。勾选框没了，这是唯一还能一眼分出
            // 「今晚要跑」和「装着但先不用」的地方——但别压到看不清字，0.55 是下限。
            Opacity = onDuty ? 1.0 : 0.55,
        };
        tile.MouseRightButtonUp += ToolTile_RightClick;
        // v1.22：长按拖拽。按下去 350ms 不动才算「抓起」，轻点仍然是打开设置——
        // 手机上也是这么分的：点是一下，抓要按住。
        // 松开时由 ToolTile_PressUp 统一判断是「点」还是「放过来了」，
        // 所以这里不再单独挂 ToolTile_Click（两个 handler 抢同一个 Up 事件会双重触发）。
        tile.MouseLeftButtonDown += ToolTile_PressDown;
        tile.MouseMove += ToolTile_DragMove;
        tile.MouseLeftButtonUp += ToolTile_PressUp;

        // 悬停时点亮边框：格子小，没有这一步会看不出哪里能点
        tile.MouseEnter += (_, _) => tile.BorderBrush = BrushOf("BorderStrongBrush") ?? DotGray;
        tile.MouseLeave += (_, _) => tile.BorderBrush = runningThis
            ? (BrushOf("BrandBrush") ?? DotGray)
            : (BrushOf("BorderBrush") ?? DotGray);

        // 交给 RefreshToolGridStates 用的两处引用（按 ToolId 找回，下次只改字不重画）
        if (!string.IsNullOrWhiteSpace(profile.ToolId))
        {
            _tileStates[profile.ToolId] = stateText_;
            _tileBorders[profile.ToolId] = tile;
        }

        // 光靠 Grid 星号列还不够：窗口没真正上屏时（渲染探针、刚构造完那一刻）
        // 测量宽度是不可靠的，TextTrimming 就永远等不到有限宽度。
        // 这里在布局完成后按格子实际宽度给文字一个 MaxWidth，省略号才一定出现。
        tile.SizeChanged += (_, e) =>
        {
            double room = e.NewSize.Width - 12 - 2;   // 减去左右 Padding 与边框
            if (room > 0)
            {
                nameText.MaxWidth = room;
                stateText_.MaxWidth = room;
                summaryText.MaxWidth = room;
            }
        };

        return tile;
    }

    /// <summary>
    /// v1.41 / M3-5：格子上那行状态摘要——「① · 间隔 2 分钟 · 跑完通知」。
    ///
    /// <para>
    /// 三段都只在代码里真实存在的地方取值：序号来自队列顺序（不在队列里就没序号），
    /// 间隔与跑完动作读的是全局设置（当前代码里没有「按工具单独设间隔 / 单独设跑完动作」这回事，
    /// 所以这里不编一个出来）。任何一段取不到就整段省掉，不写占位词。
    /// </para>
    /// </summary>
    private static string BuildToolTileSummary(string toolId, int order)
    {
        var parts = new List<string>();
        // 不在队列里的工具不参与排序，序号位写「—」——写个数字会骗人，
        // 让人以为它会按这个次序跑。
        bool scheduled = QueueEntryOf(toolId) != null;
        parts.Add(scheduled && order > 0 ? OrderLabel(order) : "—");

        int minutes = AppState.Settings.QueueIntervalMinutes;
        parts.Add(minutes > 0 ? $"间隔 {minutes} 分钟" : "不间隔");

        string afterRun = DescribeAfterRun(AppState.Settings.AfterRunAction);
        if (!string.IsNullOrWhiteSpace(afterRun)) parts.Add("跑完" + afterRun);

        return string.Join(" · ", parts);
    }

    /// <summary>1..20 用带圈数字，超过就退回普通数字——再多也数不清了。</summary>
    private static string OrderLabel(int order) => order switch
    {
        1 => "①", 2 => "②", 3 => "③", 4 => "④", 5 => "⑤",
        6 => "⑥", 7 => "⑦", 8 => "⑧", 9 => "⑨", 10 => "⑩",
        11 => "⑪", 12 => "⑫", 13 => "⑬", 14 => "⑭", 15 => "⑮",
        16 => "⑯", 17 => "⑰", 18 => "⑱", 19 => "⑲", 20 => "⑳",
        _ => order.ToString(),
    };

    /// <summary>
    /// 把 AfterRunAction 的机器值翻成人话。取值集合以 <c>MainWindow.xaml</c> 里
    /// CbAfterRun 的五项为准（Notify / RunAgain / OpenApp / Sleep / Shutdown），
    /// 认不出来的返回空串，让调用方整段省掉。
    /// </summary>
    private static string DescribeAfterRun(string? action) => action switch
    {
        "Notify" => "通知",
        "RunAgain" => "再跑一轮",
        "OpenApp" => "打开程序",
        "Sleep" => "自动睡眠",
        "Shutdown" => "自动关机",
        _ => "",
    };

    private string BuildToolTileTip(ToolProfile profile, bool ready, bool runningThis)
    {
        var lines = new List<string>();
        lines.Add(runningThis ? profile.DisplayName + " · 正在跑" : profile.DisplayName);
        var known = ToolCatalog.Find(profile.ToolId);
        if (known != null && !string.IsNullOrWhiteSpace(known.GameName)) lines.Add("对应的游戏：" + known.GameName);
        lines.Add(ready ? "程序：" + profile.ExecutablePath : "还没选程序，点进来选一次");
        if (!string.IsNullOrWhiteSpace(profile.LaunchArguments)) lines.Add("启动参数：" + profile.LaunchArguments);
        var entry = QueueEntryOf(profile.ToolId);
        if (entry != null && !entry.Enabled) lines.Add("今晚不跑（右键可以改回来）");
        lines.Add("左键改启动设置 · 右键改顺序、今晚跑不跑");
        return string.Join("\n", lines);
    }

    private Border BuildAddTile()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = "＋",
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "添加",
            FontSize = 10.5,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        });

        var tile = new Border
        {
            Margin = new Thickness(4),
            Padding = new Thickness(10, 9, 10, 9),
            MinHeight = 54,
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderBrush = BrushOf("BorderBrush") ?? DotGray,
            BorderThickness = new Thickness(1),
            Child = stack,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "加一个自己能启动的 exe（内置清单里没有的游戏工具也行）",
        };
        tile.MouseLeftButtonUp += (_, _) => BtnAddCustomTool_Click(tile, new RoutedEventArgs());
        tile.MouseEnter += (_, _) => tile.BorderBrush = BrushOf("BorderStrongBrush") ?? DotGray;
        tile.MouseLeave += (_, _) => tile.BorderBrush = BrushOf("BorderBrush") ?? DotGray;
        return tile;
    }

    /// <summary>
    /// v1.22：空格子。画成虚线框而不是留白——留白看起来像「卡片到这儿就结束了」，
    /// 虚线才说得出「这里还有位置，可以放东西」。
    /// </summary>
    private Border BuildEmptyTile()
    {
        // WPF 的 Border 只认实线，虚线得自己拿 Rectangle 描一个（StrokeDashArray），
        // 所以这里 Border 不画边，边交给里面那层 Rectangle。
        var outline = new System.Windows.Shapes.Rectangle
        {
            Stroke = BrushOf("BorderBrush") ?? DotGray,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            RadiusX = 10,
            RadiusY = 10,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        var label = new TextBlock
        {
            Text = "空",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
            Opacity = 0.5,
        };

        var layer = new Grid();
        layer.Children.Add(outline);
        layer.Children.Add(label);

        return new Border
        {
            Margin = new Thickness(4),
            Padding = new Thickness(10, 9, 10, 9),
            MinHeight = 54,
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "空位。点「＋ 添加」或者把 exe 拖进窗口，就能放一个工具进来。",
            Child = layer,
        };
    }

    // ===== 长按拖拽（v1.22，v1.27 改成跨页）=====
    // 状态机：按下 → 350ms 计时器；到点且没移出 6px 就进入拖拽态；
    // 进入拖拽态时把这一格搬进卡片顶层的 ToolDragLayer（Canvas）——
    // 那一层在裁切外面，所以拖到卡片边缘时不会被切成一半，露出来的半个格子
    // 正好是「那边还能放」的提示。松手时算出落点（页 + 格），插入，再重排并放动画。
    // 为什么不用 WPF 的 DragDrop：它是给跨程序拖文件用的，会带来一个跟随系统的
    // 半透明幽灵，而且拖回自己身上常常收不到 Drop。这里只要「挪个位置」，自己算更快也更稳。
    private Border? _dragTile;
    private string? _dragToolId;
    private System.Windows.Threading.DispatcherTimer? _dragHold;
    private Point _dragOrigin;
    private Point _dragGrab;          // 按住那一刻，鼠标在格子内部的相对位置
    private Size _dragSize;           // 抓起时的尺寸（Canvas 不会替它撑开）
    private bool _dragging;

    /// <summary>拖到边缘停留多久翻页。太快会一路翻过头，太慢又像卡住。</summary>
    private const int EdgeFlipMs = 420;

    /// <summary>离左右边缘多少像素算「想翻页」。</summary>
    private const double EdgeFlipZone = 30;

    private System.Windows.Threading.DispatcherTimer? _edgeFlip;
    private int _edgeDir;

    private void ToolTile_PressDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border tile || tile.Tag is not string toolId) return;
        _dragTile = tile;
        _dragToolId = toolId;
        _dragging = false;
        _dragOrigin = e.GetPosition(ToolDragLayer);
        _dragGrab = default;

        _dragHold?.Stop();
        _dragHold = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _dragHold.Tick += (_, _) =>
        {
            _dragHold?.Stop();
            if (_dragTile == null) return;
            BeginTileDrag();
        };
        _dragHold.Start();
    }

    /// <summary>真正把格子搬进拖拽层：量好尺寸、换算坐标，让它悬在鼠标下面。</summary>
    private void BeginTileDrag()
    {
        var tile = _dragTile;
        if (tile == null || ToolDragLayer == null) return;

        _dragging = true;
        _dragSize = new Size(Math.Max(1, tile.ActualWidth), Math.Max(1, tile.ActualHeight));

        // 抓起那一刻的手指位置（相对拖拽层）与格子左上角之间的差，
        // 后面每一步都保持它不变——否则格子会「跳到」鼠标中心，手感很假。
        Point corner;
        try { corner = tile.TranslatePoint(new Point(0, 0), ToolDragLayer); }
        catch { corner = new Point(0, 0); }
        _dragGrab = new Point(_dragOrigin.X - corner.X, _dragOrigin.Y - corner.Y);

        if (tile.Parent is Panel parent) parent.Children.Remove(tile);
        tile.Width = _dragSize.Width;
        tile.Height = _dragSize.Height;
        tile.HorizontalAlignment = HorizontalAlignment.Left;
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0);
        tile.RenderTransform = Transform.Identity;
        tile.Opacity = 0.92;
        tile.BorderBrush = BrushOf("BrandBrush") ?? DotGreen;
        tile.IsHitTestVisible = false;   // 让它彻底跟着鼠标，不再接自己的事件
        Canvas.SetLeft(tile, corner.X);
        Canvas.SetTop(tile, corner.Y);
        ToolDragLayer.Children.Add(tile);

        TbGridHint.Text = "松手放到想放的位置。拖到边缘可以翻页。";
    }

    private void ToolTile_DragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTile == null || !_dragging)
        {
            // 还没进入拖拽态：手一动就取消长按，免得「想点一下」被当成「抓起」
            if (_dragHold != null && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                var now = e.GetPosition(ToolDragLayer);
                if (Math.Abs(now.X - _dragOrigin.X) > 6 || Math.Abs(now.Y - _dragOrigin.Y) > 6)
                {
                    _dragHold.Stop();
                    _dragHold = null;
                }
            }
            return;
        }

        var p = e.GetPosition(ToolDragLayer);
        Canvas.SetLeft(_dragTile, p.X - _dragGrab.X);
        Canvas.SetTop(_dragTile, p.Y - _dragGrab.Y);
        CheckEdgeFlip(p);
    }

    /// <summary>
    /// 拖到左右边缘停一会儿就翻页。
    /// <para>
    /// 为什么不「一碰到边缘就翻」：那样从第一页拖到第二页时，手稍微靠边就翻，
    /// 想放边缘那两格就永远放不进去。停留 420ms 是「我确实想过去」的意思。
    /// </para>
    /// </summary>
    private void CheckEdgeFlip(Point p)
    {
        double w = PageWidth;
        int dir = 0;
        if (w > 0)
        {
            if (p.X < EdgeFlipZone) dir = -1;
            else if (p.X > w - EdgeFlipZone) dir = 1;
        }
        var pages = PageList();
        if (dir < 0 && _toolPage <= 0) dir = 0;
        // v1.29：最后一页再往右不再「自动开一页新的」——页数现在由工具数量决定，
        // 手动开出来的页没有工具可放，就只是一个空壳。真要放到后面，先「＋ 添加」。
        if (dir > 0 && _toolPage >= pages.Count - 1) dir = 0;

        if (dir == _edgeDir && _edgeFlip != null) return;
        _edgeFlip?.Stop();
        _edgeFlip = null;
        _edgeDir = dir;
        if (dir == 0) return;

        _edgeFlip = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(EdgeFlipMs),
        };
        _edgeFlip.Tick += (_, _) =>
        {
            _edgeFlip?.Stop();
            _edgeFlip = null;
            if (!_dragging) return;
            GoToolPage(_toolPage + dir);
            _edgeDir = 0;
            SoundService.Play("tick.wav");
        };
        _edgeFlip.Start();
    }

    private void ToolTile_PressUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragHold?.Stop();
        _dragHold = null;
        _edgeFlip?.Stop();
        _edgeFlip = null;
        _edgeDir = 0;

        var tile = _dragTile;
        string? toolId = _dragToolId;
        bool dragging = _dragging;
        var dropPoint = e.GetPosition(ToolDragLayer);

        _dragTile = null;
        _dragToolId = null;
        _dragging = false;

        if (tile == null || toolId == null) return;

        if (!dragging)
        {
            // 没长按够，就当成一次点击
            e.Handled = true;
            OpenToolLaunch(toolId);
            return;
        }

        e.Handled = true;
        TbGridHint.Text = "点一下改启动方式；按住一格拖到别处能换顺序；右键能改「今晚跑不跑」。";

        var (page, slot) = SlotAtPage(dropPoint);
        tile.Opacity = 1;
        tile.IsHitTestVisible = true;

        // 先把抓起来的那个临时壳收掉（重画会造新的，留着只会重复一份）
        if (tile.Parent is Panel p) p.Children.Remove(tile);

        var pages = PageList();
        if (page < 0 || page >= pages.Count)
        {
            RenderToolGrid();
            return;
        }

        var next = ToolPages.Insert(pages, toolId, page, slot);
        string after = string.Join("|", next.Select(pg => string.Join(",", pg)));
        if (after == string.Join("|", pages.Select(pg => string.Join(",", pg))))
        {
            RenderToolGrid();
            return;
        }

        ReplacePages(next);
        _toolPage = page;
        RenderToolGrid(animate: true);
        UpdatePageDots();
        LogService.LogEvent($"工具拖动：{toolId} → 第 {page + 1} 页第 {slot + 1} 格");
    }

    /// <summary>
    /// 落点换算成（第几页, 第几格）。
    /// <para>
    /// 会把「拖出可视区」也算进来：拖到卡片左边就是上一页的最后一格，
    /// 拖到右边就是下一页的第一格——用户不用先翻页再拖。
    /// </para>
    /// </summary>
    private (int Page, int Slot) SlotAtPage(Point p)
    {
        var pages = PageList();
        if (pages.Count == 0) return (-1, -1);
        double w = PageWidth;
        double h = ToolPagerClip?.ActualHeight ?? 0;
        if (w <= 0 || h <= 0) return (-1, -1);

        int page = _toolPage + (int)Math.Floor(p.X / w);
        page = Math.Clamp(page, 0, pages.Count - 1);

        double x = p.X - (page - _toolPage) * w;
        double cellW = w / ToolPages.Columns;
        double cellH = h / ToolPages.Rows;
        if (cellW <= 0 || cellH <= 0) return (-1, -1);
        int col = Math.Clamp((int)(x / cellW), 0, ToolPages.Columns - 1);
        int row = Math.Clamp((int)(p.Y / cellH), 0, ToolPages.Rows - 1);
        return (page, row * ToolPages.Columns + col);
    }

    /// <summary>点格子 = 打开这个工具的启动设置；改完立刻重画，不重启。</summary>
    private void OpenToolLaunch(string toolId)
    {
        try
        {
            var profile = ProfileRegistry.Get(toolId);
            var w = new ToolLaunchWindow(profile) { Owner = this };
            w.ShowDialog();
            if (w.Deleted)
            {
                SaveToolOrder(ToolGrid.Remove(CurrentToolOrder(), toolId));
                RebuildCustomToolCards();
                RenderQueue();
                RenderToolGrid();
            }
            if (w.Changed || w.Deleted)
            {
                RefreshToolCards();
                RefreshHomeHero();
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开工具启动设置失败：" + ex.Message);
            AppDialog.Show("打不开这个工具的设置，详情见日志。",
                kind: AppDialog.Kind.Warning, title: "桌面", primary: "知道了", owner: this);
        }
    }

    /// <summary>
    /// v1.42 / M8-2：工具行的右键菜单。六项，按「现在就要它做点什么」排前面。
    ///
    /// <para>
    /// 「跳过这次」和「今晚不跑它」是两件事，菜单上就得分得清：
    /// 前者只作用这一趟（队列跑完就清掉，<see cref="QueueService.ToggleSkipOnce"/>），
    /// 后者是持久开关（写进 <see cref="QueueEntry.Enabled"/>，明天照旧不跑）。
    /// 文案跟着各自的状态变，不用打开别的地方确认现在是哪种。
    /// </para>
    /// </summary>
    private void ToolTile_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement tile || tile.Tag is not string toolId) return;
        e.Handled = true;

        var profile = ProfileRegistry.Get(toolId);
        string name = string.IsNullOrWhiteSpace(profile.DisplayName) ? toolId : profile.DisplayName;
        var entry = QueueEntryOf(toolId);
        bool onDuty = entry == null || entry.Enabled;

        var menu = new ContextMenu();

        // 1) 马上跑一次
        menu.Items.Add(MenuItem("立即跑这一个", () => RunToolNow(toolId)));

        // 2) 只作用于这一趟
        menu.Items.Add(MenuItem(
            QueueService.IsSkippedOnce(toolId) ? "这次还是跑" : "跳过这次",
            () => ToggleSkipOnce(toolId)));

        // 3) 持久开关，到明天也还算数
        if (onDuty)
        {
            menu.Items.Add(MenuItem("今晚不跑它", () => ToggleQueueEnabled(toolId)));
        }
        else
        {
            menu.Items.Add(MenuItem("今晚跑它", () => ToggleQueueEnabled(toolId)));
        }

        menu.Items.Add(new Separator());

        // 4-5) 改它是怎么跑的
        menu.Items.Add(MenuItem("改间隔…", () => RevealIntervalSetting()));
        menu.Items.Add(MenuItem("编辑…", () => OpenToolLaunch(toolId)));

        // 6) 它刚才干了什么
        menu.Items.Add(MenuItem("查看日志", () => OpenLogView(LogFilter.ForTool(toolId))));

        menu.Items.Add(new Separator());

        // 7) 从今晚清单里拿掉。内置工具只「移出清单」，自定义工具才真的删档案。
        menu.Items.Add(MenuItem("移除…", () => RemoveFromList(toolId)));

        menu.PlacementTarget = tile;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>
    /// v1.42 / M8-2：把工具从今晚清单里移除。
    ///
    /// <para>
    /// 内置工具只关队列项，档案和配置原样留着——下次在「＋ 添加工具」里点它就能加回来。
    /// 自定义工具走原来的「彻底删掉」，那条路会一并清档案，问法也更重。
    /// </para>
    /// </summary>
    private void RemoveFromList(string toolId)
    {
        var profile = ProfileRegistry.Get(toolId);
        string name = string.IsNullOrWhiteSpace(profile.DisplayName) ? toolId : profile.DisplayName;

        if (!ProfileRegistry.IsBuiltIn(toolId))
        {
            RemoveToolCompletely(toolId);
            return;
        }

        bool ok = AppDialog.Show(
            $"把 {name} 从今晚清单移除？配置会保留，随时可以再加回来。",
            kind: AppDialog.Kind.Question,
            title: "移除",
            primary: "移除",
            secondary: "算了",
            owner: this);
        if (!ok) return;

        var entry = QueueEntryOf(toolId);
        if (entry != null)
        {
            entry.Enabled = false;
            AppState.SaveSettings();
        }
        _expandedRows.Remove(toolId);
        RenderQueue();
        RenderToolRows();
        RefreshToolCards();
        AppState.PublishLog($"✓ 已从今晚清单移除：{name}");
        LogService.LogEvent($"移出清单：{toolId}");
    }

    /// <summary>
    /// v1.41 / M3-7：马上单独跑这一个。走的是与命令面板 <c>run.tool:</c> 同一条路——
    /// 一份执行逻辑，出问题只需要修一个地方。
    /// </summary>
    private void RunToolNow(string toolId)
    {
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
        AppState.PublishLog($"✓ 单独跑一次：{profile.DisplayName}");
        LogService.LogEvent("桌面菜单：立即跑 " + toolId);
        _ = QueueService.StartSingleAsync(profile, QueueTrigger.Manual);
    }

    /// <summary>
    /// v1.41 / M3-7：跳过这一次。与「今晚不跑它」不同——后者是把这项从队列里关掉，
    /// 得手动开回来；这里只是这一次不跑，下一趟定时照旧。
    /// </summary>
    private void ToggleSkipOnce(string toolId)
    {
        bool nowSkipped = QueueService.ToggleSkipOnce(toolId);
        var profile = ProfileRegistry.Get(toolId);
        AppState.PublishLog(nowSkipped
            ? $"这次不跑 {profile.DisplayName}，下一趟照旧"
            : $"这次照跑 {profile.DisplayName}");
        RenderToolGrid();
    }

    /// <summary>
    /// v1.41 / M3-7：改间隔。间隔目前只有全局一份（<see cref="AppSettings.QueueIntervalMinutes"/>），
    /// 所以这里不假装能单独改某个工具——把人送到那个真正的开关旁边，并说清它是全局的。
    /// </summary>
    private void RevealIntervalSetting()
    {
        ShowPage(Pager.Settings);
        RevealCard(CbInterval);
        NotifyService.Show(NotifyLevel.Info, "龙管家",
            "工具之间歇多久是全局一份，改一次所有工具都跟着变。");
    }

    private MenuItem MenuItem(string text, Action action)
    {
        var item = new MenuItem
        {
            Header = text,
            Padding = new Thickness(10, 6, 10, 6),
            Foreground = BrushOf("TextPrimaryBrush") ?? DotGray,
        };
        item.Click += (_, _) => { try { action(); } catch (Exception ex) { LogService.LogEvent("桌面菜单出错：" + ex.Message); } };
        return item;
    }

    /// <summary>
    /// v1.23：真的删掉一个自定义工具。
    ///
    /// 以前这个菜单项调的是 <see cref="OpenToolLaunch"/>——点「删除」打开的是设置窗，
    /// 什么都没删，用户得在设置窗里再找一次「删除」才算数。现在就地问一句，问完才动手。
    /// </summary>
    private void RemoveToolCompletely(string toolId)
    {
        if (ProfileRegistry.IsBuiltIn(toolId)) return;

        var profile = ProfileRegistry.Get(toolId);
        string name = string.IsNullOrWhiteSpace(profile.DisplayName) ? toolId : profile.DisplayName;

        bool ok = AppDialog.Show(
            $"从桌面移除「{name}」？",
            detail: "这个工具在管家里的档案、队列里的那一项、以及它的图标缓存都会一起清掉。\n"
                  + "它自己的程序和配置不受影响，装在哪还在哪。",
            kind: AppDialog.Kind.Question,
            title: "移除工具",
            primary: "移除",
            secondary: "算了",
            owner: this);
        if (!ok) return;

        if (!ProfileRegistry.Remove(toolId))
        {
            // Remove 拒绝内置工具；走到这里说明状态不一致，别假装删成功了
            AppDialog.Show("这个工具没能删掉（内置工具不允许移除）。",
                kind: AppDialog.Kind.Warning, title: "移除工具", primary: "知道了", owner: this);
            return;
        }

        SaveToolOrder(ToolGrid.Remove(CurrentToolOrder(), toolId));
        RebuildCustomToolCards();
        RenderToolGrid();
        RenderQueue();
        RefreshToolsSummary();
        AppState.PublishLog($"✓ 已移除工具：{name}");
        LogService.LogEvent($"移除工具：{toolId}");
    }

    private void MoveTool(string toolId, int delta)
    {
        var moved = ToolGrid.Move(CurrentToolOrder(), toolId, delta);
        if (moved == null) return;      // 到边了：不动，也不提示——格子没反应就是最清楚的回答
        SaveToolOrder(moved);
        RenderToolGrid(animate: true);
    }

    private void MoveToolToEnd(string toolId)
    {
        var order = CurrentToolOrder();
        if (!order.Contains(toolId, StringComparer.OrdinalIgnoreCase)) return;
        order.RemoveAll(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase));
        order.Add(toolId);
        SaveToolOrder(order);
        RenderToolGrid(animate: true);
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

        // v1.21：新加的工具排到桌面最后，不插队——用户排好的顺序不该被一次「添加」打乱
        SaveToolOrder(ToolGrid.Append(CurrentToolOrder(), dlg.Result.ToolId));
        RebuildCustomToolCards();
        RenderQueue();
        // v1.27：新工具可能落在最后那一页，翻过去让人看见它，否则「我加了怎么没出现」
        try
        {
            var (page, _) = ToolPages.Locate(PageList(), dlg.Result.ToolId);
            if (page >= 0) GoToolPage(page);
        }
        catch { }
        RenderToolGrid(animate: true);
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

    // ===== 三页切换（v1.22）=====

    /// <summary>
    /// 当前停在哪一页：0 = 日报（v1.41 起站最左），1 = 主页（打开就在这），2 = 设置，3 = 插件。
    /// 日志不再是其中一页——它变成了一层带过滤条件打开的覆盖视图（见 <see cref="OpenLogView"/>）。
    /// 存成字段而不是读控件状态，是因为切页动画期间 RadioButton 已经变了、页面还没到位，
    /// 中间会有一瞬间两者不一致，靠控件判断会把自己绊一下。
    /// </summary>
    private int _page = Pager.Home;

    /// <summary>可视区宽度变了（首次上屏、改窗口大小）：把三页重新摆到各自位置，不动画。</summary>
    private void OnPageClipSized(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0) return;
        // 改窗口大小时不该放动画——拖边框的过程中每帧都想滑一次，看起来像抖。
        PlacePages(_page, e.NewSize.Width, animate: false);
    }

    /// <summary>
    /// 摆好三页：当前页在 0，左边的页在 -1 屏，右边的页在 +1 屏。
    /// 每页自己撑满可视区（布局给的宽度），这里只负责挪。
    /// </summary>
    private void PlacePages(int index, double width, bool animate)
    {
        if (width <= 0) return;
        // 每页摆在哪一屏由 Pager.Offsets 算（纯函数，探针验过），这里只负责喂给动画。
        var offsets = Pager.Offsets(index);
        var transforms = new (TranslateTransform? T, int Offset)[]
        {
            (ShiftReport, offsets[0]), (ShiftHome, offsets[1]),
            (ShiftSettings, offsets[2]), (ShiftPlugins, offsets[3]),
        };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);
        foreach (var (t, offset) in transforms)
        {
            if (t == null) continue;
            double target = offset * width;
            if (!animate)
            {
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.X = target;
            }
            else
            {
                t.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(target, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
            }
        }
    }

    private void SegPage_Checked(object sender, RoutedEventArgs e)
    {
        // 构造期间 XAML 里的 IsChecked="True" 就会触发一次，那时几个平移量还没建好
        if (ShiftReport == null || ShiftHome == null
            || ShiftSettings == null || ShiftPlugins == null) return;
        if (sender is RadioButton rb && int.TryParse(rb.Tag as string, out int want)) ShowPage(want);
    }

    /// <summary>
    /// 触摸板/触屏横滑。判定逻辑在 <see cref="Pager.Swipe"/>（纯函数，可断言）：
    /// 手指抖一下、或者在卡片上轻轻斜滑都不该把整屏滑走；
    /// 往左滑是下一页，滑到头就停住、不环绕。
    /// 这里只做一件事：把手指位移和可视宽度交给它，拿回该停在哪一页。
    /// </summary>
    private void PageClip_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = PageClip;
        e.Mode = ManipulationModes.TranslateX;
    }

    private void PageClip_ManipulationDelta(object sender, ManipulationDeltaEventArgs e) => e.Handled = true;

    private void PageClip_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
    {
        int want = Pager.Swipe(_page, e.TotalManipulation.Translation.X, PageClip.ActualWidth);
        if (want != _page) ShowPage(want);
    }

    // ===== 鼠标拖动换页（v1.35）=====
    //
    // 为什么以前不做：鼠标按下就会被卡片里的按钮、格子的长按拖拽当成「自己开始的那件事」，
    // 我们要是按下就抢鼠标，按钮就点不着了。
    // 现在的做法是「晚一步」：
    //   按下 → 只记住起点，什么都不抢；
    //   移动 → 横向真的拉开 40px（而且比纵向拉得更开）才 CaptureMouse，这时那件事早就开始了，
    //          Mouse.Captured 已经是别人，我们直接放弃；
    //   松手 → 位移够一屏的 12%（Pager.SwipeThreshold）就翻页，不够就弹回去。

    private const double PageDragStart = 40;      // 横向拖多远才算「想换页」，不是「手抖」
    private Point _pageDragFrom;
    private bool _pageDragArmed;                  // 按下了，还没决定要不要夺鼠标
    private bool _pageDragging;                   // 已经在拖了（鼠标已捕获）
    private double _pageDragShift;                // 当前跟手位移，松手时用来判翻页

    private void PageClip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 已经有别人抓着鼠标（按钮按住了、格子在长按拖拽）就别掺和
        if (Mouse.Captured != null && !ReferenceEquals(Mouse.Captured, PageClip)) return;
        _pageDragFrom = e.GetPosition(PageClip);
        _pageDragArmed = true;
        _pageDragging = false;
        _pageDragShift = 0;
    }

    private void PageClip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pageDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(PageClip);
        double dx = now.X - _pageDragFrom.X;
        double dy = now.Y - _pageDragFrom.Y;

        if (!_pageDragging)
        {
            if (Math.Abs(dx) < PageDragStart) return;
            // 竖着划拉得多，说明人家是在滚列表，不是在换页
            if (Math.Abs(dy) > Math.Abs(dx)) { _pageDragArmed = false; return; }
            // 到头了往反方向拖没有意义，直接放弃，别让页面回弹一下
            if ((dx > 0 && _page == 0) || (dx < 0 && _page == Pager.Count - 1)) { _pageDragArmed = false; return; }
            if (Mouse.Captured != null && !ReferenceEquals(Mouse.Captured, PageClip)) { _pageDragArmed = false; return; }

            _pageDragging = true;
            PageClip.CaptureMouse();
        }

        // 跟手：当前页跟着鼠标走，相邻页从边上探进来。
        // 位移打个折（0.85），拖到头那一下不会显得「怎么拖都还在原地」。
        _pageDragShift = dx;
        DragPages(dx * 0.85);
        e.Handled = true;
    }

    private void PageClip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool wasDragging = _pageDragging;
        _pageDragArmed = false;
        _pageDragging = false;
        if (!wasDragging) return;

        if (PageClip.IsMouseCaptured) PageClip.ReleaseMouseCapture();
        int want = Pager.Swipe(_page, _pageDragShift, PageClip.ActualWidth);
        ShowPage(want);   // 同一页时它会动画弹回原位，不换页也不会僵在半路
        e.Handled = true;
    }

    /// <summary>
    /// 拖动过程中把四页临时挪到「当前页 + shift」。不碰 _page，也不碰 Pager 的档位，
    /// 松手之后 ShowPage 会重新摆正——所以这里只要管好看，不用管对不对。
    /// </summary>
    private void DragPages(double shift)
    {
        double width = PageClip.ActualWidth;
        if (width <= 0) return;
        var offsets = Pager.Offsets(_page);
        var transforms = new (TranslateTransform? T, int Offset)[]
        {
            (ShiftReport, offsets[0]), (ShiftHome, offsets[1]),
            (ShiftSettings, offsets[2]), (ShiftPlugins, offsets[3]),
        };
        foreach (var (t, offset) in transforms)
        {
            if (t == null) continue;
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.X = offset * width + shift;
        }
    }

    /// <summary>
    /// 整屏横移。三页叠在 PageClip 里、各自撑满，靠三个 TranslateTransform 摆位，
    /// 所以「移一屏」就是把当前页的 X 归零、把另外两页推到 ±1 屏。
    /// 动画只碰 RenderTransform（不参与布局），所以 260ms 不会让卡片重排、也不会闪。
    /// </summary>
    private void ShowPage(int index, bool animate = true)
    {
        try
        {
            index = Pager.Clamp(index);
            _page = index;

            // 从日志视图里点底栏切换：先把覆盖层收掉，否则切了也看不见。
            CloseLogView(keepHistory: true);

            SyncPageSegments(index);

            double width = PageClip.ActualWidth;
            if (width <= 0) width = ActualWidth;   // 还没量出来：先按窗口宽猜，SizeChanged 到位后会纠正
            PlacePages(index, width, animate);

            // 日报页切过去才刷：这一页摆的是「今天到现在为止」，隔一夜再看会全是昨天的数。
            if (index == Pager.Report) RefreshReportPage();

            // v1.41 / M4-1：从底栏进设置，先站一级（五张入口卡）。
            // 从别处定位某个开关进来的（RevealCard）会在后面紧接着翻开对应那一类。
            if (index == Pager.Settings) BackToSettingsHome();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("切页失败：" + ex.Message);
        }
    }

    // ===== v1.41 / M4-1：设置页二级化的路由 =====
    //
    // 一级只有五张入口卡，点一张才进二级铺开那一类的全部开关。
    // 为什么这么切：以前五张卡并排，一眼看到二十多个开关，想改「失败之后还跑不跑」
    // 得先在一堆字里认出哪一行属于它。按「你什么时候会想改它」分成五类之后，
    // 一级那屏只回答一个问题：我要改的是哪一类。
    private enum SettingsPane { None, Guard, Timing, Notify, Api, Ui }

    private SettingsPane _settingsPane = SettingsPane.None;

    /// <summary>一级入口卡的标题、副标题、图标路径，以及点进去对应哪个分区。</summary>
    private static readonly (SettingsPane Pane, string Title, string Subtitle, string Icon)[] SettingsEntries =
    {
        (SettingsPane.Guard,  "守护",       "跑的时候出事怎么办", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M12,7 L12,12 L15.5,14"),
        (SettingsPane.Timing, "定时",       "几点跑、中间歇多久", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M12,7.5 L12,12 L15,13.5"),
        (SettingsPane.Notify, "通知",       "跑完怎么告诉你",     "M6,9 A6,6 0 0 1 18,9 L18,14 L20,16 L4,16 L6,14 Z M10,19 A2,2 0 0 0 14,19"),
        (SettingsPane.Api,    "接口",       "给外面的东西开个口", "M7,8 L4,12 L7,16 M17,8 L20,12 L17,16 M14,6 L10,18"),
        (SettingsPane.Ui,     "界面与启动", "平时怎么用它",       "M4,6 L20,6 L20,17 L4,17 Z M9,21 L15,21 M12,17 L12,21"),
    };

    /// <summary>把一级入口卡铺出来。卡片只有五张，不值得缓存。</summary>
    private void BuildSettingsHome()
    {
        if (SettingsEntryHost == null) return;
        SettingsEntryHost.Children.Clear();
        foreach (var entry in SettingsEntries)
        {
            SettingsEntryHost.Children.Add(
                SettingsEntryCard(entry.Pane, entry.Title, entry.Subtitle, entry.Icon));
        }

        // 没走完那五步的人，一级里给一句话指个路——但不再重复「三步就能开始」。
        if (TbSettingsWizardHint != null)
        {
            TbSettingsWizardHint.Visibility = AppState.Settings.OnboardingCompleted
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // 每次进一级都把二级收回去，免得下次进来还停在上一回翻到的那页。
        BackToSettingsHome();
    }

    /// <summary>一张入口卡：图标 + 标题 + 一句「这一类管什么」。整块可点。</summary>
    private UIElement SettingsEntryCard(SettingsPane pane, string title, string subtitle, string icon)
    {
        var border = new Border
        {
            Width = 208,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(12),   // M6-2：卡片圆角 12
            Background = BrushOf("CardBg2Brush") ?? BrushOf("ControlBgBrush") ?? DotGray,
            BorderBrush = BrushOf("BorderBrush") ?? DotGray,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBox = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = BrushOf("BrandSoftBrush") ?? DotGray,
            VerticalAlignment = VerticalAlignment.Top,
        };
        iconBox.Child = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(icon),
            Stroke = BrushOf("BrandTextBrush") ?? DotGreen,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var text = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf("TextPrimaryBrush") ?? DotGray,
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        border.Child = grid;
        border.MouseLeftButtonUp += (_, _) => ShowSettingsPane(pane);
        return border;
    }

    /// <summary>进二级：收起一级，铺开这一个分区。</summary>
    private void ShowSettingsPane(SettingsPane pane)
    {
        if (pane == SettingsPane.None) return;
        _settingsPane = pane;

        var hosts = new (SettingsPane Pane, StackPanel? Host)[]
        {
            (SettingsPane.Guard,  SecGuard),
            (SettingsPane.Timing, SecTiming),
            (SettingsPane.Notify, SecNotify),
            (SettingsPane.Api,    SecApi),
            (SettingsPane.Ui,     SecUi),
        };
        foreach (var pair in hosts)
        {
            if (pair.Host == null) continue;
            pair.Host.Visibility = pair.Pane == pane ? Visibility.Visible : Visibility.Collapsed;
        }

        // v1.41 / M4-2：说明只有露出来才量得到行高，收折得等这一屏真的显示之后再跑。
        // 放在布局之后（Loaded 优先级）而不是当场，是因为此刻 ActualHeight 还是 0。
        StackPanel? shown = null;
        foreach (var pair in hosts) if (pair.Pane == pane) shown = pair.Host;
        if (shown != null)
        {
            Dispatcher.BeginInvoke(new Action(() => HintFold.Apply(shown)), DispatcherPriority.Loaded);
        }

        if (SettingsHome != null) SettingsHome.Visibility = Visibility.Collapsed;
        if (SettingsSection != null) SettingsSection.Visibility = Visibility.Visible;

        // v1.41 / M4-1：通知卡默认是收起的（平时不看渠道，收着才不占地方）。
        // 但人既然点了「通知」这一个入口，就是想改通知——替他把卡打开，
        // 不然点进来只看见一个标题，还得再点一次。
        if (pane == SettingsPane.Notify && NotifyBody != null)
        {
            NotifyBody.Visibility = Visibility.Visible;
        }

        if (TbSettingsSectionName != null)
        {
            foreach (var entry in SettingsEntries)
                if (entry.Pane == pane) TbSettingsSectionName.Text = entry.Title;
        }

        // 换分区等于换一整屏，从顶上开始看才不像「中间缺了一块」。
        ScrollSettings?.ScrollToVerticalOffset(0);
    }

    /// <summary>回到一级。返回按钮、以及 ESC 都走这里。</summary>
    private void BackToSettingsHome()
    {
        _settingsPane = SettingsPane.None;
        if (SettingsHome != null) SettingsHome.Visibility = Visibility.Visible;
        if (SettingsSection != null) SettingsSection.Visibility = Visibility.Collapsed;
    }

    private void BtnSettingsBack_Click(object sender, RoutedEventArgs e) => BackToSettingsHome();

    /// <summary>某个控件属于哪一类——RevealCard 靠它先把二级门打开，不然滚了也看不见。</summary>
    private SettingsPane PaneOfSettingsTarget(FrameworkElement target)
    {
        if (SecGuard != null && (SecGuard == target || SecGuard.IsAncestorOf(target))) return SettingsPane.Guard;
        if (SecTiming != null && (SecTiming == target || SecTiming.IsAncestorOf(target))) return SettingsPane.Timing;
        if (SecNotify != null && (SecNotify == target || SecNotify.IsAncestorOf(target))) return SettingsPane.Notify;
        if (SecApi != null && (SecApi == target || SecApi.IsAncestorOf(target))) return SettingsPane.Api;
        if (SecUi != null && (SecUi == target || SecUi.IsAncestorOf(target))) return SettingsPane.Ui;
        return SettingsPane.None;
    }
    /// <summary>四个 RadioButton 与 _page 对齐。切页可能来自按钮、也可能来自命令面板或代码，两个方向都要能同步。</summary>
    private void SyncPageSegments(int index)
    {
        var pairs = new (RadioButton? Btn, int Index)[]
        {
            (SegPageReport, Pager.Report), (SegPageHome, Pager.Home),
            (SegPageSettings, Pager.Settings), (SegPagePlugins, Pager.Plugins),
        };
        foreach (var (btn, i) in pairs)
        {
            if (btn == null) continue;
            if (i == index)
            {
                if (btn.IsChecked != true) btn.IsChecked = true;
            }
            else if (btn.IsChecked == true)
            {
                btn.IsChecked = false;
            }
        }

        // 那块会滑的底跟着走。放在这里而不是 ShowPage 里，是因为「用户点了哪一项」和
        // 「代码要求切到哪一页」都会走到这里，两条路都得让底动起来。
        MoveNavPill(index, animate: true);
    }

    // ===== 悬浮标签栏（v1.32）=====

    /// <summary>单个标签项的宽度，与 MainWindow.xaml 里 NavBtn 样式的 Width 保持一致。</summary>
    private const double NavItemWidth = 84;

    private double _navPillX;
    private bool _navDragging;
    private Point _navGrabPoint;
    private double _navGrabOffset;

    /// <summary>
    /// 把选中底移到第 index 项下面。
    /// 动画用 BackEase 而不是 CubicEase：滑到位置时会轻微冲过头再回来，
    /// 这一点点回弹是「有质量的东西被推过去」的全部观感来源，去掉之后就是生硬的平移动画。
    /// </summary>
    private void MoveNavPill(int index, bool animate)
    {
        if (NavPillShift == null) return;
        double target = Math.Max(0, index) * NavItemWidth;
        _navPillX = target;

        if (!animate)
        {
            NavPillShift.BeginAnimation(TranslateTransform.XProperty, null);
            NavPillShift.X = target;
            return;
        }

        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 };
        NavPillShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = spring,
                FillBehavior = FillBehavior.HoldEnd,
            });
    }

    /// <summary>按「底当前停在哪」反推该是哪一页，用于拖动后松手吸附。</summary>
    private int NavIndexAt(double x) => Pager.Clamp((int)Math.Round(x / NavItemWidth));

    private void NavShell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (NavPillShift == null || NavShell == null) return;
        _navGrabPoint = e.GetPosition(NavShell);
        _navGrabOffset = _navGrabPoint.X - _navPillX;
        _navDragging = false;

        // v1.33：这里原来无条件 CaptureMouse()，于是底下的 RadioButton 永远收不到完整的
        // 按下/松开序列，Checked 不触发 —— 表现就是「点标签栏没反应」。
        // 现在按下不抢，等真的拖出 4px 才在 MouseMove 里捕获。
    }

    private void NavShell_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (NavShell == null || NavPillShift == null) return;

        // 只有按在导航栏上、并且左键还按着，才算「可能拖」。
        // 没按着就只是在上面划过，直接走人。
        if (e.LeftButton != MouseButtonState.Pressed) return;

        double x = e.GetPosition(NavShell).X - _navGrabOffset;

        // 超过 4px 才算「在拖底」，否则当成普通点击让下面的 RadioButton 自己处理
        if (!_navDragging && Math.Abs(x - _navPillX) < 4) return;

        if (!_navDragging)
        {
            _navDragging = true;
            // 到这里才真正开始拖：抢鼠标，接下来一直到松手都归我们管。
            // 抢早了会把点击吃掉；抢晚了会在拖动中途「跳一下」。
            NavShell.CaptureMouse();
        }

        double max = (Math.Max(0, Pager.Count - 1)) * NavItemWidth;
        NavPillShift.BeginAnimation(TranslateTransform.XProperty, null);
        NavPillShift.X = _navPillX = Math.Clamp(x, 0, max);
    }

    private void NavShell_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (NavShell == null) return;
        bool wasDragging = _navDragging;
        _navDragging = false;
        if (NavShell.IsMouseCaptured) NavShell.ReleaseMouseCapture();
        if (!wasDragging) return;

        // 拖完松手：吸附到最近的一项，并真的切过去。拖了等于点了，这是视频里那一下的关键。
        int want = NavIndexAt(_navPillX);
        MoveNavPill(want, animate: true);
        if (want != _page) ShowPage(want);
        // 拖动已经处置过这次点击了，别再让 RadioButton 补一次，否则会连切两页。
        e.Handled = true;
    }

    private void BtnRunToday_Click(object sender, RoutedEventArgs e) => OpenToday();

    // ===== 滚动显隐导航栏（v1.40）=====
    //
    // 为什么要有：窗口默认高度收到「内容刚好放下」之后，胶囊就会压住最后一行。
    // 与其在每页底部永远留 96px 的死空间，不如让胶囊自己躲开——
    // 往下滚（人正在往下看）就让开，往上滚（人想看别的）就回来。
    //
    // 判据只看「这一下滚动的方向」，不看绝对位置：
    // 在页面中部来回微调滚动条时，绝对位置几乎不变，按方向判才不会闪。
    // 另外「滚到顶部/底部」直接定死状态——那里再滚也不会有新内容，按方向判会卡住。

    private const double NavHideOffset = 74;     // 藏起来要挪多远：58 胶囊 + 14 边距 + 2 呼吸
    private bool _navHidden;

    private void PageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (NavShellShift == null || NavShell == null) return;
        if (Math.Abs(e.VerticalChange) < 0.5) return;     // 只是横向或改尺寸，不算滚动

        bool shouldHide = _navHidden;
        if (e.VerticalOffset <= 0.5)
        {
            shouldHide = false;                            // 顶到头：一定要看得见
        }
        else if (Math.Abs(e.VerticalChange) > 0.5)
        {
            // 往下滚 = 内容在往上走 = 人正在看下面 → 收；往上滚 → 放出来。
            // 阈值 2px：滚轮的每一格都会触发一次，太灵敏会让胶囊发抖。
            if (e.VerticalChange > 2) shouldHide = true;
            else if (e.VerticalChange < -2) shouldHide = false;
        }

        if (shouldHide == _navHidden) return;
        SetNavHidden(shouldHide, animate: true);
    }

    /// <summary>
    /// 把胶囊藏起来或放回去。位移和透明度一起动：
    /// 只位移会留一块模糊的阴影在那儿，只调透明度又会像「凭空消失」。
    /// </summary>
    private void SetNavHidden(bool hidden, bool animate)
    {
        if (NavShellShift == null || NavShell == null) return;
        _navHidden = hidden;
        double target = hidden ? NavHideOffset : 0;
        double opacity = hidden ? 0 : 1;

        if (!animate)
        {
            NavShellShift.BeginAnimation(TranslateTransform.YProperty, null);
            NavShellShift.Y = target;
            NavShell.BeginAnimation(OpacityProperty, null);
            NavShell.Opacity = opacity;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(220);
        NavShellShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(target, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        NavShell.BeginAnimation(OpacityProperty,
            new DoubleAnimation(opacity, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }

    // ===== 设置页卡内滚动（v1.41）=====

    /// <summary>
    /// 卡片自己滚到头之后，把滚轮交回给整页。
    ///
    /// 为什么要这道手工转发：滚轮事件是在冒泡阶段被 ScrollViewer 吃掉的，
    /// 内层吃掉之后外层根本收不到 —— 于是「卡片滚到底，再往下滚整页不动」，
    /// 而外层不动，导航的「下滑收起」也跟着没反应。
    /// 这里只在「内层已经到头」时才接管，其余情况一个字都不做，让卡片先滚自己的。
    /// </summary>
    private void SetCardBody_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer inner || ScrollSettings == null) return;

        bool atTop = inner.VerticalOffset <= 0.5;
        bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
        bool roomLeft = e.Delta > 0 ? !atTop : !atBottom;

        // 卡片自己还滚得动：交给它，别插手。
        if (roomLeft && inner.ScrollableHeight > 1) return;

        e.Handled = true;
        var forward = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = ScrollSettings,
        };
        ScrollSettings.RaiseEvent(forward);
    }

    private void BtnRunLog_Click(object sender, RoutedEventArgs e)
    {
        try { AppState.PublishLog("日志在 " + LogService.Dir); } catch { }
        RevealInExplorer(LogService.Dir);
    }

    private void BtnRunHandover_Click(object sender, RoutedEventArgs e) => OpenHandover();

    /// <summary>
    /// 「更多 ▾」把菜单挂在按钮下沿弹出来。
    /// 不直接点开 MenuItem：WPF 的 ContextMenu 默认跟着鼠标走，
    /// 挂在按钮上按「按钮左下角」定位，人点的是这里、菜单也从这里长出来。
    /// </summary>
    private void BtnRunMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.ContextMenu == null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
    }

    /// <summary>M1-3：不带条件的全量日志——四个下钻入口都用不上时的兜底。</summary>
    private void BtnRunAllLogs_Click(object sender, RoutedEventArgs e) => OpenLogView(LogFilter.None);

    // ===== 日志视图（v1.41 / M1-2）=====
    //
    // 日志不再占底栏一格，改成一层「带着问题进来」的覆盖视图。
    // 四个固定入口，进来时已经把条件筛好：
    //   主页告警卡「看现场」      → { runId: 最近一次不顺的运行 }
    //   工具卡右键「查看日志」    → { toolId }
    //   日报「配置组执行」某一行  → { 组名, date: 当日 }
    //   主页「更多 → 全部日志」   → 不带条件，全量兜底
    // 关掉它就回到进来之前那一页，底栏四项一动不动。

    private LogFilter _logFilter = LogFilter.None;
    private int _logReturnPage = Pager.Home;

    /// <summary>带着条件打开日志视图。条件为空 = 全量。</summary>
    private void OpenLogView(LogFilter filter)
    {
        if (LogLayer == null) return;
        // 记下从哪一页来的：这不是「多一个返回栈」，只是让人关掉之后回到原地。
        if (LogLayer.Visibility != Visibility.Visible) _logReturnPage = _page;
        _logFilter = filter;
        LogLayer.Visibility = Visibility.Visible;
        RefreshLogFilterBar();
        RefreshRunPage();
        RenderLogList();
        LogScroll?.ScrollToVerticalOffset(0);
    }

    /// <summary>
    /// 收起日志视图。<paramref name="keepHistory"/> 只是给调用处表意用——
    /// 视图本身不留任何状态，收起来就是收起来。
    /// </summary>
    private void CloseLogView(bool keepHistory)
    {
        if (LogLayer == null || LogLayer.Visibility != Visibility.Visible) return;
        LogLayer.Visibility = Visibility.Collapsed;
        _logFilter = LogFilter.None;
    }

    private void BtnLogClose_Click(object sender, RoutedEventArgs e) => CloseLogView(keepHistory: true);

    private void RefreshLogFilterBar()
    {
        if (TbLogFilter == null) return;
        string what = _logFilter.Describe(id => ProfileRegistry.Get(id)?.DisplayName);
        TbLogFilter.Text = "正在看：" + what;
    }

    /// <summary>
    /// v1.41 / M1-2：主页告警卡的「看现场」。
    /// 先找出最近一次出事的那一趟（日志里带 [run:xxxxxxxx] 且写了卡住原因的行），
    /// 带着这个 runId 打开日志视图——进来看见的就是出事那一段，不用自己翻。
    /// 找不到就问一句要不要截图：编一个「最近一次」出来比不给更糟。
    /// </summary>
    private void OpenSceneOfLatestTrouble()
    {
        string? runId = null;
        try { runId = LogQuery.LastFailedRunId(DateTime.Now); }
        catch (Exception ex) { LogService.LogEvent("找最近一次异常失败：" + ex.Message); }

        if (!string.IsNullOrWhiteSpace(runId))
        {
            OpenLogView(LogFilter.ForRun(runId!));
            return;
        }

        _ = Task.Run(() =>
        {
            string msg = SnapshotService.CaptureMessage("命令面板");
            NotifyService.Show(NotifyLevel.Info, "龙管家", msg);
        });
    }

    /// <summary>
    /// 把筛出来的日志铺到界面上。每行「时间 + 正文」两列：
    /// 时间用等宽数字对齐，正文允许换行——日志正文里有引号、括号、英文,
    /// 强行走省略号会让人看不到最关键的半句。
    /// </summary>
    private void RenderLogList()
    {
        if (LogListHost == null || TbLogEmpty == null) return;
        LogListHost.Children.Clear();
        TbLogEmpty.Visibility = Visibility.Collapsed;

        List<LogQuery.Entry> entries;
        try
        {
            entries = LogQuery.Read(_logFilter, DateTime.Now,
                id => ProfileRegistry.Get(id)?.DisplayName);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("读日志失败：" + ex.Message);
            entries = new List<LogQuery.Entry>();
        }

        if (TbLogCount != null)
            TbLogCount.Text = entries.Count > 0 ? $"最近 {entries.Count} 条" : "";

        if (entries.Count == 0)
        {
            // 空状态先安抚再给出口（VOICE.md 第三句的路数）：不指挥人「去跑一次」，
            // 只说现在为什么是空的、以及去哪儿让它不空。
            TbLogEmpty.Text = _logFilter.IsEmpty
                ? "今天这里还是空的。跑过一次之后，每一步都会写在这儿。"
                : "这组条件下没有记到东西。可能那一天没跑，也可能这次跑得很干净。";
            TbLogEmpty.Visibility = Visibility.Visible;
            if (TbLogHint != null) TbLogHint.Text = "";
            return;
        }

        if (TbLogHint != null)
            TbLogHint.Text = _logFilter.IsEmpty ? "全量" : "已按上面的条件筛过";

        foreach (var entry in entries)
        {
            LogListHost.Children.Add(LogRow(entry));
        }
    }

    private UIElement LogRow(LogQuery.Entry entry)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var time = new TextBlock
        {
            Text = entry.Time,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = BrushOf("TextTertiaryBrush"),
        };
        // 时间列用等宽数字：否则 08:00 和 11:11 的冒号对不齐，整列看着是歪的。
        time.FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New");
        Grid.SetColumn(time, 0);
        row.Children.Add(time);

        var text = new TextBlock
        {
            Text = entry.Text,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf("TextSecondaryBrush"),
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// 运行页的三句话：刚才怎么样（复用复盘）、这个月省了多少（时间银行）、
    /// 比平时如何（基线）。三句都为空时给一句实话，而不是留三个空框。
    /// </summary>
    private void RefreshRunPage()
    {
        try
        {
            var now = DateTime.Now;
            var input = BuildRecapInput();
            var lines = input == null ? new List<Recap.Line>() : Recap.Build(input).ToList();

            if (lines.Count == 0)
            {
                TbRunState.Text = QueueService.IsRunning ? "正在跑" : "还没有记录";
                TbRunDetail.Text = QueueService.IsRunning
                    ? "跑完会在这里留三句话。"
                    : "今晚跑一次，明天这里就有话可说了。";
            }
            else
            {
                TbRunState.Text = lines[0].Text;
                var rest = lines.Skip(1).Select(l => l.Text).ToList();
                TbRunDetail.Text = string.Join("\n", rest);
            }

            // 决定清单：只贴今天最近的几条，多的去「今日」窗口看。
            RunDecisionHost.Children.Clear();
            var decisions = DecisionLog.Today(now).Take(4).ToList();
            foreach (var d in decisions)
            {
                RunDecisionHost.Children.Add(new TextBlock
                {
                    Text = "· " + DecisionLog.Describe(d),
                    FontSize = 11.5,
                    LineHeight = 18,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = BrushOf("TextSecondaryBrush"),
                });
            }

            var bank = TimeBank.Describe(TimeBank.Sum(
                GroupArchive.DailyMinutes(now.Date.AddDays(-365), now.Date)
                            .Select(d => new TimeBank.Day(d.Date, d.Minutes)), now), now);
            TbRunBank.Text = bank.Length > 0 ? bank : "还没攒够时间，跑几天就有数了。";

            var baseline = BaselineFacts.Today(DateTime.Today);
            TbRunBaseline.Visibility = baseline.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TbRunBaseline.Text = baseline.Text;
            TbRunBaseline.Foreground = baseline.Level == Baseline.Level.Ok
                ? BrushOf("TextSecondaryBrush")
                : BrushOf("WarningBrush");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("运行页刷新失败：" + ex.Message);
        }
    }

    // ===== 汇报页（v1.40）=====
    //
    // 这一页只回答一个问题：「时间去哪了」。
    // 数字全部来自 DurationLedger（每次工具跑完由队列记一笔），
    // 这里只负责排版，不做任何推算——推算出来的时长没人敢信。
    //
    // 和日志页的分工：日志答「成没成、为什么没成」，这一页答「花了多少」。
    // 两页都摆「已经发生的事」，所以挨着放。

    /// <summary>把秒数说成人话。一小时以内按分钟，超过按时分。</summary>
    private static string Minutes(int seconds)
    {
        if (seconds <= 0) return "0";
        if (seconds < 60) return $"{seconds} 秒";
        int totalMinutes = (seconds + 30) / 60;      // 四舍五入到分钟：59 秒不该显示成「0 分钟」
        int h = totalMinutes / 60;
        int m = totalMinutes % 60;
        if (h > 0) return m > 0 ? $"{h} 小时 {m} 分" : $"{h} 小时";
        return $"{m} 分钟";
    }

    /// <summary>
    /// 日报页（v1.41 / M5-8）。
    ///
    /// <para>
    /// 所有数字都来自 <see cref="DailyReport.Build"/> 那一份——IM 晨报读的也是它。
    /// 这里只负责把它摆成卡片：谁排前面、哪句话放哪儿。
    /// 以前这一页自己从 DurationLedger 汇总，跟推送那一路各算各的，
    /// 同一天两边对不上时没人说得清哪个对；现在只有一个源头，就不会再打起来。
    /// </para>
    /// </summary>
    private void RefreshReportPage()
    {
        try
        {
            var now = DateTime.Now;
            var data = DailyReport.Build(now);

            RenderReportHeader(data, now);
            RenderReportAnomalies(data);
            RenderReportGroups(data);
            RenderReportTools(data);
            RenderReportEmptyMerge(data);
            RenderReportBars(now);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("日报页刷新失败：" + ex.Message);
        }
    }

    /// <summary>合计、一句话、以及「较昨日」。空的时候不许说「一切正常」以外的好听话。</summary>
    private void RenderReportHeader(DailyReportData data, DateTime now)
    {
        if (data.IsEmpty)
        {
            TbReportTotal.Text = "还没跑过";
            TbReportSummary.Text = "";
            TbReportTrend.Text = "";
            TbReportHint.Text = QueueService.IsRunning
                ? "这一轮还在跑，跑完一项就会记一笔。"
                : "今晚跑一次，明天这里就有数了。";
            TbReportComparedNote.Text = "";
            return;
        }

        TbReportTotal.Text = Minutes(data.TotalSeconds);
        TbReportSummary.Text = $"{data.ToolCount} 个工具 · 一共 {data.RunCount} 次";

        // v1.41 / M5-5：没有昨天的数就不说这句。硬凑一句「和昨天一样」是在替人打包票。
        string? trend = DailyReport.SayTrend(data.TotalSeconds, data.PreviousTotalSeconds);
        TbReportTrend.Text = trend ?? "";
        TbReportTrend.Visibility = string.IsNullOrEmpty(trend) ? Visibility.Collapsed : Visibility.Visible;

        // 只挑一句最值得说的，不堆排比句。
        var top = data.Tools.Count > 0 ? data.Tools[0] : null;
        TbReportHint.Text = top == null
            ? ""
            : data.Tools.Count == 1
                ? $"{top.Name} 一项就把它跑完了。"
                : $"最费时间的是 {top.Name}，占了 {Minutes(top.Seconds)}。";

        // v1.41 / M5-2：只在真有「较上次」这种东西出现时才解释这一句，
        // 否则页面上会多一行说明在讲一件没发生的事。
        bool anyDelta = data.Groups.Any(g => g.DeltaSeconds != null);
        // v1.41 / M5-2：这句只说清「较上次」是哪一次，别的一句话不多加。
        TbReportComparedNote.Text = anyDelta ? "较上次 = 与该任务上一次运行相比" : "";
        TbReportComparedNote.Visibility = anyDelta ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 「异常与处理」（v1.41 / M5-4）。
    ///
    /// <para>
    /// 没有异常时明确写「没有需要你操心的事」——这句话本身就是信息，
    /// 比留一块空白让人猜「是没出事还是没记到」要好。
    /// 有异常时每行只写「哪个组、卡在哪、管家做了什么」，动作全部来自日志原文。
    /// 行尾那个「看现场」只在能定位到具体某次运行（有 runId）时才挂。
    /// </para>
    /// </summary>
    private void RenderReportAnomalies(DailyReportData data)
    {
        ReportAnomalyHost.Children.Clear();

        if (data.Anomalies.Count == 0)
        {
            TbReportAnomalyHint.Text = "";
            ReportAnomalyHost.Children.Add(new TextBlock
            {
                // 这句是任务书 M5-4 指定的原文，不要改写。
                Text = data.ToolCount > 0
                    ? $"✅ 异常与处理：{data.ToolCount} 个配置组全部顺利，没有需要你操心的事。"
                    : "今天还没有跑过，暂时没什么可说的。",
                FontSize = 11.5,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushOf("TextSecondaryBrush"),
            });
            return;
        }

        TbReportAnomalyHint.Text = $"{data.Anomalies.Count} 条";

        foreach (var a in data.Anomalies)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new TextBlock
            {
                Text = "·",
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = BrushOf("WarningBrush") ?? BrushOf("TextSecondaryBrush"),
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var text = new TextBlock
            {
                Text = $"{a.Group} 在 {a.Stage} 卡住 → 管家已{a.Action}",
                FontSize = 11.5,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushOf("TextSecondaryBrush"),
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (!string.IsNullOrEmpty(a.RunId))
            {
                string runId = a.RunId!;
                var link = new Button
                {
                    Content = "看现场",
                    Style = TryFindResource("LinkButton") as Style,
                    FontSize = 11.5,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = "打开这次运行的日志",
                };
                link.Click += (_, _) => OpenLogView(LogFilter.ForRun(runId));
                Grid.SetColumn(link, 2);
                row.Children.Add(link);
            }

            ReportAnomalyHost.Children.Add(row);
        }
    }

    /// <summary>
    /// 配置组执行（v1.41 / M5-1）。
    ///
    /// <para>
    /// 绝对耗时是普通文字，不带底色；只有差异值是一个彩色 pill。
    /// 两个都上色的话，人会以为「跑得久」也是一种错——它只是慢。
    /// 差额小于 1 秒显示「持平」并留灰，免得秒表的抖动被当成今天变快了。
    /// </para>
    /// </summary>
    private void RenderReportGroups(DailyReportData data)
    {
        ReportGroupHost.Children.Clear();

        if (data.Groups.Count == 0)
        {
            TbReportGroupHint.Text = "";
            ReportGroupHost.Children.Add(new TextBlock
            {
                Text = data.ToolCount == 0
                    ? "还没有数据。"
                    : "今天还没记到配置组。跑的时候 BetterGI 那边没报组名，或者那一组还没结束——只有整组跑完的才算数。",
                FontSize = 11.5,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushOf("TextSecondaryBrush"),
            });
            return;
        }

        TbReportGroupHint.Text = $"{data.Groups.Count} 个组";

        foreach (var g in data.Groups)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = g.Name,
                FontSize = 12,
                Foreground = BrushOf("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                // v1.41 / M1-2：点组名进日志视图，按「这一组 + 今天」筛好。
                ToolTip = "看这一组的日志",
            };
            name.MouseLeftButtonUp += (_, _) => OpenLogView(LogFilter.ForGroup(g.Name, DateTime.Now));
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            // 绝对耗时：普通文本，无底色。数字用等宽字体，纵向才对得齐。
            var secs = new TextBlock
            {
                Text = DailyReport.SayDuration(g.Seconds),
                FontSize = 12,
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("TextPrimaryBrush"),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            };
            Grid.SetColumn(secs, 1);
            row.Children.Add(secs);

            if (g.DeltaSeconds != null)
            {
                string text = DailyReport.SayDelta(g.DeltaSeconds.Value, out bool? faster);
                Brush? pillBg = faster == null
                    ? BrushOf("ControlBgBrush")
                    : faster.Value ? BrushOf("SuccessBrush") : BrushOf("WarningBrush");
                Brush? pillFg = faster == null
                    ? BrushOf("TextTertiaryBrush")
                    : BrushOf("CardBgBrush");

                var pill = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = pillBg,
                    ToolTip = g.ComparedWith == null ? null : $"与 {g.ComparedWith} 那次相比",
                    Child = new TextBlock
                    {
                        Text = text,
                        FontSize = 10.5,
                        Foreground = pillFg ?? BrushOf("TextPrimaryBrush"),
                    },
                };
                Grid.SetColumn(pill, 2);
                row.Children.Add(pill);
            }

            ReportGroupHost.Children.Add(row);
        }
    }

    /// <summary>每个工具一行：名字、占比条、总时长、跑了几次。</summary>
    private void RenderReportTools(DailyReportData data)
    {
        ReportToolHost.Children.Clear();
        if (data.Tools.Count == 0)
        {
            TbReportToolHint.Text = "";
            return;
        }

        TbReportToolHint.Text = data.TotalSeconds > 0 ? $"合计 {Minutes(data.TotalSeconds)}" : "";

        foreach (var e in data.Tools)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = e.Name.Length > 0 ? e.Name : e.ToolId,
                FontSize = 12.5,
                Foreground = BrushOf("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // 占比条：固定 160 宽，只用来横向比较，不随窗口拉伸——
            // 一拉伸它就跟右边那个数字对不齐了。
            double frac = data.TotalSeconds > 0 ? (double)e.Seconds / data.TotalSeconds : 0;
            var trackHost = new Grid
            {
                Width = 160,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            trackHost.Children.Add(new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = BrushOf("ControlBgBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 160,
            });
            trackHost.Children.Add(new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = BrushOf("BrandBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, 160 * frac),
            });
            left.Children.Add(trackHost);

            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            right.Children.Add(new TextBlock
            {
                Text = Minutes(e.Seconds),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = BrushOf("TextPrimaryBrush"),
            });
            right.Children.Add(new TextBlock
            {
                Text = e.Runs > 1 ? $"{e.Runs} 次" : "",
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = BrushOf("TextTertiaryBrush"),
            });

            Grid.SetColumn(right, 1);
            row.Children.Add(right);

            ReportToolHost.Children.Add(row);
        }
    }

    /// <summary>
    /// 空分区合并（v1.41 / M5-3）。
    ///
    /// <para>
    /// 两个以上分区都只剩「暂无」时，合成一句放末尾，别在页面上挂三行一样的空话。
    /// 分区名从数据里取，不写死——以后多一个分区不用回来改这里。
    /// </para>
    /// </summary>
    private void RenderReportEmptyMerge(DailyReportData data)
    {
        if (data.EmptySections.Count < 2)
        {
            TbReportEmptyMerge.Text = "";
            TbReportEmptyMerge.Visibility = Visibility.Collapsed;
            return;
        }

        TbReportEmptyMerge.Text = $"📦 {string.Join(" / ", data.EmptySections)}：今天都没有记录。";
        TbReportEmptyMerge.Visibility = Visibility.Visible;

        // 合成之后，那几块本身就不用再各说一遍「还没有数据」了。
        ReportGroupCard.Visibility = data.Groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReportToolCard.Visibility = data.Tools.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    /// <summary>
    /// 最近 7 天：一天一根柱子，高的那天跑得久。
    /// 用 Border 撑高度而不是画 Chart——这一块只想让人看出「有没有变多」，
    /// 加个坐标系反而要多读好几眼。
    /// </summary>
    private void RenderReportBars(DateTime now)
    {
        ReportBars.Children.Clear();
        var recent = DurationLedger.Recent(7);
        if (recent.Count == 0)
        {
            TbReportBarsHint.Text = "";
            return;
        }

        double max = recent.Max(r => r.Total.TotalSeconds);
        if (max <= 0) max = 1;

        foreach (var (day, total) in recent)
        {
            bool isToday = day.Date == now.Date;
            double seconds = total.TotalSeconds;
            // 最小 3px：0 秒的那天也要留个印子，不然那根柱子整个消失，
            // 看上去像「那天不存在」，而不是「那天没跑」。
            double h = seconds <= 0 ? 3 : Math.Max(3, 48 * seconds / max);

            var col = new StackPanel
            {
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            var bar = new Border
            {
                Width = 22,
                Height = h,
                CornerRadius = new CornerRadius(4, 4, 2, 2),
                Background = seconds > 0
                    ? (isToday ? BrushOf("BrandBrush") : BrushOf("BorderStrongBrush"))
                    : BrushOf("BorderBrush"),
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = $"{day:MM-dd}　{(seconds > 0 ? Minutes((int)Math.Round(seconds)) : "没跑")}",
            };
            col.Children.Add(bar);

            col.Children.Add(new TextBlock
            {
                Text = day.ToString("dd"),   // 只留日，省地方
                FontSize = 9.5,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isToday ? BrushOf("TextPrimaryBrush") : BrushOf("TextTertiaryBrush"),
            });

            ReportBars.Children.Add(col);
        }

        var best = recent.OrderByDescending(r => r.Total.TotalSeconds).First();
        TbReportBarsHint.Text = best.Total.TotalSeconds > 0
            ? $"最近 7 天，{best.Day:MM-dd} 跑得最久（{Minutes((int)Math.Round(best.Total.TotalSeconds))}）。"
            : "最近 7 天都还没跑过。";
    }

    private void BtnReportRefresh_Click(object sender, RoutedEventArgs e) => RefreshReportPage();

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

    // ===== 出门前看一眼（v1.20 / 奇思妙想 #6）=====

    /// <summary>
    /// 收集交接单要的事实。和体检共用同一批数据源，但问的问题不同：
    /// 体检问「现在能不能跑」，交接单问「这几天会怎么跑、哪几件会让这趟白跑」。
    /// </summary>
    private static Handover.Input CollectHandover(int days)
    {
        var s = AppState.Settings;
        var bgi = ProfileRegistry.Get("bettergi");
        bool bgiOk = bgi != null && !string.IsNullOrWhiteSpace(bgi.ExecutablePath)
                     && File.Exists(bgi.ExecutablePath);

        var queue = s.Queue ?? new List<QueueEntry>();
        int enabled = queue.Count(q => q.Enabled);
        int ready = queue.Where(q => q.Enabled)
                         .Select(q => ProfileRegistry.Get(q.ToolId))
                         .Count(p => p != null && ToolRunner.HasExecutable(p));

        int groups = 0;
        try { groups = BgiDiscovery.Scan().Groups.Count; } catch { }

        int restarts = 0;
        try { restarts = SentinelService.HealthSnapshot().RestartCount; } catch { }

        int freeMb = -1;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\");
            freeMb = (int)Math.Min(int.MaxValue, drive.AvailableFreeSpace / (1024 * 1024));
        }
        catch { }

        string next = $"{Math.Clamp(s.ScheduleHour, 0, 23):00}:{Math.Clamp(s.ScheduleMinute, 0, 59):00}";

        return new Handover.Input(
            Days: days,
            ScheduleEnabled: s.ScheduleEnabled,
            AwayStartEnabled: s.AwayStartEnabled,
            NextRunText: next,
            NotifyLocalOnly: Preflight.IsLocalChannel(s.NotifyMode),
            NotifyReady: Preflight.NotifyReady(s.NotifyMode, Preflight.NotifyEndpointOf(s.NotifyMode, s)),
            EnabledTools: enabled,
            ReadyTools: ready,
            AutoRestartBgi: s.AutoRestartBgi,
            RestartsToday: restarts,
            StopWhenFailed: string.Equals(s.FailStrategy, "Abort", StringComparison.OrdinalIgnoreCase),
            AfterRunDestructive: AfterRunActionService.IsDestructive(s.AfterRunAction),
            BgiPathOk: bgiOk,
            GroupCount: groups,
            FreeDiskMb: freeMb);
    }

    private static Handover.Sheet? SafeHandover(int days)
    {
        try { return Handover.Build(CollectHandover(days)); }
        catch (Exception ex)
        {
            LogService.LogEvent("交接单生成失败：" + ex.Message);
            return null;
        }
    }

    private void BtnHandover_Click(object sender, RoutedEventArgs e) => OpenHandover();

    private void BtnTidyConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new TidyWindow { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开收拾设置失败：" + ex.Message);
        }
    }

    private void OpenHandover()
    {
        try
        {
            new HandoverWindow(SafeHandover, CollectHandover) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开交接单失败：" + ex.Message);
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

    /// <summary>
    /// v1.42 / M9-2：写勾选的唯一入口。所有地方都从这里写，事实源就只有一份。
    /// 顺手回写渲染展开区/摘要，省得调用方各记一遍。
    /// </summary>
    private static void WriteGroups(IEnumerable<string> groups)
    {
        var entry = QueueEntryOf("bettergi");
        if (entry == null) return;
        entry.Groups = groups.ToList();
        AppState.SaveSettings();
    }
    private void SyncGroupSelection()
    {
        // v1.42 / M9-2：唯一事实源是 bettergi 那一项的 QueueEntry.Groups，不再是全局勾选。
        var settings = (QueueEntryOf("bettergi")?.Groups ?? new List<string>()).ToArray();
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
            WriteGroups(_selectedOrder);
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

            // v1.42 / M9-2：收敛的也是唯一事实源。
            var selected = (QueueEntryOf("bettergi")?.Groups ?? new List<string>());
            if (selected.Count == 0) return;
            var valid = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = selected.Where(valid.Contains).ToList();
            if (kept.Count == selected.Count) return;
            WriteGroups(kept);
            AppState.PublishLog(kept.Count == 0
                ? "⚠ 原勾选的配置组在 BetterGI 中已不存在，已自动取消勾选"
                : $"⚠ 已移除 {selected.Count - kept.Count} 个在 BetterGI 中已不存在的配置组");
            LogService.LogEvent($"配置组同步：移除失效勾选 {selected.Count - kept.Count} 个");
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

            WriteGroups(_selectedOrder);
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

            double keepOffset = ScrollHome.VerticalOffset;

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
            ScrollHome.ScrollToVerticalOffset(keepOffset);

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
            // v1.22：内容区拆成三页之后，纵向滚动归各页自己的 ScrollViewer 管。
            // 定时卡在中页，所以这里取中页那个；原来只有一个 ContentScroll 时才这么写。
            double keepOffset = ScrollHome.VerticalOffset;

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
            ScrollHome.ScrollToVerticalOffset(keepOffset);

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
        // v1.38：写完之后要听结果。原来这里不看返回值，注册表被拒 / 被安全软件改回去 /
        // 路径没写对，界面上勾子照样亮着，用户只能在第二天早上发现「昨天开了但没自启」。
        var (ok, msg) = AutoStartHelper.Set(on);
        AppState.PublishLog((ok ? (on ? "✓ " : "") : "⚠ ") + msg);
        LogService.LogEvent("开机自启：" + msg);
        if (!ok)
        {
            // 没成功就把开关拨回去——勾子亮着但实际没生效，比没开更糟。
            _suppressToggle = true;
            TgAutoStart.IsChecked = !on;
            _suppressToggle = false;
            NotifyService.Show(NotifyLevel.Warning, "龙管家", msg);
            return;
        }
        // 只在真写成了之后才记下「用户想要自启」这个意图。
        // 写失败还记意图，下次启动就会一直对着一个写不进去的值补写。
        AppState.Settings.AutoStartOnBoot = on;
        AppState.SaveSettings();
    }

    private void TgStayTray_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.StayInTray = TgStayTray.IsChecked == true;
        // v1.35：ResetTrayNote 跟着那条提示一起删了——提示本就不该存在。
        AppState.SaveSettings();
    }

    /// <summary>
    /// v1.38：启动时收进托盘。只在「关闭时驻留托盘」开着时有意义——
    /// 不驻留托盘的话，收进去了就没有托盘图标可以点回来，等于启动即失踪。
    /// </summary>
    private void TgStartMin_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgStartMin.IsChecked == true;
        if (on && !AppState.Settings.StayInTray)
        {
            _suppressToggle = true;
            TgStartMin.IsChecked = false;
            _suppressToggle = false;
            NotifyService.Show(NotifyLevel.Warning, "龙管家",
                "先打开「关闭时驻留托盘」。不然启动就收进托盘，你连托盘图标都没有，点不回来。");
            return;
        }
        AppState.Settings.StartMinimized = on;
        AppState.SaveSettings();
        LogService.LogEvent("启动时收进托盘：" + (on ? "开" : "关"));
    }

    private void TgSound_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        AppState.Settings.SoundEnabled = TgSound.IsChecked == true;
        AppState.SaveSettings();
        if (AppState.Settings.SoundEnabled) SoundService.Play("toggle.wav");
    }

    /// <summary>
    /// v1.21 内存方案 B 的开关。默认关——实测省钱只有 3.7 MB，代价是每次重开多等 1 秒多。
    /// 打开时把实测数字再说一遍，避免有人以为开了会更「轻快」。
    /// </summary>
    private void TgTrimToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgTrimToTray.IsChecked == true;
        AppState.Settings.TrimToTrayEnabled = on;
        AppState.SaveSettings();
        TbTrimHint.Text = on
            ? "收进托盘后会销毁主窗，省 3.7 MB；再打开要多等 1 秒多。队列在跑时不销毁。"
            : "实测只省 3.7 MB，但再打开一次要多等 1 秒多。想腾内存再开。";
        LogService.LogEvent("收进托盘释放窗口：" + (on ? "开" : "关"));
    }

    // v1.38：TgVacation_Click 删了。度假模式只有一个作用——让定时不跑，
    // 而定时本身就有开关。两个开关做同一件事，只会让人在「到底哪个在拦我」上耗时间。

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
            // v1.41 / M2-12：还没走完向导的人，状态栏不该挂一个「近期有告警」——
            // 那玩意儿是他装之前的事，写了也帮不上，只让人以为自己错过了什么。
            bool showAlert = alerted && AppState.Settings.OnboardingCompleted;
            bool escort = QueueService.IsRunning && KeepAwakeService.IsActive;

            ShieldDot.Fill = escort ? DotYellow : !enabled ? DotGray : showAlert ? DotYellow : DotGreen;
            ShieldText.Text = escort ? "护航中" : !enabled ? "守护已关闭"
                : showAlert ? "守护中 · 近期有告警" : "守护中";

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
            // v1.41 / M2-12：同理，没走完五步的人也不该看到它——他今天才装，
            // 「刚才」发生过的只可能是向导里那次自动寻找没找到，那不是事故。
            bool trouble = AppState.Settings.OnboardingCompleted
                           && s.ShieldAlertAt.HasValue
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

            // v1.37：首次运行（一个工具都没配好）时，「一键开跑」点了必然失败。
            // 首屏上原本并排三个绿色主按钮（选择 exe / 打开定时 / 一键开跑），
            // 后两个在此时都是噪音——只留主卡那一个，剩下的说明「配好之后这里能用」。
            // 不做 Collapsed：整卡消失会让下面布局跳一下，而且用户也就看不到它在那儿等着。
            bool firstRun = view.Phase == HomePhase.FirstRun;
            BtnQueueStart.IsEnabled = !firstRun;
            // v1.37：压暗到 0.72 而不是任务书最初写的 0.55——0.55 之下「一键开跑」四个字
            // 叠在灰底上基本读不出来，用户会以为那里是一条空白。压暗的目的是「往后退」，
            // 不是「看不见」：按钮本身已经换成灰底灰字，卡再压一层只是把它推远一点。
            QueueCard.Opacity = firstRun ? 0.72 : 1.0;
            TbGridHint.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;

            // 「每个工具怎么跑」整块也是同一件事：首次运行时它只会显示一行
            // 「还没配，点开填一下」的橙色小字，而卡已经整体压暗了，那行字不再提供信息。
            ToolSetupDivider.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
            ToolSetupBlock.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;

            RenderHomeSteps(view.ShowSteps);
            RefreshHomeFirstVisit();
            RefreshHomeRecap();

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

    /// <summary>
    /// v1.20 复盘：主卡下方那三两句。
    /// 只在「昨天/今天已经跑完」时出现——正在跑的时候主卡已经说了跑到哪，
    /// 再贴一段复盘只会让人以为跑完了。
    /// </summary>
    private void RefreshHomeRecap()
    {
        try
        {
            HomeRecap.Children.Clear();

            var input = BuildRecapInput();
            // v1.41 / M2-12：没走完五步的人不该看到「昨晚没跑」——
            // 他今天才装，昨天当然没跑。那句话在首屏读起来像指责。
            bool show = input != null && !QueueService.IsRunning && AppState.Settings.OnboardingCompleted;
            HomeRecap.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show || input == null) return;

            var lines = Recap.Build(input);
            if (lines.Count == 0)
            {
                HomeRecap.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var line in lines)
            {
                HomeRecap.Children.Add(RecapLine(line));
            }
        }
        catch (Exception ex)
        {
            HomeRecap.Visibility = Visibility.Collapsed;
            LogService.LogEvent("复盘生成失败：" + ex.Message);
        }
    }

    /// <summary>把五张表读成 <see cref="Recap.Input"/>。读失败就是 null，界面按没有处理。</summary>
    private static Recap.Input? BuildRecapInput()
    {
        try
        {
            var s = AppState.Settings;
            var today = DateTime.Today;

            var groups = GroupArchive.GetDay(today)
                .Select(g => new Recap.GroupRow(
                    g.Name,
                    GroupArchive.DurationOf(g),
                    g.End == null,
                    GroupArchive.MedianMinutes(g.Name)))
                .ToList();

            var entries = RunJournal.GetDay(today);
            var tools = entries
                .Select(e => new Recap.ToolRow(e.Name, e.State, e.Reason))
                .ToList();

            var harvest = HarvestLedger.GetDay(today);
            int done = QueueService.DoneToolIdsToday().Count;
            int total = done + QueueService.UnfinishedToday().Count;

            // 起止时刻：**不能从台账的 At 推**。台账是一条覆盖式的「今天这项最后什么结果」，
            // 每项都在记录那一刻写入，几项挤在同一秒，据此算出的时长永远是「不到 1 分钟」。
            // 真正可靠的只有 Settings 里那对：LastRunAt 是这一轮的开始，LastRunSeconds 是它跑了多久。
            DateTime? startAt = ParseLastRunAt(s);
            DateTime? endAt = startAt.HasValue && s.LastRunSeconds > 0
                ? startAt.Value.AddSeconds(s.LastRunSeconds)
                : null;

            return new Recap.Input(
                startAt,
                endAt,
                done,
                total,
                groups,
                tools,
                harvest.Mora,
                harvest.Exp,
                harvest.Materials,
                RunEvidence.LastErrorLine,
                StoppedByUser: false,
                RanNothing: total == 0 && groups.Count == 0 && tools.Count == 0);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("复盘取数失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>一条复盘。Gap 用 Warning 色，其余用次要色——一致性比好看重要。</summary>
    private UIElement RecapLine(Recap.Line line)
    {
        var tb = new TextBlock
        {
            Text = line.Text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, line.Kind == Recap.LineKind.Lead ? 0 : 5, 0, 0),
            Foreground = line.Kind switch
            {
                Recap.LineKind.Lead => BrushOf("TextPrimaryBrush") ?? DotGreen,
                Recap.LineKind.Gap => BrushOf("WarningBrush") ?? DotYellow,
                _ => BrushOf("TextSecondaryBrush") ?? DotGray,
            },
            FontWeight = line.Kind == Recap.LineKind.Lead ? FontWeights.SemiBold : FontWeights.Normal,
        };
        return tb;
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

    /// <summary>
    /// v1.41 / M3-1：首访清单。
    ///
    /// <para>
    /// 只在没走完五步时出现，替代原来的「昨晚没跑」复盘和告警描边——
    /// 那两样对一个今天才装的人来说都是错的：他昨天没跑是因为昨天还没装。
    /// </para>
    /// <para>
    /// 三行都带完成状态：装完路径回来，第一行自己会打勾。不给勾的话，
    /// 用户只能靠「我记得我设过了」来判断，而这种判断十次有三次是错的。
    /// </para>
    /// </summary>
    private void RefreshHomeFirstVisit()
    {
        try
        {
            HomeFirstVisit.Children.Clear();

            bool onboarded = AppState.Settings.OnboardingCompleted;
            HomeFirstVisit.Visibility = onboarded ? Visibility.Collapsed : Visibility.Visible;
            if (onboarded) return;

            var s = AppState.Settings;

            bool pathDone = false;
            try { pathDone = ToolRunner.HasExecutable(ProfileRegistry.Get("bettergi")); } catch { }
            bool timeDone = s.ScheduleEnabled;
            bool ranDone = !string.IsNullOrEmpty(s.LastRunAt);

            HomeFirstVisit.Children.Add(new TextBlock
            {
                Text = "还没跑过——三步就能开始",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("TextPrimaryBrush") ?? DotGray,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
            });

            HomeFirstVisit.Children.Add(FirstVisitRow("①", "选 BetterGI 在哪里", pathDone,
                () => PickBetterGi()));
            HomeFirstVisit.Children.Add(FirstVisitRow("②", "设个每天几点跑", timeDone,
                () => OpenWizardAtStep(3)));
            HomeFirstVisit.Children.Add(FirstVisitRow("③", "点一下「一键开跑」，先看它动起来", ranDone,
                () => RevealCard(BtnQueueStart)));
        }
        catch (Exception ex)
        {
            HomeFirstVisit.Visibility = Visibility.Collapsed;
            LogService.LogEvent("首访清单刷新失败：" + ex.Message);
        }
    }

    /// <summary>首访清单的一行：序号 + 文案 + 勾。整行可点，点了就跳到能完成它的地方。</summary>
    private UIElement FirstVisitRow(string no, string text, bool done, Action jump)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 6, 0, 0),
            Background = Brushes.Transparent,   // 透明底也要有，否则空白处点不中
            Cursor = Cursors.Hand,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = no,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = done
                ? (BrushOf("TextTertiaryBrush") ?? DotGray)
                : (BrushOf("BrandTextBrush") ?? DotGreen),
        });

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = done
                ? (BrushOf("TextTertiaryBrush") ?? DotGray)
                : (BrushOf("TextPrimaryBrush") ?? DotGray),
            TextDecorations = done ? TextDecorations.Strikethrough : null,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        if (done)
        {
            var tick = new TextBlock
            {
                Text = "✓",
                FontSize = 12.5,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("SuccessBrush") ?? DotGreen,
            };
            Grid.SetColumn(tick, 2);
            row.Children.Add(tick);
        }
        else
        {
            var go = new TextBlock
            {
                Text = "去设置",
                FontSize = 11.5,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("BrandTextBrush") ?? DotGreen,
            };
            Grid.SetColumn(go, 2);
            row.Children.Add(go);
        }

        row.MouseLeftButtonUp += (_, _) =>
        {
            try { jump(); }
            catch (Exception ex) { LogService.LogEvent("首访清单跳转失败：" + ex.Message); }
        };
        return row;
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
            ChipOff.IsChecked = ch == PushChannel.Off;
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
            bool local = ch is PushChannel.Off or PushChannel.Toast or PushChannel.Balloon;
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
                PushChannel.Off => "什么都不弹。跑完的结果留在界面的运行页和日志里，手机上也收不到——挂机时别选这个",
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
            TbLocalHint.Text = ch switch
            {
                PushChannel.Off => "现在是「不推送」。想被提醒就选上面任意一个渠道；只想不影响别的程序，选「托盘气泡」。",
                PushChannel.Toast => "本机通知：不需要任何配置，发送测试即可验证系统通知是否正常。",
                _ => "本机提示：只弹托盘气泡，不写入操作中心。",
            };

            bool configured = NotifyChannelConfigured(ch);
            TbNotifyChannelState.Text = ch == PushChannel.Off ? "关" : local ? "本机" : configured ? "已配置" : "待配置";
            TbNotifyChannelState.Foreground = configured || ch == PushChannel.Off
                ? (BrushOf("SuccessBrush") ?? DotGray)
                : (BrushOf("TextTertiaryBrush") ?? DotGray);
            BtnTestWebhook.Content = ch == PushChannel.Off ? "发送测试消息" : local ? "发送测试通知" : "发送测试消息";
            // 「不推送」时测试按钮没有意义：发出去也不会有人收到。
            // 禁用比让它点了弹一句失败更诚实。
            BtnTestWebhook.IsEnabled = ch != PushChannel.Off;
            BtnTestWebhook.ToolTip = ch == PushChannel.Off ? "现在是「不推送」，先在上面选一个渠道再测。" : null;
            TbNotifyTestResult.Text = "";
        }
        catch { }
    }

    private bool NotifyChannelConfigured(PushChannel ch)
    {
        var s = AppState.Settings;
        return ch switch
        {
            PushChannel.Off => true,
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

    // ===== 插件卡（v1.29）=====

    /// <summary>
    /// 扫 plugins 目录，把「读得懂的」和「读不懂的」分开列出来。
    /// 读不懂的也要显示——否则用户放进去一个包、界面上什么都没变，
    /// 他只会以为这个功能坏了，而不是「这个包少写了名字」。
    /// </summary>
    private void RefreshPlugins()
    {
        try
        {
            PluginsHost.Children.Clear();
            var found = PluginLoader.Scan();
            var okList = found.Where(f => f.Refuse == null && f.HasAssembly).ToList();
            var badList = found.Where(f => f.Refuse != null || !f.HasAssembly).ToList();

            TbPluginsSummary.Text = found.Count == 0
                ? "还没装"
                : okList.Count > 0
                    ? $"已装 {okList.Count} 个"
                    : "有目录，但都读不懂";

            foreach (var f in okList)
            {
                string name = f.Meta?.Name ?? f.DirName;
                string ver = f.Meta?.Version ?? "";
                string needs = f.Meta?.Needs ?? "";
                var card = new Border
                {
                    Background = BrushOf("CardBg2Brush") ?? DotGray,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var panel = new StackPanel();
                var head = new Grid();
                head.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(ver) ? name : $"{name} {ver}",
                    FontSize = 12.5,
                    Foreground = BrushOf("TextPrimaryBrush") ?? DotGray,
                });
                head.Children.Add(new TextBlock
                {
                    Text = "已就绪",
                    FontSize = 10.5,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = BrushOf("BrandTextBrush") ?? DotGray,
                });
                panel.Children.Add(head);
                if (!string.IsNullOrWhiteSpace(needs))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = needs,
                        FontSize = 10.5,
                        Margin = new Thickness(0, 3, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
                    });
                }
                var hosts = f.Meta?.Hosts ?? new List<string>();
                if (hosts.Count > 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "会访问：" + string.Join("、", hosts),
                        FontSize = 10,
                        Margin = new Thickness(0, 3, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
                    });
                }
                card.Child = panel;
                PluginsHost.Children.Add(card);
            }

            foreach (var f in badList)
            {
                string why = f.Refuse ?? "目录里没找到入口程序集（默认 Plugin.dll）。";
                var card = new Border
                {
                    Background = BrushOf("CardBg2Brush") ?? DotGray,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = f.DirName + "（没加载）",
                    FontSize = 12.5,
                    Foreground = BrushOf("TextSecondaryBrush") ?? DotGray,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = why,
                    FontSize = 10.5,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = BrushOf("WarningBrush") ?? DotGray,
                });
                card.Child = panel;
                PluginsHost.Children.Add(card);
            }

            if (found.Count == 0)
            {
                PluginsHost.Children.Add(new TextBlock
                {
                    Text = "现在一个插件都没有。这不影响任何功能——管家自己就能跑完一套。",
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = BrushOf("TextTertiaryBrush") ?? DotGray,
                });
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新插件列表失败：" + ex.Message);
        }
    }

    private void BtnPluginsOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = PluginLoader.Root;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppDialog.Show("打不开插件目录，详情见日志。", detail: ex.Message,
                kind: AppDialog.Kind.Warning, title: "插件", primary: "知道了", owner: this);
        }
    }

    // ===== 米游社签到：v1.34 起搬出本体 =====
    //
    // 为什么删：米游社这段时间连着加强校验，扫码拿到的凭证经常当天就失效，
    // 留着它等于在「设置页最上面」摆一张永远登不上的卡——那不是功能，那是一个每天都要看一遍的失败。
    // 原来那套源码（EdgeCdp / MiyousheQr / MiyousheBbs / QrCode / QrBitmap / 登录窗）整份留在
    // docs/plugin-samples/miyoushe/ 下，谁想接着搓可以拿去做成插件。
    // 本体只保留一句话：这里不碰米游社。

    // ===== 折叠卡通用 =====

    private void FoldHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        FrameworkElement? body = btn.Name switch
        {
            "BtnFoldNotify" => NotifyBody,
            _ => null,
        };
        if (body == null) return;
        body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===== 主题（v1.33 起是玻璃开关；v1.34 收成深/浅两档，见上面的「两段式主题开关」段）=====

    private void OpenCustomThemeEditor()
    {
        try
        {
            var dlg = new CustomThemeWindow { Owner = this };
            bool ok = dlg.ShowDialog() == true;
            SyncThemeSeg();
            RefreshAppearanceSummary();
            if (ok) { RenderQueue(); RefreshShield(); }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开自定义主题失败：" + ex.Message);
            SyncThemeSeg();
        }
    }

    // ===== 外观卡（v1.33）=====

    private void BtnCustomTheme_Click(object sender, RoutedEventArgs e) => OpenCustomThemeEditor();

    /// <summary>
    /// 一句话说清现在的配色是怎么来的。
    /// 主题开关只切「深/浅/跟随系统」三段，自定义是第四种状态——它不在那三段里，
    /// 所以这里要主动说「你现在用的是自定义」，否则开关停在「跟随系统」上、
    /// 界面却是一张图，人会以为开关坏了。
    /// </summary>
    private void RefreshAppearanceSummary()
    {
        if (TbAppearanceSummary == null) return;
        try
        {
            var s = AppState.Settings;
            string scheme = s.CustomScheme switch
            {
                "Light" => "浅色控件",
                "Dark" => "深色控件",
                _ => "跟随配色自动选控件深浅",
            };
            string body = s.CustomBgKind == "Image" && !string.IsNullOrWhiteSpace(s.CustomBgImage)
                ? $"用一张图当底（叠色 {s.CustomOverlay}%），{scheme}。"
                : $"用 {s.CustomBgColor} 当底，{scheme}。";
            TbAppearanceSummary.Text = Theme.Mode == "Custom"
                ? "现在用的就是自定义配色：" + body
                : "深/浅/跟随系统之外，还可以自己挑底色或放一张图。" + body;
        }
        catch { }
    }

    // ===== 首启向导 =====

    private void TryShowFirstRunWizard()
    {
        try
        {
            if (AppState.Settings.FirstRunDone) return;
            ShowWizard(0);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("首启向导失败：" + ex.Message);
            AppState.Settings.FirstRunDone = true;
            AppState.SaveSettings();
        }
    }

    /// <summary>
    /// v1.41 / M3-1：从首访清单的第二行直接跳进向导的第 3 步。
    /// 走同一个窗口、同一套保存逻辑——主页那张清单只是入口，不是第二套向导。
    /// </summary>
    private void OpenWizardAtStep(int step)
    {
        try
        {
            ShowWizard(step);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开向导失败：" + ex.Message);
        }
    }

    /// <summary>打开向导。<paramref name="startStep"/> 为 0 表示从第一步开始。</summary>
    private void ShowWizard(int startStep)
    {
        var wiz = new FirstRunWindow { Owner = this };
        if (startStep > 0) wiz.JumpToStep(startStep);
        wiz.ShowDialog();
        AppState.Settings.FirstRunDone = true;
        AppState.SaveSettings();

        // v1.41 / M2-4：向导最后一步的「完成并试跑一次」不是一句客套——
        // 它真的会把选中的第一个工具跑起来，跑完在这一页就能看到结果。
        // 只用 StartSingleAsync 走和「一键开跑」同一条路径：另写一条执行链，
        // 迟早会和队列里的行为对不上（一个会做前置检查，一个不会）。
        if (wiz.TrialRequested)
        {
            var first = AppState.Settings.Queue?
                .FirstOrDefault(q => q.Enabled && !string.IsNullOrWhiteSpace(ProfileRegistry.Get(q.ToolId).ExecutablePath));
            if (first != null)
            {
                try
                {
                    var profile = ProfileRegistry.Get(first.ToolId);
                    AppState.PublishLog($"✓ 试跑一次：{profile.DisplayName}");
                    _ = QueueService.StartSingleAsync(profile, QueueTrigger.Manual);
                }
                catch (Exception ex)
                {
                    AppState.PublishLog("⚠ 试跑没起来：" + ex.Message);
                }
            }
            else
            {
                AppState.PublishLog("还没有配置好路径的工具，试跑先跳过——在工具卡里设好后随时能跑。");
            }
        }
        // v1.39：向导改的是设置，而设置页的开关是构造时就按**旧值**摆好的
        // （LoadSettingsToUi 在构造函数里跑，向导要等 Loaded 之后才弹）。
        // 不在这儿重读一遍，用户就会看到「向导里勾了开机自启，设置里还是关着」——
        // 值其实已经写进注册表和 settings.json，只是界面停在几分钟前。
        LoadSettingsToUi();
        RenderQueue();
        RefreshToolCards();
        RefreshDetectedGroups(force: true);
        // v1.41 / M3-1：清单的勾选状态要跟着变——用户刚设完时间，第二行该打勾了。
        RefreshHomeHero();
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
        // v1.41 / M6-1：先记位置再谈别的——下面那条 return 会让「不驻留托盘」的人
        // 直接关掉窗口，位置得在那之前存下来。
        SaveWindowBounds();

        if (App.ForceExit || !AppState.Settings.StayInTray) return;
        e.Cancel = true;
        Hide();
        // v1.35：不弹了。
        //
        // 这条以前是「关窗口时告诉你还在后台」，v1.28 给它加过闸（选了不推送就不弹、
        // 一台机器只说一次），但闸的判据是「通知渠道 != 不推送」——而「关窗口」跟
        // 「跑完了要不要通知我」根本是两件事。选了 Windows 通知的人，在意的是跑完的
        // 结果；不是每次关窗都被系统弹一次「还在后台」。
        //
        // 真正的判据只有一条：这一刻用户就坐在屏幕前，看着窗户消失。他知道自己关了什么。
        // 托盘图标还在，双击能开回来——不需要谁告诉他。

        // v1.21 / 内存方案 B：收进托盘就把整窗销毁，把「已经画到屏幕上」的那 28 MB 还回去。
        // 挂在托盘图标上的只有一项设置，重建一次约 1 秒——用 1 秒换常驻少 28 MB，
        // 对一个整晚都在后台待着的程序是划算的。
        // 但要先确认用户真的不要再打开它了：这里加一个很短的延迟，
        // 期间如果又收到显示请求（双击托盘、浮条双击、命令面板），就放弃这次销毁。
        RequestTrimToTray();
    }

    /// <summary>
    /// v1.21 / 内存方案 B 的一半：先把所有定时器停掉，再让 App 销毁本窗。
    ///
    /// 顺序不能反。这五个 <see cref="DispatcherTimer"/> 的 Tick 是闭包，
    /// 闭包里抓着 this——只要还有一个在跑，WeakReference 就永远不会变 null，
    /// 关掉窗口也回收不掉（实测：先 Close 再置 null 仍存活，先停再 Close 才回收）。
    /// </summary>
    internal void PrepareForTrim()
    {
        try { _scheduleTimer.Stop(); } catch { }
        try { _countdownTimer.Stop(); } catch { }
        try { _groupScanTimer.Stop(); } catch { }
        try { _uiRefreshTimer.Stop(); } catch { }
        try { _statusBarTimer.Stop(); } catch { }
        try { _hotkeys?.Dispose(); } catch { }
        try { _miniBar?.Close(); } catch { }
        _miniBar = null;
    }

    /// <summary>请求把主窗收进托盘（真正做决策的是 App：它知道现在有没有队列在跑）。</summary>
    private void RequestTrimToTray()
    {
        try { App.Instance?.TrimToTray(); }
        catch (Exception ex) { LogService.LogEvent("收进托盘失败：" + ex.Message); }
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
                // v1.35：高级设置与系统不再是折叠卡，内容直接摊在「守护与恢复 / 节奏与报告」两张卡里。
                // 这条命令还留着，落到那张卡的第一行上，位置一样是「设置页往下一点的地方」。
                case "view.advanced": RevealCard(TgSentinel); break;
                // v1.41：日志不再是一页。这条命令现在直接开日志视图（全量）。
                case "view.log": OpenLogView(LogFilter.None); break;
                case "view.today": OpenToday(); break;
                case "view.logrules": OpenLogRules(); break;
                // v1.42 / M8-3：view.calendar / view.depends / view.groups 三条命令随四个视图窗口的入口一起撤掉。
                case "view.preflight": OpenPreflight(); break;
                case "view.handover": OpenHandover(); break;
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
                    // v1.41 / M1-2：「看现场」先带人去看那一次的日志。
                    // 以前它只会截一张图——图能证明「当时屏幕上长什么样」，
                    // 但「到底哪一步卡住」只有日志说得清。翻不到运行标记时才退回截图。
                    OpenSceneOfLatestTrouble();
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
            // v1.22：内容分了三页，先判断目标在哪个页里，不然滚了也看不见。
            // 判断顺序从里到外：先进页，再展开折叠卡，最后才滚。
            // v1.41：日志那两页搬进了覆盖层，落点改成「带着条件打开日志视图」。
            if (PageRun != null && (PageRun == target || PageRun.IsAncestorOf(target)))
            {
                OpenLogView(LogFilter.None);
                target.BringIntoView();
                return;
            }
            if (PageReport != null && (PageReport == target || PageReport.IsAncestorOf(target)))
            {
                ShowPage(Pager.Report);
                target.BringIntoView();
                return;
            }
            if (PageSettings != null && (PageSettings == target || PageSettings.IsAncestorOf(target)))
            {
                ShowPage(Pager.Settings);
                // v1.41 / M4-1：设置分了五类，得先把目标所在的那一类翻开——
                // 否则定位到的是一个还收着的小门后面，滚到底也看不见。
                SettingsPane pane = PaneOfSettingsTarget(target);
                if (pane != SettingsPane.None) ShowSettingsPane(pane);
            }

            // 折叠卡里的元素是 Collapsed 的，先展开父级（只往上找一层已知的折叠体）
            // v1.35：只剩通知还是折叠的。别的设置项现在直接是卡片里的一行，本来就在。
            foreach (var body in new FrameworkElement?[] { NotifyBody })
            {
                if (body == null) continue;
                if (body == target || body.IsAncestorOf(target))
                    body.Visibility = Visibility.Visible;
            }
            target.BringIntoView();
            ScrollSettings.ScrollToVerticalOffset(ScrollSettings.VerticalOffset);
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
