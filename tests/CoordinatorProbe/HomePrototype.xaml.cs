using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CoordinatorProbe;

/// <summary>
/// v1.15 首屏原型（只看效果，不接业务）。
///
/// 要回答的问题只有一个：新用户打开软件的第一眼，能不能不读说明就知道「点哪里」。
/// 所以这里用同一张卡画四种状态，摊开对比：
///   first  —— 什么都没配
///   ready  —— 配好了，还没到点
///   running—— 正在跑
///   done   —— 跑完了
/// </summary>
public partial class HomePrototype : Window
{
    public HomePrototype(string state)
    {
        InitializeComponent();
        Build(state);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private static Brush B(string key) =>
        (Brush)(Application.Current.TryFindResource(key) ?? Brushes.Gray);

    private static TextBlock T(string text, double size, string brushKey, bool bold = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = B(brushKey),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// 正文段：加 MaxWidth 的同时必须显式靠左——Stretch + MaxWidth 会让元素在可用宽度里居中，
    /// 渲染出来左边空一大块，看着像缩进错了。
    /// </summary>
    private static TextBlock Body(string text, string brushKey = "TextSecondaryBrush")
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = B(brushKey),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private void Build(string state)
    {
        switch (state)
        {
            case "ready": BuildReady(); break;
            case "running": BuildRunning(); break;
            case "done": BuildDone(); break;
            default: BuildFirst(); break;
        }
    }

    // ---------- 一、什么都没配 ----------
    private void BuildFirst()
    {
        HeroLeft.Children.Add(T("先告诉我在哪", 20, "TextPrimaryBrush", true));
        HeroLeft.Children.Add(Body("找到 BetterGI 的 exe，选一下就好。剩下的我按默认值配。"));

        var steps = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        steps.Children.Add(StepLine("1", "选 BetterGI 的 exe", "点右边按钮，或者把 exe 拖进来", true));
        steps.Children.Add(StepLine("2", "确认每天几点跑", "默认早上 8 点，之后随时能改", false));
        steps.Children.Add(StepLine("3", "点开跑，睡觉", "跑完会给你一句话", false));
        HeroLeft.Children.Add(steps);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var pick = new Button { Content = "选择 BetterGI.exe", Style = (Style)FindResource("ProtoPrimary"), Height = 44 };
        var later = new Button { Content = "我先看看", Style = (Style)FindResource("ProtoGhost"), Height = 44, Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(pick);
        right.Children.Add(later);
        HeroRight.Children.Add(right);

        // 队列行：全部未配置
        QueueHost.Children.Add(Row("BetterGI", "一条龙本体", "未配置", "TextTertiaryBrush", false, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("三月七助手", "体力 / 委托", "未配置", "TextTertiaryBrush", false, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("ok-ww", "鸣潮", "未配置", "TextTertiaryBrush", false, "没开"));

        Tile(TileA, "守护", "关着", "开起来会自己盯着，出问题先提醒你");
        Tile(TileB, "通知", "只在这台电脑弹", "想推到手机就点这里");
        Tile(TileC, "今天", "还没有记录", "跑完第一次后这里会有东西");
    }

    // ---------- 二、配好了，还没到点 ----------
    private void BuildReady()
    {
        HeroLeft.Children.Add(T("今晚 23:30 自己开跑", 20, "TextPrimaryBrush", true));
        HeroLeft.Children.Add(Body("还有 13 小时 28 分。到点如果你在玩游戏，我会等你退出再开始。"));

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        chips.Children.Add(Pill("每天", true));
        chips.Children.Add(Pill("08:00", false));
        chips.Children.Add(Pill("跑完通知我", false));
        HeroLeft.Children.Add(chips);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        right.Children.Add(new Button { Content = "现在就开跑", Style = (Style)FindResource("ProtoPrimary"), Height = 44 });
        right.Children.Add(new Button { Content = "改时间", Style = (Style)FindResource("ProtoGhost"), Height = 44, Margin = new Thickness(8, 0, 0, 0) });
        HeroRight.Children.Add(right);

        QueueHost.Children.Add(Row("BetterGI", "一条龙本体", "已就绪", "SuccessBrush", true, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("三月七助手", "体力 / 委托", "已就绪", "SuccessBrush", true, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("ok-ww", "鸣潮", "今天不跑", "TextTertiaryBrush", false, "没开"));

        Tile(TileA, "守护", "开着", "内存、卡住、游戏掉了都会说");
        Tile(TileB, "通知", "只在这台电脑弹", "想推到手机就点这里");
        Tile(TileC, "最近一次", "昨天 08:00", "跑了 46 分钟，全部成功");
    }

    // ---------- 三、正在跑 ----------
    private void BuildRunning()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse
        {
            Width = 7, Height = 7, Fill = B("BrandBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 9, 0),
        });
        head.Children.Add(T("日常 · 已跑 18 分钟", 20, "TextPrimaryBrush", true));
        HeroLeft.Children.Add(head);

        var runBody = Body("通常这时候差不多了，再等等。整体进度 5 / 7。");
        runBody.Margin = new Thickness(16, 8, 0, 0);
        HeroLeft.Children.Add(runBody);

        // 进度条
        var track = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            Background = B("BorderBrush"), Margin = new Thickness(16, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Width = 380,
        };
        var fill = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            Background = B("BrandBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 271,
        };
        var trackGrid = new Grid { Margin = new Thickness(16, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        trackGrid.Children.Add(track);
        trackGrid.Children.Add(fill);
        HeroLeft.Children.Add(trackGrid);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        right.Children.Add(new Button { Content = "停下", Style = (Style)FindResource("ProtoGhost"), Height = 44 });
        HeroRight.Children.Add(right);

        QueueHost.Children.Add(Row("BetterGI", "日常 · 活动", "正在跑", "BrandTextBrush", true, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("三月七助手", "体力 / 委托", "等上一个跑完", "TextTertiaryBrush", false, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("ok-ww", "鸣潮", "今天不跑", "TextTertiaryBrush", false, "没开"));

        Tile(TileA, "守护", "开着", "刚才 23:14 提醒过一次内存偏高");
        Tile(TileB, "通知", "只在这台电脑弹", "想推到手机就点这里");
        Tile(TileC, "昨天", "46 分钟", "全成功，比往常快 6 分钟");
    }

    // ---------- 四、跑完了 ----------
    private void BuildDone()
    {
        HeroLeft.Children.Add(T("跑完了，46 分钟", 20, "TextPrimaryBrush", true));
        HeroLeft.Children.Add(Body("3 个工具全成功，比往常快 6 分钟。电脑还是我刚才设的：跑完就睡。"));

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        chips.Children.Add(Pill("明天 08:00 再来", true));
        chips.Children.Add(Pill("看今天都跑了啥", false));
        HeroLeft.Children.Add(chips);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        right.Children.Add(new Button { Content = "看今天", Style = (Style)FindResource("ProtoPrimary"), Height = 44 });
        HeroRight.Children.Add(right);

        QueueHost.Children.Add(Row("BetterGI", "日常 · 活动", "已完成", "SuccessBrush", true, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("三月七助手", "体力 / 委托", "已完成", "SuccessBrush", true, "每天"));
        QueueHost.Children.Add(Sep());
        QueueHost.Children.Add(Row("ok-ww", "鸣潮", "今天不跑", "TextTertiaryBrush", false, "没开"));

        Tile(TileA, "守护", "开着", "跑完了，不用盯着了");
        Tile(TileB, "通知", "已推送", "微信收到一句话");
        Tile(TileC, "这个月", "平均 48 分钟", "最快 41，最慢 62");
    }

    // ---------- 小零件 ----------
    private FrameworkElement StepLine(string no, string title, string hint, bool active)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 11) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            Background = active ? B("BrandBrush") : B("ControlBgBrush"),
            BorderBrush = active ? B("BrandBrush") : B("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.Child = new TextBlock
        {
            Text = no, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = active ? B("BrandOnBrush") : B("TextTertiaryBrush"),
        };
        g.Children.Add(badge);

        // 标题和补充说明贴在一起（原来分三列，中间空出两百多像素，读起来像两张表）
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 0, 0) };
        line.Children.Add(T(title, 12.5, active ? "TextPrimaryBrush" : "TextSecondaryBrush", active));
        var h = T(hint, 11.5, "TextTertiaryBrush");
        h.Margin = new Thickness(10, 0, 0, 0);
        h.TextTrimming = TextTrimming.CharacterEllipsis;
        line.Children.Add(h);
        Grid.SetColumn(line, 1);
        g.Children.Add(line);

        return g;
    }

    private Border Pill(string text, bool active)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Background = active ? B("BrandSoftBrush") : Brushes.Transparent,
            BorderBrush = active ? B("BrandBrush") : B("BorderBrush"),
            BorderThickness = new Thickness(1),
        };
        b.Child = new TextBlock
        {
            Text = text, FontSize = 11.5,
            Foreground = active ? B("BrandTextBrush") : B("TextSecondaryBrush"),
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
        };
        return b;
    }

    private FrameworkElement Sep()
    {
        return new Border
        {
            Height = 1, Background = B("BorderBrush"),
            Margin = new Thickness(14, 0, 14, 0), Opacity = 0.6,
        };
    }

    /// <summary>一行队列：状态字形 + 名字 + 附注 + 状态字 + 计划。</summary>
    private FrameworkElement Row(string name, string sub, string status, string statusBrush, bool on, string plan)
    {
        var g = new Grid { Height = 44 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });

        var glyph = new TextBlock
        {
            Text = on ? "●" : "○",
            FontSize = on ? 10 : 12,
            Foreground = on ? B("BrandBrush") : B("TextTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        g.Children.Add(glyph);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(T(name, 12.5, on ? "TextPrimaryBrush" : "TextSecondaryBrush", on));
        var s = T(sub, 11, "TextTertiaryBrush");
        s.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(s);
        Grid.SetColumn(left, 1);
        g.Children.Add(left);

        var st = T(status, 11.5, statusBrush);
        st.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(st, 2);
        g.Children.Add(st);

        var p = T(plan, 11, "TextTertiaryBrush");
        p.HorizontalAlignment = HorizontalAlignment.Right;
        p.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(p, 3);
        g.Children.Add(p);

        return g;
    }

    private void Tile(Border host, string title, string value, string hint)
    {
        var sp = new StackPanel();
        sp.Children.Add(T(title, 11, "TextTertiaryBrush"));
        var v = T(value, 14, "TextPrimaryBrush", true);
        v.Margin = new Thickness(0, 3, 0, 0);
        sp.Children.Add(v);
        var h = new TextBlock
        {
            Text = hint, FontSize = 11, Foreground = B("TextTertiaryBrush"),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        sp.Children.Add(h);
        host.Child = sp;
    }
}
