using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 消息中心（v1.28）。
///
/// <para>
/// 存在的理由写一句就够了：<b>以前出错要弹一个挡路的对话框</b>，而那个框在启动流程里排在
/// 自动开跑前面，人不在电脑前的时候，程序就卡在那儿等一个没人点的「知道了」——
/// 用户第二天看到的正是「只弹了个崩溃提示，一条龙整晚没跑」。
/// </para>
/// <para>
/// 现在改成：出错只<b>记一条</b>，标题栏右上角亮个红点。想看的自己点开，
/// 不想看的照样让程序继续跑。窗口本身不模态（<c>Show()</c> 不是 <c>ShowDialog()</c>），
/// 开着它也不挡后面的活。
/// </para>
/// </summary>
public partial class NoticeWindow : Window
{
    public NoticeWindow()
    {
        InitializeComponent();
        Build();
    }

    // tidy：查主题资源的规则只在 Theme.BrushOf 一处，这里只留转发（16 个调用点都不改）。
    private static Brush BrushOf(string key, Brush fallback) => Theme.BrushOf(key, fallback);

    private void Build()
    {
        var all = NoticeCenter.All();

        TbCount.Text = all.Count == 0
            ? ""
            : $"共 {all.Count} 条" + (NoticeCenter.UnreadCount > 0 ? $"，{NoticeCenter.UnreadCount} 条没看" : "");

        ListHost.Children.Clear();
        bool empty = all.Count == 0;
        TbEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        BtnMarkRead.IsEnabled = !empty;
        BtnClear.IsEnabled = !empty;

        TbSummary.Text = BuildSummary(all);

        foreach (var n in all) ListHost.Children.Add(BuildCard(n));

        // 打开就算看过了：红点说的是「有新的」，不是「还有没处理的」
        if (!empty && NoticeCenter.UnreadCount > 0) NoticeCenter.MarkAllRead();
    }

    /// <summary>
    /// 顶部那句摘要。有崩溃就直说崩溃——这是唯一一件「你今晚可能白等了」的事。
    /// </summary>
    private static string BuildSummary(System.Collections.Generic.List<Notice> all)
    {
        if (all.Count == 0) return "";

        var today = all.FindAll(n => n.At.Date == DateTime.Today);
        int crash = all.FindAll(n => n.Kind == NoticeKind.Crash).Count;
        int ourFault = all.FindAll(n => n.OurFault).Count;

        var parts = new System.Collections.Generic.List<string>();
        if (today.Count > 0) parts.Add($"今天留下 {today.Count} 条");
        else parts.Add($"最近 {all.Count} 条从 {all[^1].At:M月d日} 开始");

        if (crash > 0)
        {
            parts.Add(ourFault > 0
                ? $"其中 {crash} 次是程序自己出错（不是你配错了）"
                : $"其中 {crash} 次是程序没能正常收尾");
        }

        return string.Join("，", parts) + "。";
    }

    private Border BuildCard(Notice n)
    {
        var (accent, glyph) = n.Kind switch
        {
            NoticeKind.Crash => (BrushOf("DangerBrush", Brushes.IndianRed), "!"),
            NoticeKind.Error => (BrushOf("WarningBrush", Brushes.Goldenrod), "!"),
            NoticeKind.Warn => (BrushOf("WarningBrush", Brushes.Goldenrod), "·"),
            _ => (BrushOf("BrandBrush", Brushes.SeaGreen), "i"),
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧一条竖色带：靠颜色分类，不靠标签堆字
        var bar = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 10, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 3,
        };
        Grid.SetColumn(bar, 0);
        root.Children.Add(bar);

        var body = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        Grid.SetColumn(body, 1);
        root.Children.Add(body);

        // 第一行：类型 + 时间
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = glyph + " " + NoticeCenter.KindName(n.Kind),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = n.At.ToString("M月d日 HH:mm"),
            FontSize = 10.5,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (n.OurFault)
        {
            head.Children.Add(new Border
            {
                // 没有「浅红」这个令牌，就用插槽色打底——重点靠字色（accent）表达，
                // 底色只要比卡片暗一点点就够，不必为此新增一个主题键。
                Background = BrushOf("InsetBrush", Brushes.Transparent),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = "程序的问题",
                    FontSize = 10,
                    Foreground = accent,
                },
            });
        }
        body.Children.Add(head);

        // 标题
        body.Children.Add(new TextBlock
        {
            Text = n.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = BrushOf("TextPrimaryBrush", Brushes.White),
        });

        // 人话解释
        if (n.Body.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = n.Body,
                FontSize = 11.5,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = BrushOf("TextSecondaryBrush", Brushes.LightGray),
            });
        }

        // 原始内容：折叠着。它有用，但它是给作者用的。
        if (n.Raw.Length > 0)
        {
            var toggle = new Button
            {
                Content = "看原始内容",
                FontSize = 10.5,
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            var rawBox = new Border
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(8),
                Background = BrushOf("InsetBrush", Brushes.Black),
                Child = new TextBlock
                {
                    Text = n.Raw.Length > 4000 ? n.Raw.Substring(0, 4000) + "\n…（后面还有）" : n.Raw,
                    FontSize = 10.5,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = BrushOf("TextSecondaryBrush", Brushes.LightGray),
                },
            };
            toggle.Click += (_, _) =>
            {
                bool show = rawBox.Visibility != Visibility.Visible;
                rawBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = show ? "收起原始内容" : "看原始内容";
            };
            body.Children.Add(toggle);
            body.Children.Add(rawBox);
        }

        // 卡片底部：复制 / 删掉
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var copy = MakeMiniButton("复制这一段");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(NoticeCenter.ToClipboard(n));
                copy.Content = "复制好了";
            }
            catch { copy.Content = "复制失败"; }
        };
        var del = MakeMiniButton("删掉这条");
        del.Click += (_, _) =>
        {
            NoticeCenter.Remove(n);
            Build();
        };
        actions.Children.Add(copy);

        // v1.45 / M12-2（D26）：只有「本进程里刚失败、原样还留着」的那条才给重发按钮。
        //
        // 重启之后原来的正文就不在了，这里硬给一个按钮，点下去只能发出一条空消息——
        // 比没有按钮更糟。所以没有载荷的条目改成一句实话，而不是一个假按钮。
        if (NotifyService.HasResendPayload(n))
        {
            var resend = MakeMiniButton("重发");
            resend.Click += async (_, _) =>
            {
                // 连点防护第一层：自己先禁掉，等人点完这一下再说。
                resend.IsEnabled = false;
                resend.Content = "正在重发…";
                try
                {
                    var (ok, err) = await NotifyService.ResendAsync(n);
                    resend.Content = ok ? "重发成功" : "还是没发出去";
                    if (!ok)
                    {
                        resend.IsEnabled = true;
                        resend.ToolTip = err;
                    }
                    else
                    {
                        Build();
                    }
                }
                catch (Exception ex)
                {
                    resend.Content = "重发失败";
                    resend.IsEnabled = true;
                    resend.ToolTip = ex.Message;
                }
            };
            actions.Children.Add(resend);
        }
        else if (n.Kind == NoticeKind.Error && n.Body.Contains("没能发出去", StringComparison.Ordinal))
        {
            actions.Children.Add(new TextBlock
            {
                Text = NotifyService.ResendHintFor(n),
                FontSize = 10.5,
                Margin = new Thickness(2, 4, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BrushOf("TextTertiaryBrush", Brushes.Gray),
            });
        }

        actions.Children.Add(del);
        body.Children.Add(actions);

        return new Border
        {
            Background = BrushOf("CardBg2Brush", BrushOf("CardBgBrush", Brushes.DimGray)),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = root,
        };
    }

    private Button MakeMiniButton(string text)
    {
        return new Button
        {
            Content = text,
            FontSize = 10.5,
            Height = 22,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Foreground = BrushOf("TextSecondaryBrush", Brushes.Gray),
            Background = BrushOf("InsetBrush", Brushes.Black),
            BorderThickness = new Thickness(0),
        };
    }

    private void BtnMarkRead_Click(object sender, RoutedEventArgs e)
    {
        NoticeCenter.MarkAllRead();
        Build();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (NoticeCenter.Count == 0) return;
        bool ok = AppDialog.Show(
            $"清掉全部 {NoticeCenter.Count} 条消息？",
            detail: "清掉之后就没了。日志目录里的运行日志和崩溃转储不受影响，还能去那里翻。",
            kind: AppDialog.Kind.Question,
            title: "清空消息",
            primary: "清空",
            secondary: "算了",
            owner: this);
        if (!ok) return;
        NoticeCenter.Clear();
        Build();
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogService.Dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + LogService.Dir + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.LogEvent("打开日志目录失败：" + ex.Message);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }
}
