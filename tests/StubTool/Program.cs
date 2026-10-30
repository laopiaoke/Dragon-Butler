using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace StubTool;

/// <summary>
/// 替身工具：模拟一个"跑一段时间后退出"的上游工具，用于驱动 RunCoordinator 的各条分支。
///
/// 用法：
///   StubTool --seconds 5 --exit 0     跑 5 秒后以退出码 0 退出（正常完成）
///   StubTool --seconds 5 --exit 3     同上，但退出码 3（失败）
///   StubTool --hang                   永不退出（用于超时 / 悬置分支）
///
/// v1.5 追加（自定义信号验收用）：
///   --title &lt;文本&gt;                 设置控制台窗口标题
///   --window &lt;标题&gt;                创建一个真实的顶层窗口（窗口标题信号按进程主窗口匹配）
///   --log &lt;文件&gt; --log-text &lt;文本&gt; --log-after &lt;秒&gt;
///                                   到达秒数后把文本追加进日志文件（日志文本信号）
///   --create-file &lt;文件&gt; --file-after &lt;秒&gt;
///                                   到达秒数后创建文件（文件出现信号）
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int seconds = 5;
        int exitCode = 0;
        bool hang = false;
        string? title = null;
        string? windowTitle = null;
        string? logPath = null;
        string logText = "";
        int logAfter = 1;
        string? createFile = null;
        int fileAfter = 1;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds":
                case "-s":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out seconds);
                    break;
                case "--exit":
                case "-e":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out exitCode);
                    break;
                case "--hang":
                    hang = true;
                    break;
                case "--title":
                    if (i + 1 < args.Length) title = args[++i];
                    break;
                case "--window":
                    if (i + 1 < args.Length) windowTitle = args[++i];
                    break;
                case "--log":
                    if (i + 1 < args.Length) logPath = args[++i];
                    break;
                case "--log-text":
                    if (i + 1 < args.Length) logText = args[++i];
                    break;
                case "--log-after":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out logAfter);
                    break;
                case "--create-file":
                    if (i + 1 < args.Length) createFile = args[++i];
                    break;
                case "--file-after":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out fileAfter);
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("StubTool --seconds N --exit C | --hang | --window T | --log P --log-text T --log-after N | --create-file P --file-after N");
                    return 0;
            }
        }

        if (!string.IsNullOrEmpty(title))
        {
            try { Console.Title = title; } catch { }
        }

        Console.WriteLine($"[StubTool] start pid={Environment.ProcessId} seconds={seconds} exit={exitCode} hang={hang}");

        StartSideEffect(logAfter, () =>
        {
            try
            {
                string? dir = Path.GetDirectoryName(logPath!);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(logPath!, logText + Environment.NewLine);
                Console.WriteLine($"[StubTool] log written: {logPath}");
            }
            catch (Exception ex) { Console.WriteLine("[StubTool] log failed: " + ex.Message); }
        }, logPath, logText);

        StartSideEffect(fileAfter, () =>
        {
            try
            {
                string? dir = Path.GetDirectoryName(createFile!);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(createFile!, "done");
                Console.WriteLine($"[StubTool] file created: {createFile}");
            }
            catch (Exception ex) { Console.WriteLine("[StubTool] file failed: " + ex.Message); }
        }, createFile, "");

        if (hang)
        {
            while (true) Thread.Sleep(1000);
        }

        // v1.5：创建一个真实顶层窗口，供"窗口标题"信号按进程主窗口匹配
        if (!string.IsNullOrEmpty(windowTitle))
        {
            IntPtr hwnd = CreateWindowExW(0, "STATIC", windowTitle, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                140, 140, 460, 140, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_SHOW);
                int tid = GetCurrentThreadId();
                var closer = new Thread(() =>
                {
                    Thread.Sleep(Math.Max(0, seconds) * 1000);
                    PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                })
                { IsBackground = true };
                closer.Start();
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                Console.WriteLine($"[StubTool] window closed, exit {exitCode}");
                return exitCode;
            }
        }

        Thread.Sleep(Math.Max(0, seconds) * 1000);
        Console.WriteLine($"[StubTool] exit {exitCode}");
        return exitCode;
    }

    private static void StartSideEffect(int afterSeconds, Action action, string? target, string payload)
    {        if (string.IsNullOrWhiteSpace(target)) return;

        var t = new Thread(() =>
        {
            Thread.Sleep(Math.Max(0, afterSeconds) * 1000);
            action();
        })
        { IsBackground = true };
        t.Start();
    }

    // ===== Win32（仅 --window 用）=====

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;
    private const int SW_SHOW = 5;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();
}
