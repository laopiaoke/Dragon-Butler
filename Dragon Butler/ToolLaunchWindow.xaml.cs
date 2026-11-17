using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// v1.21：单个工具的「启动设置」。
///
/// 这个窗口只做一件事：把「程序在哪、怎么启动、怎么判断它跑完了」三件事集中在一页里说完。
/// 以前这三件事分散在工具卡（路径）+ 高级设置（信号）+ 自定义工具窗口（信号类型），
/// 想改一个启动参数得翻三个地方。
///
/// 设计约束：
/// - 参数下拉里的候选值都带可信度标记（✓ 文档里写过 / · 别人这么用 / 空 自己确认），
///   因为「这个 -e 到底关不关游戏」我们不敢替用户打包票。
/// - 下拉可以选，也可以直接改下面的输入框；改输入框不会动下拉（下拉是「从这里挑」，不是绑定）。
/// - 保存后立刻回写 profile 并刷新调用方，不需要重启。
/// </summary>
public partial class ToolLaunchWindow : Window
{
    private readonly ToolProfile _profile;
    private readonly KnownTool? _known;
    private bool _loading = true;

    /// <summary>被删除了（调用方据此刷新网格）。</summary>
    public bool Deleted { get; private set; }

    /// <summary>有没有改动（调用方据此决定要不要重画）。</summary>
    public bool Changed { get; private set; }

    /// <summary>用档案打开；档案的 ToolId 能在目录里查到就顺带拿到参数候选。</summary>
    public ToolLaunchWindow(ToolProfile profile)
    {
        // 先给字段赋值再 InitializeComponent：XAML 里没有任何会提前触发回调的控件，
        // 但保持这个顺序是个习惯——v1.20 的 HandoverWindow 就是在这一步崩过一次。
        _profile = profile;
        _known = ToolCatalog.Find(profile.ToolId);
        InitializeComponent();
        Fill();
        _loading = false;
    }

    private void Fill()
    {
        TbTitle.Text = string.IsNullOrWhiteSpace(_profile.DisplayName) ? "启动设置" : _profile.DisplayName;
        TbIcon.Source = ToolIconService.GetBitmap(_profile);
        TbBlurb.Text = _known?.Blurb ?? "这个工具不在已知清单里，参数要你自己确认。";

        TbPath.Text = _profile.ExecutablePath ?? "";
        TbProcess.Text = _profile.ManagedProcessName ?? "";
        TbTimeout.Text = (_profile.MaxDurationMinutes > 0 ? _profile.MaxDurationMinutes : 120).ToString();
        TbArgs.Text = _profile.LaunchArguments ?? "";

        // 参数候选
        CbArgs.Items.Clear();
        if (_known != null)
        {
            foreach (var c in _known.Args)
            {
                CbArgs.Items.Add(new ComboBoxItem
                {
                    Content = KnownTool.ArgText(c),
                    Tag = c,
                    ToolTip = c.Hint,
                });
            }
        }
        // 当前值不在候选里时补一条，免得下拉一打开就显示空白（用户会以为自己填丢了）
        if (!string.IsNullOrWhiteSpace(TbArgs.Text))
        {
            bool found = _known != null && _known.Args.Any(a =>
                string.Equals(a.Value, TbArgs.Text, StringComparison.Ordinal));
            if (!found)
            {
                CbArgs.Items.Add(new ComboBoxItem
                {
                    Content = TbArgs.Text + "（当前值）",
                    Tag = new ArgChoice { Value = TbArgs.Text, Confidence = ArgConfidence.Custom, Hint = "这是你自己填的" },
                });
            }
        }
        if (CbArgs.Items.Count > 0) CbArgs.SelectedIndex = FindSelectedIndex();

        BtnDelete.Visibility = ProfileRegistry.IsBuiltIn(_profile.ToolId) ? Visibility.Collapsed : Visibility.Visible;
        RefreshArgsHint();
        RefreshRiskNote();
    }

    private int FindSelectedIndex()
    {
        for (int i = 0; i < CbArgs.Items.Count; i++)
        {
            if (CbArgs.Items[i] is ComboBoxItem it && it.Tag is ArgChoice c
                && string.Equals(c.Value, TbArgs.Text, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private ArgChoice? SelectedChoice
        => CbArgs.SelectedItem is ComboBoxItem it ? it.Tag as ArgChoice : null;

    private void CbArgs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SelectedChoice is not ArgChoice c) return;
        TbArgs.Text = c.Value;
        RefreshArgsHint();
    }

    private void TbArgs_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        RefreshArgsHint();
        RefreshRiskNote();
    }

    private void RefreshArgsHint()
    {
        var c = SelectedChoice;
        bool matches = c != null && string.Equals(c.Value, TbArgs.Text, StringComparison.Ordinal);

        if (matches && c != null)
        {
            TbArgsHint.Text = c.Hint + "　（" + KnownTool.ConfidenceText(c.Confidence) + "）";
            TbArgsHint.Foreground = (Brush)FindResource(
                c.Confidence == ArgConfidence.Custom ? "WarningBrush" : "TextTertiaryBrush");
            return;
        }

        // 用户改过，跟下拉选中的不一样：说清楚现在用的是他自己写的
        if (string.IsNullOrWhiteSpace(TbArgs.Text))
        {
            TbArgsHint.Text = "不传参数，程序会按它自己的默认方式启动。";
        }
        else
        {
            TbArgsHint.Text = "用你填的这条。参数原样传给程序，写错了它可能起不来——可以用下面的「试启动一次」验一下。";
        }
        TbArgsHint.Foreground = (Brush)FindResource("TextTertiaryBrush");
    }

    /// <summary>
    /// 权限提示：有些自动化工具必须管理员权限才能模拟输入。
    /// 我们不做「自动提权」，只提示——提权弹 UAC 是用户该看见的事。
    /// </summary>
    private void RefreshRiskNote()
    {
        var lines = new List<string>();
        if (string.Equals(_profile.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase))
            lines.Add("BetterGI 模拟输入需要管理员权限，启动时会弹一次 UAC。");
        if (TbArgs.Text.Contains("\"{组名}\"", StringComparison.Ordinal))
            lines.Add("「{组名}」会换成你在队列里给这一项勾的配置组。");
        lines.Add("判断跑完：目前按「进程退出且退出码 0」算。");
        TbRisk.Text = string.Join(" ", lines);
        TbRisk.Visibility = Visibility.Visible;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择主程序",
            Filter = "可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(_profile.ExecutablePath))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(_profile.ExecutablePath); } catch { }
        }
        if (dlg.ShowDialog(this) != true) return;
        SetPath(dlg.FileName);
    }

    private void BtnAutoFind_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? found = ToolDiscovery.TryAutoDiscover(_profile, out string source);
            if (!string.IsNullOrWhiteSpace(found))
            {
                TbPath.Text = _profile.ExecutablePath ?? "";
                TbFindHint.Text = "找到了：" + source;
                TbFindHint.Foreground = (Brush)FindResource("BrandTextBrush");
                TbFindHint.Visibility = Visibility.Visible;
                ToolIconService.Invalidate(_profile.ToolId);
                TbIcon.Source = ToolIconService.GetBitmap(_profile);
                return;
            }
            TbFindHint.Text = "没找到。点「浏览」手动选一次，之后它就能记住。";
            TbFindHint.Foreground = (Brush)FindResource("WarningBrush");
            TbFindHint.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            TbFindHint.Text = "自动寻找出错：" + ex.Message + "，点「浏览」手动选一次。";
            TbFindHint.Foreground = (Brush)FindResource("WarningBrush");
            TbFindHint.Visibility = Visibility.Visible;
        }
    }

    private void SetPath(string path)
    {
        _profile.ExecutablePath = path ?? "";
        TbPath.Text = _profile.ExecutablePath;

        // 进程名留空时按 exe 名补一个——绝大多数工具都是这样，省得用户自己想
        if (string.IsNullOrWhiteSpace(TbProcess.Text))
        {
            try { TbProcess.Text = Path.GetFileNameWithoutExtension(path) ?? ""; } catch { }
        }
        // 认识的工具顺手补默认参数
        if (string.IsNullOrWhiteSpace(TbArgs.Text) && _known != null && !string.IsNullOrWhiteSpace(_known.DefaultArguments))
        {
            TbArgs.Text = _known.DefaultArguments;
        }

        ToolIconService.Invalidate(_profile.ToolId);
        TbIcon.Source = ToolIconService.GetBitmap(_profile);
        TbFindHint.Visibility = Visibility.Collapsed;
    }

    private void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        SaveToProfile();
        if (string.IsNullOrWhiteSpace(_profile.ExecutablePath) || !File.Exists(_profile.ExecutablePath))
        {
            AppDialog.Show("还没选好程序，先点「浏览」选一次。", kind: AppDialog.Kind.Warning,
                title: "启动设置", primary: "知道了", owner: this);
            return;
        }

        string args = ToolRunner.BuildArguments(_profile, null);
        string shown = string.IsNullOrWhiteSpace(args) ? "（无参数）" : args;
        bool ok = AppDialog.Show(
            "会真的启动一次，看一眼它能不能起来。\n\n程序：" + Path.GetFileName(_profile.ExecutablePath) + "\n参数：" + shown,
            "启动后不会自动关掉它，看完自己关。",
            AppDialog.Kind.Info, "试启动一次", "启动", "算了", owner: this);
        if (!ok) return;

        try
        {
            var p = ToolRunner.StartProcess(_profile, args);
            if (p == null)
            {
                AppDialog.Show("没能启动，可能是路径不对，或者程序需要管理员权限。",
                    kind: AppDialog.Kind.Warning, title: "启动设置", primary: "知道了", owner: this);
                return;
            }
            LogService.LogEvent($"试启动 {_profile.ToolId}：{_profile.ExecutablePath} {args}");
            AppDialog.Show("已经启动了，去看一眼窗口有没有正常出现。",
                kind: AppDialog.Kind.Info, title: "试启动一次", primary: "知道了", owner: this);
        }
        catch (Exception ex)
        {
            AppDialog.Show("启动失败：" + ex.Message,
                kind: AppDialog.Kind.Warning, title: "启动设置", primary: "知道了", owner: this);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveToProfile()) return;
        Changed = true;
        Close();
    }

    private bool SaveToProfile()
    {
        string path = (TbPath.Text ?? "").Trim();
        if (path.Length > 0 && !File.Exists(path))
        {
            bool keep = AppDialog.Show("这个路径现在找不到文件了。\n\n" + path + "\n\n还是要保存吗？",
                kind: AppDialog.Kind.Warning, title: "启动设置", primary: "仍然保存", secondary: "回去改",
                owner: this);
            if (!keep) return false;
        }

        _profile.ExecutablePath = path;
        _profile.LaunchArguments = (TbArgs.Text ?? "").Trim();
        _profile.ManagedProcessName = (TbProcess.Text ?? "").Trim();
        if (int.TryParse((TbTimeout.Text ?? "").Trim(), out int minutes) && minutes > 0 && minutes <= 1440)
            _profile.MaxDurationMinutes = minutes;

        // BGI 的路径两处都要写：profile 是运行用的，Settings.BgiPath 是配置组扫描与配置读写用的
        if (string.Equals(_profile.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase))
            AppState.Settings.BgiPath = _profile.ExecutablePath;

        ProfileRegistry.Save(_profile);
        ToolIconService.Invalidate(_profile.ToolId);
        AppState.PublishLog($"✓ 已保存 {_profile.DisplayName} 的启动设置");
        LogService.LogEvent($"保存启动设置：{_profile.ToolId} args=\"{_profile.LaunchArguments}\"");
        return true;
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileRegistry.IsBuiltIn(_profile.ToolId))
        {
            AppDialog.Show("内置的三个工具不建议删——删了队列里那一项就跑不了。\n\n" +
                           "如果只是不想让它跑，在队列里取消勾选就行。",
                kind: AppDialog.Kind.Info, title: "启动设置", primary: "知道了", owner: this);
            return;
        }

        bool ok = AppDialog.Show("会把「" + _profile.DisplayName + "」从工具列表和队列里一起删掉。\n\n" +
                                "程序本身的文件不会动，以后还能再加回来。",
            kind: AppDialog.Kind.Warning, title: "删除这个工具", primary: "删掉", secondary: "算了", owner: this);
        if (!ok) return;

        if (ProfileRegistry.Remove(_profile.ToolId))
        {
            Deleted = true;
            Changed = true;
            AppState.PublishLog($"✓ 已删除工具 {_profile.DisplayName}");
            Close();
            return;
        }
        AppDialog.Show("删除没成功，详情见日志。",
            kind: AppDialog.Kind.Warning, title: "启动设置", primary: "知道了", owner: this);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
