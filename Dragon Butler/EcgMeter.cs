using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DragonButler;

/// <summary>心电图状态（业务侧通过 SetEcgState 驱动，五态可互转）</summary>
public enum EcgState
{
    /// <summary>空闲：灰色平直基线，明确表示"未处于一条龙状态"</summary>
    Idle,
    /// <summary>启动中：灰→绿，波形逐渐长出搏动（"电极刚接上"）</summary>
    Starting,
    /// <summary>运行中：绿色 P-QRS-T 波群，节律稳定</summary>
    Running,
    /// <summary>异常：红色警报，频率与振幅增大 + 轻微闪烁</summary>
    Error,
    /// <summary>结束中：平滑收敛回灰色平线</summary>
    Ending,
}

/// <summary>
/// 标准心电监护仪风格波形（Independent 组件，只操作传入 Canvas 内的若干元素）。
///
/// v0.11 重做：
/// 1) **连续滚动**：波形是"时间轴上的信号"，每个像素 x 取 (now - (w-x)/pxPerSec) 时刻的采样，
///    因此整条曲线持续向左流动、永不停顿——旧版相位只与 x 有关，画面是静的，只有扫描头在动。
/// 2) **监护仪观感**：底纹网格 + 粗而淡的辉光 + 细亮主线 + 右端写头亮点 + 左旧右新的亮度渐变；
///    走纸速度固定 128px/s（≈25mm/s），心率 74bpm ≈ 一个心动周期 104px，一屏可见十余次搏动。
/// 3) 波形用高斯核合成 P-QRS-T（R 尖峰 ~14px，P/T 圆钝），叠加低频基线漂移与轻微呼吸性心率变异。
/// 4) 状态切换全部参数缓动（振幅/心率/线色/辉光强度/闪烁），收敛到 Idle 后停掉渲染循环（0 帧开销）。
/// </summary>
public sealed class EcgMeter
{
    // ===== 波形形态（单位：秒，相对 R 峰）=====
    private const double POffset = -0.155, PSigma = 0.022, PAmp = 0.16;
    private const double QOffset = -0.028, QSigma = 0.008, QAmp = -0.12;
    private const double SOffset = 0.026, SSigma = 0.010, SAmp = -0.26;
    private const double TOffset = 0.265, TSigMa = 0.045, TAmp = 0.30;
    private const double RSigma = 0.010, RAmp = 1.00;

    private const double PxPerSecond = 128;   // 走纸速度（≈25mm/s @ 5.1px/mm）
    private const double SampleStep = 1.3;    // 波形采样步进（px）
    private const double HeadRadius = 2.6;

    private readonly Canvas _host;
    private readonly Rectangle _grid;
    private readonly Polyline _glow;          // 辉光（粗、淡）
    private readonly Polyline _live;          // 主线
    private readonly Ellipse _headHalo;
    private readonly Ellipse _head;
    private readonly PointCollection _pts = new();

    private readonly SolidColorBrush _liveBrush;
    private readonly SolidColorBrush _gridBrush;

    private double _w, _h;
    private double _time;                     // 累计时间（漂移/闪烁用）
    private double _beatPhase;                // 心动周期相位（0..1，按心率累加）

    // 当前（缓动中）参数
    private double _amp;
    private double _bpm = 74;
    private double _glowAlpha = 0.18;
    private double _jitter;
    private Color _color = Color.FromRgb(0x8A, 0x92, 0x9C);

    // 目标参数
    private double _tAmp, _tBpm = 74, _tGlowAlpha = 0.18, _tJitter;
    private Color _tColor = Color.FromRgb(0x8A, 0x92, 0x9C);

    private bool _hooked;
    private long _lastMs = -1;

    public EcgState State { get; private set; } = EcgState.Idle;

    /// <summary>状态变化通知（供业务记日志/联动）</summary>
    public event Action<EcgState>? StateChanged;

    public EcgMeter(Canvas host)
    {
        _host = host;
        _color = _tColor = ThemeColor("TextTertiary", Color.FromRgb(0x8A, 0x92, 0x9C));

        _liveBrush = new SolidColorBrush(_color);
        _gridBrush = new SolidColorBrush(Color.FromArgb(30, _color.R, _color.G, _color.B));

        // 底纹网格：8×7 一格（相当于心电纸小格），极淡，只提供"监护仪"的质感
        var gridPen = new Pen(_gridBrush, 1);
        var gridDrawing = new DrawingGroup();
        gridDrawing.Children.Add(new GeometryDrawing(null, gridPen, new RectangleGeometry(new Rect(0, 0, 8, 7))));
        _grid = new Rectangle
        {
            Fill = new DrawingBrush(gridDrawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 8, 7),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
            },
            IsHitTestVisible = false,
        };

        _glow = new Polyline
        {
            Stroke = _liveBrush,
            StrokeThickness = 5.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.16,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _live = new Polyline
        {
            Stroke = _liveBrush,
            StrokeThickness = 1.85,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            // 左侧（旧数据）略暗、右侧（刚画出的数据）最亮 → 滚动方向一目了然
            OpacityMask = new LinearGradientBrush(
                Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                new Point(0, 0.5), new Point(1, 0.5)),
        };

        _headHalo = new Ellipse
        {
            Width = HeadRadius * 4.2,
            Height = HeadRadius * 4.2,
            Fill = _liveBrush,
            Opacity = 0.20,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _head = new Ellipse
        {
            Width = HeadRadius * 2,
            Height = HeadRadius * 2,
            Fill = _liveBrush,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        // 两个 Polyline 共享同一份点集：一次计算，两种粗细
        _glow.Points = _pts;
        _live.Points = _pts;

        _host.Children.Add(_grid);
        _host.Children.Add(_glow);
        _host.Children.Add(_live);
        _host.Children.Add(_headHalo);
        _host.Children.Add(_head);

        _host.SizeChanged += Host_SizeChanged;
        _host.IsVisibleChanged += Host_IsVisibleChanged;

        ApplyProfile(ProfileOf(EcgState.Idle), immediate: true);
        Render();
    }

    // ===== 对外接口 =====

    /// <summary>切换状态（业务侧唯一入口）。所有状态可互转；切换通过参数缓动完成，不会瞬跳。</summary>
    public void SetState(EcgState state)
    {
        if (state == State) { EnsureHooked(); return; }
        State = state;
        ApplyProfile(ProfileOf(state), immediate: false);
        EnsureHooked();
        try { StateChanged?.Invoke(state); } catch { }
    }

    /// <summary>调试：按 Idle → Starting → Running → Error → Ending → Idle 循环</summary>
    public EcgState CycleState()
    {
        var next = State switch
        {
            EcgState.Idle => EcgState.Starting,
            EcgState.Starting => EcgState.Running,
            EcgState.Running => EcgState.Error,
            EcgState.Error => EcgState.Ending,
            _ => EcgState.Idle,
        };
        SetState(next);
        return next;
    }

    /// <summary>主题变化后重新取色（保持当前状态，从当前色缓动到新主题色）</summary>
    public void RefreshThemeColors()
    {
        var p = ProfileOf(State);
        _tColor = p.Color;
        if (State == EcgState.Idle) _color = p.Color; // 静态平线立即换色
        EnsureHooked();
        ApplyGridColor();
    }

    // ===== 状态参数表 =====

    private readonly struct Profile
    {
        public readonly double Amp, Bpm, Glow, Jitter;
        public readonly Color Color;
        public Profile(double amp, double bpm, double glow, double jitter, Color color)
        { Amp = amp; Bpm = bpm; Glow = glow; Jitter = jitter; Color = color; }
    }

    private Profile ProfileOf(EcgState state) => state switch
    {
        // 待机：低心率 + 低振幅灰线——仍持续轻微起伏（监护仪"接着电极但没在跑"的样子），
        // 而不是一条死平线；看着像"活着但空闲"
        EcgState.Idle => new Profile(0.34, 56, 0.12, 0, ThemeColor("TextTertiary", Color.FromRgb(0x8A, 0x92, 0x9C))),
        // 慢心率 + 振幅渐入：看起来像"搏动逐渐出现"
        EcgState.Starting => new Profile(1.0, 68, 0.20, 0, ThemeColor("Ecg", Color.FromRgb(0x3D, 0xDC, 0x84))),
        EcgState.Running => new Profile(1.0, 74, 0.26, 0, ThemeColor("Ecg", Color.FromRgb(0x3D, 0xDC, 0x84))),
        // 心率 118、振幅 1.35、轻微闪烁 → 紧迫感
        EcgState.Error => new Profile(1.35, 118, 0.34, 0.20, ThemeColor("Danger", Color.FromRgb(0xF0, 0x61, 0x6B))),
        EcgState.Ending => new Profile(0.34, 52, 0.12, 0, ThemeColor("TextTertiary", Color.FromRgb(0x8A, 0x92, 0x9C))),
        _ => new Profile(0, 0, 0.10, 0, ThemeColor("TextTertiary", Color.FromRgb(0x8A, 0x92, 0x9C))),
    };

    private static Color ThemeColor(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources[key + "Brush"] is SolidColorBrush b) return b.Color;
        }
        catch { }
        return fallback;
    }

    private void ApplyProfile(Profile p, bool immediate)
    {
        _tAmp = p.Amp;
        _tBpm = p.Bpm;
        _tGlowAlpha = p.Glow;
        _tJitter = p.Jitter;
        _tColor = p.Color;
        if (immediate)
        {
            _amp = p.Amp;
            _bpm = p.Bpm;
            _glowAlpha = p.Glow;
            _jitter = p.Jitter;
            _color = p.Color;
        }
    }

    // ===== 渲染循环 =====

    private void EnsureHooked()
    {
        if (_hooked) return;
        try
        {
            System.Windows.Media.CompositionTarget.Rendering += OnRendering;
            _hooked = true;
            _lastMs = -1;
        }
        catch { }
    }

    private void Unhook()
    {
        if (!_hooked) return;
        try
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
        catch { }
    }

    private void OnRendering(object? sender, EventArgs e) => Tick(Environment.TickCount64);

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _w = _host.ActualWidth;
        _h = _host.ActualHeight;
        EnsureHooked();
        Render();
    }

    private void Host_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_host.IsVisible) EnsureHooked();
        else Unhook(); // 窗口最小化/切走时停掉渲染，省 CPU
    }

    /// <summary>推进一帧：参数缓动 → 心跳相位前进 → 重绘</summary>
    public void Tick(long nowMs)
    {
        if (_w < 24 || _h < 6)
        {
            _w = _host.ActualWidth;
            _h = _host.ActualHeight;
            if (_w < 24 || _h < 6) return;
        }

        double dt = _lastMs < 0 ? 1.0 / 60 : Math.Min(0.05, Math.Max(0.001, (nowMs - _lastMs) / 1000.0));
        _lastMs = nowMs;
        _time += dt;

        // 参数缓动（时间常数越小越快）
        _amp += (_tAmp - _amp) * Ease(dt, 0.34);
        _bpm += (_tBpm - _bpm) * Ease(dt, 0.45);
        _glowAlpha += (_tGlowAlpha - _glowAlpha) * Ease(dt, 0.50);
        _jitter += (_tJitter - _jitter) * Ease(dt, 0.22);
        _color = Lerp(_color, _tColor, Ease(dt, 0.40));

        // 心跳相位按心率前进（静止态为 0，不平移）
        if (_bpm > 0.5) _beatPhase = (_beatPhase + dt * _bpm / 60.0) % 1.0;

        Render();

        // 已完全收敛到 Idle：停掉渲染循环，只留一条静态灰平线
        bool settled = _tAmp < 0.05 && _amp < 0.05 && _tJitter < 0.005 && _jitter < 0.01;
        if (settled && _bpm < 1)
        {
            _amp = 0;
            RenderStatic();
            Unhook();
        }
    }

    private static double Ease(double dt, double tau) => 1 - Math.Exp(-dt / tau);

    private static Color Lerp(Color a, Color b, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * k),
            (byte)Math.Round(a.G + (b.G - a.G) * k),
            (byte)Math.Round(a.B + (b.B - a.B) * k));
    }

    private void ApplyGridColor()
    {
        byte alpha = (byte)(_amp > 0.02 ? 34 : 24);
        _gridBrush.Color = Color.FromArgb(alpha, _color.R, _color.G, _color.B);
    }

    private void Render()
    {
        try
        {
            _liveBrush.Color = _color;
            ApplyGridColor();
            SizeGrid();

            if (_amp < 0.02 || _w < 24 || _h < 12)
            {
                RenderStatic();
                return;
            }

            BuildWave();
            _glow.Visibility = Visibility.Visible;
            _live.Visibility = Visibility.Visible;
            _head.Visibility = Visibility.Visible;
            _headHalo.Visibility = Visibility.Visible;
            _glow.Opacity = Math.Clamp(_glowAlpha, 0.08, 0.55);

            // 异常态轻微闪烁（限制下限，避免波形消失）
            double flicker = 1 - _jitter * (0.45 + 0.45 * Math.Sin(_time * Math.PI * 2 * 3.1));
            _live.Opacity = Math.Clamp(flicker, 0.62, 1);
            _head.Opacity = _live.Opacity;
            _headHalo.Opacity = 0.20 * _live.Opacity;

            // 写头固定在右端（最新数据处）
            double y = SampleY(_w - 2);
            Canvas.SetLeft(_head, _w - 2 - HeadRadius);
            Canvas.SetTop(_head, y - HeadRadius);
            Canvas.SetLeft(_headHalo, _w - 2 - HeadRadius * 2.1);
            Canvas.SetTop(_headHalo, y - HeadRadius * 2.1);
        }
        catch { }
    }

    /// <summary>静态渲染：只有一条基线（Idle/Ending 收敛后）</summary>
    private void RenderStatic()
    {
        try
        {
            _liveBrush.Color = _color;
            ApplyGridColor();
            SizeGrid();
            _pts.Clear();
            double y = BaselineY();
            _pts.Add(new Point(0, y));
            _pts.Add(new Point(Math.Max(1, _w), y));
            _glow.Visibility = Visibility.Collapsed;
            _live.Visibility = Visibility.Collapsed;
            _head.Visibility = Visibility.Collapsed;
            _headHalo.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    /// <summary>Canvas 的子元素不会被拉伸：网格是 Rectangle，必须显式给尺寸，否则 0×0 根本不画</summary>
    private void SizeGrid()
    {
        if (double.IsNaN(_grid.Width) || double.IsNaN(_grid.Height) ||
            Math.Abs(_grid.Width - _w) > 0.5 || Math.Abs(_grid.Height - _h) > 0.5)
        {
            _grid.Width = Math.Max(1, _w);
            _grid.Height = Math.Max(1, _h);
        }
    }

    /// <summary>基线（略偏下，给 R 峰留出上方空间）+ 极缓的基线漂移</summary>
    private double BaselineY() => _h * 0.66 + _h * 0.012 * Math.Sin(_time * 0.7);

    /// <summary>逐像素采样：x 处取 (now - (w-x)/pxPerSec) 时刻的信号 → 整条曲线持续向左流动</summary>
    private void BuildWave()
    {
        _pts.Clear();
        int n = Math.Max(8, (int)Math.Ceiling(_w / SampleStep) + 1);
        for (int i = 0; i < n; i++)
        {
            double x = Math.Min(_w, i * SampleStep);
            _pts.Add(new Point(x, SampleY(x)));
        }
    }

    /// <summary>单个像素的心电值（P-QRS-T + 基线漂移 + 呼吸性心率变异）</summary>
    private double SampleY(double x)
    {
        double bpm = Math.Max(1, _bpm);
        double period = 60.0 / bpm;

        // 该像素对应的心动相位：右端 = 当前，往左依次是更早的时刻
        double delaySec = (_w - x) / PxPerSecond;
        double phase = _beatPhase - delaySec / period;

        // 呼吸性变异：让相邻 R-R 间期略有长短（幅度 ±2.5%）
        phase *= 1 + 0.025 * Math.Sin(_time * 1.55 + x * 0.0009);

        double u = phase - Math.Floor(phase);          // 0..1
        double v = Beat(u, period);

        double gain = _h * 0.50 * _amp;
        double baseline = BaselineY();
        double drift = _h * 0.010 * Math.Sin(x / Math.Max(1, _w) * Math.PI * 2 * 1.3 + _time * 0.6);

        // 轻微本底噪声：让线看起来像"活的"，不是几何曲线
        double noise = 0.22 * Math.Sin(x * 2.7 + _time * 41.0) * Math.Min(1, _amp) * (_h / 28.0);

        double y = baseline - v * gain + drift + noise;
        return Math.Clamp(y, 1.2, Math.Max(2.5, _h - 1.2));
    }

    /// <summary>一个心动周期内的形态（u=0 为 R 峰），全部用高斯核叠加</summary>
    private double Beat(double u, double period)
    {
        double t = u * period;
        double v = RAmp * Kernel(t, 0, RSigma, period);
        v += QAmp * Kernel(t, QOffset, QSigma, period);
        v += SAmp * Kernel(t, SOffset, SSigma, period);
        v += PAmp * Kernel(t, POffset, PSigma, period);
        v += TAmp * Kernel(t, TOffset, TSigMa, period);
        return v;
    }

    /// <summary>周期信号上的高斯核（自动取环绕到 [-T/2, T/2] 的最近距离）</summary>
    private static double Kernel(double offsetSec, double mu, double sigma, double period)
    {
        double d = offsetSec - mu;
        if (d > period * 0.5) d -= period;
        else if (d < -period * 0.5) d += period;
        double z = d / sigma;
        if (z > 4 || z < -4) return 0;   // 远处直接 0，省掉大量 exp()
        return Math.Exp(-0.5 * z * z);
    }
}
