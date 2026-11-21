using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// v1.41 / M4-2：把「超过一行」的设置说明收成一行，行尾挂一个 ⓘ，点一下或把鼠标放上去再展开。
///
/// 为什么要有这个：设置页里有几行副文案本来就长（比如夜间息屏那句「运行指定分钟数后关闭显示器，
/// 系统保持唤醒」），窗口一窄就折成三行，五张卡里全是字，真正要看的开关反倒被挤到看不出层次。
/// 收成一行之后，一屏能看见「有什么可改」；想知道细节的人自己把 ⓘ 点开——不删字，只是先收着。
///
/// 只在真的超过一行时才动手：本来一行放得下的说明不挂 ⓘ，免得每个设置项后面都吊着一个小图标。
/// </summary>
internal static class HintFold
{
    /** 挂在元素上的标记，避免同一个说明被包两次（窗口改大小时会重新走一遍）。 */
    private static readonly DependencyProperty FoldedProperty =
        DependencyProperty.RegisterAttached("Folded", typeof(bool), typeof(HintFold), new PropertyMetadata(false));

    public static bool GetFolded(DependencyObject d) => (bool)d.GetValue(FoldedProperty);
    private static void SetFolded(DependencyObject d, bool v) => d.SetValue(FoldedProperty, v);

    /// <summary>把这个容器里所有「超过一行」的说明收起来。可以反复调用，已经收过的不会再包一层。</summary>
    public static void Apply(DependencyObject? root)
    {
        if (root == null) return;
        foreach (TextBlock hint in Collect(root))
        {
            try { Fold(hint); }
            catch { /* 一句说明收不起来不算事，别让它把整个设置页拖下水 */ }
        }
    }

    /// <summary>在可视树里找所有 SetHint 的 TextBlock。</summary>
    private static List<TextBlock> Collect(DependencyObject root)
    {
        var found = new List<TextBlock>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (node is TextBlock tb && ReferenceEquals(tb.Style, Application.Current?.TryFindResource("SetHint")))
            {
                found.Add(tb);
                continue;   // 说明里不会再套说明
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return found;
    }

    private static void Fold(TextBlock hint)
    {
        if (GetFolded(hint)) return;
        if (hint.Parent is not Panel panel) return;

        double line = hint.LineHeight > 0 && !double.IsNaN(hint.LineHeight) ? hint.LineHeight : hint.FontSize * 1.5;
        // 还没量出来（高度为 0）就先不动，等下一次布局到位再来。
        if (hint.ActualHeight <= 0) return;
        if (hint.ActualHeight <= line + 1.0) { SetFolded(hint, true); return; }   // 一行放得下，不挂 ⓘ

        int index = panel.Children.IndexOf(hint);
        if (index < 0) return;

        panel.Children.RemoveAt(index);

        var row = new Grid { Margin = hint.Margin };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        hint.Margin = new Thickness(0);
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(hint, 0);
        row.Children.Add(hint);

        var info = new TextBlock
        {
            Text = "ⓘ",
            FontSize = hint.FontSize + 0.5,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = hint.Foreground,
            Cursor = Cursors.Hand,
            ToolTip = "点一下看完整说明",
        };
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        panel.Children.Insert(index, row);
        SetFolded(hint, true);

        bool expanded = false;

        void Expand()
        {
            if (expanded) return;
            expanded = true;
            hint.TextWrapping = TextWrapping.Wrap;
            hint.TextTrimming = TextTrimming.None;
            info.Text = "ⓘ";
            info.ToolTip = null;
        }

        // 悬停就展开：说明是给「想看一眼」的人看的，不该逼人先点一下。
        row.MouseEnter += (_, _) => Expand();
        info.MouseLeftButtonUp += (_, e) => { Expand(); e.Handled = true; };
    }
}
