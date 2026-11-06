using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 挂机日历（v1.8 / F13）：过去一年每天一格。
///
/// 为什么这个功能值得做：这个软件一直在悄悄记录「哪天跑了、跑成没成」，
/// 但那些记录只躺在 config/group_archive.json 里，谁也不会去看。
/// 数据已经有了，缺的只是一种能一眼看完的画法。
///
/// 刻意不做的事：不做数字排行榜、不做导出、不做筛选。
/// 那是周报（F8）和日志的活，这里只回答一个问题——「这半年我勤快吗」。
/// 判定逻辑全在 CalendarModel，这里只管画。
/// </summary>
public partial class CalendarWindow : Window
{
    private const int Weeks = CalendarModel.Weeks;
    private const int Cell = 11;
    private const int Gap = 2;

    private static readonly Brush IdleBrush = Frozen(Color.FromRgb(0x2A, 0x2E, 0x36));

    public CalendarWindow()
    {
        InitializeComponent();
        Build();
        ApplyLegend();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>从 Theme 的语义令牌取色。Theme.C 返回的是 System.Drawing.Color，这里换成 WPF 要的 Media.Color。</summary>
    private static Color Parse(string key, Color fallback)
    {
        try
        {
            var c = Theme.C(key);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        catch
        {
            return fallback;
        }
    }

    private void ApplyLegend()
    {
        DotSuccess.Fill = Frozen(Parse("Success", Color.FromRgb(0x3D, 0xDC, 0x84)));
        DotWarning.Fill = Frozen(Parse("Warning", Color.FromRgb(0xE0, 0xA3, 0x2E)));
        DotDanger.Fill = Frozen(Parse("Danger", Color.FromRgb(0xE5, 0x53, 0x53)));
        DotIdle.Fill = IdleBrush;
    }

    /// <summary>
    /// 铺格子。列=周、行=星期（周一到周日自上而下），
    /// 起点由 CalendarModel.FirstDay 给出，保证同一行永远是同一个星期几。
    /// </summary>
    private void Build()
    {
        var today = DateTime.Today;
        DateTime first = CalendarModel.FirstDay(today);

        var canvas = new Grid
        {
            Width = Weeks * (Cell + Gap),
            Height = 7 * (Cell + Gap),
        };
        GridHost.Children.Add(canvas);

        var success = Frozen(Parse("Success", Color.FromRgb(0x3D, 0xDC, 0x84)));
        var warning = Frozen(Parse("Warning", Color.FromRgb(0xE0, 0xA3, 0x2E)));
        var danger = Frozen(Parse("Danger", Color.FromRgb(0xE5, 0x53, 0x53)));

        int ran = 0, allOk = 0, partial = 0, failed = 0;

        for (int w = 0; w < Weeks; w++)
        {
            for (int d = 0; d < 7; d++)
            {
                DateTime day = first.AddDays(w * 7 + d);
                if (day > today) continue;

                List<GroupRecord> records;
                try { records = GroupArchive.GetDay(day); }
                catch { records = new List<GroupRecord>(); }

                var level = CalendarModel.LevelOf(records);
                int finished = 0;
                foreach (var r in records)
                    if (r != null && !string.IsNullOrEmpty(r.End)) finished++;

                Brush brush = level switch
                {
                    CalendarModel.DayLevel.AllOk => success,
                    CalendarModel.DayLevel.Partial => warning,
                    CalendarModel.DayLevel.Failed => danger,
                    _ => IdleBrush,
                };

                string tip = CalendarModel.TipFor(day, level, records.Count, finished);
                if (day == today) tip += "（今天）";

                canvas.Children.Add(new Border
                {
                    Width = Cell,
                    Height = Cell,
                    CornerRadius = new CornerRadius(2),
                    Background = brush,
                    ToolTip = tip,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(w * (Cell + Gap), d * (Cell + Gap), 0, 0),
                });

                switch (level)
                {
                    case CalendarModel.DayLevel.AllOk: ran++; allOk++; break;
                    case CalendarModel.DayLevel.Partial: ran++; partial++; break;
                    case CalendarModel.DayLevel.Failed: ran++; failed++; break;
                }
            }
        }

        TbMonthLabels.Text = BuildMonthLabels(first, today);
        TbSummary.Text = CalendarModel.Summary(ran, allOk, partial, failed);
    }

    /// <summary>
    /// 月份标签只给一个起止范围。
    /// 试过逐月打标签：53 列里有的月份只占 4 列，标签会叠在一起，
    /// 反而不如「2025-11 → 2026-11」一眼看得出这面墙覆盖多久。
    /// </summary>
    private static string BuildMonthLabels(DateTime first, DateTime today)
        => $"{first:yyyy-MM} → {today:yyyy-MM}";

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
