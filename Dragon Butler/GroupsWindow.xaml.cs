using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// v1.9 / P2：配置组切片编辑器——左边选队列项，右边勾它要跑哪几个 BGI 配置组。
/// 不做"保存时才发现写错"的事：这里的勾选项全部来自真实扫到的组名。
/// </summary>
public partial class GroupsWindow : Window
{
    private readonly List<QueueEntry> _entries;
    private readonly List<string> _known;
    private readonly Dictionary<string, HashSet<string>> _picks = new(StringComparer.OrdinalIgnoreCase);
    private List<CheckBox> _boxes = new();
    private string _current = "";

    /// <summary>用户点了保存（取消时为 false）。</summary>
    public bool Saved { get; private set; }

    public GroupsWindow()
    {
        InitializeComponent();
        _entries = (AppState.Settings.Queue ?? new List<QueueEntry>()).ToList();
        _known = BgiDiscovery.Scan().Groups ?? new List<string>();

        foreach (var entry in _entries)
            _picks[entry.ToolId] = new HashSet<string>(GroupSlicing.Sanitize(entry.Groups), StringComparer.OrdinalIgnoreCase);

        BuildEntryList();
        if (LbEntries.Items.Count > 0) LbEntries.SelectedIndex = 0;
        else
        {
            TbPickHint.Text = "队列里还没有工具。先去主界面下方添加或勾选一个。";
            TbSummary.Text = "";
        }
    }

    private void BuildEntryList()
    {
        foreach (var entry in _entries)
        {
            var profile = ProfileRegistry.Get(entry.ToolId);
            string name = string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length == 0
                ? entry.ToolId
                : profile.DisplayName;

            LbEntries.Items.Add(new ListBoxItem
            {
                Content = name,
                Tag = entry.ToolId,
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12,
            });
        }
    }

    private void LbEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LbEntries.SelectedItem is not ListBoxItem item) return;
        _current = item.Tag as string ?? "";
        RefreshPicks();
    }

    private void RefreshPicks()
    {
        GroupHost.Children.Clear();
        _boxes = new List<CheckBox>();

        if (_current.Length == 0) return;

        if (_known.Count == 0)
        {
            TbPickHint.Text = "还没识别到 BetterGI 配置组：先在上面设置好 BetterGI 路径，或点一次「重新识别」。";
            TbSummary.Text = "";
            return;
        }

        TbPickHint.Text = "勾上就跑这几个；一个都不勾＝沿用全局勾选。";
        var chosen = _picks.TryGetValue(_current, out var set) ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in _known)
        {
            var cb = new CheckBox
            {
                Content = name,
                IsChecked = chosen.Contains(name),
                Margin = new Thickness(0, 4, 14, 4),
                FontSize = 12,
                Tag = name,
            };
            if (TryFindResource("GroupCheck") is Style style) cb.Style = style;
            cb.Checked += Box_Toggled;
            cb.Unchecked += Box_Toggled;
            GroupHost.Children.Add(cb);
            _boxes.Add(cb);
        }

        UpdateSummary();
    }

    private void Box_Toggled(object sender, RoutedEventArgs e)
    {
        if (_current.Length == 0) return;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cb in _boxes)
            if (cb.IsChecked == true && cb.Tag is string tag) set.Add(tag);
        _picks[_current] = set;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int own = _picks.Values.Count(s => s.Count > 0);
        int total = _entries.Count;
        if (total == 0) { TbSummary.Text = ""; return; }

        if (own == 0)
            TbSummary.Text = $"全部 {total} 项都用全局勾选。";
        else
            TbSummary.Text = $"{own} 项指定了组，其余 {total - own} 项用全局勾选；改完点保存才生效。";
    }

    private void BtnClearOne_Click(object sender, RoutedEventArgs e)
    {
        if (_current.Length == 0) return;
        foreach (var cb in _boxes) cb.IsChecked = false;
        _picks[_current] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        UpdateSummary();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var key in _picks.Keys.ToList())
            _picks[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cb in _boxes) cb.IsChecked = false;
        UpdateSummary();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // 下标 → 组名：界面是按 _entries 的顺序铺的，直接按下标写回，避免按名字找导致重名/找不到
        var picks = new Dictionary<int, List<string>>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_picks.TryGetValue(_entries[i].ToolId, out var set))
                picks[i] = set.ToList();
        }

        GroupSlicing.Apply(_entries, picks, _known);
        AppState.SaveSettings();

        int own = _entries.Count(x => (x.Groups?.Count ?? 0) > 0);
        if (own == 0) LogService.LogEvent("配置组切片：已全部改回全局勾选");
        else
        {
            foreach (var entry in _entries.Where(x => (x.Groups?.Count ?? 0) > 0))
            {
                var profile = ProfileRegistry.Get(entry.ToolId);
                string name = string.IsNullOrWhiteSpace(profile.DisplayName) ? entry.ToolId : profile.DisplayName;
                LogService.LogEvent($"配置组切片：{name} → {string.Join("、", entry.Groups!)}");
            }
        }

        Saved = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
