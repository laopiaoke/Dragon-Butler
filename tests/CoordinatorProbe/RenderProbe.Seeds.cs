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

namespace CoordinatorProbe;

// v1.45.3 / #15：截图用的样例数据摆盘，从 RenderProbe.cs 抽出来，单文件 <= 20 KB。
// 只改内存、不写盘的那些 Seed*，函数体一字未改。
internal static partial class RenderProbe
{

    internal static void SeedSampleQueue()
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
            // v1.42 / M9-2（D16）：勾选只有 QueueEntry.Groups 一份，不再铺全局那份。
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
    internal static void SeedSampleBgiDir()
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

    internal static void WriteIfMissing(string path, string content)
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
    internal static void SeedRunningState()
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

    internal static void SetStatic(Type type, string field, object value)
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
    internal static void SeedHeroState(string state)
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

    internal static void SetBacking(Type type, string property, object value)
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
    internal static void SeedDecisions()
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

    internal static void SeedRecapToday()
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
    internal static void SeedSampleJournal()
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
    internal static void SeedJournalFor(DateTime day, int failed)
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

    internal static void SeedSampleArchive()
    {
        try
        {
            GroupArchive.PathOverride = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-group-archive.json");
            if (File.Exists(GroupArchive.PathOverride)) File.Delete(GroupArchive.PathOverride);
            GroupArchive.Load();

            DateTime today = DateTime.Today;
            var rand = new Random(20261108);
            // v1.42.1 / M9-4：CalendarModel 随日历窗口一起删了，这里直接用周数。
            for (int back = 0; back < 53 * 7; back++)
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
    internal static void SeedSamplePresets()
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
    internal static void SeedSampleLogRules()
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
}
