using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DragonButler;

/// <summary>
/// 时间选择器（v0.5）：三种视图，默认「圆盘」（钱迹式 24 小时表盘）——
/// 圆盘：实心盘面框住数字，外圈 1-12（大而亮）+ 内圈 13-00（小而暗，下沉一档），
///       两圈共用 12 个角位，按拖动落点半径选圈；气泡内显示选中值、指针止于气泡边缘；
///       松开小时 → 气泡沿圆环滑到分钟位置、数字交叉淡入淡出，再进入分钟步进。
/// 滚筒：9 槽位循环复用实现**无限滚动**，松手按速度惯性滑行（fling）再吸附；
/// 键盘：大号（34px）输入框，非法值确定置灰。
/// 全部 WPF 内置能力，无第三方库；动画短促、异常静默降级。
/// </summary>
public partial class TimePickerControl : UserControl
{
    public static readonly DependencyProperty HourProperty =
        DependencyProperty.Register(nameof(Hour), typeof(int), typeof(TimePickerControl), new PropertyMetadata(8));

    public static readonly DependencyProperty MinuteProperty =
        DependencyProperty.Register(nameof(Minute), typeof(int), typeof(TimePickerControl), new PropertyMetadata(0));

    /// <summary>当前小时 0-23</summary>
    public int Hour
    {
        get => (int)GetValue(HourProperty);
        set => SetValue(HourProperty, value);
    }

    /// <summary>当前分钟 0-59</summary>
    public int Minute
    {
        get => (int)GetValue(MinuteProperty);
        set => SetValue(MinuteProperty, value);
    }

    /// <summary>确定事件</summary>
    public event Action<int, int>? Confirmed;

    /// <summary>取消事件</summary>
    public event Action? Cancelled;

    private enum PickMode { Clock, Drum, Keyboard }

    // 滚筒几何
    private const double ItemHeight = 44;
    private const double Viewport = 220;
    private const double CenterTop = (Viewport - ItemHeight) / 2;

    // 圆盘几何（300 画布）：外圈 1-12（大而亮）、内圈 13-00（小而暗，同一组 12 个角位），
    // 分钟 00-55 每 5 分一个标签，拖动仍可按 1 分精度取整
    private const double FaceSize = 300;
    private const double RadOuter = 116;    // 外圈数字半径
    private const double RadInner = 82;     // 内圈数字半径（下沉一圈）
    private const double RadMinute = 116;   // 分钟数字半径
    private const double RingSplit = 99;    // 内外圈分界半径：拖动落点大于它选外圈，否则内圈
    private const double DotOuter = 38;     // 选中气泡：外圈
    private const double DotInner = 30;     // 选中气泡：内圈
    private const double DotMinute = 36;    // 选中气泡：分钟
    private const int FaceAnimMs = 340;     // 松开后转到分钟的动画时长

    private PickMode _mode = PickMode.Clock;
    private bool _minuteStep;        // 圆盘：false=选小时，true=选分钟
    private bool _suppressMode;      // 回填图标选中态时防 Checked 递归
    private bool _suppressInput;     // 代码回填输入框时防 TextChanged 回环

    private int _hour = 8;
    private int _minute;

    private Canvas? _hourLayer;      // 小时数字层（外圈 + 内圈）
    private Canvas? _minuteLayer;    // 分钟数字层
    private readonly List<TextBlock> _outerNums = new();
    private readonly List<TextBlock> _innerNums = new();
    private readonly List<TextBlock> _minuteNums = new();
    private Ellipse? _selDot;        // 选中气泡
    private TextBlock? _selText;     // 气泡内数字
    private Line? _hand;             // 指针（画在数字层下方）
    private Ellipse? _centerDot;     // 圆心点
    private bool _faceDragging;

    // 松开后「小时 → 分钟」的过渡动画（逐帧驱动气泡沿圆环滑到目标角度）
    private DispatcherTimer? _faceAnim;
    private double _animFromAngle, _animToAngle, _animFromRadius, _animToRadius;
    private double _animFromDot, _animToDot;
    private long _animStartMs;

    private DrumColumn? _hourCol;
    private DrumColumn? _minuteCol;

    public TimePickerControl()
    {
        InitializeComponent();
        BuildClockFace();
        Theme.Changed += OnThemeChanged;
        Unloaded += (_, _) =>
        {
            Theme.Changed -= OnThemeChanged;
            StopFaceTransition();
        };
        // 页面收起（Visibility=Collapsed）时也要把过渡收干净，避免下次打开时计时器还挂着
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopFaceTransition(); };
        // 键盘模式：聚焦即全选，直接敲数字覆盖旧值
        TbHourInput.GotKeyboardFocus += (_, _) => TbHourInput.SelectAll();
        TbMinuteInput.GotKeyboardFocus += (_, _) => TbMinuteInput.SelectAll();
        SyncModeIcons();
        SetMode(PickMode.Clock);
    }

    /// <summary>用外部初始值填充控件（默认圆盘视图）</summary>
    public void InitFrom(int hour, int minute)
    {
        StopFaceTransition();   // 复位上一次可能被打断的小时→分钟过渡
        _hour = Math.Clamp(hour, 0, 23);
        _minute = Math.Clamp(minute, 0, 59);
        _minuteStep = false;
        BtnConfirm.IsEnabled = true;
        if (_mode == PickMode.Drum) EnsureDrum();
        SyncDrumFromValues();
        SyncKeyboardInputs();
        UpdatePreview();
        UpdateFace();
    }

    // ===== 视图切换（时钟 / 滚筒 / 键盘）=====

    private void ModeIcon_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressMode) return;
        var tag = (sender as FrameworkElement)?.Tag as string;
        SetMode(tag switch
        {
            "Drum" => PickMode.Drum,
            "Keyboard" => PickMode.Keyboard,
            _ => PickMode.Clock,
        });
    }

    private void SetMode(PickMode mode)
    {
        _mode = mode;
        ClockView.Visibility = mode == PickMode.Clock ? Visibility.Visible : Visibility.Collapsed;
        DrumView.Visibility = mode == PickMode.Drum ? Visibility.Visible : Visibility.Collapsed;
        KeyboardView.Visibility = mode == PickMode.Keyboard ? Visibility.Visible : Visibility.Collapsed;

        if (mode != PickMode.Clock) StopFaceTransition();
        if (mode == PickMode.Drum) { EnsureDrum(); SyncDrumFromValues(); }
        if (mode == PickMode.Keyboard)
        {
            SyncKeyboardInputs();
            // 切到键盘就把焦点放进小时框：少一次点击，且全选后可直接覆盖
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { TbHourInput.Focus(); TbHourInput.SelectAll(); } catch { }
            }), DispatcherPriority.Input);
        }
        if (mode == PickMode.Clock) _minuteStep = false;

        SyncModeIcons();
        UpdatePreview();
        UpdateFace();
    }

    private void SyncModeIcons()
    {
        _suppressMode = true;
        try
        {
            ModeClock.IsChecked = _mode == PickMode.Clock;
            ModeDrum.IsChecked = _mode == PickMode.Drum;
            ModeKeyboard.IsChecked = _mode == PickMode.Keyboard;
        }
        finally { _suppressMode = false; }
    }

    private void HourPart_Click(object sender, RoutedEventArgs e)
    {
        // 点大数字的小时段：回到圆盘的选小时步进
        if (_mode == PickMode.Clock) { _minuteStep = false; UpdatePreview(); UpdateFace(); }
        else SetMode(PickMode.Clock);
    }

    private void MinutePart_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != PickMode.Clock) SetMode(PickMode.Clock);
        _minuteStep = true;
        UpdatePreview();
        UpdateFace();
    }

    // ===== 圆盘：构建与绘制 =====

    private void BuildClockFace()
    {
        double c = FaceSize / 2;

        // 盘面：实心圆且比卡片更深——把数字全部框进一个"内嵌表盘"里
        var dial = new Ellipse { Width = FaceSize, Height = FaceSize, IsHitTestVisible = false };
        dial.SetResourceReference(Shape.FillProperty, "DialBgBrush");
        ClockView.Children.Add(dial);

        // 盘沿描边：明确"圆框住数字"
        var rim = new Ellipse { Width = FaceSize - 2, Height = FaceSize - 2, StrokeThickness = 1, IsHitTestVisible = false };
        rim.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
        Canvas.SetLeft(rim, 1);
        Canvas.SetTop(rim, 1);
        ClockView.Children.Add(rim);

        // 内外圈分界细环：内圈 13-00 下沉一档的视觉暗示
        var split = new Ellipse
        {
            Width = RingSplit * 2, Height = RingSplit * 2,
            StrokeThickness = 1, Opacity = 0.45, IsHitTestVisible = false,
        };
        split.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
        Canvas.SetLeft(split, c - RingSplit);
        Canvas.SetTop(split, c - RingSplit);
        ClockView.Children.Add(split);

        // 指针先加入 → 永远位于数字层下方；终点只到气泡边缘，不会压住任何数字
        _hand = new Line
        {
            X1 = c, Y1 = c, X2 = c, Y2 = c,
            StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        _hand.SetResourceReference(Shape.StrokeProperty, "BrandBrush");
        ClockView.Children.Add(_hand);

        _hourLayer = new Canvas { Width = FaceSize, Height = FaceSize, IsHitTestVisible = false };
        _minuteLayer = new Canvas
        {
            Width = FaceSize, Height = FaceSize,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        ClockView.Children.Add(_hourLayer);
        ClockView.Children.Add(_minuteLayer);

        // 外圈 1-12（大而亮）与内圈 13-00（小而暗）共用同一组 12 个角位，靠拖动半径区分
        for (int h = 1; h <= 12; h++)
            _outerNums.Add(AddNum(_hourLayer, h.ToString(), RadOuter, h * 30 % 360, 18, "TextPrimaryBrush", 1.0));
        _innerNums.Add(AddNum(_hourLayer, "00", RadInner, 0, 13, "TextTertiaryBrush", 0.85));
        for (int h = 13; h <= 23; h++)
            _innerNums.Add(AddNum(_hourLayer, h.ToString(), RadInner, (h % 12) * 30, 13, "TextTertiaryBrush", 0.85));
        for (int m = 0; m < 60; m += 5)
            _minuteNums.Add(AddNum(_minuteLayer, m.ToString("00"), RadMinute, m * 6, 18, "TextPrimaryBrush", 1.0));

        // 选中气泡（盖住选中数字）+ 气泡内数字
        _selDot = new Ellipse { Width = DotOuter, Height = DotOuter, IsHitTestVisible = false };
        _selDot.SetResourceReference(Shape.FillProperty, "BrandBrush");
        ClockView.Children.Add(_selDot);

        _selText = new TextBlock { Text = "08", FontSize = 16, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
        _selText.SetResourceReference(TextBlock.ForegroundProperty, "BrandOnBrush");
        ClockView.Children.Add(_selText);

        // 圆心点（钱迹式小圆点）：指针从这里出发
        _centerDot = new Ellipse { Width = 9, Height = 9, IsHitTestVisible = false };
        _centerDot.SetResourceReference(Shape.FillProperty, "BrandBrush");
        Canvas.SetLeft(_centerDot, c - 4.5);
        Canvas.SetTop(_centerDot, c - 4.5);
        ClockView.Children.Add(_centerDot);

        ClockView.MouseLeftButtonDown += Face_MouseDown;
        ClockView.MouseMove += Face_MouseMove;
        ClockView.MouseLeftButtonUp += Face_MouseUp;
        // 捕获丢失（Alt+Tab、窗口失活等）也要收尾，避免卡在拖动态
        ClockView.LostMouseCapture += (_, _) => { if (_faceDragging) EndFaceDrag(); };
    }

    /// <summary>在指定层添加一个数字（极坐标定位 + 居中）</summary>
    private TextBlock AddNum(Canvas layer, string text, double r, double angleDeg, double size, string brushKey, double opacity)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.Normal,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (x, y) = Polar(r, angleDeg);
        Canvas.SetLeft(tb, x - tb.DesiredSize.Width / 2);
        Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
        layer.Children.Add(tb);
        return tb;
    }

    /// <summary>极坐标：角度以 12 点方向为 0°，顺时针为正</summary>
    private static (double x, double y) Polar(double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double c = FaceSize / 2;
        return (c + r * Math.Sin(rad), c - r * Math.Cos(rad));
    }

    private void UpdateFace()
    {
        try
        {
            if (_selDot == null || _hand == null || _selText == null || _hourLayer == null || _minuteLayer == null) return;
            bool hourStep = !_minuteStep;

            _hourLayer.Visibility = hourStep ? Visibility.Visible : Visibility.Collapsed;
            _minuteLayer.Visibility = hourStep ? Visibility.Collapsed : Visibility.Visible;
            _hourLayer.Opacity = 1;
            _minuteLayer.Opacity = 1;

            double angle, radius, dot, textSize;
            string text = "";

            if (hourStep)
            {
                bool outer = _hour >= 1 && _hour <= 12;
                angle = _hour % 12 * 30;
                radius = outer ? RadOuter : RadInner;
                dot = outer ? DotOuter : DotInner;
                text = _hour.ToString("00"); // 08 / 12 / 16：与顶部大数字一致
                textSize = outer ? 16 : 13;
                SyncHourLabels(outer);
            }
            else
            {
                angle = _minute * 6;
                radius = RadMinute;
                dot = DotMinute;
                text = _minute.ToString("00");
                textSize = 15;
            }

            PositionBubble(angle, radius, dot);
            _selText.Text = text;
            _selText.FontSize = textSize;
            _selText.Opacity = 1;
            CenterBubbleText(angle, radius);
        }
        catch { }
    }

    /// <summary>小时步进的数字层次：外圈亮、内圈淡；选中外圈时同角位内圈数字让位，指针不压字</summary>
    private void SyncHourLabels(bool outer)
    {
        foreach (var tb in _innerNums) tb.Opacity = 0.85;
        foreach (var tb in _outerNums) tb.Opacity = 1.0;
        if (!outer) return;
        int pos = _hour % 12;
        if (pos >= 0 && pos < _innerNums.Count) _innerNums[pos].Opacity = 0;
    }

    /// <summary>摆放气泡与指针：指针止于气泡边缘，且位于数字层之下</summary>
    private void PositionBubble(double angle, double radius, double dot)
    {
        if (_selDot == null || _hand == null) return;
        var (x, y) = Polar(radius, angle);
        _selDot.Width = dot;
        _selDot.Height = dot;
        Canvas.SetLeft(_selDot, x - dot / 2);
        Canvas.SetTop(_selDot, y - dot / 2);

        var (hx, hy) = Polar(Math.Max(0, radius - dot / 2 - 1), angle);
        _hand.X2 = hx;
        _hand.Y2 = hy;
    }

    private void CenterBubbleText(double angle, double radius)
    {
        if (_selText == null) return;
        _selText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var (x, y) = Polar(radius, angle);
        Canvas.SetLeft(_selText, x - _selText.DesiredSize.Width / 2);
        Canvas.SetTop(_selText, y - _selText.DesiredSize.Height / 2);
    }

    private void Face_MouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_faceAnim != null) StopFaceTransition(); // 过渡中按下：先立即落位，再接管拖动
            _faceDragging = true;
            ClockView.CaptureMouse();
            ApplyFacePoint(e.GetPosition(ClockView));
        }
        catch { }
    }

    private void Face_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_faceDragging) return;
        ApplyFacePoint(e.GetPosition(ClockView));
    }

    private void Face_MouseUp(object sender, MouseButtonEventArgs e) => EndFaceDrag();

    /// <summary>结束圆盘拖动：从小时位置平滑转到分钟（气泡沿圆环滑动 + 数字层交叉淡入淡出）</summary>
    private void EndFaceDrag()
    {
        if (!_faceDragging) return;
        _faceDragging = false;
        try { ClockView.ReleaseMouseCapture(); } catch { }
        if (!_minuteStep) BeginMinuteTransition();
    }

    /// <summary>松开后的过渡：气泡从当前小时滑到分钟角度，小时数字淡出、分钟数字淡入</summary>
    private void BeginMinuteTransition()
    {
        try
        {
            if (_hourLayer == null || _minuteLayer == null || _selText == null)
            {
                _minuteStep = true;
                UpdatePreview();
                UpdateFace();
                return;
            }

            bool outer = _hour >= 1 && _hour <= 12;
            _animFromAngle = _hour % 12 * 30;
            _animFromRadius = outer ? RadOuter : RadInner;
            _animFromDot = outer ? DotOuter : DotInner;
            _animToAngle = _minute * 6;
            _animToRadius = RadMinute;
            _animToDot = DotMinute;
            _animStartMs = Environment.TickCount64;

            _minuteStep = true;
            _minuteLayer.Visibility = Visibility.Visible;
            _minuteLayer.Opacity = 0;
            _hourLayer.Opacity = 1;

            _hourLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _minuteLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            _selText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(90)));

            _faceAnim ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _faceAnim.Tick -= FaceAnim_Tick;
            _faceAnim.Tick += FaceAnim_Tick;
            _faceAnim.Start();
            UpdatePreview();
        }
        catch
        {
            _minuteStep = true;
            UpdatePreview();
            UpdateFace();
        }
    }

    private void FaceAnim_Tick(object? sender, EventArgs e)
    {
        try
        {
            double t = Math.Clamp((Environment.TickCount64 - _animStartMs) / (double)FaceAnimMs, 0, 1);
            double k = 1 - Math.Pow(1 - t, 3); // EaseOutCubic
            double angle = _animFromAngle + ShortestDelta(_animFromAngle, _animToAngle) * k;
            double radius = _animFromRadius + (_animToRadius - _animFromRadius) * k;
            double dot = _animFromDot + (_animToDot - _animFromDot) * k;
            PositionBubble(angle, radius, dot);
            if (t >= 1) StopFaceTransition();
        }
        catch
        {
            StopFaceTransition();
        }
    }

    /// <summary>结束过渡并复位到目标步进（可被视图切换打断）</summary>
    private void StopFaceTransition()
    {
        try { _faceAnim?.Stop(); } catch { }
        _faceAnim = null;
        try
        {
            _hourLayer?.BeginAnimation(OpacityProperty, null);
            _minuteLayer?.BeginAnimation(OpacityProperty, null);
            _selText?.BeginAnimation(OpacityProperty, null);
        }
        catch { }
        if (_hourLayer != null) _hourLayer.Opacity = 1;
        if (_minuteLayer != null) _minuteLayer.Opacity = 1;

        UpdateFace();

        try
        {
            if (_selText != null)
            {
                _selText.Opacity = 0;
                _selText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        catch { }
    }

    /// <summary>最短转向（-180°, 180°]，顺时针/逆时针取近路</summary>
    private static double ShortestDelta(double from, double to)
    {
        double d = (to - from) % 360;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    /// <summary>按落点取角度/半径 → 数值：外圈 1-12、内圈 13-00；分钟 6° 一格</summary>
    private void ApplyFacePoint(Point p)
    {
        if (_faceAnim != null) return;
        double c = FaceSize / 2;
        double dx = p.X - c, dy = p.Y - c;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r < 10) return; // 太靠圆心忽略
        double ang = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        if (ang < 0) ang += 360;

        if (!_minuteStep)
        {
            int pos = (int)Math.Round(ang / 30) % 12;  // 0 = 12 点方向
            bool outerRing = r >= RingSplit;
            int h = outerRing
                ? (pos == 0 ? 12 : pos)                // 外圈 1-12
                : (pos == 0 ? 0 : pos + 12);           // 内圈 13-00
            if (h != _hour) { _hour = h; SoundService.Play("tick.wav"); }
        }
        else
        {
            int m = (int)Math.Round(ang / 6) % 60;
            if (m != _minute) { _minute = m; SoundService.Play("tick.wav"); }
        }
        UpdatePreview();
        UpdateFace();
    }

    // ===== 预览（大数字 + 提示）=====

    private void UpdatePreview()
    {
        TbHourBig.Text = _hour.ToString("00");
        TbMinuteBig.Text = _minute.ToString("00");

        bool clock = _mode == PickMode.Clock;
        TbHourBig.SetResourceReference(TextBlock.ForegroundProperty,
            clock && !_minuteStep ? "BrandBrush" : "TextPrimaryBrush");
        TbMinuteBig.SetResourceReference(TextBlock.ForegroundProperty,
            clock && _minuteStep ? "BrandBrush" : "TextPrimaryBrush");

        TbHint.Text = _mode switch
        {
            PickMode.Clock => _minuteStep ? "拖动选择分钟，精确到 1 分钟" : "按住拖动选小时，松开自动转到分钟",
            PickMode.Drum => "拖动滚筒可无限滚动，松手会按惯性滑行",
            _ => "直接输入 0-23 与 0-59",
        };
    }

    // ===== 滚筒：9 槽位循环复用（无限滚动）+ 惯性 =====

    private void EnsureDrum()
    {
        if (_hourCol != null && _minuteCol != null) return;
        _hourCol = BuildColumn(24, _hour, true);
        _minuteCol = BuildColumn(60, _minute, false);
        HourSlot.Children.Add(_hourCol.Host);
        MinuteSlot.Children.Add(_minuteCol.Host);
    }

    private DrumColumn BuildColumn(int count, int value, bool isHour)
    {
        var host = new Grid
        {
            Width = 90,
            Height = Viewport,
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var canvas = new Canvas { Width = 90, Height = Viewport };
        var slots = new List<TextBlock>(9);
        for (int i = 0; i < 9; i++)
        {
            var tb = new TextBlock
            {
                FontSize = 13,
                Width = 90,
                Height = ItemHeight,
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Canvas.SetLeft(tb, 0);
            canvas.Children.Add(tb);
            slots.Add(tb);
        }
        host.Children.Add(canvas);

        var lineTop = new Border { Height = 1, Margin = new Thickness(0, CenterTop, 0, 0), VerticalAlignment = VerticalAlignment.Top, Opacity = 0.35 };
        var lineBottom = new Border { Height = 1, Margin = new Thickness(0, CenterTop + ItemHeight, 0, 0), VerticalAlignment = VerticalAlignment.Top, Opacity = 0.35 };
        lineTop.SetResourceReference(Border.BackgroundProperty, "BrandBrush");
        lineBottom.SetResourceReference(Border.BackgroundProperty, "BrandBrush");
        host.Children.Add(lineTop);
        host.Children.Add(lineBottom);

        var fadeTop = new Rectangle { Height = 44, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        var fadeBottom = new Rectangle { Height = 44, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        ApplyFadeBrush(fadeTop, top: true);
        ApplyFadeBrush(fadeBottom, top: false);
        host.Children.Add(fadeTop);
        host.Children.Add(fadeBottom);

        host.MouseLeftButtonDown += Column_MouseLeftButtonDown;
        host.MouseMove += Column_MouseMove;
        host.MouseLeftButtonUp += Column_MouseLeftButtonUp;
        host.MouseWheel += Column_MouseWheel;

        var col = new DrumColumn
        {
            Host = host,
            Canvas = canvas,
            Slots = slots,
            FadeTop = fadeTop,
            FadeBottom = fadeBottom,
            Count = count,
            IsHour = isHour,
            Value = value,
        };
        host.Tag = col;
        col.Shift = -ItemHeight * value;
        col.Paint();
        return col;
    }

    /// <summary>渐隐遮罩画刷：从卡片底色生成竖向渐变（top=上不透明下透明）</summary>
    private static void ApplyFadeBrush(Rectangle rect, bool top)
    {
        var wb = (ResourceBrush("CardBgBrush") ?? ResourceBrush("WindowBgBrush")) as SolidColorBrush;
        if (wb == null) { rect.Fill = Brushes.Transparent; return; }
        var c = wb.Color;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (top)
        {
            brush.GradientStops.Add(new GradientStop(c, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
            brush.GradientStops.Add(new GradientStop(c, 1));
        }
        rect.Fill = brush;
    }

    private static Brush? ResourceBrush(string key)
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res != null && res.Contains(key) && res[key] is Brush b) return b;
        }
        catch { }
        return null;
    }

    /// <summary>主题切换：渐隐遮罩重建（其余元素用动态资源自动跟随）</summary>
    private void OnThemeChanged()
    {
        try
        {
            if (_hourCol != null) { ApplyFadeBrush(_hourCol.FadeTop, true); ApplyFadeBrush(_hourCol.FadeBottom, false); }
            if (_minuteCol != null) { ApplyFadeBrush(_minuteCol.FadeTop, true); ApplyFadeBrush(_minuteCol.FadeBottom, false); }
        }
        catch { }
    }

    private void SyncDrumFromValues()
    {
        try
        {
            if (_hourCol != null && _hourCol.Value != _hour) { _hourCol.Value = _hour; _hourCol.Shift = -ItemHeight * _hour; }
            if (_minuteCol != null && _minuteCol.Value != _minute) { _minuteCol.Value = _minute; _minuteCol.Shift = -ItemHeight * _minute; }
        }
        catch { }
    }

    private void OnDrumValue(DrumColumn col)
    {
        if (col.IsHour) _hour = col.Value; else _minute = col.Value;
        UpdatePreview();
    }

    private void StopFling(DrumColumn col)
    {
        try { col.Fling?.Stop(); } catch { }
        col.Fling = null;
    }

    private void Column_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid host || host.Tag is not DrumColumn col) return;
        try
        {
            StopFling(col);
            double cur = col.Shift;
            col.BeginAnimation(DrumColumn.ShiftProperty, null);
            col.Shift = cur;
            col.Dragging = true;
            col.StartY = e.GetPosition(host).Y;
            col.DownY = col.StartY;
            col.StartShift = cur;
            col.Moved = 0;
            col.Velocity = 0;
            col.LastMoveY = col.StartY;
            col.LastMoveTicks = Environment.TickCount64;
            col.DownTicks = col.LastMoveTicks;
            host.CaptureMouse();
        }
        catch { }
    }

    private void Column_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Grid host || host.Tag is not DrumColumn col) return;
        if (!col.Dragging) return;
        try
        {
            double y = e.GetPosition(host).Y;
            col.Moved = Math.Max(col.Moved, Math.Abs(y - col.DownY));
            col.Shift = col.StartShift + (y - col.StartY);

            long now = Environment.TickCount64;
            long dt = now - col.LastMoveTicks;
            if (dt >= 8)
            {
                double v = (y - col.LastMoveY) / dt;          // px/ms
                col.Velocity = col.Velocity * 0.55 + v * 0.45; // 平滑，避免抖动
                col.LastMoveY = y;
                col.LastMoveTicks = now;
            }

            int nv = col.NearestValue;
            if (nv != col.Value) { col.Value = nv; SoundService.Play("tick.wav"); OnDrumValue(col); }
        }
        catch { }
    }

    private void Column_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid host || host.Tag is not DrumColumn col) return;
        if (!col.Dragging) return;
        col.Dragging = false;
        try { host.ReleaseMouseCapture(); } catch { }
        try
        {
            if (col.Moved < 6)
            {
                // 点选：把点击所在行滑到中心
                double y = e.GetPosition(host).Y;
                double vf = col.ValueFloat;
                int rp = (int)Math.Round(vf);
                double sub = vf - rp;
                int k = (int)Math.Round((y - CenterTop) / ItemHeight + sub);
                double target = -ItemHeight * (rp + k);
                AnimateShift(col, target, 200, () =>
                {
                    col.Value = DrumColumn.Mod(rp + k, col.Count);
                    OnDrumValue(col);
                });
                return;
            }
            if (Math.Abs(col.Velocity) > 0.06) StartFling(col);
            else SnapColumn(col);
        }
        catch { }
    }

    private void Column_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Grid host || host.Tag is not DrumColumn col) return;
        e.Handled = true; // 防止外层 ScrollViewer 跟着滚
        try
        {
            StopFling(col);
            double target = Math.Round(col.ValueFloat) + (e.Delta > 0 ? -1 : 1);
            AnimateShift(col, -ItemHeight * target, 180, () =>
            {
                col.Value = DrumColumn.Mod((int)target, col.Count);
                OnDrumValue(col);
            }, from: col.Shift);
        }
        catch { }
    }

    /// <summary>把平移量动画到目标像素（显式起止值，避免 NaN 起点）</summary>
    private static void AnimateShift(DrumColumn col, double target, int ms, Action? done = null, double? from = null)
    {
        try
        {
            double cur = from ?? col.Shift;
            col.BeginAnimation(DrumColumn.ShiftProperty, null);
            col.Shift = cur;
            var anim = new DoubleAnimation(cur, target, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) =>
            {
                try
                {
                    col.BeginAnimation(DrumColumn.ShiftProperty, null);
                    col.Shift = target;
                    done?.Invoke();
                }
                catch { }
            };
            col.BeginAnimation(DrumColumn.ShiftProperty, anim);
        }
        catch
        {
            col.Shift = target;
            done?.Invoke();
        }
    }

    /// <summary>松手吸附到最近刻度（无限循环，最近整数即可）</summary>
    private void SnapColumn(DrumColumn col)
    {
        double target = -ItemHeight * Math.Round(col.ValueFloat);
        AnimateShift(col, target, 220, () =>
        {
            col.Value = col.NearestValue;
            OnDrumValue(col);
        });
    }

    /// <summary>惯性滑行：16ms 定时器按速度推进并衰减，速度足够小后吸附</summary>
    private void StartFling(DrumColumn col)
    {
        StopFling(col);
        double v = Math.Clamp(col.Velocity, -3.5, 3.5);
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            try
            {
                col.Shift += v * 16;
                v *= 0.92;
                int nv = col.NearestValue;
                if (nv != col.Value) { col.Value = nv; SoundService.Play("tick.wav"); OnDrumValue(col); }
                if (Math.Abs(v) < 0.02)
                {
                    timer.Stop();
                    col.Fling = null;
                    SnapColumn(col);
                }
            }
            catch
            {
                try { timer.Stop(); } catch { }
                col.Fling = null;
            }
        };
        col.Fling = timer;
        timer.Start();
    }

    // ===== 紧凑键盘模式 =====

    private void SyncKeyboardInputs()
    {
        _suppressInput = true;
        try
        {
            TbHourInput.Text = _hour.ToString("00");
            TbMinuteInput.Text = _minute.ToString("00");
        }
        finally { _suppressInput = false; }
        ValidateCompact();
    }

    /// <summary>紧凑键盘校验：时 0-23 / 分 0-59；合法则写回当前值，非法"确定"置灰</summary>
    private bool ValidateCompact()
    {
        bool okH = int.TryParse(TbHourInput.Text, out int h) && h >= 0 && h <= 23;
        bool okM = int.TryParse(TbMinuteInput.Text, out int m) && m >= 0 && m <= 59;
        if (okH && okM)
        {
            _hour = h;
            _minute = m;
            UpdatePreview();
            UpdateFace();
        }
        BtnConfirm.IsEnabled = okH && okM;
        return okH && okM;
    }

    private void HourInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressInput) return;
        ValidateCompact();
    }

    private void MinuteInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressInput) return;
        ValidateCompact();
    }

    /// <summary>失焦归一化：非法/空 → 回退当前有效值</summary>
    private void CompactInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ValidateCompact()) return;
        _suppressInput = true;
        if (ReferenceEquals(sender, TbHourInput)) TbHourInput.Text = _hour.ToString("00");
        else TbMinuteInput.Text = _minute.ToString("00");
        _suppressInput = false;
        ValidateCompact();
    }

    // ===== 确定 / 取消 =====

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == PickMode.Keyboard && !ValidateCompact()) return;
        Hour = _hour;
        Minute = _minute;
        Confirmed?.Invoke(Hour, Minute);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke();
    }

    // ===== 滚筒列状态 =====

    /// <summary>
    /// 一列滚筒。Shift 为像素平移量（负值向下滚值变大），由动画/拖动驱动；
    /// 9 个槽位按「取模」循环复用 → 视觉上无限滚动，越界自动归一化。
    /// </summary>
    private sealed class DrumColumn : Animatable
    {
        public static readonly DependencyProperty ShiftProperty = DependencyProperty.Register(
            nameof(Shift), typeof(double), typeof(DrumColumn),
            new PropertyMetadata(0.0, static (d, _) => { try { ((DrumColumn)d).Paint(); } catch { } }));

        public double Shift
        {
            get => (double)GetValue(ShiftProperty);
            set => SetValue(ShiftProperty, value);
        }

        protected override Freezable CreateInstanceCore() => new DrumColumn();

        public Grid Host = null!;
        public Canvas Canvas = null!;
        public List<TextBlock> Slots = null!;
        public Rectangle FadeTop = null!;
        public Rectangle FadeBottom = null!;
        public int Count;
        public bool IsHour;
        public int Value;

        // 拖动 / 惯性状态
        public bool Dragging;
        public double StartY;
        public double DownY;
        public double StartShift;
        public double Moved;
        public double Velocity;
        public double LastMoveY;
        public long LastMoveTicks;
        public long DownTicks;
        public DispatcherTimer? Fling;

        /// <summary>连续虚拟值（可越界；取模后即真实值）</summary>
        public double ValueFloat => -Shift / ItemHeight;

        public int NearestValue => Mod((int)Math.Round(ValueFloat), Count);

        public static int Mod(int v, int n) => ((v % n) + n) % n;

        /// <summary>重排 9 个槽位：文本取模循环、位置按平移量、阶梯字号透明度按偏离中心距离</summary>
        public void Paint()
        {
            double vf = ValueFloat;
            int rp = (int)Math.Round(vf);
            double sub = vf - rp;
            for (int j = 0; j < Slots.Count; j++)
            {
                var tb = Slots[j];
                int off = j - 4;
                string text = Mod(rp + off, Count).ToString("00");
                if (tb.Text != text) tb.Text = text;
                Canvas.SetTop(tb, CenterTop + ItemHeight * off - ItemHeight * sub);

                double d = Math.Abs(off - sub);
                int tier = d <= 0.5 ? 0 : d <= 1.5 ? 1 : d <= 2.5 ? 2 : 3;
                if (tb.Tag is int last && last == tier) continue;
                tb.Tag = tier;
                switch (tier)
                {
                    case 0: tb.FontSize = 30; tb.FontWeight = FontWeights.Bold; tb.Opacity = 1.0; tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); break;
                    case 1: tb.FontSize = 22; tb.FontWeight = FontWeights.Normal; tb.Opacity = 0.55; tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); break;
                    case 2: tb.FontSize = 16; tb.FontWeight = FontWeights.Normal; tb.Opacity = 0.30; tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); break;
                    default: tb.FontSize = 13; tb.FontWeight = FontWeights.Normal; tb.Opacity = 0.12; tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); break;
                }
            }
        }
    }
}
