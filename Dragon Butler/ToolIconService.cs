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

    /// <summary>取工具图标（失败返回 null，调用方回退到首字母）。</summary>
    public static BitmapSource? GetBitmap(ToolProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ToolId)) return null;
        if (string.IsNullOrWhiteSpace(profile.ExecutablePath) || !File.Exists(profile.ExecutablePath)) return null;

        string source = profile.ExecutablePath;
        DateTime write;
        try { write = File.GetLastWriteTimeUtc(source); }
        catch { return null; }

        lock (Gate)
        {
            if (Memory.TryGetValue(profile.ToolId, out var hit)
                && string.Equals(hit.Source, source, StringComparison.OrdinalIgnoreCase)
                && hit.SourceWrite == write)
                return hit.Bitmap;

            string? file = EnsureIconFile(profile, source, write);
            BitmapSource? bmp = file == null ? null : Load(file);
            Memory[profile.ToolId] = new CacheEntry { Source = source, SourceWrite = write, Bitmap = bmp };
            return bmp;
        }
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
