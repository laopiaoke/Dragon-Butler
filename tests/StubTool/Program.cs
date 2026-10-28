using System;
using System.Threading;

namespace StubTool;

/// <summary>
/// 替身工具：模拟一个"跑一段时间后退出"的上游工具，用于驱动 RunCoordinator 的各条分支。
///
/// 用法：
///   StubTool --seconds 5 --exit 0     跑 5 秒后以退出码 0 退出（正常完成）
///   StubTool --seconds 5 --exit 3     同上，但退出码 3（失败）
///   StubTool --hang                   永不退出（用于超时 / 悬置分支）
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int seconds = 5;
        int exitCode = 0;
        bool hang = false;

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
                case "--help":
                case "-h":
                    Console.WriteLine("StubTool --seconds N --exit C | --hang");
                    return 0;
            }
        }

        Console.WriteLine($"[StubTool] start pid={Environment.ProcessId} seconds={seconds} exit={exitCode} hang={hang}");
        if (hang)
        {
            while (true) Thread.Sleep(1000);
        }

        Thread.Sleep(Math.Max(0, seconds) * 1000);
        Console.WriteLine($"[StubTool] exit {exitCode}");
        return exitCode;
    }
}
