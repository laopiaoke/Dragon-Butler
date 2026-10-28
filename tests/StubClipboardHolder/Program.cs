using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace StubClipboardHolder;

/// <summary>
/// 剪贴板占用替身：创建一个隐藏窗口并对其调用 OpenClipboard，保持指定秒数后释放。
/// 用来复现"复制地址偶发失败（CLIPBRD_E_CANT_OPEN）"的环境竞争。
///
/// 注意：必须传窗口句柄。OpenClipboard(IntPtr.Zero) 只把剪贴板关联到"当前任务"，
/// 在 Windows 10/11 上并不会阻止其他进程打开剪贴板，无法复现占用场景。
///
/// 用法：
///   StubClipboardHolder.exe                 → 占用 5 秒
///   StubClipboardHolder.exe --seconds 8     → 占用 8 秒
///   StubClipboardHolder.exe --seconds 3 --quiet
///
/// 只在开发机使用，不随产品发布。
/// </summary>
internal static class Program
{
    private const int HWND_MESSAGE = -3;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private static int Main(string[] args)
    {
        int seconds = 5;
        bool quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds" when i + 1 < args.Length:
                    if (int.TryParse(args[i + 1], out int s) && s > 0) seconds = s;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
            }
        }

        // 用一个消息窗口做剪贴板所有者（隐藏、0 尺寸）
        // WS_POPUP + 不 ShowWindow：普通隐藏窗口（message-only 窗口在部分系统上不参与剪贴板互斥）
        IntPtr hwnd = CreateWindowExW(0, "STATIC", "StubClipboardHolder", 0x80000000u, 0, 0, 0, 0,
                                      IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            if (!quiet) Console.WriteLine("HOLDER window=False err=" + Marshal.GetLastWin32Error());
            return 2;
        }

        // 剪贴板可能正被别的程序占用：最多尝试 5 秒抢占用权
        bool opened = false;
        for (int i = 0; i < 50 && !opened; i++)
        {
            opened = OpenClipboard(hwnd);
            if (!opened) Thread.Sleep(100);
        }

        if (!opened)
        {
            if (!quiet) Console.WriteLine("HOLDER open=False err=" + Marshal.GetLastWin32Error());
            DestroyWindow(hwnd);
            return 1;
        }

        if (!quiet) Console.WriteLine($"HOLDER open=True seconds={seconds} hwnd=0x{hwnd.ToInt64():X} openWnd=0x{GetOpenClipboardWindow().ToInt64():X} owner=0x{GetClipboardOwner().ToInt64():X}");
        Thread.Sleep(seconds * 1000);
        CloseClipboard();
        if (!quiet) Console.WriteLine("HOLDER released");
        DestroyWindow(hwnd);
        return 0;
    }
}
