using System;
using System.Threading.Tasks;
using System.Windows;

namespace DragonButler;

/// <summary>
/// 全局异常兜底（SPBGI）：三钩子 + 上次异常退出检测。
/// 原则：先保证写盘，再让程序崩；Task 异常必须观测，避免进程静默死亡。
/// </summary>
public static class CrashGuard
{
    private static bool _installed;

    /// <summary>
    /// 安装全局异常兜底：
    /// Cleanup → CheckLastExit（异常则弹提示）→ 挂三钩子
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try { LogService.Cleanup(); } catch { }

        try
        {
            if (LogService.CheckLastExit())
            {
                LogService.LogEvent("检测到上次为异常退出");
                // 统一的应用内弹窗（替代系统 MessageBox）：标题/配色/字体/图标与主界面一致
                AppDialog.Show(
                    "检测到上次为异常退出，程序未能正常收尾。\n崩溃信息与运行日志已保存，可打开日志目录查看。",
                    detail: LogService.Dir,
                    kind: AppDialog.Kind.Warning,
                    title: "一条龙管家",
                    primary: "知道了",
                    secondary: "打开日志目录",
                    secondaryAction: OpenLogDir);
            }
        }
        catch { }

        // 钩子一：非 UI 线程未处理异常 → 先写盘
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { LogService.WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception); }
            catch { }
        };

        // 钩子二：UI 线程未处理异常 → 写盘；不设 Handled，保持默认崩溃行为（确保写盘后再崩）
        Application.Current.DispatcherUnhandledException += (s, e) =>
        {
            try { LogService.WriteCrash("DispatcherUnhandledException", e.Exception); }
            catch { }
        };

        // 钩子三：Task 未观测异常 → 写盘 + SetObserved
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                LogService.WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            }
            catch { }
        };
    }

    /// <summary>正常退出：记日志 + 打退出标记，幂等</summary>
    public static void Shutdown()
    {
        try
        {
            LogService.LogEvent("退出：正常退出");
            LogService.MarkCleanExit();
        }
        catch { }
    }

    /// <summary>打开日志目录（异常退出提示的次按钮）</summary>
    private static void OpenLogDir()
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogService.Dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + LogService.Dir + "\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
