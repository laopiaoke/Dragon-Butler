using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 首启向导：选工具 → 找路径 → 每天几点跑 → 跑完怎么提醒 → 完成。
/// 只在"首次运行且没有任何已配置工具"时由主界面弹出，可随时跳过。
///
/// v1.25 加了第 3 步（推送渠道）。原因是通知默认从「Windows 通知」改成了「不推送」：
/// 默认改安静之后，得在第一次打开的地方把选择权交出去，否则「默认不推送」就变成
/// 「这软件没有通知」——两件事不一样，前者是选择，后者是缺陷。
///
/// v1.41（任务书 M2）：步骤改成 工具 / 路径 / 时间 / 通知 / 完成。
/// 「每天几点跑」提到第 3 步，是因为定时是这个软件的入口——装了不用定时的人，
/// 每次都要自己点一下，那不叫自动化。开机自启两项挪到完成页收尾，
/// 它俩是「顺手」而不是「必经」，摆在中间会让人以为不答就过不去。
/// </summary>
public partial class FirstRunWindow : Window
{
    private int _step = 1;

    /// <summary>v1.38：向导共五步（原来是四步，中间插了「开机自启」那一页）。</summary>
    private const int TotalSteps = 5;
    private readonly Dictionary<string, string> _found = new();
    private readonly Dictionary<string, CheckBox> _picks = new();
    private readonly Dictionary<string, RadioButton> _notifyPicks = new();

    /// <summary>已经打过字、但还没落盘的地址，按渠道分开记（换渠道再换回来不该丢）。</summary>
    private readonly Dictionary<string, string> _notifyTyped = new();

    /// <summary>上一次 SyncNotifyStep 时显示的是哪个渠道，用来在切换前把当前输入收起来。</summary>
    private string _notifyShownMode = "";

    /// <summary>已经自动找过的工具：第 2 步只自动跑一次，来回翻页不该反复扫盘。</summary>
    private readonly HashSet<string> _autoTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>点了「完成并试跑一次」之后置 true，由主窗读它决定要不要立刻跑第一个工具。</summary>
    public bool TrialRequested { get; private set; }

    /// <summary>向导里能配的渠道：只放「填一个地址就能用」的。
    /// Telegram 要两个字段、邮件要七行，塞进向导会把这一页撑成表单，
    /// 那两个留在通知卡里配——这里给一句提示就够。</summary>
    private static readonly (string Mode, string Label, string FieldLabel, string Note)[] NotifyChoices =
    {
        ("Off", "不推送", "", ""),
        ("Toast", "Windows 通知", "", ""),
        ("Balloon", "托盘气泡", "", ""),
        ("Wecom", "企业微信", "群机器人 Webhook",
            "群设置 → 群机器人 → 添加机器人 → 复制 Webhook 地址"),
        ("Feishu", "飞书", "自定义机器人 Webhook",
            "开启签名校验的群，密钥稍后在「通知」卡里补"),
        ("DingTalk", "钉钉", "自定义机器人 Webhook",
            "安全设置选「加签」的，密钥稍后在「通知」卡里补"),
        ("Bark", "Bark（iPhone）", "推送地址",
            "形如 https://api.day.app/你的Key，可填自建服务器"),
        ("Xxtui", "xxtui", "API 地址或 Key",
            "只填 Key 会自动补全成 https://www.xxtui.com/xxtui/Key"),
        ("Webhook", "自定义 Webhook", "地址",
            "POST JSON：source / level / title / message / timestamp"),
    };

    /// <summary>v1.41 / M2-8：渠道分三组。分组的意义是让人先判断「我要发到哪儿」，
    /// 再去认具体是哪个软件——九个胶囊平铺时，找「飞书」和找「Bark」一样费劲。</summary>
    private static readonly (string Group, string[] Modes)[] NotifyGroups =
    {
        ("本机", new[] { "Toast", "Balloon" }),
        ("IM", new[] { "Wecom", "Feishu", "DingTalk" }),
        ("手机", new[] { "Bark", "Xxtui", "Webhook" }),
    };

    /// <summary>v1.41 / M2-1：第 3 步的时间预设。</summary>
    private static readonly (int Hour, int Minute)[] TimePresets =
    {
        (8, 0), (12, 0), (23, 30),
    };

    /// <summary>「我自己挑」这一档的 Tag。</summary>
    private const string CustomTimeTag = "custom";

    public FirstRunWindow()
    {
        InitializeComponent();
        BuildToolPicks();
        ApplyQueueSelection();
        BuildNotifyPicks();
        BuildTimePicks();
        UpdateUi();
        Loaded += (_, _) => FitToOwner();
    }

    /// <summary>
    /// v1.41 / M3-1：主页那张「三步就能开始」的清单会跳到这里来。
    /// 不新建一个「只问时间」的小窗——那等于把向导拆成两套步骤逻辑，
    /// 以后加一步要改两处。
    /// </summary>
    public void JumpToStep(int step)
    {
        _step = Math.Clamp(step, 1, TotalSteps);
        UpdateUi();
    }

    /// <summary>
    /// v1.41 / M2-10：向导不再是一张飘在桌面上的小卡，而是压在主窗上的一层遮罩。
    /// 窗口本身铺满主窗，中间那张 760 宽的卡才是内容。
    /// </summary>
    private void FitToOwner()
    {
        try
        {
            if (Owner == null) return;
            var o = Owner;
            Left = o.Left;
            Top = o.Top;
            Width = o.ActualWidth > 0 ? o.ActualWidth : o.Width;
            Height = o.ActualHeight > 0 ? o.ActualHeight : o.Height;
        }
        catch { }
    }

    /// <summary>
    /// v1.22：工具清单不再写死三个。列表来自 ToolCatalog（AUTO-MAS 那边有的都收进来了），
    /// 只显示「已经在队列里 / 已经配好路径 / 是内置」的那几个，
    /// 顺序也按这个优先级——让人先看到自己已经有的，而不是从十六行里找。
    /// </summary>
    private void BuildToolPicks()
    {
        ToolPickHost.Children.Clear();
        _picks.Clear();

        var queue = AppState.Settings.Queue ?? new List<QueueEntry>();
        var interesting = new List<ToolProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Want(ToolProfile? p)
        {
            if (p == null) return;
            if (!seen.Add(p.ToolId)) return;
            interesting.Add(p);
        }

        foreach (var entry in queue) Want(ProfileRegistry.Get(entry.ToolId));
        foreach (var known in ToolCatalog.All)
        {
            if (known.BuiltIn) Want(ProfileRegistry.Get(known.ToolId));
        }
        foreach (var profile in ProfileRegistry.All)
        {
            if (!string.IsNullOrWhiteSpace(profile.ExecutablePath)) Want(profile);
        }
        // v1.22：AUTO-MAS 托管列表里有的工具这里也要有。之前只列三个，
        // 用别的脚本的人第一眼就找不到自己那款，只能关掉向导再回头加。
        foreach (var known in ToolCatalog.All) Want(ProfileRegistry.Get(known.ToolId));

        foreach (var profile in interesting) ToolPickHost.Children.Add(BuildPickRow(profile));

        if (_picks.Count == 0)
        {
            ToolPickHost.Children.Add(new TextBlock
            {
                Text = "没有可选的工具，先「跳过」，进主界面后点「＋ 添加」。",
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush?)TryFindResource("TextTertiaryBrush") ?? Brushes.Gray,
            });
        }
        RefreshPickSummary();
    }

    /// <summary>v1.41 / M2-7：命令行类工具的图标画成 <c>&gt;_</c>。</summary>
    private static bool LooksLikeCommandLine(ToolProfile profile)
    {
        string name = profile.DisplayName ?? "";
        if (name.Contains("命令行", StringComparison.Ordinal)
            || name.Contains("CLI", StringComparison.OrdinalIgnoreCase)) return true;

        var known = ToolCatalog.Find(profile.ToolId);
        string shortName = known?.ShortName ?? "";
        return shortName.Contains("CLI", StringComparison.OrdinalIgnoreCase);
    }

    private UIElement BuildPickRow(ToolProfile profile)
    {
        var known = ToolCatalog.Find(profile.ToolId);

        // v1.31：从「一行一个」改成「一格一个」，按 Windows 桌面图标的尺寸与手感做。
        // 两个理由：十六行要滚三屏才看得完；一行一个的列表里图标小得只算装饰，
        // 而这一页恰恰要人做「认出自己在用的那个」这件事。
        var tile = new Border
        {
            Width = 92,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(4, 8, 4, 7),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderBrush = (Brush?)TryFindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1.4),
            Cursor = Cursors.Hand,
            ToolTip = known != null && !string.IsNullOrWhiteSpace(known.Blurb)
                ? profile.DisplayName + "\n" + known.Blurb
                : profile.DisplayName,
        };

        // 图标 44×44：和托管配置窗口里那格一个量级，两处看起来是同一套东西。
        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(11),
            Background = (Brush?)TryFindResource("ControlBgBrush") ?? Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var bitmap = ToolIconService.GetBitmap(profile);
        if (bitmap != null)
        {
            icon.Child = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(5),
            };
        }
        else if (LooksLikeCommandLine(profile))
        {
            // M2-7：命令行工具用一个终端提示符当图标，一眼能和图形界面的那几个分开。
            icon.Child = new TextBlock
            {
                Text = ">_",
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush?)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
            };
        }
        else
        {
            icon.Child = new TextBlock
            {
                Text = profile.DisplayName.Length > 0 ? profile.DisplayName[..1] : "?",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush?)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
            };
        }

        // 勾选框压在图标左上角——桌面图标选中时那个角标的位置。
        var check = new CheckBox
        {
            Style = TryFindResource("WizTileCheck") as Style,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-3, -5, 0, 0),
        };

        var iconBox = new Grid();
        iconBox.Children.Add(icon);
        iconBox.Children.Add(check);

        var nameText = new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 82,
            MaxHeight = 30,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.White,
        };
        if (known != null && !string.IsNullOrWhiteSpace(known.GameName))
            nameText.ToolTip = profile.DisplayName + "（" + known.GameName + "）";

        var stack = new StackPanel();
        stack.Children.Add(iconBox);
        stack.Children.Add(nameText);
        tile.Child = stack;

        // 整格可点：勾选框只有 17px，点在名字或图标上都该算。
        tile.MouseLeftButtonUp += (_, _) => check.IsChecked = check.IsChecked != true;
        check.Checked += (_, _) =>
        {
            tile.BorderBrush = (Brush?)TryFindResource("BrandBrush") ?? Brushes.Green;
            tile.Background = (Brush?)TryFindResource("BrandSoftBrush") ?? Brushes.Transparent;
            RefreshPickSummary();
        };
        check.Unchecked += (_, _) =>
        {
            tile.BorderBrush = (Brush?)TryFindResource("BorderBrush") ?? Brushes.Gray;
            tile.Background = Brushes.Transparent;
            RefreshPickSummary();
        };

        _picks[profile.ToolId] = check;
        return tile;
    }

    private void RefreshPickSummary()
    {
        int on = _picks.Count(kv => kv.Value.IsChecked == true);
        TbPickSummary.Text = on == 0
            ? "一个都没开。选好之后点「下一步」，或者「跳过」稍后再设。"
            : $"已选 {on} 个。下一步给它们找主程序。";
    }

    private void ApplyQueueSelection()
    {
        var queue = AppState.Settings.Queue ?? new List<QueueEntry>();
        foreach (var kv in _picks)
        {
            string toolId = kv.Key;
            var entry = queue.FirstOrDefault(q => string.Equals(q.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
            // 第一次打开时默认只把 BetterGI 打上：这不是「推荐」，是「多数人第一个装的是它」
            kv.Value.IsChecked = entry != null ? entry.Enabled : string.Equals(toolId, "bettergi", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateUi()
    {
        TbStep.Text = $"第 {_step} 步 / 共 {TotalSteps} 步";
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5.Visibility = _step == 5 ? Visibility.Visible : Visibility.Collapsed;

        // v1.41 / M2-6：五段进度条。当前那段高亮，走过的填充，没到的留灰。
        var done = (Brush?)TryFindResource("BrandBrush") ?? Brushes.Green;
        var current = (Brush?)TryFindResource("BrandBrush") ?? Brushes.Green;
        var todo = (Brush?)TryFindResource("BorderStrongBrush") ?? Brushes.Gray;
        var bars = new[] { Prog1, Prog2, Prog3, Prog4, Prog5 };
        for (int i = 0; i < bars.Length; i++)
        {
            bars[i].Background = i + 1 < _step ? done : i + 1 == _step ? current : todo;
            bars[i].Opacity = i + 1 == _step ? 1.0 : 0.85;
        }

        // v1.41 / M2-4：最后一步不留「跳过」和「上一步」——那时候该问的已经问完了，
        // 再摆一个退出口只会让人怀疑自己漏了什么。
        bool last = _step == TotalSteps;
        BtnSkip.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        BtnBack.Visibility = _step > 1 && !last ? Visibility.Visible : Visibility.Collapsed;
        BtnDoneLater.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Content = last ? "完成并试跑一次" : "下一步";

        TbNextPreview.Text = _step switch
        {
            1 => "下一步：给它们找主程序，多数情况下会自动找到。",
            2 => "下一步：定个每天几点跑。",
            3 => "下一步：选跑完之后要不要提醒你。",
            4 => "下一步：看一眼总结，然后就能开跑了。",
            _ => "",
        };

        if (_step == 2) BuildFindRows();
        if (_step == 3) SyncTimeStep();
        if (_step == 4) SyncNotifyStep();
        if (_step == 5) { BuildDoneSummary(); SyncStartupStep(); }
    }

    private List<ToolProfile> SelectedProfiles()
    {
        return _picks.Where(kv => kv.Value.IsChecked == true)
                     .Select(kv => ProfileRegistry.Get(kv.Key))
                     .ToList();
    }

    private void BuildFindRows()
    {
        FindHost.Children.Clear();
        foreach (var profile in SelectedProfiles())
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = profile.DisplayName,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 88,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.Black,
            });

            var box = new TextBox
            {
                // 回到上一步再进来时，得把刚「自动寻找」的结果带回来。
                // 只读 profile.ExecutablePath 的话，_found 里明明有值，框里却还是旧路径，
                // 而点「下一步」存的是 _found——看得见和存得下的不是同一个东西。
                Text = _found.TryGetValue(profile.ToolId, out var kept) ? kept : profile.ExecutablePath,
                Height = 30,
                // v1.41 / M2-3：改成可编辑。有人的程序装在很偏的地方，浏览器一点点翻
                // 比直接粘一条路径慢得多；自动找到时它照样是填好的。
                IsReadOnly = false,
                Style = TryFindResource("InputBox") as Style,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = profile.ToolId,
            };

            var placeholder = new TextBlock
            {
                Text = "比如 D:\\Games\\BetterGI\\BetterGI.exe",
                FontSize = 11.5,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Foreground = (Brush?)TryFindResource("TextTertiaryBrush") ?? Brushes.Gray,
                Visibility = string.IsNullOrWhiteSpace(box.Text) ? Visibility.Visible : Visibility.Collapsed,
            };
            box.TextChanged += (_, _) =>
            {
                placeholder.Visibility = string.IsNullOrWhiteSpace(box.Text) ? Visibility.Visible : Visibility.Collapsed;
                _found[profile.ToolId] = box.Text ?? "";
            };

            var boxHost = new Grid();
            boxHost.Children.Add(box);
            boxHost.Children.Add(placeholder);
            Grid.SetColumn(boxHost, 1);
            row.Children.Add(boxHost);

            // v1.41 / M2-3：找到之后留一个绿勾，让人不用去读路径也能确认这一步过了。
            var tick = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush?)TryFindResource("SuccessBrush") ?? Brushes.Green,
                Visibility = string.IsNullOrWhiteSpace(box.Text) ? Visibility.Collapsed : Visibility.Visible,
            };
            Grid.SetColumn(tick, 2);
            row.Children.Add(tick);

            var find = new Button
            {
                Content = "自动寻找",
                Style = TryFindResource("WizGhost") as Style,
                Height = 30,
                MinWidth = 84,
                Tag = profile.ToolId,
                Margin = new Thickness(0, 0, 6, 0),
            };
            find.Click += (_, _) => AutoFind(profile, box, tick);
            Grid.SetColumn(find, 3);
            row.Children.Add(find);

            var browse = new Button
            {
                Content = "浏览",
                Style = TryFindResource("WizGhost") as Style,
                Height = 30,
                MinWidth = 64,
                Tag = profile.ToolId,
            };
            browse.Click += (_, _) =>
            {
                Browse(profile, box);
                tick.Visibility = string.IsNullOrWhiteSpace(box.Text) ? Visibility.Collapsed : Visibility.Visible;
            };
            Grid.SetColumn(browse, 4);
            row.Children.Add(browse);

            FindHost.Children.Add(row);

            // M2-3：进这一步就先替你找一遍。找得到的人只用点「下一步」，一次都不用手动选。
            if (_autoTried.Add(profile.ToolId))
                AutoFind(profile, box, tick);
        }

        TbFindSummary.Text = "提示：多数情况下点「自动寻找」就能命中；如果没找到，选到工具的 exe 即可。";
    }

    private void AutoFind(ToolProfile profile, TextBox box, TextBlock? tick = null)
    {
        var found = ToolDiscovery.TryAutoDiscover(profile, out string source);
        if (string.IsNullOrWhiteSpace(found))
        {
            // 没找到就说没找到，并把框留空、提示照旧——别猜一个官方安装位置填进去。
            TbFindSummary.Text = $"{profile.DisplayName}：未自动找到，请手动选择文件夹。";
            if (tick != null && string.IsNullOrWhiteSpace(box.Text)) tick.Visibility = Visibility.Collapsed;
            return;
        }
        box.Text = found;
        _found[profile.ToolId] = found;
        if (tick != null) tick.Visibility = Visibility.Visible;
        TbFindSummary.Text = $"{profile.DisplayName}：已自动找到（{source}），可直接下一步。";
    }

    private void Browse(ToolProfile profile, TextBox box)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"选择 {profile.DisplayName} 的主程序",
            Filter = "可执行文件|*.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        box.Text = dlg.FileName;
        _found[profile.ToolId] = dlg.FileName;
    }

    // ===== 第 3 步：每天几点跑（v1.41 / M2-1）=====
    //
    // 时间写在 AppState.Settings.ScheduleHour / ScheduleMinute 上——和主页那张
    // 「每天自动跑一条龙」卡是同一份数据，不另存一份。两份时间最后一定会不一致，
    // 而用户只会看到「向导里设的 8 点，主页显示 23:30」。

    private void BuildTimePicks()
    {
        TimeChipHost.Children.Clear();
        foreach (var (hour, minute) in TimePresets)
        {
            var chip = new RadioButton
            {
                Content = $"{hour:00}:{minute:00}",
                Tag = $"{hour:00}:{minute:00}",
                Style = TryFindResource("WizChip") as Style,
                GroupName = "WizTime",
            };
            chip.Checked += (_, _) =>
            {
                CustomTimeHost.Visibility = Visibility.Collapsed;
                RefreshTimeSummary();
            };
            TimeChipHost.Children.Add(chip);
        }

        var custom = new RadioButton
        {
            Content = "我自己挑",
            Tag = CustomTimeTag,
            Style = TryFindResource("WizChip") as Style,
            GroupName = "WizTime",
        };
        custom.Checked += (_, _) =>
        {
            CustomTimeHost.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(TbTimeHour.Text)) TbTimeHour.Text = AppState.Settings.ScheduleHour.ToString("00");
            if (string.IsNullOrWhiteSpace(TbTimeMinute.Text)) TbTimeMinute.Text = AppState.Settings.ScheduleMinute.ToString("00");
            TbTimeHour.Focus();
            TbTimeHour.SelectAll();
            RefreshTimeSummary();
        };
        TimeChipHost.Children.Add(custom);
    }

    /// <summary>进这一页时把当前设置回显：已经是 08:00 就选中 08:00，不在预设里就落到「我自己挑」。</summary>
    private void SyncTimeStep()
    {
        int h = AppState.Settings.ScheduleHour;
        int m = AppState.Settings.ScheduleMinute;

        string hit = "";
        foreach (var (hour, minute) in TimePresets)
            if (hour == h && minute == m) hit = $"{hour:00}:{minute:00}";

        bool presetHit = false;
        foreach (var child in TimeChipHost.Children)
        {
            if (child is not RadioButton chip) continue;
            string tag = chip.Tag as string ?? "";
            if (tag == CustomTimeTag)
            {
                chip.IsChecked = !presetHit && hit.Length == 0;
            }
            else
            {
                bool on = tag == hit;
                chip.IsChecked = on;
                if (on) presetHit = true;
            }
        }

        TbTimeHour.Text = h.ToString("00");
        TbTimeMinute.Text = m.ToString("00");
        CustomTimeHost.Visibility = (presetHit || hit.Length > 0) ? Visibility.Collapsed : Visibility.Visible;
        RefreshTimeSummary();
    }

    /// <summary>当前选中的时间（小时，分钟）。没选或填得不像时间就退回设置里现有的值。</summary>
    private (int Hour, int Minute) SelectedTime()
    {
        foreach (var child in TimeChipHost.Children)
        {
            if (child is not RadioButton chip || chip.IsChecked != true) continue;
            string tag = chip.Tag as string ?? "";
            if (tag == CustomTimeTag) break;
            var parts = tag.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int ph) && ph is >= 0 and <= 23
                && int.TryParse(parts[1], out int pm) && pm is >= 0 and <= 59)
                return (ph, pm);
        }

        int hh = int.TryParse((TbTimeHour.Text ?? "").Trim(), out int a) && a is >= 0 and <= 23 ? a : AppState.Settings.ScheduleHour;
        int mm = int.TryParse((TbTimeMinute.Text ?? "").Trim(), out int b) && b is >= 0 and <= 59 ? b : AppState.Settings.ScheduleMinute;
        return (hh, mm);
    }

    private void RefreshTimeSummary()
    {
        var (h, m) = SelectedTime();
        TbTimeSummary.Text = $"到点（每天 {h:00}:{m:00}）自动拉起选中的工具跑一遍。想改随时改，主页那张卡就是它。";
        TbTimeNote.Text = "24 小时制，0–23 点";
    }

    private void TbTimeField_LostFocus(object sender, RoutedEventArgs e)
    {
        // 只收两位数字。填成 25 点这种明显不像时间的，留给人自己看，不偷偷改成 8 点。
        if (sender is TextBox box)
        {
            string raw = (box.Text ?? "").Trim();
            if (raw.Length == 0) return;
            if (!int.TryParse(raw, out int v))
            {
                TbTimeNote.Text = "填数字，比如 08";
                return;
            }
            int max = ReferenceEquals(box, TbTimeHour) ? 23 : 59;
            if (v < 0 || v > max)
            {
                TbTimeNote.Text = ReferenceEquals(box, TbTimeHour) ? "小时是 0–23" : "分钟是 0–59";
                return;
            }
            box.Text = v.ToString("00");
            TbTimeNote.Text = "24 小时制，0–23 点";
        }
        RefreshTimeSummary();
    }

    /// <summary>把向导里选的时间落进定时配置——和主页定时卡共用一份。</summary>
    private void SaveTimeChoice()
    {
        var (h, m) = SelectedTime();
        var s = AppState.Settings;
        bool changed = s.ScheduleHour != h || s.ScheduleMinute != m;
        s.ScheduleHour = h;
        s.ScheduleMinute = m;
        s.ScheduleEnabled = true;
        AppState.SaveSettings();
        if (changed)
            LogService.LogEvent($"首启向导：定时设为每天 {h:00}:{m:00}");
    }

    // ===== 第 5 步：完成（v1.41 / M2-4、M2-5）=====

    private void BuildDoneSummary()
    {
        DoneSummaryHost.Children.Clear();
        string? firstBrokenTool = null;

        foreach (var profile in SelectedProfiles())
        {
            bool ok = !string.IsNullOrWhiteSpace(profile.ExecutablePath);
            if (!ok && firstBrokenTool == null) firstBrokenTool = profile.ToolId;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 0),
            };
            row.Children.Add(new TextBlock
            {
                Text = ok
                    ? $"{profile.DisplayName}：已就绪"
                    : $"{profile.DisplayName}：还没设置路径（可稍后在工具卡里设置）",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush?)TryFindResource(ok ? "TextPrimaryBrush" : "WarningBrush")
                             ?? Brushes.Gray,
            });

            // M2-5：没办完的事就地给个入口，不用关掉向导再去找。
            if (!ok)
            {
                var link = new Button
                {
                    Content = "现在去设置",
                    Style = TryFindResource("WizLink") as Style,
                    Margin = new Thickness(8, 0, 0, 0),
                    Tag = 2,
                };
                link.Click += (_, _) => { _step = 2; UpdateUi(); };
                row.Children.Add(link);
            }
            DoneSummaryHost.Children.Add(row);
        }

        // 定时这一行永远有值（第 3 步一定选过一个），所以不给「现在去设置」，
        // 只把将要发生的事说清楚。
        var (h, m) = SelectedTime();
        DoneSummaryHost.Children.Add(new TextBlock
        {
            Text = $"每天 {h:00}:{m:00}：自动跑一遍",
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.White,
        });

        var notifyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 0),
        };
        notifyRow.Children.Add(new TextBlock
        {
            Text = "跑完提醒：" + DescribeNotifySelection(),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.White,
        });
        DoneSummaryHost.Children.Add(notifyRow);
    }

    // ===== 第 4 步：推送渠道（v1.25）=====

    /// <summary>
    /// 建渠道胶囊。渠道清单直接来自 <see cref="NotifyChoices"/>，
    /// 与主窗通知卡用的是同一套 <see cref="PushChannel"/> 解析（模式串取自 Tag），
    /// 所以这里选的、主窗显示的、真发出去的永远是同一个值。
    /// </summary>
    private void BuildNotifyPicks()
    {
        NotifyTopHost.Children.Clear();
        NotifyGroupsHost.Children.Clear();
        _notifyPicks.Clear();

        RadioButton MakeChip(string mode, string label)
        {
            var chip = new RadioButton
            {
                Content = label,
                Tag = mode,
                Style = TryFindResource("WizChip") as Style,
                GroupName = "WizNotify",
            };
            chip.Checked += (_, _) => SyncNotifyStep();
            _notifyPicks[mode] = chip;
            return chip;
        }

        // M2-8：「不推送」不是一个渠道，是「不选渠道」，所以单独置顶。
        var off = NotifyChoices.First(c => c.Mode == "Off");
        NotifyTopHost.Children.Add(MakeChip(off.Mode, off.Label));

        foreach (var (group, modes) in NotifyGroups)
        {
            var head = new TextBlock
            {
                Text = group,
                Style = TryFindResource("WizGroupLabel") as Style,
            };
            NotifyGroupsHost.Children.Add(head);

            var wrap = new WrapPanel();
            foreach (string mode in modes)
            {
                var choice = NotifyChoices.FirstOrDefault(c => c.Mode == mode);
                if (choice.Label == null) continue;
                wrap.Children.Add(MakeChip(choice.Mode, choice.Label));
            }
            NotifyGroupsHost.Children.Add(wrap);
        }

        // 预选当前设置。老配置升级上来是 Off（见 AppState.LoadSettings 的迁移），
        // 所以默认停在「不推送」——和这个程序平时的行为一致，不吓人。
        string hit = NormalizeMode(AppState.Settings.NotifyMode);
        if (_notifyPicks.TryGetValue(hit, out var picked)) picked.IsChecked = true;
        else if (_notifyPicks.TryGetValue("Off", out var fallback)) fallback.IsChecked = true;
    }

    /// <summary>设置里的值 → 向导里有的那几档。没列进向导的（Telegram/邮件/WebSocket）落到最近的一档。</summary>
    private static string NormalizeMode(string? mode)
    {
        return NotifyService.ParseChannel(mode) switch
        {
            PushChannel.Toast => "Toast",
            PushChannel.Balloon => "Balloon",
            PushChannel.Wecom => "Wecom",
            PushChannel.Feishu => "Feishu",
            PushChannel.DingTalk => "DingTalk",
            PushChannel.Bark => "Bark",
            PushChannel.Xxtui => "Xxtui",
            PushChannel.Webhook => "Webhook",
            _ => "Off",
        };
    }

    /// <summary>当前选中的渠道模式字符串（取自胶囊 Tag，不猜）。</summary>
    private string SelectedNotifyMode()
    {
        foreach (var kv in _notifyPicks)
            if (kv.Value.IsChecked == true) return kv.Key;
        return "Off";
    }

    private static (string Mode, string Label, string FieldLabel, string Note) ChoiceOf(string mode)
    {
        foreach (var c in NotifyChoices)
            if (string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase)) return c;
        return NotifyChoices[0];
    }

    private string DescribeNotifySelection()
    {
        string mode = SelectedNotifyMode();
        if (mode == "Off") return "不推送（结果在界面里自己看）";
        string name = NotifyService.NameOf(NotifyService.ParseChannel(mode));
        if (mode is "Toast" or "Balloon") return name;
        string typed = FieldFor(mode);
        return string.IsNullOrWhiteSpace(typed)
            ? $"{name}（地址还没填，稍后在「通知」卡里补）"
            : $"{name}（地址已填）";
    }

    /// <summary>
    /// 这个渠道现在该显示在框里的地址：优先用这一步里打过的字，
    /// 其次是设置里已经存着的。刻意不直接读 TextBox——切换渠道时先要把上一个渠道的字收走。
    /// </summary>
    private string FieldFor(string mode)
    {
        if (_notifyTyped.TryGetValue(mode, out var typed)) return typed;
        return ExistingNotifyField(mode);
    }

    private void SyncNotifyStep()
    {
        if (TbNotifyPickHint == null) return;
        string mode = SelectedNotifyMode();

        // 先把「刚才那个渠道」框里的字收进草稿。
        // 不做这一步的话，从飞书切到企业微信，飞书的地址会原样留在企业微信的框里，
        // 人一不小心就把飞书的地址存成了企业微信的。
        if (!string.IsNullOrEmpty(_notifyShownMode) && _notifyShownMode != mode)
            _notifyTyped[_notifyShownMode] = TbNotifyField.Text ?? "";
        _notifyShownMode = mode;

        var (_, label, fieldLabel, note) = ChoiceOf(mode);
        bool needsField = mode is not ("Off" or "Toast" or "Balloon");

        NotifyFieldHost.Visibility = needsField ? Visibility.Visible : Visibility.Collapsed;
        if (needsField)
        {
            TbNotifyFieldLabel.Text = fieldLabel;
            TbNotifyFieldNote.Text = note;
            // 换渠道时把这个渠道自己的地址带出来，别让人重填一遍
            TbNotifyField.Text = FieldFor(mode);
        }
        else
        {
            TbNotifyField.Text = "";
        }

        TbNotifyPickHint.Text = mode switch
        {
            "Off" => "什么都不弹。跑完之后结果只留在界面和日志里——这是默认值，之后随时能改。",
            "Toast" => "右下角弹一条 Windows 通知；系统把通知关掉时自动退回托盘气泡。",
            "Balloon" => "只弹托盘气泡，不写进操作中心；适合已经关掉系统通知的电脑。",
            _ => $"{label}：填完地址点「发一条试试」，能收到就说明这条路是通的。",
        };

        TbNotifyTest.Text = "";
        BtnNotifyTest.IsEnabled = mode != "Off";
    }

    /// <summary>设置里这个渠道已经存着的地址（只用于带出来，不写回）。</summary>
    private static string ExistingNotifyField(string mode)
    {
        var s = AppState.Settings;
        return mode switch
        {
            "Wecom" => s.NotifyWecomUrl ?? "",
            "Feishu" => s.NotifyFeishuUrl ?? "",
            "DingTalk" => s.NotifyDingUrl ?? "",
            "Bark" => s.NotifyBarkUrl ?? "",
            "Xxtui" => s.NotifyXxtuiUrl ?? "",
            "Webhook" => s.NotifyWebhookUrl ?? "",
            _ => "",
        };
    }

    /// <summary>把向导里选的渠道与填的地址落进设置。</summary>
    private void SaveNotifyChoice()
    {
        string mode = SelectedNotifyMode();
        AppState.Settings.NotifyMode = mode;

        string v = (TbNotifyField.Text ?? "").Trim();
        if (v.Length > 0)
        {
            switch (mode)
            {
                case "Wecom": AppState.Settings.NotifyWecomUrl = v; break;
                case "Feishu": AppState.Settings.NotifyFeishuUrl = v; break;
                case "DingTalk": AppState.Settings.NotifyDingUrl = v; break;
                case "Bark": AppState.Settings.NotifyBarkUrl = v; break;
                case "Xxtui": AppState.Settings.NotifyXxtuiUrl = v; break;
                case "Webhook": AppState.Settings.NotifyWebhookUrl = v; break;
            }
        }
        AppState.SaveSettings();
    }

    private void TbNotifyField_LostFocus(object sender, RoutedEventArgs e)
    {
        // 失焦就存：填了地址却直接关窗，也不该丢
        try { SaveNotifyChoice(); } catch { }
    }

    private async void BtnNotifyTest_Click(object sender, RoutedEventArgs e)
    {
        string mode = SelectedNotifyMode();
        if (mode == "Off") return;

        SaveNotifyChoice();
        BtnNotifyTest.IsEnabled = false;
        TbNotifyTest.Foreground = (Brush?)TryFindResource("TextTertiaryBrush") ?? Brushes.Gray;
        TbNotifyTest.Text = "正在发送…";
        try
        {
            var (ok, err) = await NotifyService.SendTestAsync(NotifyService.ParseChannel(mode));
            TbNotifyTest.Text = ok ? "✓ 发出去了，看看手机／桌面收到没有" : "✗ " + err;
            TbNotifyTest.Foreground = ok
                ? ((Brush?)TryFindResource("BrandBrush") ?? Brushes.Green)
                : ((Brush?)TryFindResource("DangerBrush") ?? Brushes.Red);
        }
        catch (Exception ex)
        {
            TbNotifyTest.Text = "✗ " + ex.Message;
        }
        finally { BtnNotifyTest.IsEnabled = true; }
    }

    // ===== 按钮 =====

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1)
        {
            SaveSelection();
            if (SelectedProfiles().Count == 0)
            {
                TbPickSummary.Text = "";
                NotifyService.Show(NotifyLevel.Warning, "龙管家", "至少勾选一个工具，或点「跳过」稍后再设置。");
                return;
            }
            _step = 2;
            UpdateUi();
            return;
        }

        if (_step == 2)
        {
            SavePaths();
            _step = 3;
            UpdateUi();
            return;
        }

        if (_step == 3)
        {
            SaveTimeChoice();
            _step = 4;
            UpdateUi();
            return;
        }

        if (_step == 4)
        {
            SaveNotifyChoice();
            _step = 5;
            UpdateUi();
            return;
        }

        Finish(trial: true);
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        UpdateUi();
    }

    /// <summary>
    /// 中途跳过：只留已经选好的工具，别的一概不写。
    /// v1.41 / M2-12：这种情况不算「走完了五步」，OnboardingCompleted 保持 false——
    /// 下次首页该给的还是那张引导卡，而不是「昨晚没跑」。
    /// </summary>
    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        SaveSelection();
        Close();
    }

    private void BtnDoneLater_Click(object sender, RoutedEventArgs e) => Finish(trial: false);

    /// <summary>v1.41 / M2-4：两个完成按钮共用的收尾。区别只有一个：要不要马上去跑一次。</summary>
    private void Finish(bool trial)
    {
        ApplyAll();
        TrialRequested = trial;
        Close();
    }

    // ===== v1.41 / M2-11：键盘 =====

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 等同旧的「跳过」：不留二次确认。向导是随时能重开的东西，
            // 弹一个「真的要跳过吗」只会让人觉得被拦住了。
            e.Handled = true;
            SaveSelection();
            Close();
            return;
        }

        if (e.Key == Key.Enter)
        {
            // 输入框里按回车是「确认这一格」，不是「翻页」。
            if (Keyboard.FocusedElement is TextBox) return;
            e.Handled = true;
            BtnNext_Click(this, new RoutedEventArgs());
        }
    }

    private void SaveSelection()
    {
        var queue = AppState.Settings.Queue ?? new List<QueueEntry>();

        // v1.33：没勾的工具不要进队列。
        //
        // 这里原来无条件 Set(每个 id, 是否勾上)，于是 16 个工具全进了 Queue（15 个 Enabled=false）。
        // 队列是「今晚要跑什么」的清单，不是「这台机器上可能有哪些工具」的清单——
        // 用户只勾了 BetterGI，结果进主界面看到一排十六个小按键，那不叫配置好了，那叫吓人。
        //
        // 没勾的不写进队列，但已经在队列里的（老用户手动加过）保留原状，只是置成不启用。
        foreach (var kv in _picks)
        {
            bool on = kv.Value.IsChecked == true;
            var existing = queue.FirstOrDefault(q => string.Equals(q.ToolId, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                if (on) queue.Add(new QueueEntry { ToolId = kv.Key, Enabled = true });
            }
            else
            {
                existing.Enabled = on;
            }
        }
        AppState.Settings.Queue = queue;

        // v1.30：桌面上摆什么 = 用户在这里勾了什么，顺序就是勾上的先后。
        //
        // 以前这一页只写 Queue.Enabled，桌面靠 EnsureQueueDefaults 顺手塞了三个内置工具，
        // 于是「我只勾了 BAAH」的人进来会看到 BetterGI / 三月七 / ok-ww 也在桌上，
        // 三个都写着「未配置」。现在桌面直接照着勾选重建，一个不多一个不少。
        // 已经有 ToolPages 的（老用户）不覆盖：那是他自己拖过的排法，比勾选顺序更值钱。
        try
        {
            var picked = new List<string>();
            foreach (var known in ToolCatalog.All)
            {
                if (_picks.TryGetValue(known.ToolId, out var pill) && pill.IsChecked == true)
                    picked.Add(known.ToolId);
            }
            if (picked.Count > 0)
                AppState.Settings.ToolPages = ToolPages.Repack(new List<List<string>> { picked });
        }
        catch { }

        // v1.23：勾上的工具必须真的有一份档案落到磁盘上。
        // 只写队列是不够的——主窗的桌面网格与自定义工具卡都是照着 ProfileRegistry.All 画的，
        // 档案不存在的话，用户在这一页勾了 BAAH，进主界面却找不到它。
        foreach (var kv in _picks)
        {
            if (kv.Value.IsChecked != true) continue;
            try { ProfileRegistry.Save(ProfileRegistry.Get(kv.Key)); } catch { }
        }

        AppState.SaveSettings();
    }

    private void SavePaths()
    {
        foreach (var kv in _found)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            var profile = ProfileRegistry.Get(kv.Key);
            profile.ExecutablePath = kv.Value;
            if (string.Equals(kv.Key, "bettergi", StringComparison.OrdinalIgnoreCase))
                AppState.Settings.BgiPath = kv.Value;
            ProfileRegistry.Save(profile);
        }
    }

    private void ApplyAll()
    {
        SaveSelection();
        SavePaths();
        SaveNotifyChoice();
        SaveTimeChoice();

        // v1.38：开机自启留到最后一步才真写。
        // 中途跳过的人不该在系统里留一条自己都不知道的启动项。
        if (WizAutoStart.IsChecked == true)
        {
            var (ok, msg) = AutoStartHelper.Set(true);
            LogService.LogEvent("首启向导：开机自启 " + msg);
            AppState.Settings.AutoStartOnBoot = ok;
            if (!ok) AppState.PublishLog("⚠ " + msg);
        }
        else
        {
            AutoStartHelper.Set(false);
            AppState.Settings.AutoStartOnBoot = false;
        }
        AppState.Settings.StartMinimized = WizStartMin.IsChecked == true;

        // v1.41 / M2-12：这两个按钮就是「走完了五步」的证据。
        AppState.Settings.OnboardingCompleted = true;
        AppState.SaveSettings();

        LogService.LogEvent("首启向导：完成");
    }

    // ===== 第 5 步：开机自启（v1.38，v1.41 从第 3 步挪过来）=====
    //
    // 为什么这两项不再单独占一步：一个还没决定要用哪些工具的人，
    // 问「要不要开机自启」是没有意义的——他不知道自己在让什么东西自启。
    // 放在完成页下方当「顺手两项」，不勾也能过，才是它该有的分量。

    /// <summary>进这一页时把当前注册表状态读回来，别让勾子显示一个想当然的值。</summary>
    private void SyncStartupStep()
    {
        bool already = AutoStartHelper.IsSet();
        WizAutoStart.IsChecked = already || AppState.Settings.StartMinimized;
        WizStartMin.IsChecked = AppState.Settings.StartMinimized;
        TbStartupNote.Text = already
            ? "这条启动项已经在注册表里了，指向的就是现在这个位置。"
            : "还没设。勾上会往当前用户的启动项里写一条，卸载时点掉这个勾就行。";
    }

    private void WizAutoStart_Click(object sender, RoutedEventArgs e)
    {
        bool on = WizAutoStart.IsChecked == true;
        // 只在「关掉」时立刻动注册表；打开留到最后一起写——
        // 用户在中途关掉向导，就不该已经在系统里留了一条。
        if (!on)
        {
            var (ok, msg) = AutoStartHelper.Set(false);
            if (ok) AppState.Settings.AutoStartOnBoot = false;
            TbStartupNote.Text = msg;
            if (!ok) WizAutoStart.IsChecked = true;
            return;
        }
        TbStartupNote.Text = AutoStartHelper.IsSet()
            ? "这条启动项已经在注册表里了。"
            : "点完成时会写进启动项。";
    }

    private void WizStartMin_Click(object sender, RoutedEventArgs e)
    {
        bool on = WizStartMin.IsChecked == true;
        if (on)
        {
            // 收进托盘要有托盘图标才点得回来，所以顺手把驻留托盘也打开。
            // 这里不弹窗拦人——向导是「一次决定一串默认值」的地方，
            // 拦一次就多一次「你为什么问我这个」。
            WizAutoStart.IsChecked = true;
            AppState.Settings.StayInTray = true;
        }
        AppState.Settings.StartMinimized = on;
        TbStartupNote.Text = on
            ? "开机后它会待在托盘里。双击托盘图标就能打开。"
            : "开机后会正常弹出窗口。";
    }
}
