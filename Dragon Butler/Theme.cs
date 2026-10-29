using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 主题令牌：22 个语义令牌写入 Application.Current.Resources，
/// 资源键名 = 令牌名 + "Brush"，XAML 一律 DynamicResource 引用——页面里不出现任何色值。
/// 早期为平滑迁移保留的旧键别名（TextMain / AccentOn 等）已全部迁移完毕并移除。
/// System 模式订阅 SystemEvents.UserPreferenceChanged，系统深浅色变化即时跟随（无需重启/重选）。
/// </summary>
public static class Theme
{
    /// <summary>21 个语义令牌（唯一色值来源）</summary>
    public static readonly string[] Keys =
    {
        "WindowBg", "CardBg", "CardBg2", "ControlBg", "Inset", "DialBg",
        "Border", "BorderStrong", "Hover",
        "TextPrimary", "TextSecondary", "TextTertiary",
        "Brand", "BrandPressed", "BrandOn", "BrandSoft", "BrandText", "Ecg",
        "Success", "Warning", "Danger", "DangerPressed",
    };

    /// <summary>主题切换后触发：供代码创建的控件（滚筒渐隐遮罩等）重建颜色</summary>
    public static event Action? Changed;

    /// <summary>当前主题模式：Dark / Light / System / Custom</summary>
    public static string Mode { get; private set; } = "Dark";

    // ===== 自定义主题参数（应用后保留，供编辑器回填与重启恢复）=====

    /// <summary>背景方式：Color（调色板取色）/ Image（图片）</summary>
    public static string CustomBgKind { get; private set; } = "Color";
    /// <summary>调色板颜色，ARGB 十六进制</summary>
    public static string CustomBgColor { get; private set; } = "#FF1B2430";
    /// <summary>背景图片绝对路径（Image 模式）</summary>
    public static string CustomBgImage { get; private set; } = "";
    /// <summary>遮罩强度 0-90（%）：越高压暗越多、前景越稳</summary>
    public static int CustomOverlay { get; private set; } = 55;
    /// <summary>文字配色：Auto（按背景亮度自动）/ Light（浅底深字）/ Dark（深底浅字）</summary>
    public static string CustomScheme { get; private set; } = "Auto";

    /// <summary>最近一次应用后的关键色（供编辑器实时算对比度）</summary>
    public static Color LastBgColor { get; private set; } = Color.FromRgb(0x0D, 0x0F, 0x13);
    public static Color LastCardColor { get; private set; } = Color.FromRgb(0x16, 0x19, 0x1F);
    public static Color LastTextColor { get; private set; } = Color.FromRgb(0xEE, 0xF1, 0xF4);
    /// <summary>最近一次生效的是否为浅色方案（浅底深字）</summary>
    public static bool LastSchemeLight { get; private set; }
    /// <summary>当前背景的实测相对亮度（0-1，已含遮罩）</summary>
    public static double LastBgLuminance { get; private set; }

    // 深色（默认）：近黑底 + 高对比文字；Brand 只用于主按钮/开关开启/运行中
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["WindowBg"] = "#FF0D0F13",
        ["CardBg"] = "#FF16191F",
        ["CardBg2"] = "#FF1C2027",
        ["ControlBg"] = "#FF232830",
        ["Inset"] = "#FF0B0D11",
        ["DialBg"] = "#FF0A0C10",
        ["Border"] = "#FF252A32",
        ["BorderStrong"] = "#FF333A44",
        ["Hover"] = "#FF2A303A",
        ["TextPrimary"] = "#FFEEF1F4",
        ["TextSecondary"] = "#FFA3ABB4",
        ["TextTertiary"] = "#FF8A929C",
        ["Brand"] = "#FF3DDC84",
        ["BrandPressed"] = "#FF2FB86E",
        ["BrandOn"] = "#FF0B1410",
        ["BrandSoft"] = "#FF1B302B",
        ["BrandText"] = "#FF3DDC84",
        ["Ecg"] = "#FF3DDC84",
        ["Success"] = "#FF3DDC84",
        ["Warning"] = "#FFE8B64B",
        ["Danger"] = "#FFF0616B",
        ["DangerPressed"] = "#FFC74A54",
    };

    // 浅色：白卡 + 深字；Brand 压暗保证深色字对比度
    private static readonly Dictionary<string, string> Light = new()
    {
        ["WindowBg"] = "#FFEEF1F5",
        ["CardBg"] = "#FFFFFFFF",
        ["CardBg2"] = "#FFF6F8FA",
        ["ControlBg"] = "#FFECEFF4",
        ["Inset"] = "#FFF7F9FB",
        ["DialBg"] = "#FFEDF1F5",
        ["Border"] = "#FFE1E6EB",
        ["BorderStrong"] = "#FFCFD7DF",
        ["Hover"] = "#FFE4E9EF",
        ["TextPrimary"] = "#FF12161B",
        ["TextSecondary"] = "#FF58616B",
        ["TextTertiary"] = "#FF667080",
        ["Brand"] = "#FF0F9D58",
        ["BrandPressed"] = "#FF0C7F47",
        ["BrandOn"] = "#FF08130D",
        ["BrandSoft"] = "#FFE2F3EB",
        ["BrandText"] = "#FF0C6B3D",
        ["Ecg"] = "#FF0B7A43",
        ["Success"] = "#FF0F9D58",
        ["Warning"] = "#FF8A5F12",
        ["Danger"] = "#FFC93A45",
        ["DangerPressed"] = "#FFA82F39",
    };

    private static bool _appliedLight;

    static Theme()
    {
        TryHookSystemTheme();
    }

    /// <summary>系统主题变化时，System 模式下即时跟随（回调在独立线程 → 回 UI 线程应用）</summary>
    private static void TryHookSystemTheme()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += (_, _) =>
            {
                if (Mode != "System") return;
                try
                {
                    var palette = ResolvePalette("System", out bool light);
                    if (light == _appliedLight) return; // 与当前一致，不做无谓重建
                    var app = Application.Current;
                    if (app == null) return;
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Mode != "System") return;
                        Apply(palette, light);
                    }));
                }
                catch { }
            };
        }
        catch
        {
            // SystemEvents 不可用（无消息泵等场景）→ 静默降级为仅手动切换
        }
    }

    /// <summary>设置主题：Dark / Light / System（System 读注册表 AppsUseLightTheme，=1 为浅色）</summary>
    public static void Set(string? name)
    {
        Mode = Normalize(name);
        var map = ResolvePalette(Mode, out bool light);
        Apply(map, light);
    }

    /// <summary>把调色板写入资源字典（含旧键别名），并通知托盘菜单与代码创建的控件</summary>
    private static void Apply(Dictionary<string, string> map, bool light)
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res == null) return;
            var brushes = new Dictionary<string, SolidColorBrush>(map.Count);
            foreach (var kv in map)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                brush.Freeze();
                brushes[kv.Key] = brush;
                res[kv.Key + "Brush"] = brush;
            }
            _appliedLight = light;
            if (Mode != "Custom")
            {
                res["BgArtBrush"] = Brushes.Transparent;
                res["ScrimBrush"] = Brushes.Transparent;
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("应用主题失败：" + ex.Message);
        }
        App.Instance?.RecolorMenu();
        // 通知代码创建的控件（滚筒渐隐遮罩等）跟随新主题重建颜色
        try { Changed?.Invoke(); } catch { }
    }

    private static string Normalize(string? name)
    {
        if (string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
        if (string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
        return "System";
    }

    private static Dictionary<string, string> ResolvePalette(string mode, out bool light)
    {
        light = false;
        if (mode == "Light") { light = true; return Light; }
        if (mode == "System")
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (k?.GetValue("AppsUseLightTheme") is int i && i == 1) { light = true; return Light; }
            }
            catch { }
            return Dark;
        }
        return Dark;
    }

    // ===== 自定义主题（调色板 / 图片背景）=====

    /// <summary>按设置项应用主题：Custom 走自定义通道，其余走预设三态</summary>
    public static void ApplyFromSettings()
    {
        var s = AppState.Settings;
        if (string.Equals(s.Theme, "Custom", StringComparison.OrdinalIgnoreCase))
            ApplyCustom(s.CustomBgKind, s.CustomBgColor, s.CustomBgImage, s.CustomOverlay, s.CustomScheme);
        else
            Set(s.Theme);
    }

    /// <summary>
    /// 应用自定义主题。
    ///
    /// 可读性策略（三层结构）：
    ///   1) 背景层——纯色或图片；
    ///   2) 遮罩层——黑/白半透明（强度 0-90%），把背景亮度推向某一侧；
    ///   3) 内容层——卡片与文字。卡片底色 = 背景色向方案卡片色混合（深色方案 86%、浅色方案 88%），
    ///      所以卡片亮度被锚定在方案附近，正文对比度不随背景色漂移。
    /// 方案（浅底深字 / 深底浅字）先按背景原始亮度自动判定，遮罩只会同向加强，不会翻转。
    /// </summary>
    public static void ApplyCustom(string kind, string colorHex, string imagePath, int overlay, string scheme)
    {
        kind = string.Equals(kind, "Image", StringComparison.OrdinalIgnoreCase) ? "Image" : "Color";
        overlay = Math.Clamp(overlay, 0, 90);
        scheme = NormalizeScheme(scheme);

        var baseColor = ParseHex(colorHex, Color.FromRgb(0x1B, 0x24, 0x30));
        bool hasImage = kind == "Image" && !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
        if (hasImage) baseColor = AnalyzeImage(imagePath).color;

        bool light = scheme switch
        {
            "Light" => true,
            "Dark" => false,
            _ => Luminance(baseColor) >= 0.5,
        };

        var map = new Dictionary<string, string>(light ? Light : Dark);
        byte cardAlpha = hasImage ? (byte)236 : (byte)255;   // 图片模式留 8% 透光，仍压得住文字

        Color bg, card, card2, ctrl, inset, dial, border, borderStrong, hover;
        if (light)
        {
            // 浅色方案：卡片向白靠 90%，深色正文对比度稳定在 12:1 以上
            bg = Mix(baseColor, Colors.White, 0.74);
            card = Mix(baseColor, Colors.White, 0.90);
            card2 = Mix(baseColor, Colors.White, 0.94);
            ctrl = Mix(baseColor, Colors.White, 0.84);
            inset = Mix(baseColor, Colors.White, 0.91);
            dial = Mix(baseColor, Colors.White, 0.88);
            border = Mix(card, ParseHex("#FFE1E6EB", Colors.Gray), 0.85);
            borderStrong = Mix(card, ParseHex("#FFCFD7DF", Colors.Gray), 0.90);
            hover = Mix(card, ParseHex("#FFE4E9EF", Colors.Gray), 0.90);
        }
        else
        {
            // 深色方案：卡片向 #16191F 靠 93%，即使背景是纯白，正文对比度仍在 7:1 以上
            bg = Mix(baseColor, ParseHex("#FF0D0F13", Colors.Black), 0.86);
            card = Mix(baseColor, ParseHex("#FF16191F", Colors.Black), 0.93);
            card2 = Mix(baseColor, ParseHex("#FF1C2027", Colors.Black), 0.95);
            ctrl = Mix(baseColor, ParseHex("#FF232830", Colors.Black), 0.91);
            inset = Mix(baseColor, ParseHex("#FF0B0D11", Colors.Black), 0.94);
            dial = Mix(baseColor, ParseHex("#FF0A0C10", Colors.Black), 0.93);
            border = Mix(card, ParseHex("#FF252A32", Colors.Black), 0.90);
            borderStrong = Mix(card, ParseHex("#FF333A44", Colors.Black), 0.90);
            hover = Mix(card, ParseHex("#FF2A303A", Colors.Black), 0.90);
        }

        map["WindowBg"] = Hex(bg);
        map["CardBg"] = Hex(WithAlpha(card, cardAlpha));
        map["CardBg2"] = Hex(WithAlpha(card2, cardAlpha));
        map["ControlBg"] = Hex(WithAlpha(ctrl, cardAlpha));
        map["Inset"] = Hex(WithAlpha(inset, cardAlpha));
        map["DialBg"] = Hex(WithAlpha(dial, cardAlpha));
        map["Hover"] = Hex(WithAlpha(hover, hasImage ? (byte)200 : (byte)255));
        map["Border"] = Hex(border);
        map["BorderStrong"] = Hex(borderStrong);

        CustomBgKind = kind;
        CustomBgColor = colorHex;
        CustomBgImage = imagePath ?? "";
        CustomOverlay = overlay;
        CustomScheme = scheme;
        Mode = "Custom";

        Apply(map, light);

        try
        {
            var res = Application.Current?.Resources;
            if (res != null)
            {
                res["BgArtBrush"] = hasImage ? MakeImageBrush(imagePath!) : Brushes.Transparent;
                if (hasImage)
                {
                    var scrim = light ? Colors.White : Colors.Black;
                    var sb = new SolidColorBrush(WithAlpha(scrim, (byte)(overlay * 255 / 100)));
                    sb.Freeze();
                    res["ScrimBrush"] = sb;
                }
                else
                {
                    // 纯色模式：背景色本身已按方案锚定，再叠遮罩会二次压暗
                    res["ScrimBrush"] = Brushes.Transparent;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("应用自定义背景失败：" + ex.Message);
        }

        // 记录实测值，供编辑器显示对比度与亮度
        var scrimColor = light ? Colors.White : Colors.Black;
        var effective = Mix(baseColor, scrimColor, hasImage ? overlay / 100.0 : 0);
        LastBgColor = effective;
        LastBgLuminance = Luminance(effective);
        LastCardColor = card;
        LastTextColor = ParseHex(light ? Light["TextPrimary"] : Dark["TextPrimary"], Colors.White);
        LastSchemeLight = light;
    }

    private static string NormalizeScheme(string? s)
    {
        if (string.Equals(s, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
        if (string.Equals(s, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
        return "Auto";
    }

    private static Color ParseHex(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex)) return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch { }
        return fallback;
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>线性混合：t=0 取 a，t=1 取 b</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>WCAG 2.x 相对亮度（0=黑，1=白）</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 对比度（1~21，正文建议 ≥4.5，大字 ≥3）</summary>
    public static double Contrast(Color a, Color b)
    {
        double l1 = Luminance(a), l2 = Luminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    private static readonly Dictionary<string, (Color color, double lum)> ImageCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把图片缩到 48px 宽取平均色与亮度（带缓存，拖动滑杆不会重复解码）</summary>
    private static (Color color, double lum) AnalyzeImage(string path)
    {
        if (ImageCache.TryGetValue(path, out var hit)) return hit;
        Color avg = Color.FromRgb(0x1B, 0x24, 0x30);
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 48;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();

            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            if (w > 0 && h > 0)
            {
                int stride = w * 4;
                var buf = new byte[stride * h];
                conv.CopyPixels(buf, stride, 0);
                long r = 0, g = 0, b = 0;
                for (int i = 0; i + 3 < buf.Length; i += 4)
                {
                    b += buf[i]; g += buf[i + 1]; r += buf[i + 2];
                }
                int n = w * h;
                avg = Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
        catch { }
        var val = (avg, Luminance(avg));
        ImageCache[path] = val;
        return val;
    }

    private static Brush MakeImageBrush(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            var ib = new ImageBrush(bmp)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            ib.Freeze();
            return ib;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    /// <summary>
    /// 把主题画刷转成 System.Drawing.Color，供 WinForms 托盘菜单使用；失败回退灰色
    /// </summary>
    public static System.Drawing.Color C(string key)
    {
        try
        {
            var res = Application.Current?.Resources;
            string key2 = key + "Brush";
            if (res != null && res.Contains(key2) && res[key2] is SolidColorBrush b)
                return System.Drawing.Color.FromArgb(b.Color.R, b.Color.G, b.Color.B);
        }
        catch { }
        return System.Drawing.Color.Gray;
    }
}
