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
    /// <summary>
    /// 剪贴板重试验证：StubClipboardHolder 占用期间 TrySetText 应返回 false（约 1 秒），
    /// 释放后应返回 true 且内容正确。
    /// </summary>
    private static int RunClipboard(string holder)
    {
        const string url = "http://127.0.0.1:18235/webhook";
        int exit = 1;

        var worker = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, _) =>
            {
                Process? proc = null;
                After(100, () =>
                {
                    proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = holder,
                        Arguments = "--seconds 8",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    });

                    // 等替身真正拿到占用权（读它的 "HOLDER open=True" 输出，比固定 sleep 可靠）
                    bool ready = Task.Run(() =>
                    {
                        try
                        {
                            string? line;
                            while ((line = proc?.StandardOutput.ReadLine()) != null)
                                if (line.StartsWith("HOLDER open=True", StringComparison.Ordinal)) return true;
                        }
                        catch { }
                        return false;
                    }).Wait(15000);

                    Console.WriteLine("CLIP holder-ready=" + ready);

                    After(ready ? 50 : 800, () =>
                    {
                        var sw = Stopwatch.StartNew();
                        bool busy = ClipboardHelper.TrySetText(url);
                        sw.Stop();
                        Console.WriteLine($"CLIP busy ok={busy} elapsed={sw.ElapsedMilliseconds}ms");
                        bool busyOk = !busy && sw.ElapsedMilliseconds >= 600 && sw.ElapsedMilliseconds <= 1600;
                        WaitHolderExit(proc, () => After(400, () =>
                        {
                            bool free = ClipboardHelper.TrySetText(url);
                            string got = "";
                            try { got = Clipboard.GetText(); } catch { }
                            Console.WriteLine($"CLIP free ok={free} text={got}");
                            exit = (busyOk && free && got == url) ? 0 : 1;
                            app.Shutdown();
                        }));
                    });
                });
            };
            app.Run();
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        return exit;
    }

    /// <summary>单次复制尝试（配合外部启动的 StubClipboardHolder 做对照实验）。</summary>
    private static int RunClipTry()
    {
        const string url = "http://127.0.0.1:18235/webhook";
        var sw = Stopwatch.StartNew();
        bool ok = ClipboardHelper.TrySetText(url);
        sw.Stop();
        Console.WriteLine($"CLIPTRY ok={ok} elapsed={sw.ElapsedMilliseconds}ms");
        return ok ? 0 : 1;
    }

    /// <summary>兜底弹窗外观验证：显示 9 秒供截图（只读可全选文本框）。</summary>
    private static int RunClipDialog()
    {
        var worker = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Set("Dark");
            var dlg = AppDialog.Create(AppDialog.Kind.Warning, "复制地址",
                "剪贴板被其他程序暂时占用，请手动复制（Ctrl+C）。",
                "下面的内容已全选，直接按 Ctrl+C 即可；关掉本窗口不影响其他功能。",
                "知道了", null, null, null, "http://127.0.0.1:18235/webhook");
            dlg.Show();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
            timer.Tick += (_, _) => { timer.Stop(); try { dlg.Close(); } catch { } app.Shutdown(); };
            timer.Start();
            app.Run();
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        return 0;
    }

}
