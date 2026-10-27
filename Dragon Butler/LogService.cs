using System;
using System.IO;
using System.Text;

namespace DragonButler;

/// <summary>
/// 运行日志服务（SPBGI）。
/// 全部日志写在 exe 旁「全部日志」目录；任何写盘失败都静默吞掉，绝不影响主流程。
/// </summary>
public static class LogService
{
    /// <summary>日志目录：exe 旁「全部日志」</summary>
    public static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "全部日志");

    /// <summary>正常退出标记文件名</summary>
    private const string ExitMarker = "last_exit_ok";

    /// <summary>
    /// 追加一条运行日志：yyyy-MM-dd_运行.log，行格式 HH:mm:ss 事件：{msg}，UTF-8 无 BOM
    /// </summary>
    public static void LogEvent(string msg)
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, DateTime.Now.ToString("yyyy-MM-dd") + "_运行.log");
            string line = DateTime.Now.ToString("HH:mm:ss") + " 事件：" + msg + Environment.NewLine;
            File.AppendAllText(file, line, new UTF8Encoding(false));
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    /// <summary>
    /// 写崩溃转储：yyyy-MM-dd_HHmmss_崩溃.txt（时间/场景/堆栈）
    /// </summary>
    public static void WriteCrash(string scene, Exception? ex)
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_崩溃.txt");
            var sb = new StringBuilder();
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("场景：" + scene);
            sb.AppendLine("异常：" + (ex?.ToString() ?? "(无异常对象)"));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 崩溃落盘失败也不能再抛
        }
    }

    /// <summary>清理 30 天前的日志文件（含 screenshots 子目录）</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            DateTime limit = DateTime.Now.AddDays(-30);
            foreach (var f in Directory.GetFiles(Dir))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < limit) File.Delete(f);
                }
                catch { }
            }
            string shots = Path.Combine(Dir, "screenshots");
            if (Directory.Exists(shots))
            {
                foreach (var f in Directory.GetFiles(shots))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < limit) File.Delete(f);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>标记本次为正常退出</summary>
    public static void MarkCleanExit()
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, ExitMarker), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch { }
    }

    /// <summary>
    /// 检查上次退出是否正常：有历史日志且无退出标记 = 异常退出。
    /// 检查后删除标记；首次运行（无历史日志）不算异常。
    /// </summary>
    public static bool CheckLastExit()
    {
        try
        {
            if (!Directory.Exists(Dir)) return false;
            var logs = Directory.GetFiles(Dir, "*_运行.log");
            if (logs.Length == 0) return false; // 首次运行不算异常
            string marker = Path.Combine(Dir, ExitMarker);
            bool abnormal = !File.Exists(marker);
            if (File.Exists(marker))
            {
                try { File.Delete(marker); } catch { }
            }
            return abnormal;
        }
        catch { return false; }
    }
}
