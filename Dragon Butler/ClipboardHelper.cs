using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace DragonButler;

/// <summary>
/// 剪贴板写入助手。
///
/// Windows 剪贴板是独占资源：剪贴板管理器、输入法、截图工具等会瞬时占用，
/// 此时写入会失败并返回 CLIPBRD_E_CANT_OPEN（0x800401D0），属环境竞争而非逻辑错误。
///
/// 统一策略（全库唯一入口，不要再直接调用 Clipboard.*）：
/// 1) 同步重试 8 次 × 125ms，最长阻塞约 1 秒（禁止异步化，避免 UI 线程外的 COM 单元问题）；
/// 2) 仍失败 → 由调用方调用 <see cref="ShowManualCopyFallback"/> 走"手动复制"兜底；
/// 3) 技术细节（HRESULT、Win32 错误码、异常类型）只写日志，UI 文案不含任何错误码。
///
/// 实现说明：这里直接调用 Win32 剪贴板 API，而不是 <see cref="Clipboard.SetText(string)"/>。
/// 原因是 .NET 8 的 WPF Clipboard 把"10 次 × 100ms"的内部重试写死在实现里（公开 API 无法调整），
/// 单次调用在剪贴板被占用时就要阻塞约 1 秒，外层再重试 8 次会拖到 8 秒以上；
/// 直接调用 OpenClipboard/SetClipboardData 才能精确做到"8 次 × 125ms、总阻塞 ≤1 秒"。
/// 写入格式为 CF_UNICODETEXT（与 Clipboard.SetText 等价，记事本/浏览器/终端均可粘贴）。
/// </summary>
public static class ClipboardHelper
{
    /// <summary>CLIPBRD_E_CANT_OPEN：剪贴板正被其他进程占用（仅用于日志，不面向用户）。</summary>
    private const int ClipbrdCantOpen = unchecked((int)0x800401D0);

    /// <summary>重试次数（含首次尝试）。</summary>
    private const int MaxAttempts = 8;

    /// <summary>两次尝试之间的等待（毫秒）。7 次等待合计 875ms，保证同步阻塞 ≤1 秒。</summary>
    private const int RetryDelayMs = 125;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>
    /// 尝试把文本写入剪贴板。
    /// 返回 true = 成功；false = 被其他程序持续占用（已重试 8 次）或发生其他异常。
    /// 必须在 STA 线程（UI 线程）调用。
    /// </summary>
    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (TrySetTextOnce(text))
            {
                if (attempt > 1)
                    LogService.LogEvent($"复制成功：第 {attempt} 次尝试写入（前 {attempt - 1} 次剪贴板被占用）");
                return true;
            }

            if (attempt == MaxAttempts)
            {
                LogService.LogEvent($"复制地址失败：重试 {MaxAttempts} 次仍被占用（0x{ClipbrdCantOpen:X8}）");
                return false;
            }
            Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    /// <summary>单次写入：打开剪贴板 → 清空 → 放入 CF_UNICODETEXT。失败只返回 false，不抛异常。</summary>
    private static bool TrySetTextOnce(string text)
    {
        IntPtr hMem = IntPtr.Zero;
        bool locked = false;
        bool opened = false;

        try
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened) return false;

            if (!EmptyClipboard()) return false;

            int bytes = (text.Length + 1) * 2; // UTF-16 + 结尾 '\0'
            hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) return false;

            IntPtr dst = GlobalLock(hMem);
            if (dst == IntPtr.Zero) return false;
            locked = true;

            Marshal.Copy(text.ToCharArray(), 0, dst, text.Length);
            Marshal.WriteInt16(dst, text.Length * 2, 0); // 结尾补 '\0'

            GlobalUnlock(hMem);
            locked = false;

            // SetClipboardData 成功后内存所有权归系统，不能再 GlobalFree
            if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
            hMem = IntPtr.Zero;
            return true;
        }
        catch (Exception ex)
        {
            // 非占用类异常（内存不足、封送失败等）：记录类型与消息供排查
            LogService.LogEvent("复制地址失败：" + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        finally
        {
            if (locked) { try { GlobalUnlock(hMem); } catch { } }
            if (hMem != IntPtr.Zero) { try { GlobalFree(hMem); } catch { } }
            if (opened) { try { CloseClipboard(); } catch { } }
        }
    }

    /// <summary>
    /// 兜底：弹出只读、可全选的文本框显示完整内容，让用户手动 Ctrl+C。
    /// 面向用户的文案只说"下一步做什么"，不出现 OpenClipboard / 0x800401D0 等字样。
    /// </summary>
    /// <param name="text">需要用户手动复制的内容（完整 URL 或路径）。</param>
    /// <param name="owner">父窗口，可为空（空则居中于屏幕）。</param>
    /// <param name="title">弹窗标题。</param>
    public static void ShowManualCopyFallback(string text, Window? owner = null, string title = "复制地址")
    {
        try
        {
            AppDialog.Show(
                message: "剪贴板被其他程序暂时占用，请手动复制（Ctrl+C）。",
                detail: "下面的内容已全选，直接按 Ctrl+C 即可；关掉本窗口不影响其他功能。",
                kind: AppDialog.Kind.Warning,
                title: title,
                primary: "知道了",
                owner: owner,
                copyText: text);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("手动复制兜底弹窗失败：" + ex.Message);
        }
    }
}
