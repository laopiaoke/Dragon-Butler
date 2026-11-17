using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 出门前看一眼（v1.20 / 奇思妙想 #6）。
///
/// <para>
/// 这个窗口只做一件事：把「你走之后它会做什么、什么情况下会停手」说成人话。
/// 它<b>不改任何设置</b>——风险项要修就去对应的卡片修，因为改设置需要上下文
/// （比如「没配通知」得知道你想用哪个渠道），塞进这个清单里会变成又一层表单。
/// </para>
/// </summary>
public partial class HandoverWindow : Window
{
    private readonly Func<int, Handover.Sheet?> _provider;
    private readonly Func<int, Handover.Input?> _input;
    private int _days = 3;

    public HandoverWindow(Func<int, Handover.Sheet?> provider, Func<int, Handover.Input?> input)
    {
        // 先赋值再 InitializeComponent：XAML 里 CbDays 的 IsSelected="True" 会在
        // 解析过程中就触发 SelectionChanged → Render()，那时字段还是 null。
        _provider = provider;
        _input = input;
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        // InitializeComponent 期间 CbDays 会先于 TbHeadline 建好并触发一次
        // SelectionChanged，那时控件字段还是 null。等控件齐了再画。
        if (_provider == null || TbHeadline == null) return;
        try
        {
            var sheet = _provider(_days);
            if (sheet == null)
            {
                TbHeadline.Text = "看不出来";
                ItemHost.Children.Clear();
                return;
            }

            TbHeadline.Text = sheet.Headline;
            TbHeadline.Foreground = sheet.HasRisk
                ? BrushOf("WarningBrush", Brushes.Orange)
                : BrushOf("BrandTextBrush", Brushes.Green);

            ItemHost.Children.Clear();
            foreach (var item in sheet.Items)
                ItemHost.Children.Add(BuildRow(item));

            TbFoot.Text = sheet.HasRisk
                ? "带 × 的这几件修好之后，这几天才真的能自己过。"
                : "没发现会让它白跑的事。";
        }
        catch (Exception ex)
        {
            TbHeadline.Text = "看的时候出错了";
            TbFoot.Text = ex.Message;
        }
    }

    private UIElement BuildRow(Handover.Item item)
    {
        string glyph = item.Level switch
        {
            Handover.Severity.Risk => "×",
            Handover.Severity.Plan => "·",
            _ => "✓",
        };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mark = new TextBlock
        {
            Text = glyph,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
            // 圆点是列表符号，不该比正文更醒目。
            Foreground = item.Level switch
            {
                Handover.Severity.Risk => BrushOf("DangerBrush", Brushes.IndianRed),
                Handover.Severity.Plan => BrushOf("TextTertiaryBrush", Brushes.Gray),
                _ => BrushOf("BrandTextBrush", Brushes.Green),
            },
        };
        Grid.SetColumn(mark, 0);

        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = 11.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = item.Level == Handover.Severity.Risk
                ? BrushOf("TextPrimaryBrush", Brushes.White)
                : BrushOf("TextSecondaryBrush", Brushes.LightGray),
        };
        Grid.SetColumn(text, 1);

        grid.Children.Add(mark);
        grid.Children.Add(text);
        return grid;
    }

    private Brush BrushOf(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;

    private void CbDays_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbDays.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag as string, out int days) && days > 0)
        {
            _days = days;
            Render();
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sheet = _provider(_days);
            var input = _input(_days);
            if (sheet == null || input == null) return;
            Clipboard.SetText(Handover.Report(sheet, input));
            TbFoot.Text = "已复制到剪贴板。";
        }
        catch
        {
            TbFoot.Text = "复制没成功，剪贴板可能被别的程序占着。";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
