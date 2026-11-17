using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.IO;

namespace DragonButler;

/// <summary>
/// 跑完收拾一下（v1.20 / 奇思妙想 #7）的执行侧。
///
/// <para>
/// 判定全在 <see cref="Tidy"/>（纯函数、可断言）；这里只做「把计划变成动作」。
/// 每一处外部调用都单独 try —— 收拾是收尾动作，任何一步失败都不该影响别的步骤，
/// 更不该冒泡到队列里让用户以为这次白跑了。
/// </para>
/// </summary>
public static class TidyService
{
    private static readonly object Gate = new();

    /// <summary>窗口位置的存档路径。与设置分开：它是「机器状态」不是「用户偏好」。</summary>
    public static string? PathOverride { get; set; }

    public static string FilePath => PathOverride
        ?? Path.Combine(AppContext.BaseDirectory, "config", "window_spots.json");

    private static List<Tidy.WindowSpot>? _spots;

    public static void Load()
    {
        lock (Gate)
        {
            _spots = null;
            try
            {
                if (!File.Exists(FilePath)) { _spots = new(); return; }
                string json = File.ReadAllText(FilePath);
                _spots = JsonSerializer.Deserialize<List<Tidy.WindowSpot>>(json) ?? new();
            }
            catch (Exception ex)
            {
                LogService.LogEvent("窗口位置读取失败：" + ex.Message);
                _spots = new();
            }
        }
    }

    private static List<Tidy.WindowSpot> Spots()
    {
        lock (Gate)
        {
            if (_spots == null) Load();
            return _spots ?? new();
        }
    }

    private static void Save()
    {
        lock (Gate)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(_spots ?? new(),
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                LogService.LogEvent("窗口位置保存失败：" + ex.Message);
            }
        }
    }

    /// <summary>记住一个窗口现在在哪。同一个进程只留最新的一条。</summary>
    public static void Remember(string? processName, string? title, int left, int top, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        var spot = new Tidy.WindowSpot(processName, title ?? "", left, top, width, height);
        if (!Tidy.WorthRemembering(spot, 8192, 8192)) return;

        lock (Gate)
        {
            var list = Spots();
            list.RemoveAll(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            list.Add(spot);
            _spots = list;
        }
        Save();
    }

    public static IReadOnlyList<Tidy.WindowSpot> All() => Spots().ToList();

    public static void Forget(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        lock (Gate)
        {
            var list = Spots();
            list.RemoveAll(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            _spots = list;
        }
        Save();
    }

    /// <summary>
    /// 按设置算一份计划。所有外部状态都在这里取一次，
    /// 计划本身仍然由 <see cref="Tidy.Build"/> 决定（便于断言）。
    /// </summary>
    public static Tidy.Plan PlanFromSettings(bool anyFailure)
    {
        var s = AppState.Settings;
        bool gameRunning = false, toolRunning = false;
        try { gameRunning = GameProcessNames.Any(IsRunning); } catch { }
        try
        {
            var bgi = ProfileRegistry.Get("bettergi");
            toolRunning = bgi != null && ToolRunner.IsRunning(bgi);
        }
        catch { }

        return Tidy.Build(new Tidy.Input(
            Enabled: s.TidyEnabled,
            CloseGameEnabled: s.TidyCloseGame,
            CloseToolEnabled: s.TidyCloseTool,
            RestoreWindowsEnabled: s.TidyRestoreWindows,
            GameRunning: gameRunning,
            ToolRunning: toolRunning,
            RememberedWindows: Spots().Count,
            AnyFailure: anyFailure,
            DryRun: s.AfterRunDryRun));
    }

    /// <summary>原神 / 星穹铁道 / 绝区零的进程名（不带 .exe）。</summary>
    public static readonly string[] GameProcessNames =
    {
        "YuanShen", "GenshinImpact", "StarRail", "ZenlessZoneZero",
    };

    private static bool IsRunning(string name)
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// 执行收拾计划。返回一句话说明做了什么（给队列收尾的日志与通知用）；
    /// 没开或没事可做时返回空串。
    /// </summary>
    public static string Execute(Tidy.Plan plan)
    {
        if (plan == null || !plan.Anything) return "";

        var done = new List<string>();
        foreach (var step in plan.Steps)
        {
            try
            {
                switch (step.Kind)
                {
                    case Tidy.Action.CloseGame:
                        if (step.Destructive && !AppState.Settings.AfterRunDryRun)
                        {
                            if (CloseAll(GameProcessNames)) done.Add("关掉游戏");
                        }
                        break;

                    case Tidy.Action.CloseTool:
                        if (!AppState.Settings.AfterRunDryRun)
                        {
                            var bgi = ProfileRegistry.Get("bettergi");
                            if (bgi != null)
                            {
                                ToolRunner.KillAll(bgi);
                                done.Add("关掉 BetterGI");
                            }
                        }
                        break;

                    case Tidy.Action.RestoreWindows:
                        int moved = RestoreWindows();
                        if (moved > 0) done.Add(moved == 1 ? "把 1 个窗口放回原处" : $"把 {moved} 个窗口放回原处");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogService.LogEvent($"收拾时出错（{step.Kind}）：" + ex.Message);
            }
        }

        if (done.Count == 0) return "";
        string body = string.Join("、", done);
        string text = AppState.Settings.AfterRunDryRun ? $"（演练）会{body}" : $"已{body}";
        LogService.LogEvent("收拾：" + text);
        return text;
    }

    private static bool CloseAll(IEnumerable<string> names)
    {
        bool any = false;
        foreach (string name in names)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.CloseMainWindow(); any = true; }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
        return any;
    }

    /// <summary>
    /// 把记住的窗口挪回去。只动「还开着且没在正确位置」的那些；
    /// 已经归位的不碰（差 2 个像素还去挪一次，只会打断正在用的人）。
    /// </summary>
    private static int RestoreWindows()
    {
        int moved = 0;
        foreach (var spot in Spots())
        {
            try
            {
                var procs = Process.GetProcessesByName(spot.ProcessName);
                var proc = procs.FirstOrDefault(p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero; }
                    catch { return false; }
                });
                if (proc == null) continue;

                var now = new Tidy.WindowSpot(spot.ProcessName, spot.Title,
                    spot.Left, spot.Top, spot.Width, spot.Height);
                if (Tidy.SamePlace(now, spot)) continue;

                if (MoveWindow(proc.MainWindowHandle, spot.Left, spot.Top, spot.Width, spot.Height, true))
                    moved++;
                proc.Dispose();
            }
            catch (Exception ex)
            {
                LogService.LogEvent($"窗口归位失败（{spot.ProcessName}）：" + ex.Message);
            }
        }
        return moved;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>读一个进程主窗口当前的位置，用来「记住现在在哪」。</summary>
    public static Tidy.WindowSpot? Capture(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        try
        {
            var proc = Process.GetProcessesByName(processName)
                .FirstOrDefault(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });
            if (proc == null) return null;
            if (!GetWindowRect(proc.MainWindowHandle, out var r)) return null;
            string title = "";
            try { title = proc.MainWindowTitle ?? ""; } catch { }
            return new Tidy.WindowSpot(processName, title, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        catch { return null; }
    }
}
