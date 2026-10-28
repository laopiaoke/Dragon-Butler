using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>哨兵卡列表行数据</summary>
public sealed class GroupRow
{
    public string Icon { get; set; } = "";
    public Brush IconColor { get; set; } = Brushes.Gray;
    public string Name { get; set; } = "";
    public string Time { get; set; } = "";
}

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

    /// <summary>执行序号（从 1 开始）；0 = 未勾选</summary>
    public int Order
    {
        get => _order;
        set
        {
            if (_order == value) return;
            _order = value;
            Raise(nameof(Order));
            Raise(nameof(OrderText));
            Raise(nameof(OrderVisibility));
            Raise(nameof(OrderEmptyVisibility));
        }
    }

    public string OrderText => _order > 0 ? _order.ToString() : "";
    public Visibility OrderVisibility => _order > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OrderEmptyVisibility => _order > 0 ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>守护卡"最近事件"行</summary>
public sealed class EventRow
{
    public string Time { get; set; } = "";
    public string Text { get; set; } = "";
    public Brush DotColor { get; set; } = Brushes.Gray;
}

/// <summary>
/// 主界面（SPBGI）。行为要点见任务书 7.9 / v0.2 任务书一：
/// - 大按钮四态状态机（未就绪灰 → 就绪绿 → 运行中红 → 处理中灰），统一走 RefreshMainButton()
/// - 就绪评估 300ms 防抖（打字不闪烁）；果冻按压/回弹/横向抖动均为代码驱动短动画
/// - Loaded 里 RefreshStatus（修复"检测中…"历史 bug）
/// - 30 秒定时器分钟级比对 CheckSchedule，当天只跑一次、错过不补
/// - OnClosing：StayInTray 则隐藏驻留托盘，托盘退出走 ForceExit
/// </summary>
public partial class MainWindow : Window
{
    // 状态点 / 图标颜色（黄色为主题外补充色）
    // 静态字段会在"第一个碰到 MainWindow 的线程"上初始化；若该线程不是 UI 线程
    // （例如自动化测试提前读取静态属性），未冻结的 Freezable 挂到 UI 树上会抛
    // "Freezable 属于不同线程" —— 静态画刷必须 Freeze 后共用。
    private static readonly Brush DotGray = Frozen(Color.FromRgb(0x6E, 0x70, 0x78));
    private static readonly Brush DotYellow = Frozen(Color.FromRgb(0xE0, 0xA3, 0x2E));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private bool _suppressToggle;
    private bool _busy;
    private bool _busyStopping;
    private bool _pressing;                 // 大按钮果冻按压（压扁）中
    private DateTime _lastScheduleRun = DateTime.MinValue;

    private readonly DispatcherTimer _scheduleTimer = new();
    private readonly DispatcherTimer _statusBarTimer = new();
    private readonly DispatcherTimer _bannerTimer = new();
    // 就绪评估防抖：连续输入只认最后一次，避免打字过程中按钮颜色闪烁
    private readonly DispatcherTimer _readyDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly ObservableCollection<GroupRow> _rows = new();
    private readonly ObservableCollection<GroupPickItem> _groupPicks = new();
    private readonly ObservableCollection<EventRow> _events = new();
    private readonly DispatcherTimer _groupScanTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _pathCopiedTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private string _groupSignature = "\u0000";
    private readonly List<string> _selectedOrder = new();   // 勾选顺序 = 执行顺序（内存唯一真源）
    private EcgMeter? _ecg;                                 // 拟真心电图（状态由业务驱动）
    private long _ecgHoldUntil;                             // Error/Ending 的展示保持期（毫秒时间戳）
    private bool _suppressNotifyChips;

    public MainWindow()
    {
        InitializeComponent();
        SentinelList.ItemsSource = _rows;
        SentinelEvents.ItemsSource = _events;
        GroupPickList.ItemsSource = _groupPicks;

        BgiService.OnStatusChanged += BgiService_OnStatusChanged;
        BgiService.OnLog += BgiService_OnLog;
        SentinelService.OnEvent += SentinelService_OnEvent;
        _readyDebounce.Tick += ReadyDebounce_Tick;
        Theme.Changed += OnAppThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnAppThemeChanged;
        _runTick.Tick += RunTick_Tick;
        _countdownTimer.Tick += (_, _) => { UpdateScheduleStatus(); RefreshSentinelLive(); SyncEcgState(); };
        _groupScanTimer.Tick += (_, _) => RefreshDetectedGroups(force: true);
        _pathCopiedTimer.Tick += (_, _) => { _pathCopiedTimer.Stop(); TbPathCopied.Visibility = Visibility.Collapsed; };
        _ecg = new EcgMeter(HeartCanvas);
        InitGuardStrip();

        // 全局按键音：所有可交互元素在"按下"这一瞬出声（不等 Click 抬起），
        // 由 SoundService.FindPressTarget 判定目标与音色，避免每个处理器各自绑一次导致重复/漏绑。
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(AnyInteractivePress), true);

        LoadSettingsToUi();

        Loaded += (_, _) =>
        {
            BgiService.RefreshStatus(); // 修复"检测中…"的历史 bug，必须保留
            RefreshSentinelCard();
            SyncEcgState();
        };

        // 启动定位：SizeToContent=Height 时窗口高度要等布局完成才确定，
        // 因此把居中放到 Loaded 的下一帧（Render 优先级），而不是构造函数里。
        Loaded += (_, _) =>
        {
            if (SuppressAutoCenter) return;
            Dispatcher.BeginInvoke(new Action(CenterOnActiveMonitor), DispatcherPriority.Render);
        };

        _scheduleTimer.Interval = TimeSpan.FromSeconds(30);
        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();
        _countdownTimer.Start();
        _groupScanTimer.Start();

        // 窗口高度随内容自适应（SizeToContent=Height）→ 不超过工作区，超出时内部滚动
        try { MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 24); } catch { }
    }

    /// <summary>主题切换：心跳折线与守护卡颜色需要重建（其余走动态资源）</summary>
    private void OnAppThemeChanged()
    {
        try
        {
            _ecg?.RefreshThemeColors();
            _guardVisualAbnormal = null;   // 异常底色/文字色按新主题重算
            RefreshSentinelCard();
            RefreshMainButton();
            ApplyNotifyMode();
        }
        catch { }
    }

    // ===== 全局按键音：按下即响（不等 Click 抬起）=====

    /// <summary>
    /// 一次按下只出一声：按钮/开关/勾选/分段按类型选音色，手型可点区域统一 click；
    /// 主按钮未就绪时改播"拒绝"音（点击仍会抖动提示），避免"咔"一声却什么都没发生。
    /// </summary>
    private void AnyInteractivePress(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var target = SoundService.FindPressTarget(e.OriginalSource as DependencyObject);
            if (target == null) return;

            // 未就绪的主按钮：播"拒绝"音（点击仍会抖动提示），避免"咔"一声却什么都没发生
            if (target is ButtonBase btn && ReferenceEquals(btn, BigBtn) &&
                !_busy && !BgiService.IsRunning && !MainButtonInteractive())
            {
                SoundService.Play("cancel.wav");
                return;
            }

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
            if (res != null && res.Contains(key) && res[key] is Brush b)
                return b;
        }
        catch { }
        return null;
    }

    /// <summary>回填分段控件选中态时防 Checked 递归</summary>
    private bool _suppressThemeSeg;

    /// <summary>把当前主题模式回填到四段式分段控件</summary>
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

    // ===== 初始化填充 =====

    private void LoadSettingsToUi()
    {
        var s = BgiService.Settings;
        RefreshPathDisplay();
        TbConfigName.Text = s.DragonConfigName;
        LoadNotifyFields();

        _suppressToggle = true;
        TgSchedule.IsChecked = s.ScheduleEnabled;
        TgAutoStart.IsChecked = AutoStartHelper.IsSet();
        TgAutoRun.IsChecked = s.AutoRunOnLaunch;
        TgStayTray.IsChecked = s.StayInTray;
        TgSentinel.IsChecked = s.SentinelEnabled;
        TgAutoRestart.IsChecked = s.AutoRestartBgi;
        TgAutoRestart.IsEnabled = s.SentinelEnabled;
        TgSound.IsChecked = s.SoundEnabled;
        SyncThemeSeg();
        _suppressToggle = false;

        UpdateTimeDisplay();
        SyncWeekdayChips();
        UpdateScheduleStatus();
        UpdateWebhookHint();
        RefreshMainButton();
        RefreshGroupPicker();
    }

    private void UpdateTimeDisplay()
    {
        var s = BgiService.Settings;
        TbTimeDisplay.Text = $"{s.ScheduleHour:00}:{s.ScheduleMinute:00}";
    }

    // ===== 定时：星期多选 / 倒计时 / 上次执行 =====

    private bool _suppressChips;

    /// <summary>把设置里的星期掩码回填到胶囊控件</summary>
    private void SyncWeekdayChips()
    {
        _suppressChips = true;
        try
        {
            int mask = BgiService.Settings.ScheduleWeekdays & 0x7F;
            ChipEvery.IsChecked = mask == 0x7F;
            foreach (var (chip, bit) in WeekdayChipPairs())
                chip.IsChecked = (mask & (1 << bit)) != 0;
        }
        finally { _suppressChips = false; }
    }

    private (System.Windows.Controls.Primitives.ToggleButton chip, int bit)[] WeekdayChipPairs() => new[]
    {
        (ChipMon, 0), (ChipTue, 1), (ChipWed, 2), (ChipThu, 3), (ChipFri, 4), (ChipSat, 5), (ChipSun, 6),
    };

    private void WeekdayChip_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressChips) return;
        try
        {
            var chip = sender as System.Windows.Controls.Primitives.ToggleButton;
            int mask = BgiService.Settings.ScheduleWeekdays & 0x7F;
            if ((chip?.Tag as string) == "All")
                mask = ChipEvery.IsChecked == true ? 0x7F : 0;
            else if (chip?.Tag is string t && int.TryParse(t, out int bit))
                mask ^= 1 << bit;
            BgiService.Settings.ScheduleWeekdays = mask;
            BgiService.SaveSettings();
            SyncWeekdayChips();
            UpdateScheduleStatus();
        }
        catch { }
    }

    /// <summary>刷新定时状态：左侧品牌竖条 / 倒计时（每秒） / 上次执行文案</summary>
    private void UpdateScheduleStatus()
    {
        try
        {
            var s = BgiService.Settings;
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

    /// <summary>下一个符合"时:分 + 星期掩码"的执行时刻（今天已过则向后找；无匹配返回 null）</summary>
    private DateTime? NextScheduleTime()
    {
        var s = BgiService.Settings;
        int mask = s.ScheduleWeekdays & 0x7F;
        if (mask == 0) return null;
        var now = DateTime.Now;
        for (int i = 0; i < 8; i++)
        {
            var day = now.Date.AddDays(i);
            int bit = ((int)day.DayOfWeek + 6) % 7; // 周一=0 … 周日=6
            if ((mask & (1 << bit)) == 0) continue;
            var t = day.AddHours(s.ScheduleHour).AddMinutes(s.ScheduleMinute);
            if (t > now) return t;
        }
        return null;
    }

    private void UpdateWebhookHint()
    {
        TbBgiWebhookHint.Text =
            "可选：让 BetterGI 把事件实时推给本软件（BetterGI 设置 → 通知 → Webhook）。" +
            $"地址：http://127.0.0.1:{BgiService.Settings.BgiWebhookPort}/webhook 。" +
            "不填也能通过读取 BGI 日志识别进度与配置组，填了事件更及时。";
    }

    // ===== 推送渠道（与 BetterGI 对齐的 10 个渠道 + 动态参数区）=====

    /// <summary>把设置里的渠道回填到胶囊</summary>
    private void SyncNotifyChips()
    {
        _suppressNotifyChips = true;
        try
        {
            var ch = NotifyService.ParseChannel(BgiService.Settings.NotifyMode);
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
        BgiService.Settings.NotifyMode = (sender as FrameworkElement)?.Tag as string ?? "Toast";
        BgiService.SaveSettings();
        ApplyNotifyMode();
    }

    /// <summary>按渠道切换参数区、说明与"已配置"状态</summary>
    private void ApplyNotifyMode()
    {
        try
        {
            var ch = NotifyService.ParseChannel(BgiService.Settings.NotifyMode);
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
        var s = BgiService.Settings;
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

    /// <summary>把设置回填到各渠道输入框</summary>
    private void LoadNotifyFields()
    {
        var s = BgiService.Settings;
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

    /// <summary>渠道参数输入框统一保存（Tag = AppSettings 属性名）</summary>
    private void NotifyField_LostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not TextBox tb || tb.Tag is not string prop) return;
            var pi = typeof(AppSettings).GetProperty(prop);
            if (pi == null) return;
            if (pi.PropertyType == typeof(int))
                pi.SetValue(BgiService.Settings, int.TryParse(tb.Text.Trim(), out int n) && n > 0 ? n : 465);
            else
                pi.SetValue(BgiService.Settings, tb.Text.Trim());
            BgiService.SaveSettings();
            ApplyNotifyMode();
        }
        catch { }
    }

    private void TgNotifyOnlyImportant_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        BgiService.Settings.NotifyOnlyImportant = TgNotifyOnlyImportant.IsChecked == true;
        BgiService.SaveSettings();
    }

    // ===== 大按钮四态状态机（v0.2：统一入口 RefreshMainButton，禁止散落设置颜色）=====

    /// <summary>就绪条件之一：BGI 路径非空且文件存在（File.Exists 包 try-catch，网络盘/非法字符不崩）</summary>
    private bool MainButtonPathOk()
    {
        string path = (BgiService.Settings.BgiPath ?? "").Trim();
        if (path.Length == 0) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }

    /// <summary>就绪条件之二：配置名非空 或 已勾选配置组 ≥ 1</summary>
    private bool MainButtonHasTarget()
        => TbConfigName.Text.Trim().Length > 0 || (BgiService.Settings.SelectedGroups is { Length: > 0 });

    /// <summary>可点击态 = 非 Busy 且（运行中 或 已就绪）；NotReady/Busy 由代码拦截（不用 IsEnabled=false，否则收不到点击做抖动）</summary>
    private bool MainButtonInteractive()
        => !_busy && (BgiService.IsRunning || (MainButtonPathOk() && MainButtonHasTarget()));

    /// <summary>
    /// 主按钮状态机（§7 文案表）：starting / running / done / error / idle 单一刷新入口。
    /// 背景统一 ColorAnimation 250ms 过渡；提示文字与状态行同步；不加发光（§9）。
    /// </summary>
    /// <summary>
    /// 启动台主按钮五态状态机（文案表固定）：idle / starting / running / done / error 单一刷新入口。
    /// 状态区两行高度固定（24 + 18），文案切换不改卡片高度；图标位固定 16px，starting 时换旋转指示器。
    /// </summary>
    private void RefreshMainButton()
    {
        try
        {
            bool running = BgiService.IsRunning;
            bool pathOk = MainButtonPathOk();
            bool ready = pathOk && MainButtonHasTarget();
            bool doneToday = LastRunDoneToday();

            Brush bg;            // 按钮底色
            Brush fg;            // 按钮文字 / 图标色
            string text;         // 按钮文案
            string hint;         // 副文案（第二行）
            string status;       // 主状态（第一行）
            bool clickable;
            double opacity = 1;
            bool dotOn = false, dotDanger = false;
            BtnIcon icon = BtnIcon.Play;

            if (_busy)
            {
                bg = BrushOf("ControlBgBrush") ?? DotGray;
                fg = BrushOf("TextSecondaryBrush") ?? DotGray;
                text = _busyStopping ? "正在停止…" : "正在启动…";
                hint = _busyStopping ? "正在结束 BetterGI 进程，请稍候…" : "检测进程 → 加载配置 → 等待游戏窗口";
                status = _busyStopping ? "正在停止 BetterGI" : "正在启动 BetterGI";
                clickable = false;
                opacity = 0.55;
                dotOn = true;
                icon = BtnIcon.Spinner;
            }
            else if (running)
            {
                bg = BrushOf("DangerBrush") ?? DotGray;
                fg = Brushes.White;
                text = "停止 · 已跑 " + ElapsedText();
                hint = "守护已接管，异常会自动重启并续跑";
                status = "一条龙运行中";
                clickable = true;
                dotOn = true;
                icon = BtnIcon.Stop;
            }
            else if (!pathOk)
            {
                bg = BrushOf("BrandBrush") ?? DotGray;
                fg = BrushOf("BrandOnBrush") ?? Brushes.White;
                text = "重新启动";
                hint = "路径无效，点这里重新选择";
                status = "启动失败：找不到 BetterGI";
                clickable = true;               // 点击直接打开文件选择
                dotDanger = true;
            }
            else if (ready)
            {
                bg = BrushOf("BrandBrush") ?? DotGray;
                fg = BrushOf("BrandOnBrush") ?? Brushes.White;
                text = "立刻启动一条龙";
                hint = doneToday ? DoneRunHint() : "点下面的按钮，或等 08:00 自动开始";
                status = doneToday ? "一条龙已完成" : "BetterGI 未运行";
                clickable = true;
                dotOn = true;
            }
            else
            {
                bg = BrushOf("ControlBgBrush") ?? DotGray;
                fg = BrushOf("TextSecondaryBrush") ?? DotGray;
                text = "立刻启动一条龙";
                hint = "还差一步：填写一条龙配置名，或勾选配置组";
                status = "BetterGI 未运行";
                clickable = false;
                opacity = 0.55;
            }

            ApplyMainButtonBackground(bg);
            BigBtn.Foreground = fg;
            BigBtnText.Text = text;
            SetBigBtnIcon(icon);
            BigBtn.Opacity = opacity;
            BigBtn.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;
            BigHint.Text = hint;
            StatusText.Text = status;
            StatusDot.Fill = dotDanger ? (BrushOf("DangerBrush") ?? DotGray)
                                      : dotOn ? (BrushOf("BrandBrush") ?? DotGray)
                                              : (BrushOf("TextTertiaryBrush") ?? DotGray);
            TbLastRunLaunch.Text = LaunchFooterText();

            // 进度条：只有 starting / running 显示
            if (running || _busy) StartProgressFlow(); else StopProgressFlow();
            SyncEcgState();
        }
        catch { }
    }

    /// <summary>主按钮图标位三态（固定 16px 宽度，切换不改变按钮尺寸）</summary>
    private enum BtnIcon { Play, Stop, Spinner }

    private void SetBigBtnIcon(BtnIcon icon)
    {
        try
        {
            BigBtnIconPlay.Visibility = icon == BtnIcon.Play ? Visibility.Visible : Visibility.Collapsed;
            BigBtnIconStop.Visibility = icon == BtnIcon.Stop ? Visibility.Visible : Visibility.Collapsed;
            BigBtnSpinner.Visibility = icon == BtnIcon.Spinner ? Visibility.Visible : Visibility.Collapsed;
            if (icon == BtnIcon.Spinner)
            {
                if (!BigBtnSpinnerRotate.HasAnimatedProperties)
                    BigBtnSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
                        { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                BigBtnSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }
        catch { }
    }

    /// <summary>done 态副文案：「今日第 1 组 · 用时 22 分 14 秒」</summary>
    private static string DoneRunHint()
    {
        try
        {
            var known = BgiDiscovery.Scan().Groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
            int today = GroupArchive.GetTodayGroups().Count(g => g.End != null && known.Contains(g.Name));
            int secs = BgiService.Settings.LastRunSeconds;
            string dur = secs >= 3600 ? $"{secs / 3600} 小时 {secs % 3600 / 60} 分"
                : secs >= 60 ? $"{secs / 60} 分 {secs % 60} 秒"
                : $"{secs} 秒";
            return $"今日第 {Math.Max(1, today)} 组 · 用时 {dur}";
        }
        catch { return "今日任务已完成"; }
    }

    /// <summary>启动台底栏：「上次：昨天 08:00 · 22 分 14 秒 · 成功」</summary>
    private static string LaunchFooterText()
    {
        var s = BgiService.Settings;
        if (string.IsNullOrWhiteSpace(s.LastRunAt)) return "上次：暂无记录";
        string when = s.LastRunAt;
        if (DateTime.TryParse(s.LastRunAt, out var t))
        {
            when = t.Date == DateTime.Today ? "今天 " + t.ToString("HH:mm")
                : t.Date == DateTime.Today.AddDays(-1) ? "昨天 " + t.ToString("HH:mm")
                : t.ToString("MM-dd HH:mm");
        }
        int secs = s.LastRunSeconds;
        string dur = secs >= 3600 ? $"{secs / 3600} 小时 {secs % 3600 / 60} 分"
            : secs >= 60 ? $"{secs / 60} 分 {secs % 60} 秒"
            : $"{secs} 秒";
        return $"上次：{when} · {dur} · {(s.LastRunOk ? "成功" : "中止")}";
    }

    /// <summary>最近一次执行是否是今天完成的</summary>
    private bool LastRunDoneToday()
    {
        try
        {
            var s = BgiService.Settings;
            return s.LastRunOk && s.LastRunAt.StartsWith(DateTime.Now.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private Color _mainBtnBgTarget = Colors.Transparent; // 上次过渡目标色（同色不重启动画）
    private bool _mainBtnBgInit;

    /// <summary>按钮背景过渡：统一 ColorAnimation 250ms CubicEase；用可动画的新画刷承载（主题画刷已冻结）</summary>
    private void ApplyMainButtonBackground(Brush target)
    {
        try
        {
            if (target is not SolidColorBrush sb) { BigBtn.Background = target; return; }
            if (_mainBtnBgInit && _mainBtnBgTarget == sb.Color && BigBtn.Background is SolidColorBrush)
                return; // 目标色未变：不重启过渡
            _mainBtnBgInit = true;
            _mainBtnBgTarget = sb.Color;
            var from = BigBtn.Background is SolidColorBrush cur ? cur.Color : sb.Color;
            var nb = new SolidColorBrush(from);
            BigBtn.Background = nb;
            var anim = new ColorAnimation(sb.Color, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            nb.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        catch
        {
            BigBtn.Background = target; // 动画失败静默降级为直切
        }
    }

    /// <summary>按压/回弹瞬时背景切换（果冻按压 80ms，恢复 200ms）</summary>
    private void AnimateMainButtonBg(Brush? target, int ms)
    {
        try
        {
            if (target is not SolidColorBrush sb) return;
            var from = BigBtn.Background is SolidColorBrush cur ? cur.Color : sb.Color;
            var nb = new SolidColorBrush(from);
            BigBtn.Background = nb;
            var anim = new ColorAnimation(sb.Color, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            nb.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        catch { }
    }

    /// <summary>当前正常态背景色（回弹目标）</summary>
    private Brush MainButtonNormalBrush()
        => BgiService.IsRunning ? (BrushOf("DangerBrush") ?? DotGray) : (BrushOf("BrandBrush") ?? DotGray);

    /// <summary>当前按压态背景色（比正常色深约 28% 的按压色）</summary>
    private Brush? MainButtonPressedBrush()
        => BgiService.IsRunning ? BrushOf("DangerPressedBrush") : BrushOf("BrandPressedBrush");

    /// <summary>
    /// 按压反馈（§4）：向下位移 1px + 背景转按压色，80ms ease-out。
    /// 不再使用缩放（§9 禁发光/夸张动效，只保留可感知的物理位移）。
    /// </summary>
    private void BigBtn_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (!MainButtonInteractive()) return;
            _pressing = true;
            BigBtnShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(80))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            AnimateMainButtonBg(MainButtonPressedBrush(), 80);
        }
        catch { } // 动画失败静默降级，不影响点击功能
    }

    /// <summary>松开/移出恢复：位移回 0，背景 160ms 回到正常色</summary>
    private void ReleaseMainButtonJelly()
    {
        if (!_pressing) return;
        _pressing = false;
        try
        {
            BigBtnShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            AnimateMainButtonBg(MainButtonNormalBrush(), 160);
        }
        catch { }
    }

    private void BigBtn_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ReleaseMainButtonJelly();

    private void BigBtn_MouseLeave(object sender, MouseEventArgs e) => ReleaseMainButtonJelly();

    /// <summary>未就绪点击：横向抖动 0→-4→4→-3→3→0（260ms）+ cancel 音效，不执行任何启动逻辑</summary>
    private void ShakeMainButton()
    {
        try
        {
            var kf = new DoubleAnimationUsingKeyFrames();
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(52))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(104))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(156))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(208))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
            BigBtnShift.BeginAnimation(TranslateTransform.XProperty, kf);
        }
        catch { }
    }

    private void ReadyDebounce_Tick(object? sender, EventArgs e)
    {
        _readyDebounce.Stop();
        RefreshMainButton();
    }

    /// <summary>就绪评估防抖入口：连续输入只认最后一次（300ms），避免打字过程中颜色闪烁</summary>
    private void ScheduleReadyEval()
    {
        _readyDebounce.Stop();
        _readyDebounce.Start();
    }


    private void TbConfigName_TextChanged(object sender, TextChangedEventArgs e) => ScheduleReadyEval();

    private async void BigBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; // 处理中：由 RefreshMainButton 置灰拦截

        // 路径无效（error 态）：主按钮直接引导选择文件，对应文案"点这里重新选择"
        if (!BgiService.IsRunning && !MainButtonPathOk())
        {
            BtnBrowse_Click(sender, e);
            return;
        }

        if (!BgiService.IsRunning && !MainButtonInteractive())
        {
            // 未就绪（缺配置名/配置组）：横向抖动 + cancel 音效——"这个按钮现在不能按"的物理化表达
            ShakeMainButton();
            return;
        }

        _busy = true;
        _busyStopping = BgiService.IsRunning;
        RefreshMainButton();
        try
        {
            if (_busyStopping) await BgiService.StopAsync("手动");
            else await BgiService.StartDragonOrGroupsAsync("手动");
        }
        catch (Exception ex)
        {
            BgiService.PublishLog("✗ 操作失败：" + ex.Message);
            LogService.LogEvent("主界面操作失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshMainButton();
            BgiService.RefreshStatus();
        }
    }

    // ===== 事件订阅 =====

    private void BgiService_OnStatusChanged(bool running)
    {
        try
        {
            if (running && _runStartedAt == null)
            {
                _runStartedAt = DateTime.Now;
                if (!_runTick.IsEnabled) _runTick.Start();
            }
            else if (!running && _runStartedAt != null)
            {
                FinishRun(!_busyStopping); // 手动停止算中止
            }
        }
        catch { }
        RefreshMainButton();
        RefreshSentinelLive();
    }

    // ===== 单次执行的计时与结果落盘 =====

    private DateTime? _runStartedAt;
    private readonly DispatcherTimer _runTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string ElapsedText()
    {
        var start = _runStartedAt;
        if (start == null) return "00:00";
        var span = DateTime.Now - start.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>一次执行结束：写回上次执行时间/用时/结果，刷新文案与进度条</summary>
    private void FinishRun(bool ok)
    {
        var start = _runStartedAt ?? DateTime.Now;
        int secs = Math.Max(0, (int)(DateTime.Now - start).TotalSeconds);
        _runStartedAt = null;
        _runTick.Stop();
        var s = BgiService.Settings;
        s.LastRunAt = start.ToString("yyyy-MM-dd HH:mm");
        s.LastRunSeconds = secs;
        s.LastRunOk = ok;
        BgiService.SaveSettings();
        StopProgressFlow();
        UpdateScheduleStatus();
    }

    private void RunTick_Tick(object? sender, EventArgs e)
    {
        if (!BgiService.IsRunning || _busy) return;
        try { BigBtnText.Text = "停止 · 已跑 " + ElapsedText(); } catch { }
    }

    /// <summary>进度条：不定态流动（循环平移，不假报百分比）</summary>
    private void StartProgressFlow()
    {
        try
        {
            if (RunProgressTrack.Visibility == Visibility.Visible) return;
            RunProgressTrack.Visibility = Visibility.Visible;
            double w = Math.Max(RunProgressTrack.ActualWidth, 300);
            var anim = new DoubleAnimation(-160, w, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };
            RunProgressShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        catch { }
    }

    private void StopProgressFlow()
    {
        try
        {
            if (RunProgressTrack.Visibility != Visibility.Visible) return;
            RunProgressShift.BeginAnimation(TranslateTransform.XProperty, null);
            RunProgressTrack.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void BgiService_OnLog(string msg)
    {
        TbLog.Text = msg;
        // 无消息时整条收起（窗口按内容自适应，避免底部留空条）
        try { StatusBarBorder.Visibility = string.IsNullOrWhiteSpace(msg) ? Visibility.Collapsed : Visibility.Visible; } catch { }
        if (msg.Contains("✗") || msg.Contains("异常") || msg.Contains("失败") || msg.Contains("崩溃"))
            FlashStatusError();
    }

    private void SentinelService_OnEvent(SentinelEventKind kind, string payload)
    {
        var success = BrushOf("SuccessBrush") ?? DotGray;
        var warning = BrushOf("WarningBrush") ?? DotGray;
        var danger = BrushOf("DangerBrush") ?? DotGray;
        var tertiary = BrushOf("TextTertiaryBrush") ?? DotGray;

        switch (kind)
        {
            case SentinelEventKind.GroupStarted:
                AddEvent("开始 " + payload, BrushOf("BrandBrush") ?? DotGray);
                RefreshSentinelCard();
                break;
            case SentinelEventKind.GroupEnded:
                AddEvent(payload + " 完成", success);
                RefreshSentinelCard();
                break;
            case SentinelEventKind.DragonStarted:
                if (_runStartedAt == null) { _runStartedAt = DateTime.Now; if (!_runTick.IsEnabled) _runTick.Start(); }
                AddEvent("一条龙启动", BrushOf("BrandBrush") ?? DotGray);
                SetEcgState(EcgState.Running);
                RefreshMainButton();
                RefreshSentinelCard();
                break;
            case SentinelEventKind.DragonEnded:
                if (_runStartedAt != null) FinishRun(true); // 一条龙正常结束 → 记为成功
                RefreshDetectedGroups(force: true); // 运行过程中可能新建了配置组
                SetEcgState(EcgState.Ending, 2600);  // 收尾：波形平滑回落成灰平线
                AddEvent("一条龙结束", success);
                RefreshMainButton();
                RefreshSentinelCard();
                break;
            case SentinelEventKind.ScreenFrozen:
                MarkGuardAnomaly(20000);
                SetEcgState(EcgState.Error, 4000);
                AddEvent("画面疑似冻结", warning);
                TbLog.Text = "⚠ " + payload;
                FlashStatusError();
                RefreshSentinelLive();
                break;
            case SentinelEventKind.GameLost:
                MarkGuardAnomaly(20000);
                AddEvent("原神进程消失", warning);
                TbLog.Text = "⚠ " + payload;
                FlashStatusError();
                RefreshSentinelLive();
                break;
            case SentinelEventKind.MemoryHigh:
                MarkGuardAnomaly(20000);
                SetEcgState(EcgState.Error, 4000);
                AddEvent("内存占用过高", warning);
                TbLog.Text = "⚠ " + payload;
                FlashStatusError();
                RefreshSentinelLive();
                break;
            case SentinelEventKind.GroupError:
                MarkGuardAnomaly(25000);
                SetEcgState(EcgState.Error, 5000);
                AddEvent("任务异常" + (string.IsNullOrWhiteSpace(payload) ? "" : "：" + payload), danger);
                TbLog.Text = "⚠ " + payload;
                FlashStatusError();
                RefreshSentinelLive();
                break;
            case SentinelEventKind.LogErrorLine:
                TbLog.Text = "⚠ " + payload;
                FlashStatusError();
                break;
            case SentinelEventKind.BgiExited:
                MarkGuardAnomaly(30000);
                SetEcgState(EcgState.Ending, 2400);
                AddEvent("BGI 已退出", tertiary);
                ShowBanner(payload);
                RefreshSentinelLive();
                break;
            case SentinelEventKind.BgiRestarted:
                MarkGuardAnomaly(30000);
                SetEcgState(EcgState.Error, 8000); // 崩溃 → 红色报警，恢复运行后自动转绿
                AddEvent("BGI 崩溃，已自动重启", warning);
                ShowBanner(payload);
                RefreshSentinelLive();
                break;
        }
    }

    // ===== 守护条：折叠摘要（56）+ 展开 2×2 明细 + 心跳线 =====

    private bool _guardExpanded;              // 展开态
    private bool _guardAbnormal;              // 是否处于异常态（降级 / 进程异常）
    private bool? _guardVisualAbnormal;       // 已应用的异常视觉（避免每秒重建画刷）
    private long _guardAnomalyUntil;          // 事件造成的异常展示期（毫秒时间戳）
    private readonly DispatcherTimer _guardCollapseTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private void InitGuardStrip()
    {
        _guardCollapseTimer.Tick += (_, _) =>
        {
            _guardCollapseTimer.Stop();
            if (!_guardAbnormal) SetGuardExpanded(false);
        };
        SetGuardExpanded(false);
    }

    /// <summary>展开 / 收起守护条明细（折叠态固定 56，展开态自适应）</summary>
    private void SetGuardExpanded(bool expand)
    {
        try
        {
            _guardExpanded = expand;
            GuardStripBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            GuardStripChevronRotate.Angle = expand ? 180 : 0;
            GuardStripDetail.Text = expand ? "收起" : "详情";
        }
        catch { }
    }

    /// <summary>整条可点击切换；异常期间强制保持展开</summary>
    private void GuardStripHeader_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestorButton(e.OriginalSource as DependencyObject) != null) return; // 「详情」按钮自己处理
        ToggleGuardStrip();
    }

    /// <summary>「详情」按钮（键盘可达）</summary>
    private void GuardStripToggle_Click(object sender, RoutedEventArgs e) => ToggleGuardStrip();

    private void ToggleGuardStrip()
    {
        if (_guardAbnormal && _guardExpanded)
        {
            return;
        }
        _guardCollapseTimer.Stop();
        SetGuardExpanded(!_guardExpanded);
    }

    private static Button? FindAncestorButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button b) return b;
            try
            {
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>标记一次异常展示期（事件触发；期内守护条强制展开并转警示色）</summary>
    private void MarkGuardAnomaly(int ms)
    {
        _guardAnomalyUntil = Math.Max(_guardAnomalyUntil, Environment.TickCount64 + ms);
    }

    /// <summary>小屏（可用高度不足 760）时保持折叠，避免把内容挤到屏幕外</summary>
    private bool GuardAutoExpandAllowed()
        => ActualHeight >= 760 || SystemParameters.WorkArea.Height >= 760;

    private enum FeatState { Ok, Warn, Pending, Off }

    /// <summary>设置单项检测状态：●正常 / ⚠降级 / ○未开始；返回 1 表示降级</summary>
    private int SetFeatState(Ellipse dot, TextBlock text, FeatState state)
    {
        var success = BrushOf("SuccessBrush") ?? DotGray;
        var warning = BrushOf("WarningBrush") ?? DotGray;
        var tertiary = BrushOf("TextTertiaryBrush") ?? DotGray;
        var secondary = BrushOf("TextSecondaryBrush") ?? DotGray;
        switch (state)
        {
            case FeatState.Warn:
                dot.Fill = warning; text.Text = "降级"; text.Foreground = warning; return 1;
            case FeatState.Ok:
                dot.Fill = success; text.Text = "正常"; text.Foreground = secondary; return 0;
            default:
                dot.Fill = tertiary; text.Text = "未开始"; text.Foreground = tertiary; return 0;
        }
    }

    /// <summary>守护条整条刷新：今日计数、组列表、开关状态、标题</summary>
    private void RefreshSentinelCard()
    {
        try
        {
            var scan = BgiDiscovery.Scan();
            var known = scan.Groups;
            var knownSet = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var today = GroupArchive.GetTodayGroups().Where(g => knownSet.Contains(g.Name)).ToList();
            var todayNames = today.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notRun = known.Where(n => !todayNames.Contains(n)).ToList();
            int done = today.Count(g => g.End != null);

            // 启动台头部「今日 x/y 组」：分母优先取已勾选的配置组数量
            var selected = BgiService.Settings.SelectedGroups ?? Array.Empty<string>();
            int denom = selected.Length > 0 ? selected.Length : known.Count;
            int doneCount = selected.Length > 0
                ? today.Count(g => g.End != null && selected.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
                : done;
            SentinelCountText.Text = denom > 0 ? $"今日 {doneCount}/{denom} 组" : $"今日 {doneCount} 组";
            SentinelCountText.ToolTip = selected.Length > 0
                ? "分母 = 已勾选的配置组数量"
                : "分母 = 已识别的配置组数量（尚未勾选配置组）";

            _rows.Clear();
            foreach (var g in today.Take(3))
            {
                bool isRunning = g.End == null;
                _rows.Add(new GroupRow
                {
                    Icon = isRunning ? "◐" : "✓",
                    IconColor = isRunning ? DotYellow : (BrushOf("SuccessBrush") ?? DotGray),
                    Name = g.Name,
                    Time = isRunning ? "进行中" : $"{ShortenTime(g.Start)}-{ShortenTime(g.End)}",
                });
            }
            int shown = _rows.Count;
            foreach (var n in notRun.Take(Math.Max(0, 3 - shown)))
            {
                _rows.Add(new GroupRow
                {
                    Icon = "○",
                    IconColor = BrushOf("TextTertiaryBrush") ?? DotGray,
                    Name = n,
                    Time = "未跑",
                });
                shown++;
            }
            int all = today.Count + notRun.Count;
            if (all > shown)
                _rows.Add(new GroupRow { Icon = "…", IconColor = DotGray, Name = $"还有 {all - shown} 个组", Time = "" });

            var unfinished = GroupArchive.GetUnfinished();
            bool sen = BgiService.Settings.SentinelEnabled;
            SentinelFeatures.Visibility = sen ? Visibility.Visible : Visibility.Collapsed;
            SentinelList.Visibility = sen ? Visibility.Visible : Visibility.Collapsed;
            SentinelClosedBar.Visibility = sen ? Visibility.Collapsed : Visibility.Visible;
            BtnContinue.Visibility = unfinished.Count > 0 && sen ? Visibility.Visible : Visibility.Collapsed;
            BtnContinue.Content = $"▶ 继续跑未完成（{unfinished.Count} 组）";

            GuardStripTitle.Text = sen ? "守护已开启" : "守护已暂停";
            GuardStripDot.Fill = sen ? (BrushOf("SuccessBrush") ?? DotGray) : (BrushOf("TextTertiaryBrush") ?? DotGray);
            _guardVisualAbnormal = null; // 标题/开关变化后重新应用一次视觉
            RefreshSentinelLive();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新守护条失败：" + ex.Message);
        }
    }

    private static string ShortenTime(string? t)
        => string.IsNullOrEmpty(t) ? "--:--" : t.Length >= 5 ? t[..5] : t;

    /// <summary>每秒刷新：四项检测真实状态、守护条摘要 / 异常态、心跳提示</summary>
    private void RefreshSentinelLive()
    {
        try
        {
            var s = BgiService.Settings;
            var h = SentinelService.HealthSnapshot();
            bool sen = s.SentinelEnabled;
            int degraded = 0;

            // 1) 崩溃自动重启
            bool restartOn = sen && s.AutoRestartBgi;
            FeatRestartDetail.Text = restartOn ? $"本次 {h.RestartCount} 次" : "未开启";
            degraded += SetFeatState(FeatRestartDot, FeatRestartStatus, restartOn ? FeatState.Ok : FeatState.Off);

            // 2) 内存监控
            bool memCollected = h.LastMemoryCheck != default;
            // 只有 BGI 自身内存超限（或系统濒临耗尽）才算守护降级：
            // 整机内存 85% 在常见办公机上长期存在，若据此转黄会让守护条永远停在"需要处理"
            bool memWarn = h.BgiWorkingSetMb > 4096 || h.MemUsedPercent > 96;
            bool sysTight = h.MemUsedPercent > 85;
            FeatMemDetail.Text = !sen ? "未开启"
                : !memCollected ? "采集…"
                : $"系统 {h.MemUsedPercent:F0}% · BGI {(h.BgiWorkingSetMb > 0 ? h.BgiWorkingSetMb + " MB" : "未运行")}";
            FeatMemDetail.Foreground = !sen || !memCollected ? (BrushOf("TextTertiaryBrush") ?? DotGray)
                : sysTight ? (BrushOf("WarningBrush") ?? DotGray)
                : (BrushOf("TextTertiaryBrush") ?? DotGray);
            degraded += SetFeatState(FeatMemDot, FeatMemStatus,
                !sen ? FeatState.Off : !memCollected ? FeatState.Pending : memWarn ? FeatState.Warn : FeatState.Ok);

            // 3) 画面冻结检测
            FeatState shotState;
            if (!sen) { FeatShotDetail.Text = "未开启"; shotState = FeatState.Off; }
            else if (!s.ScreenshotCheckEnabled) { FeatShotDetail.Text = "已关闭"; shotState = FeatState.Off; }
            else if (h.LastScreenCheck == default)
            {
                FeatShotDetail.Text = "运行一条龙时检测";
                shotState = FeatState.Pending;
            }
            else
            {
                bool frozenRecently = h.LastFrozenAt != default &&
                                      (DateTime.Now - h.LastFrozenAt).TotalMinutes < 5;
                FeatShotDetail.Text = frozenRecently
                    ? $"{Since(h.LastFrozenAt)}冻结 · 本次 {h.FrozenCount} 次"
                    : $"{Since(h.LastScreenCheck)} · 差异 {h.LastScreenScore:F1}";
                shotState = frozenRecently ? FeatState.Warn : FeatState.Ok;
            }
            degraded += SetFeatState(FeatShotDot, FeatShotStatus, shotState);

            // 4) 进度识别（日志兜底可用，不算降级）
            bool webhook = SentinelService.WebhookRunning;
            FeatProgressDetail.Text = !sen ? "未开启"
                : h.WebhookEvents > 0 ? $"已收 {h.WebhookEvents} 个事件"
                : webhook ? "通道就绪，等待事件"
                : h.LastTick == default ? "等待心跳" : "日志兜底 · 持续解析";
            degraded += SetFeatState(FeatProgressDot, FeatProgressStatus,
                !sen ? FeatState.Off : h.LastTick == default ? FeatState.Pending : FeatState.Ok);

            // 折叠态摘要：只占一行
            string summary;
            if (!sen) summary = "四项检测均未运行 · 点此开启守护";
            else if (degraded == 0) summary = "崩溃重启 · 内存 · 冻结检测 · 进度识别 四项正常";
            else if (degraded >= 4) summary = "崩溃重启 · 内存 · 冻结检测 · 进度识别 四项需处理";
            else summary = $"崩溃重启 · 内存 · 冻结检测 · 进度识别 {degraded} 项需处理";
            GuardStripSummary.Text = summary;

            // 心跳掉线也视为进程异常
            bool stale = sen && h.LastTick != default && (DateTime.Now - h.LastTick).TotalSeconds > 30;
            bool abnormal = degraded > 0 || stale || Environment.TickCount64 < _guardAnomalyUntil;

            if (_guardVisualAbnormal != abnormal)
            {
                _guardVisualAbnormal = abnormal;
                ApplyGuardAbnormal(abnormal, sen);
            }

            if (abnormal != _guardAbnormal)
            {
                _guardAbnormal = abnormal;
                if (abnormal)
                {
                    _guardCollapseTimer.Stop();
                    if (GuardAutoExpandAllowed()) SetGuardExpanded(true);
                }
                else if (_guardExpanded)
                {
                    _guardCollapseTimer.Stop();
                    _guardCollapseTimer.Start();   // 异常解除 10 秒后自动收回
                }
            }

            TbHeartHint.Text = h.LastTick == default
                ? "等待首次检查"
                : $"已连续 {h.TickCount:N0} 次 · {Since(h.LastTick)}";
            TbHeartHint.Foreground = h.LastTick != default && (DateTime.Now - h.LastTick).TotalSeconds < 15
                ? (BrushOf("TextSecondaryBrush") ?? DotGray)
                : (BrushOf("WarningBrush") ?? DotGray);

            HeartCanvas.Opacity = sen ? 1 : 0.45;
            if (!sen)
            {
                TbHeartHint.Text = "守护已关闭 · 心跳未运行";
                TbHeartHint.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            }
        }
        catch { }
    }

    /// <summary>异常态视觉：整条底色 Warning 12%、图标与文字转警示色、显示「需要处理」徽章</summary>
    private void ApplyGuardAbnormal(bool abnormal, bool sen)
    {
        try
        {
            var primary = BrushOf("TextPrimaryBrush") ?? DotGray;
            var secondary = BrushOf("TextSecondaryBrush") ?? DotGray;
            var warning = BrushOf("WarningBrush") ?? DotGray;
            var brand = BrushOf("BrandBrush") ?? DotGray;
            var tertiary = BrushOf("TextTertiaryBrush") ?? DotGray;

            GuardStrip.Background = abnormal ? WarningSoft() : (BrushOf("CardBgBrush") ?? Brushes.Transparent);
            GuardStrip.BorderBrush = abnormal ? warning : (BrushOf("BorderBrush") ?? Brushes.Transparent);
            GuardStripShield.Stroke = abnormal ? warning : (sen ? brand : tertiary);
            GuardStripTitle.Foreground = abnormal ? warning : primary;
            GuardStripSummary.Foreground = abnormal ? warning : secondary;
            GuardStripDetail.Foreground = abnormal ? warning : tertiary;
            GuardStripBadge.Visibility = abnormal ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    /// <summary>Warning 12% 透明底（主题切换后重算）</summary>
    private static Brush WarningSoft()
    {
        if (Application.Current?.TryFindResource("WarningBrush") is SolidColorBrush w)
        {
            var c = w.Color;
            return new SolidColorBrush(Color.FromArgb(31, c.R, c.G, c.B));
        }
        return Brushes.Transparent;
    }

    private static string Since(DateTime t)
    {
        if (t == default) return "尚未检查";
        double sec = (DateTime.Now - t).TotalSeconds;
        if (sec < 0) sec = 0;
        if (sec < 2) return "刚刚";
        if (sec < 60) return $"{(int)sec} 秒前";
        if (sec < 3600) return $"{(int)(sec / 60)} 分钟前";
        return $"{(int)(sec / 3600)} 小时前";
    }

    // ===== 拟真心电图：状态机 + 扫描式波形（细节在 EcgMeter.cs）=====

    /// <summary>
    /// 状态切换接口：业务侧显式指定心电状态，并可设置展示保持期（期间不被自动推导覆盖）。
    /// holdMs &gt; 0 用于 ERROR / ENDING 这类"需要停留一会儿才回到真实状态"的场景。
    /// </summary>
    public void SetEcgState(EcgState state, int holdMs = 0)
    {
        try
        {
            _ecgHoldUntil = holdMs > 0 ? Environment.TickCount64 + holdMs : 0;
            _ecg?.SetState(state);
        }
        catch { }
    }

    /// <summary>按业务真实状态推导心电状态（状态变化 / 每秒刷新时调用）</summary>
    private void SyncEcgState()
    {
        if (_ecg == null) return;
        try
        {
            if (Environment.TickCount64 < _ecgHoldUntil) return; // 异常/结束态展示期内不覆盖
            if (_busy) _ecg.SetState(_busyStopping ? EcgState.Ending : EcgState.Starting);
            else if (BgiService.IsRunning) _ecg.SetState(EcgState.Running);
            else _ecg.SetState(EcgState.Idle);
        }
        catch { }
    }

    private static string EcgStateName(EcgState s) => s switch
    {
        EcgState.Idle => "空闲（灰色平线）",
        EcgState.Starting => "接线中（波形渐现）",
        EcgState.Running => "运行中（绿色节律）",
        EcgState.Error => "异常（红色报警）",
        EcgState.Ending => "收尾（回落平线）",
        _ => s.ToString(),
    };

    /// <summary>调试入口：双击心电图循环 Idle → Starting → Running → Error → Ending</summary>
    private void HeartCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        try
        {
            var next = _ecg?.CycleState() ?? EcgState.Idle;
            _ecgHoldUntil = Environment.TickCount64 + 3200;
            TbHeartHint.Text = "调试 · " + EcgStateName(next);
            TbHeartHint.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            BgiService.PublishLog($"[调试] 心电图状态 → {EcgStateName(next)}（双击可继续切换）");
        }
        catch { }
    }

    // ===== 最近事件 =====

    private void AddEvent(string text, Brush? dot = null)
    {
        try
        {
            _events.Insert(0, new EventRow
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Text = text,
                DotColor = dot ?? BrushOf("BrandBrush") ?? DotGray,
            });
            while (_events.Count > 3) _events.RemoveAt(_events.Count - 1);
            TbEventsEmpty.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private async void BtnContinue_Click(object sender, RoutedEventArgs e)
    {
        var unfinished = GroupArchive.GetUnfinished().ToArray();
        if (unfinished.Length == 0) return;
        await BgiService.StartGroupsAsync(unfinished, "手动续跑");
    }

    // ===== 启动设置卡 =====

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 BetterGI.exe",
            Filter = "BetterGI|BetterGI.exe|可执行文件|*.exe",
        };
        if (dlg.ShowDialog(this) == true)
        {
            BgiService.Settings.BgiPath = dlg.FileName;
            BgiService.SaveSettings();
            RefreshPathDisplay();
            RefreshDetectedGroups(force: true);
            RefreshMainButton();
            BgiService.PublishLog("✓ 已设置 BetterGI 路径，已重新识别配置组");
        }
    }

    // ===== BGI 路径：常驻小字（省略号 + Tooltip 全路径 + 点击复制）=====

    /// <summary>刷新路径显示：有路径时按次要信息展示，无路径时用警示色引导设置</summary>
    private void RefreshPathDisplay()
    {
        try
        {
            string p = (BgiService.Settings.BgiPath ?? "").Trim();
            bool has = p.Length > 0;
            TbPathText.Text = has ? p : "未设置 BetterGI 路径 · 点「更改」选择";
            TbPathText.ToolTip = has ? p + "\n点击复制完整路径" : "点击选择 BetterGI.exe";
            TbPathText.Foreground = has
                ? (BrushOf("TextTertiaryBrush") ?? DotGray)
                : (BrushOf("WarningBrush") ?? DotGray);
            TbPathCopied.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void TbPathText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        string p = (BgiService.Settings.BgiPath ?? "").Trim();
        if (p.Length == 0)
        {
            BtnBrowse_Click(sender, new RoutedEventArgs());
            return;
        }
        if (ClipboardHelper.TrySetText(p))
        {
            TbPathCopied.Visibility = Visibility.Visible;
            _pathCopiedTimer.Stop();
            _pathCopiedTimer.Start();
            BgiService.PublishLog("✓ 已复制 BetterGI 路径到剪贴板");
        }
        else
        {
            // 剪贴板被其他程序持续占用：改为弹窗让用户手动 Ctrl+C（技术细节只进日志）
            ClipboardHelper.ShowManualCopyFallback(p, this, "复制路径");
        }
    }

    private void TbConfigName_LostFocus(object sender, RoutedEventArgs e)
    {
        string v = TbConfigName.Text.Trim();
        if (v != BgiService.Settings.DragonConfigName)
        {
            BgiService.Settings.DragonConfigName = v;
            BgiService.SaveSettings();
        }
    }

    // ===== 配置组：合并进启动卡，勾选顺序即执行顺序（实时生效，无需二次确认）=====

    /// <summary>
    /// 刷新配置组列表：增量同步 BGI 目录（外部新增/删除即时出现，保留已有项对象，不打断勾选与焦点）。
    /// 只在组名集合变化时由 8 秒巡检 / 路径变化 / 运行结束 / 手动刷新触发。
    /// </summary>
    private void RefreshGroupPicker()
    {
        try
        {
            var scan = BgiDiscovery.Scan();
            if (scan.Ok) SanitizeSelectedGroups(scan.Groups);

            var names = scan.Groups;
            // 1) 删除已消失的项（从后往前，避免下标漂移）
            for (int i = _groupPicks.Count - 1; i >= 0; i--)
            {
                bool exists = false;
                foreach (var n in names)
                    if (string.Equals(n, _groupPicks[i].Name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (!exists) _groupPicks.RemoveAt(i);
            }
            // 2) 按目录顺序插入新项 / 移动已有项
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                int idx = -1;
                for (int j = 0; j < _groupPicks.Count; j++)
                    if (string.Equals(_groupPicks[j].Name, name, StringComparison.OrdinalIgnoreCase)) { idx = j; break; }
                if (idx < 0) _groupPicks.Insert(Math.Min(i, _groupPicks.Count), new GroupPickItem { Name = name });
                else if (idx != i) _groupPicks.Move(idx, Math.Min(i, _groupPicks.Count - 1));
            }

            SyncGroupSelection();
            UpdateGroupUi(scan);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("刷新配置组选择失败：" + ex.Message);
        }
    }

    /// <summary>把设置里的勾选（含顺序）回填到列表项；Order 即执行序号</summary>
    private void SyncGroupSelection()
    {
        var settings = BgiService.Settings.SelectedGroups ?? Array.Empty<string>();
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

        // 配置组文件被外部删除时，同步剔除失效勾选（避免"虚假就绪"）
        var valid = _groupPicks.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int before = _selectedOrder.Count;
        _selectedOrder.RemoveAll(n => !valid.Contains(n));
        if (_selectedOrder.Count != before)
        {
            BgiService.Settings.SelectedGroups = _selectedOrder.ToArray();
            BgiService.SaveSettings();
        }

        foreach (var item in _groupPicks)
        {
            int idx = _selectedOrder.FindIndex(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase));
            item.IsChecked = idx >= 0;
            item.Order = idx + 1;
        }
        UpdateGroupSummary();
    }

    /// <summary>勾选统计（随勾选实时刷新，勾选本身即时写入设置）</summary>
    private void UpdateGroupSummary()
    {
        try
        {
            int n = _selectedOrder.Count;
            if (n > 0)
            {
                TbGroupSummary.Text = $"将执行 {n} 个配置组 · 按序号顺序";
                TbGroupSummary.Foreground = BrushOf("BrandBrush") ?? DotGray;
            }
            else
            {
                TbGroupSummary.Text = _groupPicks.Count > 0
                    ? $"共 {_groupPicks.Count} 个可选 · 未选时用配置名"
                    : "等待识别";
                TbGroupSummary.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            }
            TbGroupPickEmpty.Visibility = _groupPicks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnGroupDetails.Content = _groupPicks.Count > 0 ? $"配置组详情（{_groupPicks.Count}）" : "配置组详情";

            // 主按钮 Tooltip 带执行顺序，长列表只列前 5 个
            BigBtn.ToolTip = n > 0
                ? $"忽略定时设置，立即启动一条龙\n将执行 {n} 个配置组：{string.Join(" → ", _selectedOrder.Take(5))}{(n > 5 ? " …" : "")}"
                : "忽略定时设置，立即启动一条龙";
        }
        catch { }
    }

    /// <summary>识别状态与一条龙配置列表（次要信息，收在详情面板里）</summary>
    private void UpdateGroupUi(BgiScanResult scan)
    {
        try
        {
            var okBrush = BrushOf("SuccessBrush") ?? DotGray;
            var warnBrush = BrushOf("WarningBrush") ?? DotGray;
            if (!scan.Ok)
            {
                TbGroupScan.Text = "未识别：" + scan.Error;
                TbGroupScan.Foreground = warnBrush;
            }
            else if (scan.Groups.Count == 0)
            {
                TbGroupScan.Text = @"已读取 BGI 目录，User\ScriptGroup 里还没有配置组";
                TbGroupScan.Foreground = BrushOf("TextTertiaryBrush") ?? DotGray;
            }
            else
            {
                TbGroupScan.Text = $"已从 BetterGI 自动识别 {scan.Groups.Count} 个配置组（每 8 秒自动同步）";
                TbGroupScan.Foreground = okBrush;
            }

            TbConfigScan.Text = scan.DragonConfigs.Count > 0
                ? $"已识别 {scan.DragonConfigs.Count} 个一条龙配置：{string.Join("、", scan.DragonConfigs.Take(4))}{(scan.DragonConfigs.Count > 4 ? "…" : "")}"
                : (scan.Ok ? @"未找到一条龙配置（User\OneDragon 为空，留空则用 BGI 当前选中）" : "");

            _groupSignature = string.Join("\u0001", scan.Groups) + "\u0002" + scan.Ok + "\u0002" + scan.Error;
        }
        catch { }
    }

    /// <summary>次要入口：在资源管理器里打开 BGI 配置组目录（不写 BGI 数据，只在缺失时回退到根目录）</summary>
    private void BtnOpenGroupDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = BgiDiscovery.BgiRoot;
            if (string.IsNullOrEmpty(root))
            {
                RefreshPathDisplay();
                BgiService.PublishLog("✗ 还没有设置 BetterGI 路径");
                return;
            }
            string dir = System.IO.Path.Combine(root, "User", "ScriptGroup");
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
        BgiService.PublishLog("✓ 已重新识别 BetterGI 配置组");
    }

    /// <summary>
    /// 8 秒巡检 BGI 配置目录：新增/删除配置组或换新 BGI 后，界面字样与勾选状态及时同步。
    /// 只有签名变化才重建列表，避免打断用户操作。
    /// </summary>
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
            RefreshSentinelCard();
        }
        catch { }
    }

    /// <summary>把设置里已不存在于 BGI 目录的勾选组移除（新装 BGI 时不会残留旧组导致"虚假就绪"）</summary>
    private void SanitizeSelectedGroups(List<string> groups)
    {
        try
        {
            var selected = BgiService.Settings.SelectedGroups ?? Array.Empty<string>();
            if (selected.Length == 0) return;
            var valid = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = selected.Where(valid.Contains).ToArray();
            if (kept.Length == selected.Length) return;
            BgiService.Settings.SelectedGroups = kept;
            BgiService.SaveSettings();
            RefreshMainButton();
            BgiService.PublishLog(kept.Length == 0
                ? "⚠ 原勾选的配置组在 BetterGI 中已不存在，已自动取消勾选"
                : $"⚠ 已移除 {selected.Length - kept.Length} 个在 BetterGI 中已不存在的配置组");
            LogService.LogEvent($"配置组同步：移除失效勾选 {selected.Length - kept.Length} 个");
        }
        catch { }
    }

    /// <summary>次要入口：展开/收起识别详情（不占主要视觉）</summary>
    private void BtnGroupDetails_Click(object sender, RoutedEventArgs e)
    {
        bool show = GroupPickPanel.Visibility != Visibility.Visible;
        GroupPickPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) RefreshDetectedGroups(force: true);
    }

    /// <summary>
    /// 勾选/取消勾选：立即写入设置（无需二次确认），勾选顺序即执行顺序，
    /// 序号徽章与"将执行 N 个配置组"同步刷新，主按钮就绪态即时重估。
    /// </summary>
    private void GroupPickCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not CheckBox cb || cb.DataContext is not GroupPickItem item) return;
            bool on = cb.IsChecked == true;

            _selectedOrder.RemoveAll(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase));
            if (on) _selectedOrder.Add(item.Name);   // 后勾的排在后面 → 执行顺序与序号一致

            foreach (var g in _groupPicks)
            {
                int idx = _selectedOrder.FindIndex(n => string.Equals(n, g.Name, StringComparison.OrdinalIgnoreCase));
                g.IsChecked = idx >= 0;
                g.Order = idx + 1;
            }

            BgiService.Settings.SelectedGroups = _selectedOrder.ToArray();
            BgiService.SaveSettings();
            UpdateGroupSummary();
            RefreshMainButton();   // 勾选变化 → 就绪状态即时重估

            BgiService.PublishLog(_selectedOrder.Count > 0
                ? $"✓ 将执行 {_selectedOrder.Count} 个配置组：{string.Join(" → ", _selectedOrder)}"
                : "已取消全部配置组勾选，启动时使用一条龙配置名");
            LogService.LogEvent($"配置组勾选变更：{_selectedOrder.Count} 个");
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
            TimePicker.InitFrom(BgiService.Settings.ScheduleHour, BgiService.Settings.ScheduleMinute);
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
        BgiService.Settings.ScheduleHour = hour;
        BgiService.Settings.ScheduleMinute = minute;
        BgiService.SaveSettings();
        UpdateTimeDisplay();
        UpdateScheduleStatus();
        CollapsePicker();
        BgiService.PublishLog($"✓ 定时已设置为 {hour:00}:{minute:00}");
        LogService.LogEvent($"定时设置为 {hour:00}:{minute:00}（星期掩码 {BgiService.Settings.ScheduleWeekdays}）");
    }

    private void TimePicker_Cancelled()
    {
        CollapsePicker();
    }

    // ===== 系统卡开关 =====

    private void TgSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        BgiService.Settings.ScheduleEnabled = TgSchedule.IsChecked == true;
        BgiService.SaveSettings();
        UpdateScheduleStatus(); // 品牌竖条 + 倒计时即时跟随
    }

    private void TgAutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool on = TgAutoStart.IsChecked == true;
        AutoStartHelper.Set(on);
        BgiService.PublishLog(on ? "✓ 已开启开机自动启动（当前用户）" : "已关闭开机自动启动（当前用户）");
        LogService.LogEvent("开机自启（当前用户）" + (on ? "已开启" : "已关闭"));
    }

    private void TgAutoRun_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        BgiService.Settings.AutoRunOnLaunch = TgAutoRun.IsChecked == true;
        BgiService.SaveSettings();
    }

    private void TgStayTray_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        BgiService.Settings.StayInTray = TgStayTray.IsChecked == true;
        BgiService.SaveSettings();
    }

    private void TgSentinel_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        ApplySentinel(TgSentinel.IsChecked == true);
    }

    private void BtnSentinelOn_Click(object sender, RoutedEventArgs e)
    {
        ApplySentinel(true);
    }

    private void ApplySentinel(bool on)
    {
        BgiService.Settings.SentinelEnabled = on;
        BgiService.SaveSettings();
        _suppressToggle = true;
        TgSentinel.IsChecked = on;
        _suppressToggle = false;
        TgAutoRestart.IsEnabled = on;
        if (on) SentinelService.Start();
        else SentinelService.Stop();
        RefreshSentinelCard();
        BgiService.PublishLog(on ? "✓ 已开启守护" : "守护已关闭");
        LogService.LogEvent("守护" + (on ? "已开启" : "已关闭"));
    }

    private void TgAutoRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        BgiService.Settings.AutoRestartBgi = TgAutoRestart.IsChecked == true;
        BgiService.SaveSettings();
        RefreshSentinelLive(); // 守护卡状态即时刷新
    }

    private void TgSound_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        BgiService.Settings.SoundEnabled = TgSound.IsChecked == true;
        BgiService.SaveSettings();
        // 打开瞬间按下的那一下还是静音状态，这里补一声确认"已开启"
        if (BgiService.Settings.SoundEnabled) SoundService.Play("toggle.wav");
    }

    // ===== 通知设置卡 =====

    private async void BtnTestWebhook_Click(object sender, RoutedEventArgs e)
    {
        var ch = NotifyService.ParseChannel(BgiService.Settings.NotifyMode);
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
            BgiService.PublishLog(ok
                ? $"✓ 测试消息已通过「{NotifyService.NameOf(ch)}」发送"
                : $"✗ {NotifyService.NameOf(ch)} 测试失败：{err}");
        }
        finally { BtnTestWebhook.IsEnabled = true; }
    }

    private void BtnCopyWebhook_Click(object sender, RoutedEventArgs e)
    {
        string url = $"http://127.0.0.1:{BgiService.Settings.BgiWebhookPort}/webhook";
        if (ClipboardHelper.TrySetText(url))
            BgiService.PublishLog("✓ 已复制 BGI Webhook 地址");
        else
            ClipboardHelper.ShowManualCopyFallback(url, this); // 兜底：只读可全选文本框 + 手动复制提示
    }

    // ===== 主题（四段式分段控件）=====

    private void SegTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeSeg) return;
        string name = (sender as FrameworkElement)?.Tag as string ?? "System";

        // 「自定义」只是入口：打开编辑器实时预览，是否落到 Custom 由对话框决定
        if (name == "Custom")
        {
            OpenCustomThemeEditor();
            return;
        }

        BgiService.Settings.Theme = name;
        BgiService.SaveSettings();
        Theme.Set(name);
        RefreshMainButton();
        RefreshSentinelCard();
    }

    /// <summary>打开自定义主题编辑器；取消时把分段控件退回当前真实主题</summary>
    private void OpenCustomThemeEditor()
    {
        try
        {
            var dlg = new CustomThemeWindow { Owner = this };
            bool ok = dlg.ShowDialog() == true;
            SyncThemeSeg();
            if (ok)
            {
                RefreshMainButton();
                RefreshSentinelCard();
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开自定义主题失败：" + ex.Message);
            SyncThemeSeg();
        }
    }

    // ===== 底部状态条 / 横幅 =====

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

    private void ShowBanner(string msg)
    {
        BannerText.Text = "⚠ " + msg;
        BannerBorder.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Interval = TimeSpan.FromSeconds(30);
        _bannerTimer.Tick -= BannerTimer_Tick;
        _bannerTimer.Tick += BannerTimer_Tick;
        _bannerTimer.Start();
    }

    private void BannerTimer_Tick(object? sender, EventArgs e)
    {
        _bannerTimer.Stop();
        BannerBorder.Visibility = Visibility.Collapsed;
    }

    // ===== 定时检查（分钟级比对，当天只跑一次，错过不补） =====

    private void CheckSchedule()
    {
        try
        {
            var s = BgiService.Settings;
            if (!s.ScheduleEnabled) return;
            var now = DateTime.Now;
            if (now.Hour != s.ScheduleHour || now.Minute != s.ScheduleMinute) return;
            // 星期掩码：今天未勾选则跳过
            int dayBit = ((int)now.DayOfWeek + 6) % 7;
            if ((s.ScheduleWeekdays & (1 << dayBit)) == 0) return;
            if (_lastScheduleRun.Date == now.Date) return;

            _lastScheduleRun = now;
            if (BgiService.IsRunning)
            {
                string msg = $"定时：到达 {now:HH:mm}，但 BetterGI 已在运行，跳过";
                BgiService.PublishLog(msg);
                LogService.LogEvent(msg);
            }
            else
            {
                LogService.LogEvent($"定时：到达 {now:HH:mm}，自动启动一条龙");
                BgiService.PublishLog($"✓ 定时：到达 {now:HH:mm}，自动启动一条龙");
                _ = BgiService.StartDragonOrGroupsAsync("定时");
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("定时检查异常：" + ex.Message);
        }
    }

    // ===== 折叠卡（§4：标题行整行可点，220ms 高度过渡）=====

    private void FoldHeader_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var btn = sender as Button;
            FrameworkElement? body = btn == BtnFoldSettings ? SettingsBody
                                  : btn == BtnFoldSystem ? SystemBody
                                  : btn == BtnFoldNotify ? NotifyBody : null;
            if (btn == null || body == null) return;
            bool expand = body.Visibility != Visibility.Visible;
            btn.Tag = expand ? "open" : null;
            AnimateFold(body, expand);
        }
        catch { }
    }

    private void AnimateFold(FrameworkElement body, bool expand)
    {
        try
        {
            // 关键：动画必须有明确起止值。Height 为 NaN 时让 DoubleAnimation 自行推断起点会退化为
            // NaN 动画 → 布局反复失效 → 界面假死（用户反馈的"展开后再收缩卡死"）。
            body.BeginAnimation(HeightProperty, null);
            if (expand)
            {
                if (body.Visibility == Visibility.Visible) return;
                body.Visibility = Visibility.Visible;
                body.Height = double.NaN;   // 先自适应一次，量出真实高度
                body.UpdateLayout();
                double h = Math.Max(1, body.ActualHeight);
                body.Height = 0;
                var anim = new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                anim.Completed += (_, _) =>
                {
                    try { body.BeginAnimation(HeightProperty, null); body.Height = double.NaN; } catch { }
                };
                body.BeginAnimation(HeightProperty, anim);
            }
            else
            {
                if (body.Visibility != Visibility.Visible) return;
                double h = Math.Max(1, body.ActualHeight);
                body.Height = h;            // 固定像素高度，绝不从 NaN 起手
                var anim = new DoubleAnimation(h, 0, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                anim.Completed += (_, _) =>
                {
                    try
                    {
                        body.BeginAnimation(HeightProperty, null);
                        body.Visibility = Visibility.Collapsed;
                        body.Height = double.NaN;
                    }
                    catch { }
                };
                body.BeginAnimation(HeightProperty, anim);
            }
        }
        catch
        {
            // 动效异常静默降级为直切
            try
            {
                body.BeginAnimation(HeightProperty, null);
                body.Height = double.NaN;
                body.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }
    }

    // ===== 次按钮：仅启动 BGI（不跑一条龙）=====

    private async void BtnStartBgi_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!MainButtonPathOk())
        {
            ShakeMainButton();
            BgiService.PublishLog("✗ 请先选择有效的 BetterGI 路径");
            return;
        }
        _busy = true;
        _busyStopping = false;
        RefreshMainButton();
        try
        {
            var (ok, err) = await BgiService.StartCore("", "仅启动");
            if (!ok) BgiService.PublishLog("✗ 启动 BetterGI 失败：" + err);
        }
        catch (Exception ex)
        {
            BgiService.PublishLog("✗ 启动 BetterGI 失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshMainButton();
            BgiService.RefreshStatus();
        }
    }

    // ===== 窗口壳（Win11 原生圆角；Win10 自动退化为直角）=====

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch { } // 非 Win11 或 DWM 不可用 → 保持直角，不影响功能
    }

    // ===== 启动居中（多显示器 / 高 DPI 安全）=====

    /// <summary>
    /// 自动化验证等场景置 true：完全尊重调用方设置的 Left/Top，不做任何自动定位。
    /// </summary>
    public static bool SuppressAutoCenter { get; set; }

    private bool _centeredOnce;

    /// <summary>
    /// 把窗口放到"当前显示器"工作区的正中。
    ///
    /// 为什么不用 WPF 的 Left/Top（DIP）：
    ///  - 多屏 + 混合缩放时，DIP 的原点/比例随显示器变化，算出来会在副屏上偏移；
    ///  - SizeToContent=Height 让高度在 Show 之前是未知的，按旧尺寸居中会偏上。
    /// 所以统一用 Win32 的物理像素（GetWindowRect / MONITORINFO.rcWork）计算，再 SetWindowPos。
    /// 目标显示器取"鼠标所在那块"（与开始菜单/通知的直觉一致），失败则退回窗口最近的显示器。
    /// </summary>
    private void CenterOnActiveMonitor()
    {
        if (_centeredOnce) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr mon = IntPtr.Zero;
            if (GetCursorPos(out POINT pt)) mon = MonitorFromPoint(pt, MonitorDefaultToNearest);
            if (mon == IntPtr.Zero) mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return;

            RECT work = mi.rcWork;
            if (work.Right <= work.Left || work.Bottom <= work.Top) return;

            // 先按该屏工作区限制最大高度：旧实现固定用主屏 WorkArea，
            // 换到更矮的副屏时窗口会顶出屏幕下沿。
            double scale = 1.0;
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 48) scale = dpi / 96.0;
            }
            catch { }
            double maxDip = (work.Bottom - work.Top) / scale - 24;
            if (maxDip > 0) MaxHeight = Math.Max(480, maxDip);
            UpdateLayout();   // 让 MaxHeight 立刻生效，再取真实尺寸

            if (!GetWindowRect(hwnd, out RECT wr)) return;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return;

            int x = work.Left + (work.Right - work.Left - w) / 2;
            int y = work.Top + (work.Bottom - work.Top - h) / 2;
            // 窗口比工作区还大时贴左上，保证标题栏可拖
            x = Math.Max(work.Left, Math.Min(x, work.Right - w));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - h));

            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            _centeredOnce = true;
        }
        catch { }
    }

    private const int MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter,
                                            int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ===== 标题栏 / 关闭 =====

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch { }
    }

    private void BtnMinWin_Click(object sender, RoutedEventArgs e)
    {
        try { WindowState = WindowState.Minimized; } catch { }
    }

    private void BtnCloseWin_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!App.ForceExit && BgiService.Settings.StayInTray)
        {
            e.Cancel = true;
            Hide();
            App.Instance?.ShowBalloon(NotifyLevel.Info, "一条龙管家",
                "一条龙管家 仍在后台运行，双击托盘图标可打开。");
        }
    }
}
