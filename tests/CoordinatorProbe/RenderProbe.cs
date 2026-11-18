using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragonButler;

namespace CoordinatorProbe;

/// <summary>
/// 界面离屏渲染（v1.8 起用于人工验收）。
///
/// 为什么要在进程内画，而不是截屏：
/// 桌面上随时可能有别的窗口盖住主界面，CopyFromScreen 截到的是别人的画面；
/// PrintWindow 对 WPF 的合成层又常常只拿到一张白/黑图。
/// 用 RenderTargetBitmap 直接渲染视觉树，不受桌面状态影响，CI 里也能跑。
/// </summary>
internal static class RenderProbe
{
    /// <summary>把某个 Window 渲染成 PNG。必须在 STA 线程里调用。</summary>
    public static bool Save(Window window, string path, int width, int height, bool show = true)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (show)
            {
                // 用 1×1 的透明位置「显示」一次：WPF 需要一个真实窗口句柄来完成布局与模板展开，
                // 但位置在屏幕外，肉眼看不到闪窗。
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                if (width > 0) window.Width = width;
                if (height > 0) window.Height = height;
                window.Show();
            }

            window.UpdateLayout();

            // 注：主窗是 SizeToContent="Height"，但 WPF 会把窗口高度夹在工作区以内（这台机器约 792），
            // 所以「整页长图」这条路由走不通——下面那两张卡本来就是要滚动才看得到的。
            // 想看清底部就换 ui top / 展开折叠卡，别试图把窗口拉得比屏幕还高。

            // SizeToContent 的窗口高度由内容决定，硬编码会把下方留成空白
            int realWidth = width > 0 ? width : (int)Math.Ceiling(window.ActualWidth);
            int realHeight = height > 0 ? height : (int)Math.Ceiling(window.ActualHeight);
            if (realWidth <= 0) realWidth = 640;
            if (realHeight <= 0) realHeight = 480;

            var visual = (Visual)window.Content;
            var size = new Size(realWidth, realHeight);
            var bitmap = new RenderTargetBitmap(realWidth, realHeight, 96, 96, PixelFormats.Pbgra32);

            // 先用窗口背景铺一层，否则半透明窗口渲染出来是黑底
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var bg = window.Background;
                dc.DrawRectangle(bg ?? Brushes.Transparent, null, new Rect(size));
            }
            bitmap.Render(dv);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);

            if (show) window.Hide();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("RENDER-FAIL " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 给渲染用的样例队列：三个工具、两条依赖、一处切片。
    /// 只在本进程内改内存，不写盘——渲染探针不该改用户的配置。
    /// </summary>
    /// <remarks>
    /// v1.22：内容区拆成三页之后，右页（工具设置 / 高级 / 系统 / 通知）默认不在可视区里。
    /// 渲染前必须先把页面切过去，否则截出来会是中页。用反射调 ShowPage(index, false)：
    /// 不带动画、立即到位，截的才是稳定画面。
    /// </remarks>
    private static void SwitchToSettingsPage(MainWindow w)
    {
        try
        {
            var m = typeof(MainWindow).GetMethod("ShowPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            m?.Invoke(w, new object[] { 2, false });
            w.UpdateLayout();
        }
        catch (Exception ex) { Console.WriteLine("SWITCH-PAGE-FAIL " + ex.Message); }
    }

    private static void SeedSampleQueue()
    {
        try
        {
            if ((AppState.Settings.Queue?.Count ?? 0) > 0) return;
            AppState.Settings.Queue = new List<QueueEntry>
            {
                new() { ToolId = "bettergi", Enabled = true, WeekdayMask = 0x7F, Groups = new List<string> { "日常", "活动" } },
                new() { ToolId = "march7th", Enabled = true, WeekdayMask = 0x7F, DependsOn = new List<string> { "bettergi" }, OnDependencyFail = QueueGraph.PolicyAbort },
                new() { ToolId = "ok-ww", Enabled = false, WeekdayMask = 0x7F, DependsOn = new List<string> { "bettergi" }, Groups = new List<string> { "收尾" } },
            };
            AppState.Settings.SelectedGroups = new[] { "日常", "活动", "收尾" };
            SeedSampleBgiDir();
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-FAIL " + ex.Message);
        }
    }

    /// <summary>
    /// 造一个假的 BetterGI 目录（带 User\ScriptGroup\*.json），让 BgiDiscovery 走真实扫描路径。
    /// 比在界面里塞假数据更可信：渲染出来的是程序真正会看到的样子。
    /// 目录建在临时目录下，不碰用户的 BGI 安装。
    /// </summary>
    private static void SeedSampleBgiDir()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-bgi");
            string groupsDir = Path.Combine(root, "User", "ScriptGroup");
            Directory.CreateDirectory(groupsDir);
            foreach (string name in new[] { "日常", "活动", "收尾" })
            {
                string file = Path.Combine(groupsDir, name + ".json");
                if (!File.Exists(file)) File.WriteAllText(file, "{}");
            }
            // v1.17：一条龙配置也要造两份，「跑完后动作」的下拉才有候选值可显示。
            // 两份故意用不同的值，这样截图里能看出「这个值哪些配置用过」这件事。
            string oneDragonDir = Path.Combine(root, "User", "OneDragon");
            Directory.CreateDirectory(oneDragonDir);
            WriteIfMissing(Path.Combine(oneDragonDir, "日常.json"),
                "{\n  \"name\": \"日常\",\n  \"tasks\": [],\n  \"settings\": { \"completionAction\": \"Shutdown\", \"partyName\": \"旅行者\" }\n}");
            WriteIfMissing(Path.Combine(oneDragonDir, "深渊.json"),
                "{\n  \"name\": \"深渊\",\n  \"tasks\": [],\n  \"settings\": { \"completionAction\": \"Shutdown\" }\n}");
            WriteIfMissing(Path.Combine(oneDragonDir, "挖矿.json"),
                "{\n  \"name\": \"挖矿\",\n  \"tasks\": [],\n  \"settings\": { \"completionAction\": \"Sleep\" }\n}");
            AppState.Settings.BgiPath = Path.Combine(root, "BetterGI.exe");
            AppState.Settings.DragonConfigName = "日常";
            BgiDiscovery.Scan(force: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-BGI-FAIL " + ex.Message);
        }
    }

    private static void WriteIfMissing(string path, string content)
    {
        try { if (!File.Exists(path)) File.WriteAllText(path, content); } catch { }
    }

    /// <summary>
    /// v1.13：把「队列正在跑」这件事假造出来，好让实时条在离屏渲染里真的显示出来。
    ///
    /// <para>
    /// 不真跑队列：真跑要起进程、等信号，渲染探针只该画界面。
    /// 这里直接用反射把 <see cref="QueueService"/> 的运行态字段和
    /// <see cref="SentinelService"/> 的 CurrentGroup 铺好——两处都是 private set，
    /// 没有对外的测试入口，反射是唯一不为了截图去改生产代码的办法。
    /// </para>
    /// </summary>
    private static void SeedRunningState()
    {
        try
        {
            DateTime now = DateTime.Now;
            SetStatic( typeof(QueueService), "_running", true);
            SetBacking(typeof(QueueService), "CurrentToolId", "bettergi");
            SetBacking(typeof(QueueService), "CurrentToolStartedAt", now.AddMinutes(-23));

            // 当前配置组：HealthSnapshot() 返回的是副本，改它没用——
            // 要直接拿到哨兵里那只单例，改它自己的字段（和 GroupStarted 分支做的事一样）。
            var field = typeof(SentinelService).GetField("Health",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field?.GetValue(null) is SentinelHealth live)
            {
                live.CurrentGroup = "日常";
                live.CurrentGroupSince = now.AddMinutes(-18);
            }
            else
            {
                Console.WriteLine("SEED-RUNNING-MISS SentinelService.Health");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-RUNNING-FAIL " + ex.Message);
        }
    }

    private static void SetStatic(Type type, string field, object value)
    {
        var f = type.GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f == null) { Console.WriteLine($"SEED-RUNNING-MISS {type.Name}.{field}"); return; }
        f.SetValue(null, value);
    }

    /// <summary>
    /// v1.15：把首屏主卡逼到指定状态。
    ///
    /// <para>
    /// 主卡的五种脸是靠「有没有 exe / 在不在跑 / 刚跑完没 / 半小时内告过警」这四个事实推出来的，
    /// 所以这里就照着这四个输入摆道具，不去改 <see cref="HomeState"/> 的结果——
    /// 那样截出来的图只能证明排版，证明不了判定。
    /// </para>
    /// </summary>
    private static void SeedHeroState(string state)
    {
        try
        {
            DateTime now = DateTime.Now;

            // 每个状态都从「没在跑」起步：QueueService 的状态是静态字段，
            // 上一轮铺的 _running=true 会留到下一轮，不重置的话 trouble/done 会被 running 压掉。
            SetStatic(typeof(QueueService), "_running", false);
            SetBacking(typeof(QueueService), "CurrentToolId", "");
            SetBacking(typeof(QueueService), "CurrentToolStartedAt", default(DateTime));
            var health = typeof(SentinelService).GetField("Health",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (health?.GetValue(null) is SentinelHealth live)
            {
                live.CurrentGroup = "";
                live.CurrentGroupSince = default;
            }
            AppState.Settings.ShieldAlertAt = null;

            switch (state)
            {
                case "first":
                    // 默认就是「没配 exe」：SeedSampleBgiDir 指向的那个 BetterGI.exe 并不真存在。
                    break;

                case "ready":
                {
                    // 造一个真的会存在于磁盘上的 exe 占位文件，HasExecutable 才认。
                    string exe = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-bgi", "BetterGI.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                    if (!File.Exists(exe)) File.WriteAllText(exe, "");
                    var p = ProfileRegistry.Get("bettergi");
                    p.ExecutablePath = exe;
                    ProfileRegistry.Save(p);
                    AppState.Settings.BgiPath = exe;
                    break;
                }

                case "running":
                    SeedHeroState("ready");
                    SeedRunningState();
                    break;

                case "trouble":
                    SeedHeroState("ready");
                    AppState.Settings.ShieldAlertAt = now.AddMinutes(-4);
                    break;

                case "done":
                    SeedHeroState("ready");
                    AppState.Settings.LastRunAt = now.AddHours(-2).ToString("yyyy-MM-dd HH:mm");
                    AppState.Settings.LastRunOk = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-HERO-FAIL " + ex.Message);
        }
    }

    private static void SetBacking(Type type, string property, object value)
    {
        SetStatic(type, "<" + property + ">k__BackingField", value);
    }

    /// <summary>
    /// v1.20：给「跑完」那张主卡摆今天的道具——复盘要读今天的两张表，
    /// 而 <see cref="SeedSampleArchive"/> 造的是过去一年且刻意跳过了今天。
    /// 这里补三件：今天两个配置组（其中一个比往常慢，才能看到评价那句）、
    /// 今天两项台账（一项成一项没成，才能看到 Gap）、一点收益（摩拉那句）。
    /// 仍然全指向临时文件，不碰用户真实存档。
    /// </summary>
    /// <summary>
    /// v1.20：给「今日」摆几条自动决定。刻意挑四种来源各一条——
    /// 只摆同一种的话，看不出「队列/守护/定时/规则」这套来源标签是否有意义。
    /// </summary>
    private static void SeedDecisions()
    {
        try
        {
            DecisionLog.Clear();
            DateTime now = DateTime.Now;
            DecisionLog.AddAt(now.AddMinutes(-95), DecisionSource.Schedule, "先不开始",
                "你 42 秒前还在动键鼠，等 15 分钟再看");
            DecisionLog.AddAt(now.AddMinutes(-80), DecisionSource.Queue, "跳过 ok-ww",
                "它依赖的 BetterGI 这次没跑成");
            DecisionLog.AddAt(now.AddMinutes(-62), DecisionSource.Sentinel, "重启 BetterGI",
                "进程 30 秒没出现，退出码 3221225477");
            DecisionLog.AddAt(now.AddMinutes(-40), DecisionSource.Queue, "后面的不跑了",
                "三月七助手没跑成，而你选的是「一项失败就整队停」");
            DecisionLog.AddAt(now.AddMinutes(-12), DecisionSource.Rule, "留了现场",
                "日志里出现了「卡在传送锚点」");
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-DECISIONS-FAIL " + ex.Message);
        }
    }

    private static void SeedRecapToday()
    {
        try
        {
            DateTime today = DateTime.Today;

            // 往常中位数要有样本：往前铺三天「日常」都跑 20 分钟，
            // 今天跑 40 分钟 → 明显比往常慢，Detail 那句才会出现。
            for (int back = 1; back <= 3; back++)
            {
                GroupArchive.Seed(today.AddDays(-back), "日常", "23:04:00", "23:24:00");
            }
            GroupArchive.Seed(today, "日常", "23:04:00", "23:44:00");
            GroupArchive.Seed(today, "活动", "23:44:00", "23:56:00");

            // 两项都成。刻意不在这里放失败项：主卡标题读的是 LastRunOk，
            // 台账里塞失败会让标题说「全部成功」、复盘说「1 项没成」，
            // 截图看着像 bug。失败分支由 recap 探针的断言守着。
            RunJournal.Record(new QueueItemResult
            {
                ToolId = "bettergi", DisplayName = "BetterGI", State = ToolRunState.Done,
            });
            RunJournal.Record(new QueueItemResult
            {
                ToolId = "march7th", DisplayName = "三月七助手", State = ToolRunState.Done,
            });

            HarvestLedger.AddToDay(today, new Harvest
            {
                Mora = 128400,
                Exp = 42,
                Materials = new Dictionary<string, int>(StringComparer.Ordinal) { ["刀镡"] = 12 },
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-RECAP-FAIL " + ex.Message);
        }
    }

    /// <summary>
    /// 造一年的挂机记录，让日历那面墙真的有颜色。
    /// 不这么干的话渲染出来的 53 周全是灰格——图例四种颜色一个都对不上，
    /// 看不出「绿=全跑成、黄=有失败、红=全没成」这套设计到底成不成立。
    /// 落盘走 GroupArchive.PathOverride 指到临时文件，不碰用户的 group_archive.json。
    /// </summary>
    private static void SeedSampleJournal()
    {
        try
        {
            RunJournal.PathOverride = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-run-journal.json");
            if (File.Exists(RunJournal.PathOverride)) File.Delete(RunJournal.PathOverride);
            RunJournal.Load();

            // v1.20：给基线造历史——过去 6 天每天「2 项里 1 项没成」，
            // 今天两项都没成 → 明显高于平时，才验得到「今日」顶部那行。
            // 只造今天的话，历史样本为 0，基线会按设计闭嘴，截图就看不出这个功能。
            // 过去 6 天都没出过问题 → 今天这 1 项没成，正好命中「平时一次都没有」那种该报警的情况。
            for (int back = 1; back <= 6; back++)
            {
                SeedJournalFor(DateTime.Today.AddDays(-back), failed: 0);
            }

            RunJournal.Record(new QueueItemResult
            {
                ToolId = "bettergi", DisplayName = "BetterGI", State = ToolRunState.Done,
            });
            RunJournal.Record(new QueueItemResult
            {
                ToolId = "march7th", DisplayName = "三月七助手", State = ToolRunState.Failed, Reason = "退出码 1",
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-JOURNAL-FAIL " + ex.Message);
        }
    }

    /// <summary>往台账的某一天写两条记录，<paramref name="failed"/> 决定其中几条是失败。</summary>
    private static void SeedJournalFor(DateTime day, int failed)
    {
        RunJournal.SeedForTest(day,
            new JournalEntry
            {
                ToolId = "bettergi", Name = "BetterGI",
                State = nameof(ToolRunState.Done), At = "08:30:00",
            },
            new JournalEntry
            {
                ToolId = "march7th", Name = "三月七助手",
                State = failed >= 1 ? nameof(ToolRunState.Failed) : nameof(ToolRunState.Done),
                At = "09:00:00",
                Reason = failed >= 1 ? "退出码 1" : null,
            });
    }

    private static void SeedSampleArchive()
    {
        try
        {
            GroupArchive.PathOverride = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-group-archive.json");
            if (File.Exists(GroupArchive.PathOverride)) File.Delete(GroupArchive.PathOverride);
            GroupArchive.Load();

            DateTime today = DateTime.Today;
            var rand = new Random(20261108);
            for (int back = 0; back < CalendarModel.Weeks * 7; back++)
            {
                DateTime day = today.AddDays(-back);
                int roll = rand.Next(100);
                // 大约三成日子干脆没跑（长草期/出差），剩下的按 7:2:1 分成功/部分/全败
                if (roll < 30) continue;
                bool allOk = roll < 79;
                bool allFail = roll >= 97;
                int groups = rand.Next(2, 5);
                for (int i = 0; i < groups; i++)
                {
                    string name = i == 0 ? "日常" : i == 1 ? "活动" : "收尾" + i;
                    bool ok = allOk || (!allFail && i < groups - 1);
                    GroupArchive.Seed(day, name, "08:0" + i + ":00", ok ? "08:2" + i + ":30" : null);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-ARCHIVE-FAIL " + ex.Message);
        }
    }

    /// <summary>
    /// v1.15：配置组合的样例。三套里故意留一套「组名已失效」，
    /// 否则截图上永远看不到那个警告色——而它正是「别静默丢东西」的可见证据。
    /// 只改内存，不写盘。
    /// </summary>
    private static void SeedSamplePresets()
    {
        try
        {
            AppState.Settings.Presets = new List<ConfigPreset>
            {
                new()
                {
                    Id = "p-daily", Name = "晚上日常", DragonConfigName = "日常",
                    Groups = new[] { "日常", "活动" }, LastUsedAt = "2026-11-15 09:12:03",
                },
                new()
                {
                    Id = "p-weekend", Name = "周末清体力", DragonConfigName = "日常",
                    Groups = new[] { "活动", "收尾" }, LastUsedAt = "2026-11-13 20:40:11",
                },
                new()
                {
                    Id = "p-old", Name = "老的挖矿脚本", DragonConfigName = "挖矿",
                    Groups = new[] { "已删除的老组" },
                },
            };
            AppState.Settings.ActivePresetId = "p-daily";
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-PRESETS-FAIL " + ex.Message);
        }
    }

    /// <summary>
    /// v1.14：日志规则的样例。三档行为各来一条，并故意留一条没写的，
    /// 这样截图里能同时看到「填好的」「没生效的」——只看填好的看不出边界在哪。
    /// 只改内存，不写盘。
    /// </summary>
    private static void SeedSampleLogRules()
    {
        try
        {
            AppState.Settings.LogRules = new List<LogRule>
            {
                new()
                {
                    Id = "demo-1", Keyword = "卡在传送锚点", Behavior = LogRule.Notify, Enabled = true,
                    Note = "卡住超过三分钟多半是卡位了，提醒我出来看一眼",
                },
                new()
                {
                    Id = "demo-2", Keyword = "Abnormal", Behavior = LogRule.Halt, Enabled = true,
                    Note = "BGI 自己的异常退出，后面的工具没必要再跑",
                },
                new()
                {
                    Id = "demo-3", Keyword = "配错", Behavior = LogRule.Capture, Enabled = false,
                    Note = "暂时用不上，先留着",
                },
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("SEED-LOGRULES-FAIL " + ex.Message);
        }
    }

    /// <summary>
    /// 渲染一个独立窗口（浮动条、命令面板、日历），跑完就关。
    /// 这些窗口没有主窗的依赖，可以单独实例化。
    /// </summary>
    public static int RunUi(string mode)
    {
        int exit = 1;
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
                    if (mode == "calendar" || mode == "all") SeedSampleArchive();
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

                    var spot = Path.Combine(Path.GetTempPath(), "dragon-ui");
                    Directory.CreateDirectory(spot);

                    if (mode == "minibar" || mode == "minibar_run" || mode == "all")
                    {
                        if (mode != "minibar") { SeedSampleArchive(); SeedRunningState(); }
                        var w = new MiniBarWindow();
                        // v1.13：浮条宽 180，跑起来时主行是「组名 · 已跑」，次行是「还要多久」——
                        // 这两句都得塞进 180 里，只有渲染出来才知道会不会被截。
                        string file = mode == "minibar_run" ? "minibar_run.png" : "minibar.png";
                        bool ok = Save(w, Path.Combine(spot, file), 180, 44);
                        Console.WriteLine($"RENDER {Path.GetFileNameWithoutExtension(file)}={ok}");
                        w.Close();
                    }

                    if (mode == "calendar" || mode == "all")
                    {
                        var w = new CalendarWindow();
                        bool ok = Save(w, Path.Combine(spot, "calendar.png"), 0, 0);
                        Console.WriteLine($"RENDER calendar={ok}");
                        w.Close();
                    }

                    if (mode == "today" || mode == "all")
                    {
                        var w = new TodayWindow();
                        bool ok = Save(w, Path.Combine(spot, "today.png"), 720, 0);
                        Console.WriteLine($"RENDER today={ok}");
                        w.Close();
                    }

                    if (mode == "logrules" || mode == "all")
                    {
                        SeedSampleLogRules();
                        var w = new LogRulesWindow();
                        bool ok = Save(w, Path.Combine(spot, "logrules.png"), 720, 0);
                        Console.WriteLine($"RENDER logrules={ok}");
                        w.Close();
                    }

                    if (mode == "depends" || mode == "all")
                    {
                        var w = new DependsWindow();
                        bool ok = Save(w, Path.Combine(spot, "depends.png"), 820, 0);
                        Console.WriteLine($"RENDER depends={ok}");
                        w.Close();
                    }

                    if (mode == "groups" || mode == "all")
                    {
                        var w = new GroupsWindow();
                        bool ok = Save(w, Path.Combine(spot, "groups.png"), 760, 0);
                        Console.WriteLine($"RENDER groups={ok}");
                        w.Close();
                    }

                    if (mode == "palette" || mode == "all")
                    {
                        var w = new CommandPalette();
                        bool ok = Save(w, Path.Combine(spot, "palette.png"), 560, 0);
                        Console.WriteLine($"RENDER palette={ok}");
                        w.Close();
                    }

                    // v1.15：主卡五种脸各截一张。不摆道具就只能看到「先告诉我在哪」，
                    // 而这张卡的全部价值在它怎么变——只截一种等于没验。
                    // v1.20：多截一张 recap——复盘贴在主卡下方，光验内核不算数，
                    // 得看到它跟标题、按钮挤在同一张卡里是什么样。
                    // v1.22：左页（运行）单独出一张。这一页新写，内容是否挤在一起、
                    // 三句话有没有说清，只有在真窗口里看才知道。
                    if (mode == "runpage" || mode == "all")
                    {
                        SeedSampleQueue();
                        SeedSampleArchive();
                        SeedRecapToday();
                        SeedDecisions();
                        SeedHeroState("done");
                        AppState.Settings.LastRunAt = DateTime.Now.AddMinutes(-49).ToString("yyyy-MM-dd HH:mm");
                        AppState.Settings.LastRunSeconds = 49 * 60;
                        var w = new MainWindow();
                        w.UpdateLayout();
                        var show = typeof(MainWindow).GetMethod("ShowPage",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        show?.Invoke(w, new object[] { 0, false });
                        w.UpdateLayout();
                        bool ok = Save(w, Path.Combine(spot, "runpage.png"), 980, 460);
                        Console.WriteLine($"RENDER runpage={ok}");
                        w.Close();
                    }

                    if (mode == "hero" || mode == "recap" || mode == "all")
                    {
                        var states = mode == "recap"
                            ? new[] { "recap" }
                            : new[] { "first", "ready", "running", "trouble", "done" };
                        foreach (string st in states)
                        {
                            SeedSampleQueue();
                            SeedSampleArchive();
                            if (mode == "recap") SeedRecapToday();
                            SeedHeroState(st == "recap" ? "done" : st);
                            // 标题那句读 LastRunAt、复盘那句读台账 At，两边差两小时会让人以为
                            // 「跑了两轮」。截图里对齐成同一轮，看着才像真的。
                            if (st == "recap")
                            {
                                AppState.Settings.LastRunAt = DateTime.Now.AddMinutes(-49).ToString("yyyy-MM-dd HH:mm");
                                AppState.Settings.LastRunSeconds = 49 * 60;
                            }
                            var w = new MainWindow();
                            w.UpdateLayout();
                            var refresh = typeof(MainWindow).GetMethod("RefreshHomeHero",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            refresh?.Invoke(w, null);
                            w.UpdateLayout();
                            // v1.15 / L3：320 高只能看到定时卡顶部，L3 的一键默认在卡片中间，
                            // 看不到就等于没验。放宽到 460。
                            string file = st == "recap" ? "recap.png" : $"hero_{st}.png";
                            bool ok = Save(w, Path.Combine(spot, file), 980, 460);
                            Console.WriteLine($"RENDER {System.IO.Path.GetFileNameWithoutExtension(file)}={ok}");
                            w.Close();
                        }
                    }

                    // v1.16：新版提示挂在首屏主卡下面。这条只在真有新版时出现，
                    // 所以要手动把它点亮一次——不然永远截不到，等于没验版式。
                    if (mode == "update" || mode == "all")
                    {
                        SeedSampleQueue();
                        SeedHeroState("ready");
                        var w = new MainWindow();
                        w.UpdateLayout();
                        if (w.FindName("TbUpdateHint") is TextBlock hint)
                        {
                            hint.Text = "↑ 有新版本 v1.16（你现在是 1.15.0）。";
                            hint.Tag = UpdateCheck.ReleasesUrl;
                            hint.Visibility = Visibility.Visible;
                        }
                        w.UpdateLayout();
                        bool ok = Save(w, Path.Combine(spot, "update_hint.png"), 980, 340);
                        Console.WriteLine($"RENDER update_hint={ok}");
                        w.Close();
                    }

                    // v1.16：首次向导是每个新用户看到的第一个界面，从来没渲染验证过。
                    // 三步都要看——第 2 步是自动寻找的列表，第 3 步是汇总，两处都是运行时生成的。
                    if (mode == "wizard" || mode == "all")
                    {
                        SeedHeroState("ready");
                        for (int step = 1; step <= 4; step++)
                        {
                            var w = new FirstRunWindow();
                            // 步骤切换走私有方法，反射调它比自己摆一遍 Visibility 更接近真实
                            var setter = typeof(FirstRunWindow).GetField("_step",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            setter?.SetValue(w, step);
                            var update = typeof(FirstRunWindow).GetMethod("UpdateUi",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            update?.Invoke(w, null);
                            w.UpdateLayout();
                            bool ok = Save(w, Path.Combine(spot, $"wizard_{step}.png"), 620, 0);
                            Console.WriteLine($"RENDER wizard_{step}={ok}");
                            w.Close();
                        }
                    }

                    // v1.15 / L2：工具设置收成一行之后，得看两件事——
                    // 收起时那一行的摘要说得对不对，展开后原来三张卡还剩没剩。
                    // v1.21：桌面网格（2×4）。一张就够——格子本身的形态是固定的，
                    // 要看的是「三个内置 + 一个加号格」和长名字会不会把格子撑歪。
                    if (mode == "grid" || mode == "all")
                    {
                        SeedSampleQueue();
                        SeedHeroState("ready");
                        var w = new MainWindow();
                        w.UpdateLayout();
                        // 造一个名字很长的自定义工具，验证格子里的省略号
                        try
                        {
                            var extra = new ToolProfile
                            {
                                ToolId = "demo-long",
                                DisplayName = "一个名字特别特别特别长的第三方自动化工具用来试试省略号到底有没有生效",
                                ExecutablePath = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-bgi", "BetterGI.exe"),
                                LaunchArguments = "-t 1",
                                ManagedProcessName = "demo-long",
                            };
                            ProfileRegistry.Save(extra);
                            AppState.Settings.ToolOrder = new[] { "bettergi", "march7th", "ok-ww", "demo-long" };
                        }
                        catch (Exception ex) { Console.WriteLine("SEED-GRID-FAIL " + ex.Message); }

                        var render = typeof(MainWindow).GetMethod("RenderToolGrid",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        render?.Invoke(w, null);
                        w.UpdateLayout();

                        // v1.26：格子搬回启动页（中页）那张卡里——它现在就是「睡前队列」那栏，
                        // 所以截图要落在中页，而不是原来那张设置页。
                        // BringIntoView 在没 Show 过的窗口上不生效（没有滚动宿主测量），只能直接给偏移。
                        var showPage = typeof(MainWindow).GetMethod("ShowPage",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        showPage?.Invoke(w, new object[] { 1, false });
                        w.UpdateLayout();
                        if (w.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer svGrid)
                        {
                            svGrid.ScrollToVerticalOffset(0);
                            w.UpdateLayout();
                        }

                        // v1.26：格子横过来之后一格 54 高，2 列 4 行，加上上面的标题行
                        // 与下面的「一键开跑」，620 高勉强够；再矮就会把按钮裁掉。
                        bool ok = Save(w, Path.Combine(spot, "grid.png"), 980, 640);
                        Console.WriteLine($"RENDER grid={ok}");
                        w.Close();
                    }

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


                    if (mode == "tools" || mode == "all")
                    {
                        SeedSampleQueue();
                        SeedHeroState("ready");
                        var w = new MainWindow();
                        w.UpdateLayout();
                        if (w.FindName("ToolCardsHost") is FrameworkElement host)
                        {
                            host.Visibility = Visibility.Visible;
                            if (w.FindName("ChevTools") is FrameworkElement chev)
                                chev.RenderTransform = new RotateTransform(180);
                        }
                        // BetterGI 那张卡本身也是折叠的；配置组勾选区在它的展开体里。
                        if (w.FindName("Body_bettergi") is FrameworkElement bgiBody)
                            bgiBody.Visibility = Visibility.Visible;
                        // 配置组是构造窗口那一刻扫的，那时还没铺假 BGI 目录——重扫一次再刷。
                        BgiDiscovery.Scan(force: true);
                        AppState.Settings.FavoriteGroups = new[] { "收尾" };
                        var picker = typeof(MainWindow).GetMethod("RefreshGroupPicker",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        picker?.Invoke(w, null);
                        w.UpdateLayout();
                        SwitchToSettingsPage(w);
                        if (w.FindName("ScrollSettings") is System.Windows.Controls.ScrollViewer sv2)
                        {
                            // v1.15 / L4：滚到 BetterGI 那张卡本身。430 只到折叠头，
                            // 配置组勾选区在卡片下半部分，看不到就等于没验。
                            sv2.ScrollToVerticalOffset(560);
                            w.UpdateLayout();
                        }
                        bool ok = Save(w, Path.Combine(spot, "tools_open.png"), 980, 560);
                        Console.WriteLine($"RENDER tools_open={ok}");
                        w.Close();
                    }

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

                    // v1.17 / M1-B：「跑完后让 BetterGI 自己做什么」那一行。
                    // 这一行只有展开 BetterGI 卡才看得见，而且下拉里必须有真候选值，
                    // 否则截出来是个空下拉，等于没验。
                    if (mode == "completion" || mode == "all")
                    {
                        SeedSampleQueue();
                        SeedHeroState("ready");
                        var w = new MainWindow();
                        w.UpdateLayout();
                        if (w.FindName("ToolCardsHost") is FrameworkElement host3)
                            host3.Visibility = Visibility.Visible;
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
                        SwitchToSettingsPage(w);
                        if (w.FindName("ScrollSettings") is System.Windows.Controls.ScrollViewer sv3)
                        {
                            sv3.ScrollToVerticalOffset(500);
                            w.UpdateLayout();
                        }
                        bool ok = Save(w, Path.Combine(spot, "completion.png"), 980, 620);
                        Console.WriteLine($"RENDER completion={ok}");
                        w.Close();
                    }

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
                            foreach (string name in new[] { "AdvancedBody", "SystemBody", "NotifyBody" })
                            {
                                if (w.FindName(name) is FrameworkElement fe) fe.Visibility = Visibility.Visible;
                            }
                            // 主窗是 SizeToContent="Height" 且内容装在 ScrollViewer 里，
                            // 光设高度没用——得动滚动条，否则长图上下两端总有一头看不见。
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
                        // 通知卡在最底下，而模板输入框在它中段——滚到底会正好被状态条压住，
                        // 所以 notify 模式滚到「刚好看得见通知卡」的位置就停。
                        if (mode == "notify")
                        {
                            w.UpdateLayout();
                            // 用 BringIntoView 而不是手算偏移：窗口没 Show 过，TransformToAncestor
                            // 在这棵还没完全成形的视觉树上会报「指定的 Visual 不是此 Visual 的上级」。
                            if (w.FindName("NotifyCard") is FrameworkElement card)
                            {
                                card.BringIntoView();
                                w.UpdateLayout();
                            }
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
                        bool ok = Save(w, Path.Combine(spot, file), 980, 0);
                        Console.WriteLine($"RENDER {Path.GetFileNameWithoutExtension(file)}={ok}");
                        w.Close();
                    }

                    Console.WriteLine("RENDER-DIR " + spot);
                    exit = 0;
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
