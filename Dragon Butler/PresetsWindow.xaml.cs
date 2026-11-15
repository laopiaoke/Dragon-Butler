using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DragonButler;

/// <summary>
/// v1.15：配置组合的管理窗口。
///
/// 为什么不把它做成主界面上的一个大区：组合是「建一次、之后只挑」的东西，
/// 常驻在首屏等于每天都在提醒用户「你还有东西没配」。主界面只留一个下拉，
/// 需要新建/删除时才打开这里。
///
/// 刻意不做的事：不做改名（改名要处理重名、要处理正在用这套的情况，
/// 收益只有一个字），不做排序（组合数量上限 32，按最近用过的自动排）。
/// </summary>
public partial class PresetsWindow : Window
{
    /// <summary>改过数据（主窗需要重刷下拉）</summary>
    public bool Changed { get; private set; }

    public PresetsWindow()
    {
        InitializeComponent();
        Render();
    }

    // ===== 渲染 =====

    private void Render()
    {
        try
        {
            var presets = AppState.Settings.Presets ?? new List<ConfigPreset>();
            var known = BgiDiscovery.Scan().Groups ?? new List<string>();

            TbCurrent.Text = CurrentLine();
            TbEmpty.Visibility = presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TbFoot.Text = presets.Count == 0
                ? ""
                : $"共 {presets.Count} 套，最多 {ConfigPresets.MaxCount} 套。";

            PresetHost.Children.Clear();
            foreach (var p in ConfigPresets.Sort(presets))
                PresetHost.Children.Add(BuildRow(p, known));

            if (presets.Count == 0 && TbNewName.Text.Trim().Length == 0)
                TbNewName.Text = ConfigPresets.SuggestName(presets);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("渲染配置组合失败：" + ex.Message);
        }
    }

    /// <summary>「此刻的配置名和勾选」——存新组合时看到的原样</summary>
    private static string CurrentLine()
    {
        var s = AppState.Settings;
        string cfg = (s.DragonConfigName ?? "").Trim();
        var groups = GroupSlicing.Sanitize(s.SelectedGroups);
        string cfgText = cfg.Length == 0 ? "没填一条龙配置名" : "「" + cfg + "」";
        string groupText = groups.Count == 0
            ? "没勾配置组（交给 BGI 走默认流程）"
            : string.Join("、", groups) + $"（{groups.Count} 组）";
        return "此刻：" + cfgText + " · " + groupText;
    }

    private UIElement BuildRow(ConfigPreset preset, IReadOnlyList<string> known)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        bool active = string.Equals(AppState.Settings.ActivePresetId, preset.Id, StringComparison.OrdinalIgnoreCase);
        text.Children.Add(new TextBlock
        {
            Text = active ? preset.Name + "  · 当前" : preset.Name,
            FontSize = 12.5,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = TryFindResource(active ? "BrandTextBrush" : "TextPrimaryBrush") as System.Windows.Media.Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        string desc = ConfigPresets.Describe(preset);
        var missing = ConfigPresets.MissingGroups(preset, known);
        if (missing.Count > 0) desc += " · 找不到 " + string.Join("、", missing);
        var sub = new TextBlock
        {
            Text = desc,
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = TryFindResource(missing.Count > 0 ? "WarningBrush" : "TextTertiaryBrush") as System.Windows.Media.Brush,
        };
        text.Children.Add(sub);
        if (missing.Count > 0)
            sub.ToolTip = "这些组名在 BGI 里已经找不到了。开跑时它们会被忽略，其余组照常跑。";
        text.ToolTip = desc;

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var use = new Button
        {
            Content = active ? "正在用" : "用这套",
            Style = TryFindResource("Ghost") as Style,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            IsEnabled = !active,
            Tag = preset.Id,
        };
        use.Click += BtnUse_Click;
        buttons.Children.Add(use);

        var del = new Button
        {
            Content = "删除",
            Style = TryFindResource("Ghost") as Style,
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Tag = preset.Id,
            ToolTip = "只删这套组合，不动 BGI 里的配置，也不动全局勾选",
        };
        del.Click += BtnDelete_Click;
        buttons.Children.Add(del);

        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = TryFindResource("InsetBrush") as System.Windows.Media.Brush,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
    }

    // ===== 交互 =====

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = AppState.Settings;
            var next = ConfigPresets.Capture(
                s.Presets, s.DragonConfigName, s.SelectedGroups, TbNewName.Text,
                () => Guid.NewGuid().ToString("N"));

            string? why = ConfigPresets.RefuseReason(next, s.Presets);
            if (why != null)
            {
                TbNewHint.Text = why;
                TbNewHint.Foreground = TryFindResource("WarningBrush") as System.Windows.Media.Brush;
                return;
            }

            s.Presets ??= new List<ConfigPreset>();
            s.Presets.Add(next);
            s.ActivePresetId = next.Id;
            next.LastUsedAt = ConfigPresets.StampUsed(DateTime.Now);
            AppState.SaveSettings();
            Changed = true;

            TbNewName.Text = "";
            TbNewHint.Foreground = TryFindResource("TextTertiaryBrush") as System.Windows.Media.Brush;
            TbNewHint.Text = $"已保存「{next.Name}」，并设为这次要用的组合。";
            LogService.LogEvent($"保存配置组合：{next.Name}（{ConfigPresets.Describe(next)}）");
            Render();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存配置组合失败：" + ex.Message);
        }
    }

    private void BtnUse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as Button)?.Tag is not string id) return;
            var preset = ConfigPresets.FindById(AppState.Settings.Presets, id);
            if (preset == null) { Render(); return; }

            AppState.Settings.ActivePresetId = preset.Id;
            preset.LastUsedAt = ConfigPresets.StampUsed(DateTime.Now);
            AppState.SaveSettings();
            Changed = true;
            AppState.PublishLog($"✓ 这次用「{preset.Name}」：{ConfigPresets.Describe(preset)}");
            Render();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("切换配置组合失败：" + ex.Message);
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as Button)?.Tag is not string id) return;
            var preset = ConfigPresets.FindById(AppState.Settings.Presets, id);
            if (preset == null) { Render(); return; }

            // 删除是不可撤销的，但代价很低（重建只要点两下），
            // 所以用一次确认，不用「长按」这类更重的交互。
            bool ok = AppDialog.Show(
                $"删掉「{preset.Name}」？",
                "只删这套组合。BGI 里的配置不动，全局勾选也不动。",
                AppDialog.Kind.Question, "配置组合",
                primary: "删掉", secondary: "留着", owner: this);
            if (!ok) return;

            AppState.Settings.Presets?.RemoveAll(p => string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(AppState.Settings.ActivePresetId, preset.Id, StringComparison.OrdinalIgnoreCase))
                AppState.Settings.ActivePresetId = "";
            AppState.SaveSettings();
            Changed = true;
            AppState.PublishLog($"已删掉配置组合「{preset.Name}」");
            Render();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("删除配置组合失败：" + ex.Message);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
