using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DragonButler;

/// <summary>
/// 工具图标：从 exe 提取关联图标，缓存为 exe 旁的 <c>cache\icons\{toolId}.png</c>。
/// 只用 Win32 + WPF 成像，不引入 System.Drawing / 第三方包。
/// </summary>
public static class ToolIconService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Memory = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CacheEntry
    {
        public string Source = "";
        public DateTime SourceWrite;
        public BitmapSource? Bitmap;
    }

    /// <summary>图标缓存目录。</summary>
    public static string CacheDir => Path.Combine(AppContext.BaseDirectory, "cache", "icons");

    /// <summary>取工具图标（失败返回 null，调用方回退到首字母）。手动换过的图标优先。</summary>
    public static BitmapSource? GetBitmap(ToolProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ToolId)) return null;

        string ov = OverridePath(profile.ToolId);
        try
        {
            if (File.Exists(ov))
            {
                DateTime write = File.GetLastWriteTimeUtc(ov);
                lock (Gate)
                {
                    string key = "ov:" + profile.ToolId;
                    if (Memory.TryGetValue(key, out var hit) && hit.SourceWrite == write) return hit.Bitmap;
                    BitmapSource? bmp = Load(ov);
                    Memory[key] = new CacheEntry { Source = ov, SourceWrite = write, Bitmap = bmp };
                    return bmp;
                }
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(profile.ExecutablePath) || !File.Exists(profile.ExecutablePath)) return null;

        string source = profile.ExecutablePath;
        DateTime sourceWrite;
        try { sourceWrite = File.GetLastWriteTimeUtc(source); }
        catch { return null; }

        lock (Gate)
        {
            if (Memory.TryGetValue(profile.ToolId, out var hit)
                && string.Equals(hit.Source, source, StringComparison.OrdinalIgnoreCase)
                && hit.SourceWrite == sourceWrite)
                return hit.Bitmap;

            string? file = EnsureIconFile(profile, source, sourceWrite);
            BitmapSource? bmp = file == null ? null : Load(file);
            Memory[profile.ToolId] = new CacheEntry { Source = source, SourceWrite = sourceWrite, Bitmap = bmp };
            return bmp;
        }
    }

    /// <summary>手动图标文件（<c>cache\icons\ov_{toolId}.png</c>）。</summary>
    public static string OverridePath(string toolId)
        => Path.Combine(CacheDir, "ov_" + SafeName(toolId) + ".png");

    /// <summary>这个工具是否手动换过图标。</summary>
    public static bool HasOverride(string toolId)
    {
        try { return File.Exists(OverridePath(toolId)); } catch { return false; }
    }

    /// <summary>
    /// 手动设置图标：支持 .ico / .exe / .png / .jpg / .bmp，统一转成 PNG 存进缓存。
    /// 返回 null 表示成功，否则为给用户看的失败原因。
    /// </summary>
    public static string? SetOverride(string toolId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return "工具标识为空。";
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return "找不到这个文件，重新选一次。";

        try
        {
            BitmapSource? bmp = DecodeAny(sourcePath);
            if (bmp == null) return "这个文件里读不出图标，换一张图片或 .ico 试试。";

            Directory.CreateDirectory(CacheDir);
            string target = OverridePath(toolId);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(fs);

            lock (Gate) Memory.Remove("ov:" + toolId);
            LogService.LogEvent($"图标：{toolId} 已更换为 {Path.GetFileName(sourcePath)}");
            return null;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("设置图标失败：" + ex.Message);
            return "这个文件读不出来，换一张试试。";
        }
    }

    /// <summary>恢复默认图标（删掉手动图标，回到从 exe 提取）。</summary>
    public static void ClearOverride(string toolId)
    {
        try
        {
            string file = OverridePath(toolId);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("恢复默认图标失败：" + ex.Message);
        }
        lock (Gate) Memory.Remove("ov:" + toolId);
        Invalidate(toolId);
    }

    /// <summary>把任意图片/ico/exe 解码成 BitmapSource（优先取最大的一帧）。</summary>
    private static BitmapSource? DecodeAny(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".dll") return Extract(path);

        if (ext == ".ico")
        {
            var decoder = new IconBitmapDecoder(new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource? best = null;
            foreach (var frame in decoder.Frames)
                if (best == null || frame.PixelWidth > best.PixelWidth) best = frame;
            if (best != null) best.Freeze();
            return best;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bmp = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>清除某工具的图标缓存（改路径/删除工具时调用）。</summary>
    public static void Invalidate(string toolId)
    {
        lock (Gate)
        {
            Memory.Remove(toolId);
            try
            {
                string file = Path.Combine(CacheDir, SafeName(toolId) + ".png");
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }
    }

    private static string? EnsureIconFile(ToolProfile profile, string source, DateTime sourceWrite)
    {
        try
        {
            string file = Path.Combine(CacheDir, SafeName(profile.ToolId) + ".png");
            if (File.Exists(file))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= sourceWrite) return file;
                }
                catch { }
            }

            Directory.CreateDirectory(CacheDir);
            BitmapSource? bmp = Extract(source);
            if (bmp == null) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(fs);
            LogService.LogEvent($"图标：已从 {Path.GetFileName(source)} 提取 {profile.DisplayName} 的图标");
            return file;
        }
        catch (Exception ex)
        {
            LogService.LogEvent($"图标：提取失败（{profile.DisplayName}）{ex.Message}");
            return null;
        }
    }

    private static BitmapSource? Extract(string path)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        IntPtr fallback = IntPtr.Zero;
        try
        {
            uint count = ExtractIconEx(path, 0, large, small, 1);
            IntPtr h = count > 0 ? large[0] : IntPtr.Zero;
            if (h == IntPtr.Zero)
            {
                var info = new SHFILEINFO();
                IntPtr ok = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_ICON | SHGFI_LARGEICON);
                if (ok != IntPtr.Zero)
                {
                    h = info.hIcon;
                    fallback = h;
                }
            }
            if (h == IntPtr.Zero) return null;

            BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) try { DestroyIcon(large[0]); } catch { }
            if (small[0] != IntPtr.Zero) try { DestroyIcon(small[0]); } catch { }
            if (fallback != IntPtr.Zero) try { DestroyIcon(fallback); } catch { }
        }
    }

    private static BitmapSource? Load(string file)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 48;
            bmp.UriSource = new Uri(file, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static string SafeName(string toolId)
    {
        var chars = toolId.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) chars[i] = '_';
        }
        return new string(chars);
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
