using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 首启向导（三步）：选工具 → 自动寻找路径 → 完成。
/// 只在"首次运行且没有任何已配置工具"时由主界面弹出，可随时跳过。
/// </summary>
public partial class FirstRunWindow : Window
{
    private int _step = 1;
    private readonly Dictionary<string, string> _found = new();
    private readonly Dictionary<string, CheckBox> _picks = new();

    public FirstRunWindow()
    {
        InitializeComponent();
        BuildToolPicks();
        ApplyQueueSelection();
        UpdateUi();
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

    private UIElement BuildPickRow(ToolProfile profile)
    {
        var known = ToolCatalog.Find(profile.ToolId);

        var row = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(9),
            Background = (Brush?)TryFindResource("InsetBrush") ?? Brushes.Transparent,
            BorderBrush = (Brush?)TryFindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = (Brush?)TryFindResource("ControlBgBrush") ?? Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bitmap = ToolIconService.GetBitmap(profile);
        if (bitmap != null)
        {
            icon.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }
        else
        {
            icon.Child = new TextBlock
            {
                Text = profile.DisplayName.Length > 0 ? profile.DisplayName[..1] : "?",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush?)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
            };
        }
        grid.Children.Add(icon);

        var text = new StackPanel { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 12.5,
            Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (known != null && !string.IsNullOrWhiteSpace(known.GameName))
        {
            text.Children.Add(new TextBlock
            {
                Text = known.GameName,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush?)TryFindResource("TextTertiaryBrush") ?? Brushes.Gray,
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var pill = new CheckBox { Style = TryFindResource("WizPill") as Style, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        row.Child = grid;
        // 整行可点：胶囊太细，十六行往下排时很容易点到隔壁
        row.MouseLeftButtonUp += (_, _) => pill.IsChecked = pill.IsChecked != true;
        pill.Checked += (_, _) =>
        {
            row.BorderBrush = (Brush?)TryFindResource("BrandBrush") ?? Brushes.Green;
            RefreshPickSummary();
        };
        pill.Unchecked += (_, _) =>
        {
            row.BorderBrush = (Brush?)TryFindResource("BorderBrush") ?? Brushes.Gray;
            RefreshPickSummary();
        };

        _picks[profile.ToolId] = pill;
        return row;
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
        TbStep.Text = $"第 {_step} 步 / 共 3 步";
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = _step == 1 ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Content = _step == 3 ? "完成" : "下一步";

        if (_step == 2) BuildFindRows();
        if (_step == 3) BuildDoneSummary();
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

            row.Children.Add(new TextBlock
            {
                Text = profile.DisplayName,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.Black,
            });

            var box = new TextBox
            {
                // 回到上一步再进来时，得把刚「自动寻找」的结果带回来。
                // 只读 profile.ExecutablePath 的话，_found 里明明有值，框里却还是旧路径，
                // 而点「下一步」存的是 _found——看得见和存得下的不是同一个东西。
                Text = _found.TryGetValue(profile.ToolId, out var kept) ? kept : profile.ExecutablePath,
                Height = 30,
                IsReadOnly = true,
                Style = TryFindResource("InputBox") as Style,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = profile.ToolId,
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            var find = new Button
            {
                Content = "自动寻找",
                Style = TryFindResource("WizGhost") as Style,
                Height = 30,
                MinWidth = 84,
                Tag = profile.ToolId,
                Margin = new Thickness(0, 0, 6, 0),
            };
            find.Click += (_, _) => AutoFind(profile, box);
            Grid.SetColumn(find, 2);
            row.Children.Add(find);

            var browse = new Button
            {
                Content = "浏览",
                Style = TryFindResource("WizGhost") as Style,
                Height = 30,
                MinWidth = 64,
                Tag = profile.ToolId,
            };
            browse.Click += (_, _) => Browse(profile, box);
            Grid.SetColumn(browse, 3);
            row.Children.Add(browse);

            FindHost.Children.Add(row);
        }
        TbFindSummary.Text = "提示：多数情况下点「自动寻找」就能命中；如果没找到，选到工具的 exe 即可。";
    }

    private void AutoFind(ToolProfile profile, TextBox box)
    {
        var found = ToolDiscovery.TryAutoDiscover(profile, out string source);
        if (string.IsNullOrWhiteSpace(found))
        {
            TbFindSummary.Text = $"{profile.DisplayName}：未自动找到，请手动选择文件夹。";
            return;
        }
        box.Text = found;
        _found[profile.ToolId] = found;
        TbFindSummary.Text = $"{profile.DisplayName}：已找到（{source}），点「下一步」生效。";
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

    private void BuildDoneSummary()
    {
        var lines = new List<string>();
        foreach (var profile in SelectedProfiles())
        {
            bool ok = !string.IsNullOrWhiteSpace(profile.ExecutablePath);
            lines.Add($"{profile.DisplayName}：{(ok ? "已就绪" : "还没设置路径（可稍后在工具卡里设置）")}");
        }
        TbDoneSummary.Text = string.Join("\n", lines);
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1)
        {
            SaveSelection();
            if (SelectedProfiles().Count == 0)
            {
                TbFindSummary.Text = "";
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

        ApplyAll();
        Close();
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        UpdateUi();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        SaveSelection();
        Close();
    }

    private void SaveSelection()
    {
        var queue = AppState.Settings.Queue ?? new List<QueueEntry>();
        void Set(string id, bool on)
        {
            var entry = queue.FirstOrDefault(q => string.Equals(q.ToolId, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null) queue.Add(new QueueEntry { ToolId = id, Enabled = on });
            else entry.Enabled = on;
        }
        foreach (var kv in _picks) Set(kv.Key, kv.Value.IsChecked == true);
        AppState.Settings.Queue = queue;
        AppState.SaveSettings();
    }

    private void SavePaths()
    {
        foreach (var kv in _found)
        {
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
        LogService.LogEvent("首启向导：完成");
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
