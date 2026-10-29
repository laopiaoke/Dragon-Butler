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

    public FirstRunWindow()
    {
        InitializeComponent();
        ApplyQueueSelection();
        UpdateUi();
    }

    private void ApplyQueueSelection()
    {
        var queue = AppState.Settings.Queue ?? new List<QueueEntry>();
        CbBgi.IsChecked = queue.FirstOrDefault(q => q.ToolId == "bettergi")?.Enabled ?? true;
        CbMarch.IsChecked = queue.FirstOrDefault(q => q.ToolId == "march7th")?.Enabled ?? false;
        CbOkww.IsChecked = queue.FirstOrDefault(q => q.ToolId == "ok-ww")?.Enabled ?? false;
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
        var ids = new List<string>();
        if (CbBgi.IsChecked == true) ids.Add("bettergi");
        if (CbMarch.IsChecked == true) ids.Add("march7th");
        if (CbOkww.IsChecked == true) ids.Add("ok-ww");
        return ids.Select(ProfileRegistry.Get).ToList();
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
                Text = profile.ExecutablePath,
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
        Set("bettergi", CbBgi.IsChecked == true);
        Set("march7th", CbMarch.IsChecked == true);
        Set("ok-ww", CbOkww.IsChecked == true);
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
