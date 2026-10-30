using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 按日计划编辑器（v1.5）：7 天 × 队列工具的勾选矩阵。
/// 只写 <see cref="QueueEntry.WeekdayMask"/>，不改变勾选/顺序；点行标题可整行切换，点列标题可整列切换。
/// </summary>
public partial class WeeklyPlanWindow : Window
{
    private static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private readonly List<QueueEntry> _entries;
    private readonly List<ToolProfile> _profiles;
    private readonly bool[,] _cells;

    /// <summary>是否点了保存。</summary>
    public bool Saved { get; private set; }

    /// <summary>打开编辑器（数据来自当前队列）。</summary>
    public WeeklyPlanWindow()
    {
        InitializeComponent();

        _entries = (AppState.Settings.Queue ?? new List<QueueEntry>()).ToList();
        _profiles = _entries.Select(e => ProfileRegistry.Get(e.ToolId)).ToList();
        _cells = new bool[7, Math.Max(1, _entries.Count)];

        for (int d = 0; d < 7; d++)
            for (int t = 0; t < _entries.Count; t++)
                _cells[d, t] = (_entries[t].WeekdayMask & (1 << d)) != 0;

        BuildMatrix();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BuildMatrix()
    {
        MatrixHost.Children.Clear();
        MatrixHost.ColumnDefinitions.Clear();
        MatrixHost.RowDefinitions.Clear();

        MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        for (int t = 0; t < _entries.Count; t++)
            MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        MatrixHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        for (int d = 0; d < 7; d++)
            MatrixHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });

        var headStyle = new Style(typeof(TextBlock));
        headStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11.5));
        headStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, BrushOf("TextSecondaryBrush")));
        headStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        headStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        headStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));

        int today = QueueService.WeekdayBit(DateTime.Now);

        for (int t = 0; t < _entries.Count; t++)
        {
            var head = new TextBlock
            {
                Text = _profiles[t].DisplayName,
                ToolTip = "点一下：整列全选/全不选",
                Cursor = Cursors.Hand,
                Style = headStyle,
            };
            head.TextAlignment = TextAlignment.Center;
            head.Margin = new Thickness(4, 0, 4, 0);
            int col = t;
            head.MouseLeftButtonUp += (_, _) => ToggleColumn(col);
            Grid.SetColumn(head, t + 1);
            Grid.SetRow(head, 0);
            MatrixHost.Children.Add(head);
        }

        for (int d = 0; d < 7; d++)
        {
            var label = new TextBlock
            {
                Text = d == today ? DayNames[d] + " · 今天" : DayNames[d],
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "点一下：整天全选/全不选",
                Foreground = d == today ? (BrushOf("BrandBrush") ?? Brushes.SteelBlue) : (BrushOf("TextSecondaryBrush") ?? Brushes.Gray),
            };
            int day = d;
            label.MouseLeftButtonUp += (_, _) => ToggleRow(day);
            Grid.SetColumn(label, 0);
            Grid.SetRow(label, d + 1);
            MatrixHost.Children.Add(label);

            for (int t = 0; t < _entries.Count; t++)
            {
                var cell = new CheckBox
                {
                    IsChecked = _cells[d, t],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = (day, t),
                };
                cell.Checked += (s, _) => SetCell((CheckBox)s, true);
                cell.Unchecked += (s, _) => SetCell((CheckBox)s, false);
                Grid.SetColumn(cell, t + 1);
                Grid.SetRow(cell, d + 1);
                MatrixHost.Children.Add(cell);
            }
        }

        if (_entries.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "队列里还没有工具，先在主界面勾选一个再来配置。",
                FontSize = 11.5,
                Margin = new Thickness(0, 12, 0, 12),
                Foreground = BrushOf("TextTertiaryBrush") ?? Brushes.Gray,
            };
            Grid.SetRow(empty, 8);
            Grid.SetColumnSpan(empty, 2);
            MatrixHost.Children.Add(empty);
        }

        // 今天整列高亮一行分隔线，方便一眼看出今天跑什么
        var ring = new Border
        {
            BorderBrush = BrushOf("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsHitTestVisible = false,
        };
        Grid.SetRow(ring, today + 1);
        Grid.SetColumn(ring, 0);
        Grid.SetColumnSpan(ring, Math.Max(1, _entries.Count) + 1);
        Grid.SetZIndex(ring, -1);
        MatrixHost.Children.Add(ring);
    }

    private void SetCell(CheckBox box, bool value)
    {
        if (box.Tag is not ValueTuple<int, int> tag) return;
        _cells[tag.Item1, tag.Item2] = value;
    }

    private void ToggleRow(int day)
    {
        bool anyOff = Enumerable.Range(0, _entries.Count).Any(t => !_cells[day, t]);
        for (int t = 0; t < _entries.Count; t++) _cells[day, t] = anyOff;
        BuildMatrix();
    }

    private void ToggleColumn(int tool)
    {
        bool anyOff = Enumerable.Range(0, 7).Any(d => !_cells[d, tool]);
        for (int d = 0; d < 7; d++) _cells[d, tool] = anyOff;
        BuildMatrix();
    }

    private void BtnAll_Click(object sender, RoutedEventArgs e)
    {
        bool value = (sender as Button)?.Tag as string == "all";
        for (int d = 0; d < 7; d++)
            for (int t = 0; t < _entries.Count; t++)
                _cells[d, t] = value;
        BuildMatrix();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            for (int t = 0; t < _entries.Count; t++)
            {
                int mask = 0;
                for (int d = 0; d < 7; d++) if (_cells[d, t]) mask |= 1 << d;
                _entries[t].WeekdayMask = mask == 0 ? 0 : mask;
            }
            AppState.SaveSettings();
            Saved = true;
            LogService.LogEvent("按日计划：已保存 " + string.Join("，", _entries.Select(e =>
                $"{ProfileRegistry.Get(e.ToolId).DisplayName}={MaskText(e.WeekdayMask)}")));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存按日计划失败：" + ex.Message);
        }
    }

    /// <summary>掩码 → 人话（全选 / 具体几天 / 无）。</summary>
    public static string MaskText(int mask)
    {
        mask &= 0x7F;
        if (mask == 0x7F) return "每天";
        if (mask == 0) return "不跑";
        var days = new List<string>();
        for (int d = 0; d < 7; d++) if ((mask & (1 << d)) != 0) days.Add(DayNames[d]);
        return string.Join("、", days);
    }

    private static Brush? BrushOf(string key)
    {
        try { return Application.Current?.TryFindResource(key) as Brush; } catch { }
        return null;
    }
}
