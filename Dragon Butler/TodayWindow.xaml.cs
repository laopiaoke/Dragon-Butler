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

        RenderGroups(digest);
        RenderJournal(journal);

        TbFoot.Text = "数据来自本机已有的两份记录（配置组存档 / 队列台账）。"
                    + "「往常」取最近 14 天的同组中位数，样本少于 2 次就不评价快慢。";

        int doneCount = journal.Count(e => string.Equals(e.State, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase));
        TbSummary.Text = journal.Count == 0
            ? "今天队列还没跑过"
            : $"队列 {journal.Count} 项 · 跑成 {doneCount} 项 · 没成 {journal.Count - doneCount} 项";
    }

    // ===== 配置组 =====

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
    /// </summary>
    private void BtnReport_Click(object sender, RoutedEventArgs e)
    {
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
        }
        catch (Exception ex)
        {
            LogService.LogEvent("今日运行：复制失败 " + ex.Message);
            NotifyService.Show(NotifyLevel.Warning, "龙管家", "复制没成功，剪贴板可能被别的程序占着，稍后再试。");
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
