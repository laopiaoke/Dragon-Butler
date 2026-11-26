using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragonButler;
using DragonButler.Plugins;
using static CoordinatorProbe.RenderProbe;

namespace CoordinatorProbe;

// v1.45.3 / #15：界面渲染探针按截图分组拆成多个 partial 文件，单文件 <= 20 KB。
// 每张图的渲染逻辑与输出文件名一字未改，只换了所在文件。
internal sealed partial class UiRunner
{
    // ===== 共享状态：原来是 RunUi 里的局部变量 =====
    private readonly string mode;
    private string spot = "";
    private int exit = 1;
    // v1.42.1 / M9-3（D17）：收起态零滚动是契约，不是参考值——超了就让退出码非零。
    private bool zeroScrollFail;
    // v1.44 / 修复 #4：反悔窗口也得真出现过、真消失过，不然那三条 UI 只是摆设。
    private bool wrapUpFail;

    internal UiRunner(string mode) => this.mode = mode;

    internal int Run()
    {
        exit = 1;
        // v1.42.1 / M9-3（D17）：收起态零滚动是契约，不是参考值——超了就让退出码非零。
        zeroScrollFail = false;
        // v1.44 / 修复 #4：反悔窗口也得真出现过、真消失过，不然那三条 UI 只是摆设。
        wrapUpFail = false;
        var worker = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, _) =>
            {
                try
                {
                    // 主题令牌（CardBgBrush 等）是在代码里写进 Application.Current.Resources 的，
                    // 不调这一步，DynamicResource 全部落空，渲染出来是白底黑字的空壳。
                    Theme.Set("Dark");

                    // 编辑器窗口读的是 AppState.Settings.Queue；probe 里没人调 AppState.Init()，
                    // 不铺一份样例数据，渲染出来永远是「队列里还没有工具」的空壳。
                    SeedSampleQueue();
                    // 日历读的是 GroupArchive，不造历史的话 53 周全灰，看不出四色设计。
                    // v1.42.1 / M9-4：日历窗口删了，样例历史只在 today/running 这些还要它的模式里造。
                    // v1.13「今日运行」同样读 GroupArchive（今天那一段）+ RunJournal。
                    if (mode == "today")
                    {
                        SeedSampleArchive();
                        SeedSampleJournal();
                        SeedDecisions();
                    }
                    // v1.13：running 模式要在截图里看到实时条本身，所以得先造出"队列在跑"。
                    if (mode == "running")
                    {
                        SeedSampleArchive();
                        SeedRunningState();
                    }

                    spot = Path.Combine(Path.GetTempPath(), "dragon-ui");
                    Directory.CreateDirectory(spot);

                    Step0();
                    Step1();
                    Step2();
                    Step3();
                    Step4();
                    Step5();
                    Step6();
                    Step7();
                    Step8();
                    Step9();
                    Step10();
                    Step11();
                    Step12();
                    Step13();
                    Step14();
                    Step15();
                    Step16();
                    Step17();
                    Step18();
                    Step19();
                    Step20();
                    Step21();
                    Step22();
                    Step23();
                    Step24();

                    Console.WriteLine("RENDER-DIR " + spot);
                    exit = (zeroScrollFail || wrapUpFail) ? 1 : 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("RENDER-FAIL " + ex.Message);
                    exit = 1;
                }
                finally
                {
                    app.Shutdown();
                }
            };
            app.Run();
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join(TimeSpan.FromSeconds(90));
        return exit;
    }
}
