using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 依赖编排编辑器（v1.9 / P2）：行等列的勾选矩阵。
/// 只写 <see cref="QueueEntry.DependsOn"/> 与 <see cref="QueueEntry.OnDependencyFail"/>，
/// 不改变勾选与顺序——顺序由 QueueGraph 在跑之前按依赖排。
/// </summary>
public partial class DependsWindow : Window
{
    private readonly List<QueueEntry> _entries;
    private readonly List<ToolProfile> _profiles;
    private readonly bool[,] _cells;
    private readonly string _policy;

    /// <summary>是否点了保存。</summary>
    public bool Saved { get; private set; }

    /// <summary>打开编辑器（数据来自当前队列）。</summary>
    public DependsWindow()
    {
        InitializeComponent();

        _entries = (AppState.Settings.Queue ?? new List<QueueEntry>()).ToList();
        _profiles = _entries.Select(e => ProfileRegistry.Get(e.ToolId)).ToList();
        _cells = new bool[Math.Max(1, _entries.Count), Math.Max(1, _entries.Count)];

        // 回填：某一项声明了哪些依赖
        for (int row = 0; row < _entries.Count; row++)
        {
            var deps = QueueGraph.DependenciesOf(_entries[row]);
            for (int col = 0; col < _entries.Count; col++)
            {
                if (col == row) continue;
                string id = _entries[col]?.ToolId ?? "";
                _cells[row, col] = deps.Any(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        // 策略取"任一项设成 整队停下"就算停下——这是更保守的那个方向
        _policy = _entries.Any(e => QueueGraph.ShouldAbortOnDependencyFail(e))
            ? QueueGraph.PolicyAbort
            : QueueGraph.PolicySkip;
        SelectPolicy(_policy);

        CmbPolicy.SelectionChanged += (_, _) => UpdatePolicyHint();
        UpdatePolicyHint();

        BuildMatrix();
    }

    private void SelectPolicy(string policy)
    {
        foreach (var item in CmbPolicy.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, policy, StringComparison.OrdinalIgnoreCase))
            {
                CmbPolicy.SelectedItem = cbi;
                return;
            }
        }
        CmbPolicy.SelectedIndex = 0;
    }

    private string CurrentPolicy()
        => (CmbPolicy.SelectedItem as ComboBoxItem)?.Tag as string == QueueGraph.PolicyAbort
            ? QueueGraph.PolicyAbort
            : QueueGraph.PolicySkip;

    private void UpdatePolicyHint()
    {
        TbPolicyHint.Text = CurrentPolicy() == QueueGraph.PolicyAbort
            ? "依赖没跑成时，整条队列停下等你处理（后面的都不跑）。"
            : "依赖没跑成时，跳过这一项，后面不依赖它的照常跑。";
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

        MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        for (int c = 0; c < _entries.Count; c++)
            MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        MatrixHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        for (int r = 0; r < _entries.Count; r++)
            MatrixHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });

        var headStyle = new Style(typeof(TextBlock));
        headStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));
        headStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, BrushOf("TextSecondaryBrush")));
        headStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        headStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        headStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));

        for (int c = 0; c < _entries.Count; c++)
        {
            var head = new TextBlock
            {
                Text = _profiles[c].DisplayName,
                ToolTip = "点一下：整列全选/全不选（这一列＝「要先跑成的」）",
                Cursor = Cursors.Hand,
                Style = headStyle,
            };
            head.TextAlignment = TextAlignment.Center;
            head.Margin = new Thickness(4, 0, 4, 0);
            int col = c;
            head.MouseLeftButtonUp += (_, _) => ToggleColumn(col);
            Grid.SetColumn(head, c + 1);
            Grid.SetRow(head, 0);
            MatrixHost.Children.Add(head);
        }

        for (int r = 0; r < _entries.Count; r++)
        {
            var label = new TextBlock
            {
                Text = _profiles[r].DisplayName,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                ToolTip = "点一下：这一整行全选/全不选（这一行＝「谁要等」）",
                Foreground = BrushOf("TextSecondaryBrush") ?? Brushes.Gray,
            };
            int row = r;
            label.MouseLeftButtonUp += (_, _) => ToggleRow(row);
            Grid.SetColumn(label, 0);
            Grid.SetRow(label, r + 1);
            MatrixHost.Children.Add(label);

            for (int c = 0; c < _entries.Count; c++)
            {
                if (c == r)
                {
                    // 自己依赖自己没有意义，画一条斜线占位
                    var dead = new TextBlock
                    {
                        Text = "—",
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = BrushOf("TextTertiaryBrush") ?? Brushes.Gray,
                        IsHitTestVisible = false,
                    };
                    Grid.SetColumn(dead, c + 1);
                    Grid.SetRow(dead, r + 1);
                    MatrixHost.Children.Add(dead);
                    continue;
                }

                var cell = new CheckBox
                {
                    IsChecked = _cells[r, c],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = (row, c),
                };
                cell.Checked += (s, _) => SetCell((CheckBox)s, true);
                cell.Unchecked += (s, _) => SetCell((CheckBox)s, false);
                Grid.SetColumn(cell, c + 1);
                Grid.SetRow(cell, r + 1);
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
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, 2);
            MatrixHost.Children.Add(empty);
        }
    }

    private void SetCell(CheckBox box, bool value)
    {
        if (box.Tag is not ValueTuple<int, int> tag) return;
        _cells[tag.Item1, tag.Item2] = value;
    }

    private void ToggleRow(int row)
    {
        bool anyOff = Enumerable.Range(0, _entries.Count).Any(c => c != row && !_cells[row, c]);
        for (int c = 0; c < _entries.Count; c++)
        {
            if (c == row) continue;
            _cells[row, c] = anyOff;
        }
        BuildMatrix();
    }

    private void ToggleColumn(int col)
    {
        bool anyOff = Enumerable.Range(0, _entries.Count).Any(r => r != col && !_cells[r, col]);
        for (int r = 0; r < _entries.Count; r++)
        {
            if (r == col) continue;
            _cells[r, col] = anyOff;
        }
        BuildMatrix();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        for (int r = 0; r < _entries.Count; r++)
            for (int c = 0; c < _entries.Count; c++)
                _cells[r, c] = false;
        BuildMatrix();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 先在一份浅拷贝上算，确认没有环再落到真数据——写一半失败最难收拾
            var draft = _entries.Select(src => new QueueEntry
            {
                ToolId = src.ToolId,
                Enabled = src.Enabled,
                WeekdayMask = src.WeekdayMask,
            }).ToList();

            var waitsOn = new Dictionary<int, List<int>>();
            for (int r = 0; r < _entries.Count; r++)
            {
                var cols = new List<int>();
                for (int c = 0; c < _entries.Count; c++)
                    if (_cells[r, c]) cols.Add(c);
                waitsOn[r] = cols;
            }

            QueueGraph.ApplyMatrix(draft, waitsOn, CurrentPolicy());
            QueueGraph.TryOrder(draft, out List<string> stuck);
            if (stuck.Count > 0)
            {
                var names = stuck.Select(id =>
                {
                    var p = ProfileRegistry.Get(id);
                    return string.IsNullOrWhiteSpace(p.DisplayName) ? id : p.DisplayName;
                });
                AppDialog.Show("这几项互相等着，永远轮不到：\n" + string.Join("、", names) + "\n\n把其中一条勾去掉再保存。",
                    title: "依赖绕成圈了", owner: this);
                return;
            }

            QueueGraph.ApplyMatrix(_entries, waitsOn, CurrentPolicy());
            AppState.SaveSettings();
            Saved = true;

            int pairs = _entries.Sum(e => QueueGraph.DependenciesOf(e).Count);
            LogService.LogEvent(pairs == 0
                ? "依赖编排：已清空（回到一条直线）"
                : "依赖编排：已保存 " + string.Join("；", _entries
                    .Where(e => QueueGraph.DependenciesOf(e).Count > 0)
                    .Select(e => $"{ProfileRegistry.Get(e.ToolId).DisplayName} 等 "
                        + string.Join("、", QueueGraph.DependenciesOf(e)
                            .Select(id => ProfileRegistry.Get(id).DisplayName)))));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存依赖编排失败：" + ex.Message);
        }
    }

    private static Brush? BrushOf(string key)
    {
        try { return Application.Current?.TryFindResource(key) as Brush; }
        catch { return null; }
    }
}
