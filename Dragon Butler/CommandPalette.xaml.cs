using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DragonButler;

/// <summary>
/// Ctrl+K 命令面板（v1.8 / F15）。
///
/// 这个窗口只做三件事：收字、筛命令、把选中的命令 id 交出去。
/// 命令真正干什么由 MainWindow 决定——面板不认识队列、不认识主题、不认识托盘，
/// 所以二十条命令里任意一条出问题，都不会把这个面板拖垮。
/// </summary>
public partial class CommandPalette : Window
{
    /// <summary>用户选中了一条命令（参数是 CommandRegistry 里的稳定 id）。</summary>
    public event Action<string>? Executed;

    /// <summary>本次面板可搜的全部命令：固定的那二十条 + 调用方临时塞进来的（比如「单跑某工具」）。</summary>
    private readonly IReadOnlyList<CommandRegistry.Command> _source;

    public CommandPalette() : this(null) { }

    public CommandPalette(IEnumerable<CommandRegistry.Command>? extra)
    {
        InitializeComponent();

        var all = new List<CommandRegistry.Command>(CommandRegistry.All);
        if (extra != null)
        {
            foreach (var cmd in extra)
                if (!all.Exists(c => string.Equals(c.Id, cmd.Id, StringComparison.Ordinal)))
                    all.Add(cmd);
        }
        _source = all;

        Loaded += (_, _) =>
        {
            TbQuery.Focus();
            Refresh("");
        };
        // 失焦即关：命令面板是「用完就走」的东西，留在屏幕上挡住游戏画面没有意义
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private void Refresh(string query)
    {
        var hits = CommandRegistry.Search(_source, query, 8);
        LbResults.ItemsSource = hits;
        if (hits.Count > 0) LbResults.SelectedIndex = 0;
        TbHint.Text = hits.Count == 0
            ? "没有匹配的命令 · Esc 关闭"
            : "↑↓ 选择 · Enter 执行 · Esc 关闭";
    }

    private void TbQuery_TextChanged(object sender, TextChangedEventArgs e) => Refresh(TbQuery.Text);

    private void TbQuery_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = LbResults.Items.Count;
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                if (count > 0) LbResults.SelectedIndex = Math.Min(LbResults.SelectedIndex + 1, count - 1);
                break;
            case Key.Up:
                e.Handled = true;
                if (count > 0) LbResults.SelectedIndex = Math.Max(LbResults.SelectedIndex - 1, 0);
                break;
            case Key.Enter:
                e.Handled = true;
                Run();
                break;
        }
    }

    private void LbResults_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Run();

    private void Run()
    {
        if (LbResults.SelectedItem is not CommandRegistry.Command cmd) return;
        // 先关窗再执行：命令里可能有模态框（比如关于），面板留在上面会叠成一坨
        Close();
        Executed?.Invoke(cmd.Id);
    }

    /// <summary>拖动窗口：只认标题区/空白区，点列表里那一行不能拖走。</summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Border && e.OriginalSource is not TextBlock) return;
        try { DragMove(); } catch { /* 极快点击时 DragMove 会抛，无害 */ }
    }
}
