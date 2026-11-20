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

                // v1.28：这里原来调 AppDialog.Show（模态）。它在 OnStartup 里排在
                // 「软件启动后自动开跑」前面，于是人不在电脑前的时候，整晚卡在一个
                // 没人点的「知道了」上——用户第二天看到的正是「只弹了个崩溃提示，
                // 一条龙没跑」。不阻塞比「让人看见」重要：改成记一条，红点亮着等人来看。
                string? lastCrash = LogService.LastCrashSummary();
                NoticeCenter.AddCrash(
                    "上次没有正常收尾",
                    lastCrash,
                    title: "上次没有正常收尾，程序是被硬关掉或崩掉的");
            }
        }
        catch { }

        // 钩子一：非 UI 线程未处理异常 → 先写盘，再记进消息中心
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { LogService.WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception); }
            catch { }
            try
            {
                NoticeCenter.AddCrash("后台线程崩了（AppDomain）",
                    (e.ExceptionObject as Exception)?.ToString());
            }
            catch { }
        };

        // 钩子二：UI 线程未处理异常 → 写盘；不设 Handled，保持默认崩溃行为（确保写盘后再崩）
        Application.Current.DispatcherUnhandledException += (s, e) =>
        {
            try { LogService.WriteCrash("DispatcherUnhandledException", e.Exception); }
            catch { }
            try
            {
                NoticeCenter.AddCrash("界面线程出错（Dispatcher）", e.Exception?.ToString());
            }
            catch { }
        };

        // 钩子三：Task 未观测异常 → 写盘 + SetObserved
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                LogService.WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
                // 后台任务抛了没人接，进程不会死，但这一轮的事情确实没做完——
                // 以前它只落在日志里，用户永远不知道；现在留一条在消息中心。
                NoticeCenter.AddCrash("后台任务出错（Task）", e.Exception?.ToString());
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

    /// <summary>打开日志目录（消息中心里也有同一个入口，这里留着给托盘菜单复用）</summary>
    public static void OpenLogDir()
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
