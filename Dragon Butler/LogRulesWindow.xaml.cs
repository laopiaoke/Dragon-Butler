using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 日志规则（v1.14）：日志里出现某个词，就做一件事。
///
/// <para>
/// <b>为什么不做成 ABGI 那样的规则编辑器</b>：那套东西有二十几种行为、可嵌套的 data、
/// 执行顺序，本质是让用户用下拉框拼一个小脚本。它是给「想编排」的人用的。
/// 而挂机场景里用户真正缺的只有一件事：<b>出事了不用一直盯着屏幕也知道</b>。
/// 所以这里只有三个动作，每一个都映射到一个已经存在的能力：
/// 提醒（通知）、留现场（IncidentBundle）、停下队列（QueueService.Stop）。
/// </para>
/// <para>
/// <b>为什么要有「试一下」</b>：写关键词的难点不在语法，在于你不知道日志里长什么样。
/// 在网页上填完只能等下一条日志来验证，可能等一晚上都没有。
/// 这里直接扫今天的日志文件，命中几次、原文是哪一行，当场就能看见。
/// </para>
/// </summary>
public partial class LogRulesWindow : Window
{
    /// <summary>界面上的行引用，方便回写。</summary>
    private sealed class Row
    {
        public LogRule Rule = new();
        public TextBox Keyword = null!;
        public ComboBox Behavior = null!;
        public CheckBox Enabled = null!;
    }

    private readonly List<Row> _rows = new();

    public LogRulesWindow()
    {
        InitializeComponent();
        Render();
    }

    private static Brush BrushOf(string key, Brush fallback)
    {
        try { return (Brush)(Application.Current.TryFindResource(key) ?? fallback); }
        catch { return fallback; }
    }

    // ===== 渲染 =====

    private void Render()
    {
        RuleHost.Children.Clear();
        _rows.Clear();

        var rules = LogRules.Rules;
        if (rules.Count == 0)
        {
            RuleHost.Children.Add(Hint("还没有规则。点下面「＋ 加一条」，写一个日志里常见的词，比如「卡在」「失败」「重试」。"));
        }
        else
        {
            foreach (var r in rules.ToList()) RuleHost.Children.Add(BuildRow(r));
        }

        int active = LogRules.ActiveRules().Count;
        TbCount.Text = rules.Count == 0 ? "" : $"{active} 条生效 / 共 {rules.Count} 条";
    }

    private FrameworkElement BuildRow(LogRule rule)
    {
        var row = new Row { Rule = rule };

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });  // 启用勾
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 关键词
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) }); // 行为
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });  // 删除

        // 启用
        var check = new CheckBox
        {
            IsChecked = rule.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "取消勾选＝先留着但不生效",
            Style = (Style)FindResource("RuleCheck"),
        };
        check.Click += (_, _) =>
        {
            rule.Enabled = check.IsChecked == true;
            Save();
        };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);
        row.Enabled = check;

        // 关键词
        var kw = new TextBox
        {
            Style = (Style)FindResource("RuleBox"),
            Text = rule.Keyword ?? "",
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "日志里出现这个词就触发。越短越容易命中——先写「卡住」，别写整句。",
        };
        kw.LostFocus += (_, _) =>
        {
            string normalized = LogRule.Normalize(kw.Text);
            if (kw.Text != normalized) kw.Text = normalized;
            rule.Keyword = normalized;
            Save();
        };
        Grid.SetColumn(kw, 1);
        grid.Children.Add(kw);
        row.Keyword = kw;

        // 行为
        var cb = new ComboBox
        {
            Style = (Style)FindResource("RuleCombo"),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        foreach (var b in LogRule.AllBehaviors)
        {
            var item = new ComboBoxItem
            {
                Content = LogRule.BehaviorName(b),
                Tag = b,
                ToolTip = LogRule.BehaviorHint(b),
            };
            cb.Items.Add(item);
            if (string.Equals(rule.Behavior, b, StringComparison.Ordinal)) cb.SelectedItem = item;
        }
        if (cb.SelectedItem == null) cb.SelectedIndex = 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem it && it.Tag is string tag)
            {
                rule.Behavior = tag;
                Save();
            }
        };
        Grid.SetColumn(cb, 2);
        grid.Children.Add(cb);
        row.Behavior = cb;

        // 删除
        var del = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
            ToolTip = "删掉这条",
        };
        del.Click += (_, _) =>
        {
            try
            {
                LogRules.Rules.Remove(rule);
                Save();
                Render();
            }
            catch (Exception ex) { LogService.LogEvent("日志规则：删除失败 " + ex.Message); }
        };
        Grid.SetColumn(del, 3);
        grid.Children.Add(del);
        row.Rule = rule;

        // 备注：小字放在第二行，只在填了的时候显示
        if (!string.IsNullOrWhiteSpace(rule.Note))
        {
            var wrap = new StackPanel();
            wrap.Children.Add(grid);
            wrap.Children.Add(new TextBlock
            {
                Text = rule.Note,
                FontSize = 10,
                Margin = new Thickness(22, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
            });
            _rows.Add(row);
            return wrap;
        }

        _rows.Add(row);
        return grid;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 6),
        Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
    };

    // ===== 按钮 =====

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rule = new LogRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Keyword = "",
                Behavior = LogRule.Notify,
                Enabled = true,
            };
            LogRules.Rules.Add(rule);
            Save();
            Render();
        }
        catch (Exception ex) { LogService.LogEvent("日志规则：新增失败 " + ex.Message); }
    }

    /// <summary>
    /// 试一下：扫今天的日志。这是整个窗口最有价值的一个按钮——
    /// 它把「猜关键词」变成「看着原文挑关键词」。
    /// </summary>
    private void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 先把输入框里的内容收进规则表，避免「刚打完还没失焦就点试一下」
            foreach (var r in _rows)
            {
                r.Rule.Keyword = LogRule.Normalize(r.Keyword.Text);
                if (r.Behavior.SelectedItem is ComboBoxItem it && it.Tag is string tag) r.Rule.Behavior = tag;
            }
            Save();

            var rules = LogRules.ActiveRules();
            if (rules.Count == 0)
            {
                TestBox.Visibility = Visibility.Visible;
                TbTest.Text = "还没有启用的规则。先写一个关键词（比如「卡在」）再试。";
                return;
            }

            string? file = LogRules.TodayLogFile();
            var hits = LogRules.Scan(file, rules);
            TestBox.Visibility = Visibility.Visible;
            TbTest.Text = LogRules.Describe(hits, file);

            LogService.LogEvent($"日志规则：试跑 {rules.Count} 条，命中 {hits.Sum(h => h.Count)} 次");
        }
        catch (Exception ex)
        {
            TestBox.Visibility = Visibility.Visible;
            TbTest.Text = "试跑失败：" + ex.Message;
            LogService.LogEvent("日志规则：试跑失败 " + ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            // 关键词为空的规则保留（用户可能刚加完还没写），但不进 ActiveRules
            AppState.SaveSettings();
        }
        catch (Exception ex) { LogService.LogEvent("日志规则：保存失败 " + ex.Message); }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
