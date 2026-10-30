using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 自定义工具表单（v1.4）：新增/编辑任意 exe，支持「进程退出」与「到达时长」两种完成方式。
/// 保存后写入 tools/{id}.json 与 settings，并自动加入队列。
/// </summary>
public partial class CustomToolWindow : Window
{
    private readonly ToolProfile? _original;
    private bool _loaded;

    /// <summary>保存后的档案（删除时为 null）。</summary>
    public ToolProfile? Result { get; private set; }

    /// <summary>是否执行了删除。</summary>
    public bool Deleted { get; private set; }

    /// <summary>打开表单；existing=null 表示新增。</summary>
    public CustomToolWindow(ToolProfile? existing = null)
    {
        InitializeComponent();
        _original = existing?.Clone();

        Title = _original == null ? "添加自定义工具" : "编辑自定义工具";
        if (_original != null)
        {
            TbName.Text = _original.DisplayName;
            TbPath.Text = _original.ExecutablePath;
            TbArgs.Text = _original.LaunchArguments;
            TbProcess.Text = _original.ManagedProcessName;
            TbGames.Text = string.Join(";", _original.GameProcessNames ?? Array.Empty<string>());
            TbMax.Text = Math.Max(1, _original.MaxDurationMinutes).ToString();
            bool duration = _original.PrimarySignal == CompletionSignalKind.Duration;
            RbDuration.IsChecked = duration;
            RbExit.IsChecked = !duration;
            if (_original.CustomData.TryGetValue("durationMinutes", out string? d)
                && int.TryParse(d, out int minutes))
                TbDuration.Text = minutes.ToString();
            BtnDelete.Visibility = ProfileRegistry.IsBuiltIn(_original.ToolId)
                ? Visibility.Collapsed : Visibility.Visible;
            RefreshIcon();
        }
        else
        {
            RbExit.IsChecked = true;
            BtnDelete.Visibility = Visibility.Collapsed;
        }

        _loaded = true;
        DoneMode_Checked(null, null);
        Loaded += (_, _) =>
        {
            TbName.Focus();
            UpdateIconFallback();
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void DoneMode_Checked(object? sender, RoutedEventArgs? e)
    {
        if (!_loaded) return;
        bool duration = RbDuration.IsChecked == true;
        if (DurationRow != null)
            DurationRow.Visibility = duration ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要接管的程序",
            Filter = "可执行文件|*.exe|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        string oldName = "";
        try { oldName = Path.GetFileNameWithoutExtension(TbPath.Text); } catch { }
        TbPath.Text = dlg.FileName;

        string exeName = Path.GetFileNameWithoutExtension(dlg.FileName);
        if (string.IsNullOrWhiteSpace(TbProcess.Text)
            || string.Equals(TbProcess.Text.Trim(), oldName, StringComparison.OrdinalIgnoreCase))
            TbProcess.Text = exeName;

        if (string.IsNullOrWhiteSpace(TbName.Text))
            TbName.Text = exeName.Replace(" Launcher", "", StringComparison.OrdinalIgnoreCase).Trim();

        if (_original != null) ToolIconService.Invalidate(_original.ToolId);
        RefreshIcon();
        TbPathHint.Text = $"已选择：{dlg.FileName}";
        TbPathHint.Foreground = TryFindResource("SuccessBrush") as Brush ?? TbPathHint.Foreground;
        TbError.Visibility = Visibility.Collapsed;
    }

    private void BtnIconRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_original != null) ToolIconService.Invalidate(_original.ToolId);
        RefreshIcon();
    }

    private void RefreshIcon()
    {
        try
        {
            string path = TbPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                IconPreview.Visibility = Visibility.Collapsed;
                IconHint.Text = "选择程序后自动提取";
                UpdateIconFallback();
                return;
            }

            var temp = new ToolProfile
            {
                ToolId = _original?.ToolId ?? "preview",
                DisplayName = TbName.Text,
                ExecutablePath = path,
            };
            var bmp = ToolIconService.GetBitmap(temp);
            if (bmp != null)
            {
                IconPreview.Source = bmp;
                IconPreview.Visibility = Visibility.Visible;
                IconHint.Text = "已从程序中提取图标";
            }
            else
            {
                IconPreview.Visibility = Visibility.Collapsed;
                IconHint.Text = "这个程序没有可提取的图标，将显示首字母";
            }
        }
        catch
        {
            IconPreview.Visibility = Visibility.Collapsed;
        }
        UpdateIconFallback();
    }

    private void UpdateIconFallback()
    {
        string name = TbName.Text.Trim();
        IconFallback.Text = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = TbName.Text.Trim();
            string path = TbPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("给这个工具起个名字，例如「星铁助手」。");
                TbName.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowError("程序路径不对。点「浏览…」重新选一次 exe。");
                return;
            }

            string process = TbProcess.Text.Trim();
            if (string.IsNullOrWhiteSpace(process))
                process = Path.GetFileNameWithoutExtension(path);
            process = process.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(process))
            {
                ShowError("进程名不能为空。通常填 exe 的文件名，不含 .exe。");
                TbProcess.Focus();
                return;
            }

            bool duration = RbDuration.IsChecked == true;
            int durationMinutes = 60;
            if (duration
                && (!int.TryParse(TbDuration.Text.Trim(), out durationMinutes)
                    || durationMinutes < 1 || durationMinutes > 600))
            {
                ShowError("到达时长请填 1～600 之间的分钟数。");
                TbDuration.Focus();
                return;
            }

            if (!int.TryParse(TbMax.Text.Trim(), out int maxMinutes)
                || maxMinutes < 1 || maxMinutes > 1440)
            {
                ShowError("最大运行时长请填 1～1440 之间的分钟数。");
                TbMax.Focus();
                return;
            }

            var games = (TbGames.Text ?? "")
                .Split(new[] { ';', '；', ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var profile = _original?.Clone() ?? new ToolProfile
            {
                ToolId = "custom-" + Guid.NewGuid().ToString("N")[..8],
            };
            profile.DisplayName = name;
            profile.ExecutablePath = path;
            profile.LaunchArguments = (TbArgs.Text ?? "").Trim();
            profile.ManagedProcessName = process;
            profile.GameProcessNames = games;
            profile.PrimarySignal = duration ? CompletionSignalKind.Duration : CompletionSignalKind.ProcessExit0;
            profile.SecondarySignals = Array.Empty<CompletionSignalKind>();
            profile.LogCompletionPattern = null;
            profile.MaxDurationMinutes = maxMinutes;
            profile.PostExitGraceMinutes = 0;
            profile.RetryCount = 1;
            profile.CustomData["custom"] = "1";
            profile.CustomData["durationMinutes"] = durationMinutes.ToString();

            ToolIconService.Invalidate(profile.ToolId);
            ProfileRegistry.Save(profile);

            var queue = AppState.Settings.Queue ??= new List<QueueEntry>();
            if (!queue.Any(q => string.Equals(q.ToolId, profile.ToolId, StringComparison.OrdinalIgnoreCase)))
                queue.Add(new QueueEntry { ToolId = profile.ToolId, Enabled = true });
            AppState.SaveSettings();

            Result = profile;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存自定义工具失败：" + ex);
            ShowError("保存失败，请检查填写内容后重试。");
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        bool ok = AppDialog.Show(
            $"删除「{_original.DisplayName}」后，它的工具卡与队列项都会移除；程序本身不会被删除。",
            "这个操作不可撤销。",
            AppDialog.Kind.Warning,
            "删除工具",
            "删除",
            "取消",
            null,
            this);
        if (!ok) return;

        if (ProfileRegistry.Remove(_original.ToolId))
        {
            Deleted = true;
            Result = null;
            DialogResult = true;
        }
        else
        {
            ShowError("这是内置工具，不能删除。");
        }
    }

    private void ShowError(string message)
    {
        TbError.Text = message;
        TbError.Visibility = Visibility.Visible;
    }
}
