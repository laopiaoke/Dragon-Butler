using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// v1.19：开跑前体检窗口。
///
/// 这一窗一个动作都不做——只把结论摆出来，修法全是「跳去对应界面」。
/// 理由：体检要是会改设置，用户就不敢点第二次了。
/// </summary>
public partial class PreflightWindow : Window
{
    private readonly Func<PreflightReport?> _provider;
    private readonly Action<string>? _onFix;

    /// <summary>用户在这里点过「去处理」时置 true，主窗据此决定要不要顺手刷新。</summary>
    public bool WentToFix { get; private set; }

    public PreflightWindow(Func<PreflightReport?> provider, Action<string>? onFix = null)
    {
        InitializeComponent();
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _onFix = onFix;
        Render();
    }

    private void Render()
    {
        PreflightReport? report;
        try
        {
            report = _provider();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("体检失败：" + ex.Message);
            report = null;
        }

        if (report == null)
        {
            TbHeadline.Text = "检查的时候出错了";
            TbSub.Text = "详情见日志。这不影响你直接用「一键开跑」。";
            ItemHost.Children.Clear();
            return;
        }

        TbHeadline.Text = report.Headline;

        ItemHost.Children.Clear();
        foreach (var item in report.Items)
        {
            if (item.Level == PreflightLevel.Ok) continue;   // 没问题的就不列了，清单不是报表
            ItemHost.Children.Add(BuildRow(item));
        }

        if (ItemHost.Children.Count == 0)
        {
            // 一条都不用处理：这时候再说「下面这些」就露馅了——下面什么都没有。
            TbSub.Text = "路径、队列、配置组、通知都看过了，没问题。";
            ItemHost.Children.Add(new TextBlock
            {
                Text = "可以直接点「一键开跑」。",
                FontSize = 12,
                Foreground = BrushOf("TextTertiaryBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        TbSub.Text = report.CanRun
            ? "下面这些不挡路，想跑随时可以跑。点「去处理」会跳到对应的地方。"
            : "把带红色标记的那几条处理掉就能开跑了。点「去处理」会跳到对应的地方。";
    }

    private UIElement BuildRow(PreflightItem item)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = BrushOf("InsetBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushOf(BorderKeyOf(item.Level)),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(TextKeyOf(item.Level)),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        if (item.Detail.Length > 0)
        {
            left.Children.Add(new TextBlock
            {
                Text = item.Detail,
                FontSize = 11.5,
                LineHeight = 18,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = BrushOf("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        if (item.FixText.Length > 0 && item.FixCommand.Length > 0)
        {
            var fix = new Button
            {
                Content = item.FixText,
                Style = TryFindResource("FixBtn") as Style,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 1, 0, 0),
                Tag = item.FixCommand,
            };
            fix.Click += (_, _) =>
            {
                WentToFix = true;
                _onFix?.Invoke(item.FixCommand);
                Close();
            };
            Grid.SetColumn(fix, 1);
            grid.Children.Add(fix);
        }

        border.Child = grid;
        return border;
    }

    private Brush BrushOf(string key)
        => (Brush?)TryFindResource(key) ?? Brushes.Gray;

    private static string BorderKeyOf(PreflightLevel level) => level switch
    {
        PreflightLevel.Block => "DangerBrush",
        PreflightLevel.Warn => "WarningBrush",
        _ => "BorderBrush",
    };

    private static string TextKeyOf(PreflightLevel level) => level switch
    {
        PreflightLevel.Block => "DangerBrush",
        PreflightLevel.Warn => "WarningBrush",
        _ => "TextPrimaryBrush",
    };

    private void BtnRecheck_Click(object sender, RoutedEventArgs e) => Render();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
