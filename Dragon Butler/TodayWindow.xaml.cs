using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 今日运行（v1.13）：今天这一盘的回放。
///
/// <para>
/// 为什么开一个窗口而不是塞进主界面：主界面回答的是「接下来跑什么」，
/// 这一页回答的是「刚才那几小时到底跑成什么样」。两个问题不在同一个时刻被问，
/// 挤进同一张卡只会让两边都变挤。
/// </para>
/// <para>
/// 刻意不做的事：不做图表、不做趋势、不折算成钱。判定全在 <see cref="TodayDigest"/>，
/// 这里只负责把结果摆成一行行看得懂的东西。
/// </para>
/// </summary>
public partial class TodayWindow : Window
{
    public TodayWindow()
    {
        InitializeComponent();
        Build();
    }

    private static Brush BrushOf(string key, Brush fallback)
    {
        try { return (Brush)(Application.Current.TryFindResource(key) ?? fallback); }
        catch { return fallback; }
    }

    private void Build()
    {
        var day = DateTime.Today;

        List<GroupRecord> groups;
        try { groups = GroupArchive.GetDay(day); }
        catch (Exception ex) { LogService.LogEvent("今日运行：读配置组记录失败 " + ex.Message); groups = new List<GroupRecord>(); }

        var digest = TodayDigest.Build(groups, name =>
        {
            try { return GroupArchive.MedianMinutes(name); }
            catch { return null; }
        });

        List<JournalEntry> journal;
        try { journal = RunJournal.GetDay(day); }
        catch (Exception ex) { LogService.LogEvent("今日运行：读台账失败 " + ex.Message); journal = new List<JournalEntry>(); }

        TbHeadline.Text = digest.Headline;

        RenderBaseline();
        RenderTimeBank();
        RenderGroups(digest);
        RenderSuggestions();
        RenderJournal(journal);
        RenderDecisions();

        TbFoot.Text = "数据来自本机已有的两份记录（配置组存档 / 队列台账）。"
                    + "「往常」取最近 14 天的同组中位数，样本少于 2 次就不评价快慢。";

        int doneCount = journal.Count(e => string.Equals(e.State, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase));
        TbSummary.Text = journal.Count == 0
            ? "今天队列还没跑过"
            : $"队列 {journal.Count} 项 · 跑成 {doneCount} 项 · 没成 {journal.Count - doneCount} 项";
    }

    // ===== 配置组 =====

    /// <summary>
    /// v1.20 时间银行：这个月它替你跑了多久。没有记录时整行收起。
    /// </summary>
    private void RenderTimeBank()
    {
        try
        {
            var now = DateTime.Now;
            // 往前数一年。GroupArchive 不裁剪历史，但一年足够覆盖「从装上到现在」，
            // 也不至于为了一个数字把几年前的键全扫一遍。
            var days = GroupArchive.DailyMinutes(now.Date.AddDays(-365), now.Date)
                .Select(d => new TimeBank.Day(d.Date, d.Minutes))
                .ToList();
            var totals = TimeBank.Sum(days, now);
            string text = TimeBank.Describe(totals, now);
            TbTimeBank.Text = text;
            TbTimeBank.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            TbTimeBank.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// v1.20：跟自己的过去比。样本不足时整行收起——
    /// 「暂无数据」只会让人以为坏了，不如不出现。
    /// </summary>
    private void RenderBaseline()
    {
        try
        {
            var v = BaselineFacts.Today(DateTime.Today);
            TbBaseline.Text = v.Text;
            TbBaseline.Visibility = v.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            TbBaseline.Foreground = v.Level switch
            {
                Baseline.Level.Warn => BrushOf("WarningBrush", Brushes.Orange),
                Baseline.Level.Notice => BrushOf("WarningBrush", Brushes.Orange),
                _ => BrushOf("BrandTextBrush", Brushes.Green),
            };
        }
        catch
        {
            TbBaseline.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// v1.20：它替你做的决定。没有记录时整块收起——
    /// 「暂无数据」这行字只会让人以为功能坏了。
    /// </summary>
    private void RenderDecisions()
    {
        DecisionHost.Children.Clear();
        List<Decision> list;
        try { list = DecisionLog.Today(DateTime.Now); }
        catch { list = new List<Decision>(); }

        bool show = list.Count > 0;
        DecisionSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        TbDecisionHeader.Text = $"它替你做的决定 · {list.Count} 条";
        foreach (var d in list.Take(30))
        {
            DecisionHost.Children.Add(DecisionRow(d));
        }
    }

    private UIElement DecisionRow(Decision d)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var at = new TextBlock
        {
            Text = d.At.ToString("HH:mm"),
            FontSize = 11,
            Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(at, 0);
        row.Children.Add(at);

        var tag = new TextBlock
        {
            Text = DecisionLog.SourceName(d.Source),
            FontSize = 11,
            Foreground = BrushOf("BrandTextBrush", Brushes.Green),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(tag, 1);
        row.Children.Add(tag);

        var text = new TextBlock
        {
            Text = (d.Title.Length > 0 ? d.Title + " —— " : "") + (d.Why.Length > 0 ? d.Why : ""),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf("TextSecondaryBrush", Brushes.Gray),
        };
        Grid.SetColumn(text, 2);
        row.Children.Add(text);

        return row;
    }

    private void RenderGroups(TodayDigest.Digest digest)
    {
        GroupHost.Children.Clear();
        TbGroupHeader.Text = digest.Groups.Count == 0 ? "配置组（今天没有）" : $"配置组 · {digest.Groups.Count} 组";

        if (digest.Groups.Count == 0)
        {
            GroupHost.Children.Add(Hint("今天 BGI 还没有报过配置组。跑起来之后这里会按组列出耗时。"));
            return;
        }

        foreach (var g in digest.Groups)
            GroupHost.Children.Add(GroupRowView(g));
    }

    /// <summary>
    /// 一行：组名 + 起止 + 耗时 + 快慢评价，末尾留一段弹性空白。
    ///
    /// <para>
    /// 列宽特意把「组名 → 起止」贴在一起：如果让组名去吃余量，中间会空出两百多像素，
    /// 读起来像两张表被拉开。空白放在行尾不影响任何一行，也顺手给长组名留了伸展空间。
    /// </para>
    /// </summary>
    private static FrameworkElement GroupRowView(TodayDigest.GroupRow g)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 组名
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 起止
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });// 耗时
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 评价
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 填空

        var name = new TextBlock
        {
            Text = g.Name,
            FontSize = 12,
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextPrimaryBrush", Brushes.Gray),
        };
        ToolTipService.SetToolTip(name, g.Name);
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var span = new TextBlock
        {
            Text = g.Running ? g.Start + " → 进行中" : g.Start + " → " + g.End,
            FontSize = 11,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
        };
        Grid.SetColumn(span, 1);
        grid.Children.Add(span);

        var dur = new TextBlock
        {
            Text = g.Running ? "…" : g.Duration.HasValue ? LiveStatus.Short(g.Duration.Value) : "—",
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushOf(g.Running ? "BrandTextBrush" : "TextSecondaryBrush", Brushes.Gray),
        };
        Grid.SetColumn(dur, 2);
        grid.Children.Add(dur);

        var verdict = new TextBlock
        {
            Text = g.Verdict,
            FontSize = 10.5,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = VerdictBrush(g.Verdict),
        };
        Grid.SetColumn(verdict, 3);
        grid.Children.Add(verdict);

        return grid;
    }

    /// <summary>「比往常慢」用警告色，「快」用品牌色，「差不多」就退回次要色——不把正常抖动标成问题。</summary>
    private static Brush VerdictBrush(string verdict)
    {
        if (verdict.StartsWith("比往常慢", StringComparison.Ordinal)) return BrushOf("WarningBrush", Brushes.Orange);
        if (verdict.StartsWith("比往常快", StringComparison.Ordinal)) return BrushOf("BrandTextBrush", Brushes.Green);
        if (verdict == "进行中") return BrushOf("BrandTextBrush", Brushes.Green);
        return BrushOf("TextTertiaryBrush", Brushes.Gray);
    }

    // ===== 队列台账 =====

    private void RenderJournal(List<JournalEntry> journal)
    {
        ToolHost.Children.Clear();
        TbToolHeader.Text = journal.Count == 0 ? "队列台账（今天没跑过）" : $"队列台账 · {journal.Count} 项";

        if (journal.Count == 0)
        {
            ToolHost.Children.Add(Hint("今天还没有队列执行记录。"));
            return;
        }

        foreach (var e in journal)
        {
            bool ok = string.Equals(e.State, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var mark = new TextBlock
            {
                Text = ok ? "✓" : "✗",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf(ok ? "SuccessBrush" : "DangerBrush", Brushes.Gray),
            };
            Grid.SetColumn(mark, 0);
            row.Children.Add(mark);

            var text = new TextBlock
            {
                Text = TodayDigest.ToolLine(e),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf(ok ? "TextSecondaryBrush" : "TextPrimaryBrush", Brushes.Gray),
            };
            ToolTipService.SetToolTip(text, TodayDigest.ToolLine(e));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            ToolHost.Children.Add(row);

            // v1.20 / 奇思妙想 #9：失败的那一行，下面直接跟一句「这是什么、我该干什么」。
            // 只在真能解释清楚时才加——一句放之四海皆准的废话比不加更糟。
            if (!ok)
            {
                var tip = Troubleshoot.Explain(Troubleshoot.ParseExitCode(e.Reason), e.Reason);
                if (tip.Key is not ("unknown" or "exit.unknown"))
                {
                    var why = new TextBlock
                    {
                        Text = tip.Why + " " + tip.Todo,
                        FontSize = 10.5,
                        LineHeight = 16,
                        Margin = new Thickness(18, 0, 0, 6),
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
                    };
                    ToolHost.Children.Add(why);
                }
            }
        }
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #8：建议区。
    /// 只列建议、每条带「凭什么这么说」和一句「去哪里改」；
    /// <b>不提供一键改</b>——改队列顺序、关通知都是要看上下文的事，
    /// 一键改会让人在没看懂原因的情况下动了设置。
    /// </summary>
    private void RenderSuggestions()
    {
        try
        {
            var ideas = SuggestFacts.Build(DateTime.Today);
            SuggestHost.Children.Clear();

            if (ideas.Count == 0)
            {
                SuggestSection.Visibility = Visibility.Collapsed;
                return;
            }

            SuggestSection.Visibility = Visibility.Visible;
            TbSuggestHeader.Text = Suggest.Headline(ideas);

            foreach (var idea in ideas)
            {
                var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

                var title = new TextBlock
                {
                    Text = idea.Title,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = BrushOf("TextPrimaryBrush", Brushes.White),
                };
                box.Children.Add(title);

                var why = new TextBlock
                {
                    Text = idea.Why,
                    FontSize = 10.5,
                    LineHeight = 16,
                    Margin = new Thickness(0, 2, 0, 0),
                    MaxWidth = 560,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
                };
                box.Children.Add(why);

                SuggestHost.Children.Add(box);
            }
        }
        catch
        {
            SuggestSection.Visibility = Visibility.Collapsed;
        }
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2),
        Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
    };

    // ===== 按钮 =====

    /// <summary>
    /// 复制文字版：很多人要的不是「看懂」，是「发出去」。
    /// 截图发群里比复制几行字麻烦得多，而这个动作本身不需要任何新数据。
    ///
    /// <para>
    /// v1.45 / M12-5（D20 沿用）：失败时不再是死胡同。剪贴板被别的程序占着
    /// （远程桌面、剪贴板管理器、某些输入法），过两秒一般就好了——所以按钮旁边
    /// 直接给一个「重试」，而不是让人关掉窗口重开。
    /// </para>
    /// </summary>
    private void BtnReport_Click(object sender, RoutedEventArgs e) => TryCopyReport();

    private void BtnCopyRetry_Click(object sender, RoutedEventArgs e) => TryCopyReport();

    private void TryCopyReport()
    {
        // 连点防护：重试和主按钮都走这里，靠一个开关把重入挡住。
        if (_copying) return;
        _copying = true;
        BtnCopyRetry.IsEnabled = false;
        try
        {
            var day = DateTime.Today;
            var digest = TodayDigest.Build(GroupArchive.GetDay(day), name =>
            {
                try { return GroupArchive.MedianMinutes(name); }
                catch { return null; }
            });
            string text = TodayDigest.Report(digest, RunJournal.GetDay(day), day);
            Clipboard.SetText(text);
            NotifyService.Show(NotifyLevel.Info, "龙管家", "今日运行已复制到剪贴板。");
            LogService.LogEvent("今日运行：已复制文字版");
            ShowCopyFailure("");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("今日运行：复制失败 " + ex.Message);
            NotifyService.Show(NotifyLevel.Warning, "龙管家", "复制没成功，剪贴板可能被别的程序占着。点旁边的「重试」再试一次。");
            ShowCopyFailure("剪贴板被占着");
        }
        finally
        {
            _copying = false;
            BtnCopyRetry.IsEnabled = true;
        }
    }

    /// <summary>v1.45 / M12-5：失败那一格显隐只有这一处说了算。</summary>
    private void ShowCopyFailure(string reason)
    {
        bool failed = reason.Length > 0;
        TbCopyHint.Text = failed ? reason : "";
        TbCopyHint.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        BtnCopyRetry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _copying;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
