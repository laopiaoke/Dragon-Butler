using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using DragonButler;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。
internal static partial class Program
{
    private static ToolProfile Stub(string name, string path, string args, int exit = 0, int retry = 0,
                                    CompletionSignalKind primary = CompletionSignalKind.ProcessExit0,
                                    int maxMinutes = 5, int grace = 0)
        => new()
        {
            ToolId = name,
            DisplayName = name,
            ExecutablePath = path,
            LaunchArguments = args,
            ManagedProcessName = "StubTool",
            GameProcessNames = Array.Empty<string>(),
            PrimarySignal = primary,
            SecondarySignals = Array.Empty<CompletionSignalKind>(),
            LogCompletionPattern = primary == CompletionSignalKind.LogPattern ? "一条龙和配置组任务结束" : null,
            MaxDurationMinutes = maxMinutes,
            PostExitGraceMinutes = grace,
            RetryCount = retry,
        };

    private static int Run(string name, string stub, CompletionSignalKind primary, string stubArgs,
                           int maxMinutes, int graceMinutes)
    {
        var profile = new ToolProfile
        {
            ToolId = "stub",
            DisplayName = "StubTool",
            ExecutablePath = stub,
            LaunchArguments = stubArgs,
            ManagedProcessName = "StubTool",
            GameProcessNames = Array.Empty<string>(),
            PrimarySignal = primary,
            SecondarySignals = Array.Empty<CompletionSignalKind>(),
            LogCompletionPattern = primary == CompletionSignalKind.LogPattern ? "执行结束" : null,
            MaxDurationMinutes = maxMinutes,
            PostExitGraceMinutes = graceMinutes,
        };

        using var coord = ToolRunner.CreateCoordinator(profile, msg => Console.WriteLine("NOTIFY " + msg));
        coord.Transition += (from, to, reason) => Console.WriteLine($"TRANS {from}->{to} {reason}");

        coord.BeginLaunch();
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = stub,
                Arguments = stubArgs,
                WorkingDirectory = Path.GetDirectoryName(stub) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            coord.LaunchFailed(ex.Message);
            Console.WriteLine("RESULT " + name + " state=" + coord.State);
            return 1;
        }

        if (proc == null)
        {
            coord.LaunchFailed("Process.Start 返回 null");
            Console.WriteLine("RESULT " + name + " state=" + coord.State);
            return 1;
        }

        var ctx = new ToolRunContext { Profile = profile, Process = proc, Arguments = stubArgs };
        coord.LaunchSucceeded(proc, ctx);

        if (primary == CompletionSignalKind.LogPattern)
            FeedLogPattern(stub);

        var sw = Stopwatch.StartNew();
        while (!coord.IsFinished && sw.Elapsed < TimeSpan.FromMinutes(maxMinutes + graceMinutes + 2))
            Thread.Sleep(500);

        Console.WriteLine($"RESULT {name} state={coord.State} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        try { if (!proc.HasExited) proc.Kill(); } catch { }
        return 0;
    }

    /// <summary>halted 分支：在主信号源监视的日志里写一行完成标记，触发 Running→Resolving。</summary>
    private static void FeedLogPattern(string stub)
    {
        try
        {
            string logDir = Path.Combine(Path.GetDirectoryName(stub) ?? "", "log");
            Directory.CreateDirectory(logDir);
            string file = Path.Combine(logDir, "better-genshin-impact-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            Thread.Sleep(1500);
            File.AppendAllText(file, $"配置组 \"Stub\" 执行结束{Environment.NewLine}", Encoding.UTF8);
            Console.WriteLine("FEED log-pattern at " + Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            Console.WriteLine("FEED-FAIL " + ex.Message);
        }
    }

    /// <summary>在 WPF Dispatcher 上延时执行。</summary>
    private static void After(int ms, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    /// <summary>轮询替身进程是否退出（不阻塞 Dispatcher，保证 OLE 剪贴板消息能处理）。</summary>
    private static void WaitHolderExit(Process? proc, Action onExited)
    {
        if (proc == null) { onExited(); return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            bool exited;
            try { exited = proc.HasExited; } catch { exited = true; }
            if (!exited) return;
            timer.Stop();
            onExited();
        };
        timer.Start();
    }

    private static string Compact(string text)
        => string.Join(" | ", text.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && (l.Contains("SYSTEM") || l.Contains("DISPLAY") || l.Contains("EXECUTION") || l.Contains("Dragon")))
            .Take(8));

    private static string PowerRequests()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/requests")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    private static bool GetScreensaver(out bool active)
    {
        active = false;
        try
        {
            object? raw = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaveActive", null);
            if (raw != null)
            {
                active = raw.ToString()?.Trim() == "1";
                return true;
            }
        }
        catch { }
        int v = 0;
        bool ok = SystemParametersInfo(0x0010, 0, ref v, 0);
        active = v != 0;
        return ok;
    }

    private static bool SetScreensaver(bool active)
    {
        int v = active ? 1 : 0;
        bool ok = SystemParametersInfo(0x0011, 0, ref v, 0);
        try { Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaveActive", active ? "1" : "0"); }
        catch { }
        return ok;
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint flags);

    private static int GdiCount()
    {
        try { return (int)GetGuiResources(Process.GetCurrentProcess().Handle, 0); }
        catch { return -1; }
    }

    private static void SaveArchive(Dictionary<string, List<GroupRecord>> data)
    {
        try
        {
            string path = GroupArchive.FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var opt = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, opt));
        }
        catch (Exception ex) { Console.WriteLine("ARCHIVE-WRITE-FAIL " + ex.Message); }
    }

    private static void WritePng(string path, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;       // B
            pixels[i + 1] = 0x90;   // G
            pixels[i + 2] = 0xE0;   // R
            pixels[i + 3] = 0xFF;   // A
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        enc.Save(fs);
    }

    internal static int CountLog(string needle)
    {
        try
        {
            string file = Path.Combine(LogService.Dir, DateTime.Now.ToString("yyyy-MM-dd") + "_运行.log");
            if (!File.Exists(file)) return 0;
            return File.ReadAllLines(file).Count(l => l.Contains(needle, StringComparison.Ordinal));
        }
        catch { return 0; }
    }

    internal static bool LogHas(string needle) => CountLog(needle) > 0;

    /// <summary>数一个子串在文本里出现几次。源码级断言天天要用，别每处手写一遍。</summary>
    internal static int CountOf(string text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return 0;
        int n = 0, i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { n++; i += token.Length; }
        return n;
    }

    /// <summary>
    /// v1.45.3 / #15：把一组同前缀的源文件拼成一段文本。
    /// 拆分之后原来「读一个文件」的断言要改成「读这一组」，判据本身不动。
    /// </summary>
    internal static string ReadRepoSourcesGroup(string relativeDir, string pattern)
    {
        string root = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? ".";
        for (int i = 0; i < 5; i++) root = Path.GetDirectoryName(root) ?? root;
        string dir = Path.Combine(root, relativeDir);
        if (!Directory.Exists(dir)) return "";
        var text = new System.Text.StringBuilder();
        foreach (string file in Directory.GetFiles(dir, pattern))
        {
            text.AppendLine(File.ReadAllText(file));
        }
        return text.ToString();
    }
    /// <summary>
    /// v1.42.1 / M9-4：仓库根下的某个文件还在不在。
    /// 删掉的文件不能再用 ReadRepoSources 去读——File.ReadAllText 会直接抛。
    /// </summary>
    internal static bool RepoFileExists(string relative)
    {
        string root = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? ".";
        for (int i = 0; i < 5; i++) root = Path.GetDirectoryName(root) ?? root;
        return File.Exists(Path.Combine(root, relative));
    }

    /// <summary>读仓库里的一个源文件（从程序集位置上溯 5 级到仓库根）。</summary>
    internal static string ReadRepoSources(string relative)
    {
        string root = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? ".";
        for (int i = 0; i < 5; i++) root = Path.GetDirectoryName(root) ?? root;
        return File.ReadAllText(Path.Combine(root, relative));
    }

    /// <summary>
    /// 从文本里切出一段：从 from 开始，到其后第一个 to 结束（含）。
    /// 用来把「某个样式里到底写了什么」和全文分开——全文里同名容器不止一个，整段搜必误判。
    /// </summary>
    internal static string SliceBetween(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return "";
        int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        if (end < 0) return text.Substring(start);
        return text.Substring(start, end - start + to.Length);
    }

    /// <summary>数某个 x:Name 之后、下一个公开元素之前的 RowDefinition 个数（粗略但够用）。</summary>
    internal static int CountRowsBefore(string text, string anchor, int window)
    {
        int at = text.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0) return -1;
        int end = Math.Min(text.Length, at + window);
        string slice = text.Substring(at, end - at);
        int nextName = slice.IndexOf("x:Name=\"", 8, StringComparison.Ordinal);
        if (nextName > 0) slice = slice.Substring(0, nextName);
        return System.Text.RegularExpressions.Regex.Matches(slice, "<RowDefinition").Count;
    }

    /// <summary>拼一条最简 JSON-RPC 请求（id 固定 1）。</summary>
    private static string Request(string method) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\"}";

    /// <summary>拼一条 tools/call 请求。<paramref name="toolId"/> 为 null 时不带 arguments。</summary>
    private static string Call(string name, string? toolId)
    {
        string args = toolId == null
            ? "{}"
            : "{\"toolId\":\"" + toolId + "\"}";
        return "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
               + name + "\",\"arguments\":" + args + "}}";
    }

    private static int Help()
    {
        Console.WriteLine("CoordinatorProbe <done|failed|timeout|halted|haltedlog|logpattern|args|migrate|clipboard|clipdialog> [stubPath]");
        return 0;
    }

}
