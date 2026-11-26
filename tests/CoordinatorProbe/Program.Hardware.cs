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
    /// <summary>防打扰：有人在场 → 推迟；改成空闲后放行。</summary>
    private static int RunIdle()
    {
        AppState.Settings.IdleGuardEnabled = true;
        IdleGuard.PostponeDelay = TimeSpan.FromSeconds(1);
        IdleGuard.IdleOverride = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        var task = IdleGuard.WaitAsync(CancellationToken.None);
        Thread.Sleep(1500);
        IdleGuard.IdleOverride = TimeSpan.FromMinutes(10);
        bool ok = task.GetAwaiter().GetResult();
        sw.Stop();
        Console.WriteLine($"RESULT idle ok={ok} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        return ok ? 0 : 1;
    }

    /// <summary>自动寻找：运行中的进程应命中。</summary>
    private static int RunDiscover(string stub)
    {
        string dir = Path.Combine(Path.GetTempPath(), "db_find");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "March7 Launcher.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, true);
        var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "/c ping -n 12 127.0.0.1 >nul", UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(1000);

        var profile = ProfileRegistry.Get("march7th");
        var found = ToolDiscovery.TryAutoDiscover(profile, out string source);
        Console.WriteLine($"RESULT discover found={found} source={source}");
        try { if (p is { HasExited: false }) p.Kill(); } catch { }
        return string.Equals(found, exe, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    /// <summary>自定义工具：档案持久化 + 图标提取 + 队列执行。</summary>
    private static int RunCustom(string stub)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        var profile = new ToolProfile
        {
            ToolId = "custom-stub",
            DisplayName = "Stub 自定义",
            ExecutablePath = stub,
            LaunchArguments = "--seconds 1 --exit 0",
            ManagedProcessName = "StubTool",
            PrimarySignal = CompletionSignalKind.ProcessExit0,
            MaxDurationMinutes = 5,
            RetryCount = 0,
            CustomData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["custom"] = "1" },
        };
        ProfileRegistry.Save(profile);
        string file = Path.Combine(ProfileRegistry.ToolsDir, "custom-stub.json");
        var bmp = ToolIconService.GetBitmap(profile);
        string icon = Path.Combine(ToolIconService.CacheDir, "custom-stub.png");
        Console.WriteLine($"CUSTOM file={File.Exists(file)} icon={bmp != null} iconFile={File.Exists(icon)} size={(bmp == null ? "0" : bmp.PixelWidth + "x" + bmp.PixelHeight)}");

        var loaded = ProfileRegistry.Get("custom-stub");
        Console.WriteLine($"CUSTOM loaded={loaded.DisplayName}/{loaded.ExecutablePath}");
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
        var r = QueueService.LastResults.FirstOrDefault();
        Console.WriteLine($"RESULT custom state={r?.State}");
        ProfileRegistry.Remove("custom-stub");
        return r?.State == ToolRunState.Done ? 0 : 1;
    }

    /// <summary>到达时长：主信号 Duration → Done，并结束进程。</summary>
    private static int RunDuration(string stub)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        DurationSignalSource.DurationOverride = TimeSpan.FromSeconds(2);
        var profile = Stub("stub-duration", stub, "--hang", 5);
        profile.PrimarySignal = CompletionSignalKind.Duration;
        profile.CustomData["durationMinutes"] = "60";
        var sw = Stopwatch.StartNew();
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
        sw.Stop();
        var r = QueueService.LastResults.FirstOrDefault();
        int left = Process.GetProcessesByName("StubTool").Length;
        Console.WriteLine($"RESULT duration state={r?.State} elapsed={sw.Elapsed.TotalSeconds:F1}s left={left}");
        return r?.State == ToolRunState.Done && left == 0 ? 0 : 1;
    }

    /// <summary>STALLED：--hang 无 CPU/日志活动 → Stalled；autoKill 时杀进程并按失败策略继续。</summary>
    private static int RunStall(string stub, bool autoKill)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        AppState.Settings.StallAutoKill = autoKill;
        AppState.Settings.FailStrategy = "Skip";
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        SentinelService.StallThreshold = TimeSpan.FromSeconds(3);
        SentinelService.Start();

        var hang = Stub("stub-stall", stub, "--hang", 5);
        var next = Stub("stub-next", stub, "--seconds 1 --exit 0");
        var list = autoKill ? new[] { hang, next } : new[] { hang };

        var sw = Stopwatch.StartNew();
        QueueService.RunProfilesAsync(list, QueueTrigger.Manual).GetAwaiter().GetResult();
        sw.Stop();
        SentinelService.Stop();
        foreach (var r in QueueService.LastResults)
            Console.WriteLine($"RESULT stall {r.ToolId} state={r.State} skipped={r.Skipped}");
        Console.WriteLine($"RESULT stall autoKill={autoKill} setting={AppState.Settings.StallAutoKill} elapsed={sw.Elapsed.TotalSeconds:F1}s");

        foreach (var p in Process.GetProcessesByName("StubTool"))
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            finally { p.Dispose(); }
        }

        bool ok;
        if (autoKill)
            ok = QueueService.LastResults.Any(r => r.ToolId == "stub-stall" && r.State == ToolRunState.Stalled)
                 && QueueService.LastResults.Any(r => r.ToolId == "stub-next" && r.State == ToolRunState.Done);
        else
            ok = QueueService.LastResults.Count == 1
                 && QueueService.LastResults[0].State == ToolRunState.Stalled;
        return ok ? 0 : 1;
    }

    /// <summary>护航：Acquire 后 powercfg /requests 能看到 SYSTEM 请求，Release 后消失。</summary>
    private static int RunKeepAwake()
    {
        AppState.Settings.KeepAwakeEnabled = true;
        AppState.Settings.NightScreenEnabled = false;
        KeepAwakeService.Acquire();
        Thread.Sleep(1200);
        string on = PowerRequests();
        Console.WriteLine($"KEEP sleep={KeepAwakeService.SleepBlocked} restart={KeepAwakeService.RestartBlocked} screen={KeepAwakeService.ScreenSaverSuppressed}");
        Console.WriteLine("KEEP-REQ-ON " + Compact(on));
        KeepAwakeService.Release();
        Thread.Sleep(600);
        string off = PowerRequests();
        Console.WriteLine("KEEP-REQ-OFF " + Compact(off));
        bool ok = KeepAwakeService.SleepBlocked || on.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase);
        return ok ? 0 : 1;
    }

    /// <summary>屏保原值记录/关闭/恢复。</summary>
    private static int RunScreenSaver()
    {
        AppState.Settings.KeepAwakeEnabled = true;
        AppState.Settings.NightScreenEnabled = false;
        GetScreensaver(out bool original);
        Console.WriteLine("SCREEN original=" + original);
        if (!SetScreensaver(true)) { Console.WriteLine("SCREEN skip（策略不允许修改）"); return 0; }
        KeepAwakeService.Acquire();
        Thread.Sleep(500);
        GetScreensaver(out bool during);
        Console.WriteLine("SCREEN during=" + during + " suppressed=" + KeepAwakeService.ScreenSaverSuppressed);
        KeepAwakeService.Release();
        Thread.Sleep(300);
        GetScreensaver(out bool after);
        Console.WriteLine("SCREEN after=" + after);
        SetScreensaver(original);
        return (during == false && after == true) ? 0 : 1;
    }

    /// <summary>夜间息屏：排定 + DRYRUN 日志。</summary>
    private static int RunNightScreen()
    {
        AppState.Settings.KeepAwakeEnabled = true;
        AppState.Settings.NightScreenEnabled = true;
        KeepAwakeService.NightScreenDelayOverride = TimeSpan.FromSeconds(2);
        KeepAwakeService.NightScreenDryRun = true;
        KeepAwakeService.Acquire();
        Console.WriteLine("NIGHT scheduled=" + KeepAwakeService.NightScreenScheduled);
        Thread.Sleep(3000);
        KeepAwakeService.Release();
        bool hit = false;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "全部日志");
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.log"))
                    if (File.ReadAllText(f).Contains("DRYRUN 夜间息屏", StringComparison.Ordinal)) hit = true;
        }
        catch { }
        Console.WriteLine("RESULT nightscreen dryrunLog=" + hit);
        KeepAwakeService.NightScreenDelayOverride = null;
        KeepAwakeService.NightScreenDryRun = false;
        AppState.Settings.NightScreenEnabled = false;
        return hit ? 0 : 1;
    }

    /// <summary>图标提取：StubTool.exe → cache/icons/icon-stub.png。</summary>
    private static int RunIcon(string stub)
    {
        var profile = new ToolProfile { ToolId = "icon-stub", DisplayName = "图标测试", ExecutablePath = stub };
        var bmp = ToolIconService.GetBitmap(profile);
        string file = Path.Combine(ToolIconService.CacheDir, "icon-stub.png");
        bool exists = File.Exists(file);
        Console.WriteLine($"RESULT icon ok={bmp != null} file={exists} size={(bmp == null ? "0" : bmp.PixelWidth + "x" + bmp.PixelHeight)}");
        ToolIconService.Invalidate("icon-stub");
        return bmp != null && exists ? 0 : 1;
    }

    /// <summary>图标 GDI 纪律：连续提取/失效 20 次，GDI 对象数不增长。</summary>
    private static int RunGdiIcons(string stub)
    {
        var profile = new ToolProfile { ToolId = "gdi-icon", DisplayName = "GDI", ExecutablePath = stub };
        // 预热：WPF 成像/WIC 首次使用会创建固定的 GDI 资源，不计入泄漏
        ToolIconService.GetBitmap(profile);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int before = GdiCount();
        for (int i = 0; i < 20; i++)
        {
            ToolIconService.Invalidate("gdi-icon");
            var bmp = ToolIconService.GetBitmap(profile);
            if (bmp == null) { Console.WriteLine("RESULT gdiicons 提取失败"); return 1; }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int after = GdiCount();
        ToolIconService.Invalidate("gdi-icon");
        Console.WriteLine($"RESULT gdiicons before={before} after={after} delta={after - before}");
        return after - before <= 5 ? 0 : 1;
    }

}
