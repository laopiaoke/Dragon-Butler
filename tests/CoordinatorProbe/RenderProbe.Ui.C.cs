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
    private void Step14()
    {
            // v1.40：汇报页要证明的是「它真的把时长拆开说了」——
            // 合计、每个工具各多久、占比条、按配置组拆、最近七天柱子。
            // 数据用 SeedForTest 直接塞内存（不落盘），否则截图会污染用户真实的账本。
            if (mode == "report" || mode == "all")
            {
                SeedSampleQueue();
                var now = DateTime.Now;
                DurationLedger.ClearForTest();
                DurationLedger.SeedForTest(now, "bettergi", "BetterGI", 4380, 2, "08:12:05",
                    new Dictionary<string, int> { { "每日委托", 1680 }, { "清体力", 2700 } });
                DurationLedger.SeedForTest(now, "march7th", "三月七助手", 1560, 1, "08:19:40",
                    new Dictionary<string, int> { { "每日委托", 1560 } });
                DurationLedger.SeedForTest(now, "ok-ww", "ok-ww", 2870, 3, "09:04:11",
                    new Dictionary<string, int> { { "清体力", 2870 } });
                DurationLedger.SeedForTest(now.AddDays(-1), "bettergi", "BetterGI", 3900, 2);
                DurationLedger.SeedForTest(now.AddDays(-1), "ok-ww", "ok-ww", 2100, 2);
                DurationLedger.SeedForTest(now.AddDays(-2), "bettergi", "BetterGI", 1200, 1);
                DurationLedger.SeedForTest(now.AddDays(-4), "march7th", "三月七助手", 3400, 2);

                var w = new MainWindow();
                w.ShowActivated = false;
                w.Show();
                SwitchToReportPage(w);
                w.UpdateLayout();
                bool ok = Save(w, Path.Combine(spot, "report.png"), 1120, 0);
                Console.WriteLine($"RENDER report={ok}");
                w.Close();
                DurationLedger.ClearForTest();
            }
    }

    private void Step15()
    {
            // v1.21：点开一个格子之后的启动设置窗，四张分别是
            // BetterGI（文档写过参数）/ ok-ww（实战见过）/ 绝区零（全要自己填）/ 自定义工具（什么都没有）
            if (mode == "launch" || mode == "all")
            {
                SeedSampleQueue();
                SeedHeroState("ready");
                foreach (var (toolId, file) in new[]
                {
                    ("bettergi", "launch_bettergi.png"),
                    ("ok-ww", "launch_okww.png"),
                    ("zzz-od", "launch_zzz.png"),
                })
                {
                    try
                    {
                        var p = ProfileRegistry.Get(toolId);
                        if (string.IsNullOrWhiteSpace(p.ExecutablePath))
                        {
                            p.ExecutablePath = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-bgi", "BetterGI.exe");
                            ProfileRegistry.Save(p);
                        }
                        var lw = new ToolLaunchWindow(p);
                        lw.UpdateLayout();
                        bool lok = Save(lw, Path.Combine(spot, file), 660, 0);
                        Console.WriteLine($"RENDER {file}={lok}");
                        lw.Close();
                    }
                    catch (Exception ex) { Console.WriteLine("RENDER-FAIL " + file + " " + ex.Message); }
                }
            }
    }

    private void Step16()
    {
            if (mode == "tools" || mode == "all")
            {
                SeedSampleQueue();
                SeedHeroState("ready");
                var w = new MainWindow();
                w.UpdateLayout();
                // v1.32：那一层「工具设置」折叠区已经撤掉，换成一排「每个工具怎么跑」的小按键，
                // 所以这里不再强行把 ToolCardsHost 掰成可见——掰开看到的是没人会看到的界面。
                // 要看的就是「一排小按键」本身。
                // 配置组是构造窗口那一刻扫的，那时还没铺假 BGI 目录——重扫一次再刷，
                // 这样点开任意一个工具都能看到真实的分组。
                BgiDiscovery.Scan(force: true);
                AppState.Settings.FavoriteGroups = new[] { "收尾" };
                var picker = typeof(MainWindow).GetMethod("RefreshGroupPicker",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                picker?.Invoke(w, null);
                w.UpdateLayout();
                // 工具按键在启动页（Home=1）的「托管配置」卡里。
                if (w.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer sv2)
                {
                    sv2.ScrollToVerticalOffset(430);
                    w.UpdateLayout();
                }
                bool ok = Save(w, Path.Combine(spot, "tools_open.png"), 980, 560);
                Console.WriteLine($"RENDER tools_open={ok}");
                w.Close();
            }
    }

    private void Step17()
    {
            // v1.19：开跑前体检窗。两种状态各截一张——全是坑的和基本健康的，
            // 只截一种看不出「重的排前面」和「只提示不挡路」到底长什么样。
            if (mode == "preflight" || mode == "all")
            {
                SeedSampleQueue();
                SeedHeroState("ready");

                PreflightInput Messy() => new(
                    BgiConfigured: false, BgiExists: false, BgiRunning: true,
                    EnabledTools: 2, ReadyTools: 1, GroupCount: 0,
                    DragonConfigName: "日常", DragonConfigs: Array.Empty<string>(),
                    NotifyMode: "Feishu", NotifyEndpoint: "",
                    MissingPresetGroups: Array.Empty<string>(), PresetMissing: true,
                    LastRunAt: "昨天 23:10", LastRunOk: false, LastRunReason: "退出码 1",
                    ScheduleEnabled: false, Writable: false);

                PreflightInput Fine() => new(
                    BgiConfigured: true, BgiExists: true, BgiRunning: false,
                    EnabledTools: 2, ReadyTools: 2, GroupCount: 3,
                    DragonConfigName: "日常", DragonConfigs: new[] { "日常", "深渊" },
                    NotifyMode: "Feishu", NotifyEndpoint: "https://example.invalid/hook",
                    MissingPresetGroups: Array.Empty<string>(), PresetMissing: false,
                    LastRunAt: "昨天 23:10", LastRunOk: true, LastRunReason: null,
                    ScheduleEnabled: true, Writable: true);

                var wBad = new PreflightWindow(() => Preflight.Build(Messy()));
                bool okBad = Save(wBad, Path.Combine(spot, "preflight_bad.png"), 700, 0);
                Console.WriteLine($"RENDER preflight_bad={okBad}");
                wBad.Close();

                var wFine = new PreflightWindow(() => Preflight.Build(Fine()));
                bool okFine = Save(wFine, Path.Combine(spot, "preflight_fine.png"), 700, 0);
                Console.WriteLine($"RENDER preflight_fine={okFine}");
                wFine.Close();
            }
    }

    private void Step18()
    {
            // v1.20：交接单。两种状态——有会让这趟白跑的事 / 基本能自己过。
            // 只截一种就看不出「风险排前面、计划在后面」这条设计。
            if (mode == "handover" || mode == "all")
            {
                Handover.Input Risky() => new(
                    Days: 3, ScheduleEnabled: true, AwayStartEnabled: false, NextRunText: "08:00",
                    NotifyLocalOnly: true, NotifyReady: true, EnabledTools: 3, ReadyTools: 2,
                    AutoRestartBgi: true, RestartsToday: 0, StopWhenFailed: false,
                    AfterRunDestructive: true, BgiPathOk: true, GroupCount: 3, FreeDiskMb: 30000);

                Handover.Input Smooth() => new(
                    Days: 3, ScheduleEnabled: true, AwayStartEnabled: false, NextRunText: "08:00",
                    NotifyLocalOnly: false, NotifyReady: true, EnabledTools: 3, ReadyTools: 3,
                    AutoRestartBgi: true, RestartsToday: 0, StopWhenFailed: false,
                    AfterRunDestructive: false, BgiPathOk: true, GroupCount: 3, FreeDiskMb: 30000);

                var wRisk = new HandoverWindow(d => Handover.Build(Risky()), d => Risky());
                bool okRisk = Save(wRisk, Path.Combine(spot, "handover_risk.png"), 660, 0);
                Console.WriteLine($"RENDER handover_risk={okRisk}");
                wRisk.Close();

                var wSmooth = new HandoverWindow(d => Handover.Build(Smooth()), d => Smooth());
                bool okSmooth = Save(wSmooth, Path.Combine(spot, "handover_smooth.png"), 660, 0);
                Console.WriteLine($"RENDER handover_smooth={okSmooth}");
                wSmooth.Close();
            }
    }

    private void Step19()
    {
            // v1.20 / 奇思妙想 #7：跑完收拾一下。只截全关状态——
            // 这一窗的价值是「默认全关、关游戏单独一行」，开起来反而看不出这点。
            if (mode == "tidy" || mode == "all")
            {
                AppState.Settings.TidyEnabled = false;
                AppState.Settings.TidyCloseGame = false;
                AppState.Settings.TidyCloseTool = false;
                AppState.Settings.TidyRestoreWindows = false;

                var w = new TidyWindow();
                bool ok = Save(w, Path.Combine(spot, "tidy.png"), 560, 0);
                Console.WriteLine($"RENDER tidy={ok}");
                w.Close();
            }
    }

    private void Step20()
    {
            // v1.17 / M1-B：「跑完后让 BetterGI 自己做什么」那一行。
            // 这一行只有展开 BetterGI 卡才看得见，而且下拉里必须有真候选值，
            // 否则截出来是个空下拉，等于没验。
            if (mode == "completion" || mode == "all")
            {
                SeedSampleQueue();
                SeedHeroState("ready");
                var w = new MainWindow();
                w.UpdateLayout();
                // v1.32：跑完动作那一行在 BetterGI 的旧折叠卡里（代码还在、界面不再显示）。
                // 这里仍然要把它掰开才能截到——它是「配置项长什么样」的证据图，
                // 不是为了展示用户会看到这一屏。
                if (w.FindName("Body_bettergi") is FrameworkElement bgiBody3)
                    bgiBody3.Visibility = Visibility.Visible;
                BgiDiscovery.Scan(force: true);
                var picker3 = typeof(MainWindow).GetMethod("RefreshGroupPicker",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                picker3?.Invoke(w, null);
                var refresh = typeof(MainWindow).GetMethod("RefreshCompletionRow",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                refresh?.Invoke(w, new object[] { true });
                w.UpdateLayout();
                if (w.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer sv3)
                {
                    sv3.ScrollToVerticalOffset(560);
                    w.UpdateLayout();
                }
                bool ok = Save(w, Path.Combine(spot, "completion.png"), 980, 620);
                Console.WriteLine($"RENDER completion={ok}");
                w.Close();
            }
    }

    private void Step21()
    {
            // v1.15：配置组合窗口 + 队列卡那一行。两处都得看：
            // 下拉在队列卡里，窗口管增删——只截窗口看不出它在主界面上长什么样。
            if (mode == "presets" || mode == "all")
            {
                SeedSampleQueue();
                SeedHeroState("ready");
                SeedSamplePresets();
                var w = new PresetsWindow();
                bool ok = Save(w, Path.Combine(spot, "presets.png"), 640, 0);
                Console.WriteLine($"RENDER presets={ok}");
                w.Close();

                var m = new MainWindow();
                m.UpdateLayout();
                var row = typeof(MainWindow).GetMethod("RefreshPresetRow",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                row?.Invoke(m, null);
                m.UpdateLayout();
                bool ok2 = Save(m, Path.Combine(spot, "preset_row.png"), 980, 760);
                Console.WriteLine($"RENDER preset_row={ok2}");
                m.Close();
            }
    }

    private void Step22()
    {
            if (mode == "home" || mode == "all")
            {
                // v1.15 首屏原型：同一个窗口四种状态各来一张，
                // 摊开才能对比「第一眼有没有明显该点的地方」。
                foreach (string st in new[] { "first", "ready", "running", "done" })
                {
                    var w = new HomePrototype(st);
                    bool ok = Save(w, Path.Combine(spot, $"home_{st}.png"), 980, 0);
                    Console.WriteLine($"RENDER home_{st}={ok}");
                    w.Close();
                }
            }
    }

    private void Step23()
    {
            if (mode == "main" || mode == "advanced" || mode == "top" || mode == "running" || mode == "notify" || mode == "backup" || mode == "all")
            {
                var w = new MainWindow();
                // 折叠状态下的「高级设置」在渲染里看不见，而 v1.10 新增的开关就在那儿。
                // 专门留一个 advanced 模式把它展开再截图，否则只能靠读代码确认布局。
                // v1.12：advanced 滚到底，top 滚到顶——同一张长图两头都要能看清，
                // 只留滚到底那种会让第一行被切掉半截。
                bool expanded = mode == "advanced" || mode == "top" || mode == "notify" || mode == "backup";
                if (expanded)
                {
                    // v1.15 / L6：通知卡也要能展开拍——模板输入框就在那张卡里。
                    foreach (string name in new[] { "NotifyBody" })
                    {
                        if (w.FindName(name) is FrameworkElement fe) fe.Visibility = Visibility.Visible;
                    }
                    // 内容装在 ScrollViewer 里，光设窗口高度没用——
                    // 得动滚动条，否则长图上下两端总有一头看不见。
                    w.UpdateLayout();
                    // v1.22：高级/系统/通知三张卡都在右页（设置页），先把页面切过去再滚。
                    SwitchToSettingsPage(w);
                    if (w.FindName("ScrollSettings") is System.Windows.Controls.ScrollViewer sv)
                    {
                        if (mode == "top") sv.ScrollToTop();
                        else sv.ScrollToEnd();
                        w.UpdateLayout();
                    }
                }
                // v1.32：通知卡跟插件卡并排搬到了设置页第一排，不再在最底下。
                // 那时候要 BringIntoView 是因为它藏在长列表末尾；现在滚到顶就整张在眼前，
                // 再 BringIntoView 反而会被上边那些卡的边距推偏。
                if (mode == "notify")
                {
                    if (w.FindName("ScrollSettings") is System.Windows.Controls.ScrollViewer nsv) nsv.ScrollToTop();
                    w.UpdateLayout();
                }
                // v1.17：备份那一行在系统卡末尾，滚到底会被状态条压住，同样用 BringIntoView。
                if (mode == "backup")
                {
                    w.UpdateLayout();
                    if (w.FindName("BtnBackupNow") is FrameworkElement row)
                    {
                        row.BringIntoView();
                        w.UpdateLayout();
                    }
                }
                // v1.13：实时条是跑起来才出现的，构造完窗口不会自己刷一次——
                // 不在这里手动触发，running 模式截到的永远是空着的队列卡。
                if (mode == "running")
                {
                    w.UpdateLayout();
                    var refresh = typeof(MainWindow).GetMethod("RefreshLiveStrip",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    refresh?.Invoke(w, null);
                    w.UpdateLayout();
                }
                // v1.12：高度传 0 让 SizeToContent 自己量。
                // 以前硬编码 792，结果「系统」「通知」两张卡只截到标题，下面留一大片空白。
                string file = mode == "advanced" ? "advanced.png"
                            : mode == "top" ? "advanced_top.png"
                            : mode == "running" ? "running.png"
                            : mode == "notify" ? "notify.png"
                            : mode == "backup" ? "backup.png"
                            : "main.png";
                // v1.41 / M6-1：主窗默认 1080×720，截图按 1120 宽才装得下默认布局。
                bool ok = Save(w, Path.Combine(spot, file), 1120, 0);
                Console.WriteLine($"RENDER {Path.GetFileNameWithoutExtension(file)}={ok}");
                w.Close();
            }
    }
}
