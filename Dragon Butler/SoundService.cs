using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DragonButler;

/// <summary>
/// 按键音效（SPBGI）：不引用任何音频资源，全部用纯 C# 合成 16bit 单声道 22050Hz PCM WAV。
/// v0.11 起：
///  - 音色仍是 iOS 风格"哒"，但**长度从 30ms 提到 60~85ms、能量不再集中在 5ms 内**：
///    旧版 click 的包络 τ≈4.3ms，实际只有"一声咔"，系统音量 20% 时人耳直接听成没有声音；
///  - 播放改走 winmm PlaySound（返回 bool），失败会记一次日志，不再"静默失败"；
///  - 文件带版本号，升级后自动重建（避免继续沿用旧的超短音）。
///  - 统一由 <see cref="FindPressTarget"/> 判定"这次按下该不该响、响哪一种"。
/// </summary>
public static class SoundService
{
    private const int SampleRate = 22050;
    private static readonly string SoundDir = Path.Combine(AppContext.BaseDirectory, "sounds");

    /// <summary>音效文件版本：改音色时 +1，启动时发现版本不符会全部重建</summary>
    private const string SoundVersion = "5";

    /// <summary>tick 音效节流：两次播放间隔小于该值（毫秒）时跳过，防止快速滚动时机关枪音</summary>
    private const int TickThrottleMs = 40;
    private static long _lastTickMs = -1;

    /// <summary>播放失败次数（用于日志限流，避免刷屏）</summary>
    public static int PlayFailures { get; private set; }

    /// <summary>确保音效文件存在且为当前版本</summary>
    public static void EnsureSounds()
    {
        try
        {
            Directory.CreateDirectory(SoundDir);
            string verPath = Path.Combine(SoundDir, "sounds.version");
            bool outdated;
            try
            {
                outdated = !File.Exists(verPath) ||
                           !string.Equals(File.ReadAllText(verPath).Trim(), SoundVersion, StringComparison.Ordinal);
            }
            catch { outdated = true; }

            if (outdated)
            {
                // 旧版本音色直接清掉，避免"改了合成参数但用户听到的还是旧声"
                foreach (var f in Directory.GetFiles(SoundDir, "*.wav"))
                {
                    try { File.Delete(f); } catch { }
                }
            }

            WriteIfMissing("click.wav", ClickTone(1480, 88, 0.90, 40, noise: 0.85));
            WriteIfMissing("toggle.wav", ClickTone(940, 104, 0.84, 30, noise: 0.60));
            WriteIfMissing("confirm.wav", Concat(
                ClickTone(1480, 44, 0.58, 90, noise: 0.5),
                Silence(12),
                Tone(1046, 80, 22, 0.44),
                Silence(8),
                Tone(1568, 104, 18, 0.40)));
            WriteIfMissing("cancel.wav", Concat(
                ClickTone(1280, 44, 0.58, 90, noise: 0.5),
                Silence(12),
                Tone(880, 80, 22, 0.42),
                Silence(8),
                Tone(587, 104, 18, 0.38)));
            WriteIfMissing("alert.wav", Concat(
                Tone(1000, 96, 14, 0.52), Silence(40),
                Tone(1000, 96, 14, 0.52), Silence(40),
                Tone(1000, 128, 12, 0.48)));
            // 滚筒刻度音：比其他音更短更轻，但仍要有可听的"嗒"
            WriteIfMissing("tick.wav", ClickTone(2100, 62, 0.68, 55, noise: 0.45));

            try { File.WriteAllText(verPath, SoundVersion); } catch { }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("生成音效文件失败：" + ex.Message);
        }
    }

    private static void WriteIfMissing(string name, short[] samples)
    {
        string path = Path.Combine(SoundDir, name);
        if (File.Exists(path)) return;
        File.WriteAllBytes(path, BuildWav(samples));
    }

    /// <summary>
    /// 播放音效（异步不阻塞 UI）；SoundEnabled=false、文件缺失或任何异常 → 跳过。
    /// tick 额外 40ms 节流：快速拖动/滚动时跳过间隔过近的播放。
    /// </summary>
    public static void Play(string name)
    {
        try
        {
            if (!AppState.Settings.SoundEnabled) return;

            // 兼容旧调用名
            if (string.Equals(name, "tap.wav", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "tap", StringComparison.OrdinalIgnoreCase))
                name = "click.wav";
            if (!name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) name += ".wav";

            if (string.Equals(name, "tick.wav", StringComparison.OrdinalIgnoreCase))
            {
                long now = Environment.TickCount64;
                if (_lastTickMs >= 0 && now - _lastTickMs < TickThrottleMs) return;
                _lastTickMs = now;
            }

            string path = Path.Combine(SoundDir, name);
            if (!File.Exists(path))
            {
                // 第一次发现缺失就补生成一轮（用户手动删过 sounds 目录也能自愈）
                EnsureSounds();
                if (!File.Exists(path)) return;
            }

            bool ok = PlaySound(path, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault);
            if (!ok)
            {
                PlayFailures++;
                if (PlayFailures <= 3) LogService.LogEvent($"音效播放失败：{name}（winmm PlaySound 返回 false）");
            }
        }
        catch
        {
            // 音效失败绝不影响功能
        }
    }

    // ===== 统一"按下即响"的判定 =====

    /// <summary>
    /// 从命中的元素向上找"这次按下应该发声的控件"：
    /// ButtonBase（按钮/开关/勾选/分段）优先；其次是文本输入框、滚动条滑块（轻 tick）；
    /// 最后是指针手型的可点区域（守护条整条、路径小字）。
    /// 返回 null 表示不发声（空白区、时间选择器的表盘/滚筒——那里由控件自己出 tick）。
    /// </summary>
    public static FrameworkElement? FindPressTarget(DependencyObject? src)
    {
        if (src == null) return null;
        bool insidePicker = HasAncestor<TimePickerControl>(src);

        for (var d = src; d != null; d = ParentOf(d))
        {
            if (d is FrameworkElement fe && string.Equals(fe.Tag as string, "nosound", StringComparison.Ordinal))
                return null;
            if (d is ButtonBase b) return b;
            if (d is TextBoxBase or Thumb) return (FrameworkElement)d;
            if (!insidePicker && d is FrameworkElement h && h.Cursor == Cursors.Hand) return h;
            if (d is Window) break;
        }
        return null;
    }

    private static bool HasAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        for (var cur = d; cur != null; cur = ParentOf(cur))
        {
            if (cur is T) return true;
            if (cur is Window) break;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d)
    {
        try
        {
            if (d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D)
                return System.Windows.Media.VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }
        catch { return null; }
    }

    /// <summary>开关类（ToggleButton / 非 CheckBox、非 RadioButton）用 toggle 音，其余用 click 音</summary>
    public static bool IsSwitchLike(object? target)
        => target is ToggleButton and not CheckBox and not RadioButton;

    /// <summary>按下这一下该出哪种音（一次按下只出一种，避免叠音）</summary>
    public enum PressSound
    {
        None,
        Click,
        Toggle,
        Confirm,
        Cancel,
        Tick
    }

    /// <summary>
    /// 把"命中的控件"翻译成音色：确定类给上行双音、取消类给下行双音、
    /// 输入框/滚动条给更轻的 tick，其余按钮 click、开关 toggle、手型区域 click。
    /// 语义音只认语义明确的名字/文案，避免把普通按钮误判成确认或取消。
    /// </summary>
    public static PressSound PressSoundOf(FrameworkElement? target)
    {
        if (target == null) return PressSound.None;
        if (target is ButtonBase b)
        {
            if (IsConfirmLike(b)) return PressSound.Confirm;
            if (IsCancelLike(b)) return PressSound.Cancel;
            return IsSwitchLike(b) ? PressSound.Toggle : PressSound.Click;
        }
        if (target is TextBoxBase or Thumb) return PressSound.Tick;
        return PressSound.Click;
    }

    private static readonly string[] ConfirmWords = { "确定", "确认", "保存", "应用", "完成" };
    private static readonly string[] CancelWords = { "取消", "放弃", "关闭" };

    private static bool IsConfirmLike(ButtonBase b)
        => HasNameHint(b, "confirm") || ContentIn(b, ConfirmWords);

    private static bool IsCancelLike(ButtonBase b)
        => HasNameHint(b, "cancel") || ContentIn(b, CancelWords);

    private static bool HasNameHint(FrameworkElement fe, string hint)
        => fe.Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool ContentIn(ButtonBase b, string[] words)
    {
        // 只取纯文案做精确比较：图标按钮的 Content 往往是 Path/StackPanel，不能按字符串切
        string? text = (b.Content as string)?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var w in words)
            if (string.Equals(text, w, StringComparison.Ordinal)) return true;
        return false;
    }

    // ===== 播放后端（winmm）=====

    private const int SndAsync = 0x0001;
    private const int SndNoDefault = 0x0002;
    private const int SndFilename = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, int fdwSound);

    // ===== 波形合成 =====

    /// <summary>
    /// iOS 风格按键音：噪声瞬态（接触感）+ 衰减正弦（音高明确的"哒"）。
    /// 关键是**包络**：2ms 起音避免爆音，之后按 τ≈1/decay 衰减；
    /// click 用 decay=62（τ≈16ms、总长 62ms），在 20% 系统音量下也能听清，
    /// 而旧版 decay=230（τ≈4ms）实际只有 5ms 能量，等于没响。
    /// </summary>
    private static short[] ClickTone(double freq, int ms, double amp, double decay, double noise = 0.7)
    {
        int n = Math.Max(1, (int)(SampleRate * ms / 1000.0));
        var a = new short[n];
        var rnd = new Random(20261027);
        double lp = 0, prevLp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            // 噪声瞬态：3ms 内衰减，制造"咔"的接触感（不做高通，保留一点厚度）
            double nEnv = Math.Exp(-t * 420);
            double white = rnd.NextDouble() * 2 - 1;
            lp = lp * 0.55 + white * 0.45;
            double hp = (lp - prevLp) * 0.6 + lp * 0.4;
            prevLp = lp;

            // 主体：基频 + 八度泛音 + 半频厚度，指数衰减
            double env = Attack(t, 0.0015) * Math.Exp(-t * decay);
            double body = Math.Sin(2 * Math.PI * freq * t)
                        + 0.26 * Math.Sin(2 * Math.PI * freq * 2.02 * t)
                        + 0.14 * Math.Sin(2 * Math.PI * freq * 0.5 * t);

            double v = body * env * 0.72 + hp * nEnv * noise * 0.5;
            a[i] = (short)Math.Clamp(v * amp * short.MaxValue, short.MinValue, short.MaxValue);
        }
        FadeOut(a, 6);
        return a;
    }

    /// <summary>衰减正弦（用于确认/取消的柔和尾音）</summary>
    private static short[] Tone(double freq, int ms, double decayK, double amp)
    {
        int n = Math.Max(1, (int)(SampleRate * ms / 1000.0));
        var a = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            double env = Attack(t, 0.004) * Math.Exp(-t * decayK);
            a[i] = (short)Math.Clamp(Math.Sin(2 * Math.PI * freq * t) * amp * env * short.MaxValue,
                short.MinValue, short.MaxValue);
        }
        FadeOut(a, 8);
        return a;
    }

    /// <summary>线性起音，避免波形从 0 突跳产生的爆音</summary>
    private static double Attack(double t, double seconds)
        => seconds <= 0 ? 1 : Math.Min(1, t / seconds);

    /// <summary>淡出尾部，避免爆音</summary>
    private static void FadeOut(short[] a, int ms)
    {
        int n = Math.Min(a.Length, Math.Max(1, (int)(SampleRate * ms / 1000.0)));
        for (int i = 0; i < n; i++)
        {
            int idx = a.Length - n + i;
            double k = 1.0 - i / (double)n;
            a[idx] = (short)(a[idx] * k);
        }
    }

    /// <summary>静音段</summary>
    private static short[] Silence(int ms)
    {
        int n = Math.Max(1, (int)(SampleRate * ms / 1000.0));
        return new short[n];
    }

    /// <summary>拼接多段波形</summary>
    private static short[] Concat(params short[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var a = new short[total];
        int pos = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, a, pos, p.Length);
            pos += p.Length;
        }
        return a;
    }

    /// <summary>组装 16bit 单声道 22050Hz WAV 字节流</summary>
    private static byte[] BuildWav(short[] samples)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataBytes = samples.Length * 2;
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                 // fmt 块大小
        bw.Write((short)1);           // PCM
        bw.Write((short)1);           // 单声道
        bw.Write(SampleRate);         // 采样率
        bw.Write(SampleRate * 2);     // 字节率
        bw.Write((short)2);           // 块对齐
        bw.Write((short)16);          // 位深
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        foreach (var s in samples) bw.Write(s);
        bw.Flush();
        return ms.ToArray();
    }
}
