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
    /// v1.19：开跑前体检。
    /// 最要紧的三条不肯让步：只有真挡路的才 Block（其余最多 Warn/Notice）、
    /// 重的必须排在前面（用户先看到「跑不了」而不是「没开定时」）、
    /// 同一份输入必须给同一份输出（纯函数，不看时钟不碰磁盘）。
    /// </summary>
    private static int RunPreflight()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 一切正常的那份输入，后面各用例只改一处
        PreflightInput Healthy() => new(
            BgiConfigured: true,
            BgiExists: true,
            BgiRunning: false,
            EnabledTools: 2,
            ReadyTools: 2,
            GroupCount: 3,
            DragonConfigName: "日常",
            DragonConfigs: new[] { "日常", "深渊" },
            NotifyMode: "Toast",
            NotifyEndpoint: "",
            MissingPresetGroups: Array.Empty<string>(),
            PresetMissing: false,
            LastRunAt: "昨天 23:10",
            LastRunOk: true,
            LastRunReason: null,
            ScheduleEnabled: true,
            Writable: true);

        PreflightItem Of(PreflightReport r, string key) => r.Items.FirstOrDefault(i => i.Key == key)!;

        // ---- 1) 健康输入：能跑、没有 Block/Warn ----
        var ok = Preflight.Build(Healthy());
        Check("健康/能跑", ok.CanRun);
        Check("健康/没有 Block", ok.Worst != PreflightLevel.Block);
        Check("健康/没有 Warn", ok.Worst < PreflightLevel.Warn, ok.Worst.ToString());
        Check("健康/标题说可以开跑", ok.Headline.Contains("可以开跑"), ok.Headline);
        Check("健康/十项都在", ok.Items.Count == 10, ok.Items.Count.ToString());

        // ---- 2) 每条 Block 都能挡住 ----
        var noBgi = Preflight.Build(Healthy() with { BgiConfigured = false, BgiExists = false });
        Check("缺路径/挡路", !noBgi.CanRun);
        Check("缺路径/挂到 bgi.missing", noBgi.Items.Any(i => i.Key == "bgi.missing" && i.Level == PreflightLevel.Block));
        Check("缺路径/有修法", Of(noBgi, "bgi.missing").FixCommand == "pick.bettergi");
        Check("缺路径/标题说差几件", noBgi.Headline.Contains("件才能开跑"), noBgi.Headline);

        var gone = Preflight.Build(Healthy() with { BgiExists = false });
        Check("路径失效/挡路", !gone.CanRun);
        Check("路径失效/文案提到失效", Of(gone, "bgi.gone").Title.Contains("失效"), Of(gone, "bgi.gone").Title);

        var noTool = Preflight.Build(Healthy() with { EnabledTools = 0, ReadyTools = 0 });
        Check("没勾工具/挡路", !noTool.CanRun);
        Check("没勾工具/修法去工具卡", Of(noTool, "tools.none").FixCommand == "view.tools");

        var notReady = Preflight.Build(Healthy() with { EnabledTools = 2, ReadyTools = 0 });
        Check("工具全没路径/挡路", !notReady.CanRun);
        Check("工具全没路径/挂到 notready", notReady.Items.Any(i => i.Key == "tools.notready"));

        var ro = Preflight.Build(Healthy() with { Writable = false });
        Check("目录只读/挡路", !ro.CanRun);
        Check("目录只读/没有一键修法", Of(ro, "disk.readonly").FixText.Length == 0);

        // ---- 3) 该只提示的绝不能挡路（半夜弹窗拦人比不检查更烦） ----
        var noGroup = Preflight.Build(Healthy() with { GroupCount = 0 });
        Check("没配置组/不挡路", noGroup.CanRun);
        Check("没配置组/是 Warn", Of(noGroup, "groups.none").Level == PreflightLevel.Warn);
        Check("没配置组/没有修法", Of(noGroup, "groups.none").FixText.Length == 0);
        Check("没配置组/标题说可以跑", noGroup.Headline.Contains("可以跑"), noGroup.Headline);

        var noSchedule = Preflight.Build(Healthy() with { ScheduleEnabled = false });
        Check("没开定时/不挡路", noSchedule.CanRun);
        Check("没开定时/只算 Notice", Of(noSchedule, "schedule.off").Level == PreflightLevel.Notice);
        Check("没开定时/不抬高 Worst", noSchedule.Worst == PreflightLevel.Notice, noSchedule.Worst.ToString());

        var noNotify = Preflight.Build(Healthy() with { NotifyMode = "Feishu", NotifyEndpoint = "" });
        Check("远程没填地址/不挡路", noNotify.CanRun);
        Check("远程没填地址/是 Warn", Of(noNotify, "notify.none").Level == PreflightLevel.Warn);
        Check("远程没填地址/修法去通知", Of(noNotify, "notify.none").FixCommand == "view.notify");

        // ---- 4) 边界：留空的一条龙配置名是合法的，不该报错 ----
        var emptyDragon = Preflight.Build(Healthy() with { DragonConfigName = "" });
        Check("配置名留空/不报警", Of(emptyDragon, "dragon.empty").Level == PreflightLevel.Ok);
        Check("配置名留空/能跑", emptyDragon.CanRun);

        var missingDragon = Preflight.Build(Healthy() with { DragonConfigName = "不存在的" });
        Check("配置名找不到/是 Warn", Of(missingDragon, "dragon.missing").Level == PreflightLevel.Warn);
        Check("配置名找不到/能跑", missingDragon.CanRun);
        Check("配置名找不到/文案带名字", Of(missingDragon, "dragon.missing").Title.Contains("不存在的"));

        var caseDragon = Preflight.Build(Healthy() with { DragonConfigName = "日常" });
        Check("配置名/大小写不敏", Of(caseDragon, "dragon.ok").Level == PreflightLevel.Ok);

        // ---- 5) 组合 ----
        var presetGone = Preflight.Build(Healthy() with { PresetMissing = true });
        Check("组合没了/是 Warn", Of(presetGone, "preset.missing").Level == PreflightLevel.Warn);
        Check("组合没了/能跑", presetGone.CanRun);
        Check("组合没了/修法去组合", Of(presetGone, "preset.missing").FixCommand == "view.presets");

        var presetBadGroup = Preflight.Build(Healthy() with { MissingPresetGroups = new[] { "活动", "深渊" } });
        Check("组合有失效组/是 Warn", Of(presetBadGroup, "preset.groups").Level == PreflightLevel.Warn);
        Check("组合有失效组/列出来", Of(presetBadGroup, "preset.groups").Detail.Contains("活动"));
        var manyMissing = Preflight.Build(Healthy() with
        {
            MissingPresetGroups = new[] { "a", "b", "c", "d", "e", "f", "g" },
        });
        Check("组合失效组/最多列 5 个", !Of(manyMissing, "preset.groups").Detail.Contains("f、"));

        // ---- 6) 排序：重的在前面 ----
        var messy = Preflight.Build(Healthy() with
        {
            BgiConfigured = false,
            BgiExists = false,
            EnabledTools = 0,
            ReadyTools = 0,
            ScheduleEnabled = false,
            GroupCount = 0,
        });
        Check("排序/第一条是 Block", messy.Items[0].Level == PreflightLevel.Block, messy.Items[0].Key);
        Check("排序/最后一条不是 Block", messy.Items[^1].Level != PreflightLevel.Block, messy.Items[^1].Key);
        Check("排序/Rank 单调不增（重的在前）",
            messy.Items.Select((it, idx) => idx == 0 || Preflight.Rank(messy.Items[idx - 1].Level) >= Preflight.Rank(it.Level)).All(x => x));
        Check("排序/Block 都排在 Notice 前",
            messy.Items.Select(i => i.Level).ToList().IndexOf(PreflightLevel.Block)
            < messy.Items.Select(i => i.Level).ToList().LastIndexOf(PreflightLevel.Notice));

        Check("Rank/Block 最大", Preflight.Rank(PreflightLevel.Block) == 3);
        Check("Rank/Ok 最小", Preflight.Rank(PreflightLevel.Ok) == 0);

        // ---- 7) 通知渠道判定 ----
        Check("通知/本地渠道算配好", Preflight.IsLocalChannel("Toast") && Preflight.IsLocalChannel(""));
        Check("通知/远程渠道不算本地", !Preflight.IsLocalChannel("Feishu"));
        Check("通知/本地永远 ready", Preflight.NotifyReady("Toast", ""));
        Check("通知/远程空地址不 ready", !Preflight.NotifyReady("Feishu", ""));
        Check("通知/远程有地址 ready", Preflight.NotifyReady("Feishu", "https://x"));
        Check("通知/两段都要填", !Preflight.NotifyReady("Telegram", "token|") && !Preflight.NotifyReady("Telegram", "|chat"));
        Check("通知/两段都填了 ready", Preflight.NotifyReady("Telegram", "token|chat"));

        // ---- 7b) v1.25：默认不推送 ----
        Check("不推送/本地渠道（不报没配）", Preflight.IsLocalChannel("Off"));
        Check("不推送/永远 ready", Preflight.NotifyReady("Off", ""));
        var offReport = Preflight.Build(Healthy() with { NotifyMode = "Off" });
        Check("不推送/挂到 notify.off", Of(offReport, "notify.off").Level == PreflightLevel.Notice,
            Of(offReport, "notify.off").Level.ToString());
        Check("不推送/不挡路", offReport.CanRun);
        Check("不推送/不抬高 Worst", offReport.Worst <= PreflightLevel.Warn, offReport.Worst.ToString());
        Check("不推送/给一条修法", Of(offReport, "notify.off").FixCommand == "view.notify");
        Check("不推送/默认设置就是它", AppState.Settings.NotifyMode == "Off", AppState.Settings.NotifyMode);
        Check("不推送/解析成 Off", NotifyService.ParseChannel("Off") == PushChannel.Off);
        Check("不推送/小写也认", NotifyService.ParseChannel("off") == PushChannel.Off);
        Check("不推送/缺字段兜底成 Off", NotifyService.ParseChannel(null) == PushChannel.Off);
        Check("不推送/Toast 仍是 Toast", NotifyService.ParseChannel("Toast") == PushChannel.Toast);
        Check("不推送/名字是中文", NotifyService.NameOf(PushChannel.Off) == "不推送", NotifyService.NameOf(PushChannel.Off));

        var s = new AppSettings
        {
            NotifyMode = "Telegram",
            NotifyTelegramToken = "tok",
            NotifyTelegramChat = "",
        };
        Check("通知/端点拼 token|chat", Preflight.NotifyEndpointOf("Telegram", s) == "tok|");
        Check("通知/邮件拼 host|to", Preflight.NotifyEndpointOf("Email", new AppSettings { NotifyEmailHost = "h", NotifyEmailTo = "t" }) == "h|t");
        Check("通知/null 设置返回空", Preflight.NotifyEndpointOf("Feishu", null) == "");
        Check("通知/未知渠道返回空", Preflight.NotifyEndpointOf("Whatever", s) == "");

        // ---- 8) 纯函数：同一输入两次结果一样 ----
        var a1 = Preflight.Build(Healthy());
        var a2 = Preflight.Build(Healthy());
        Check("纯函数/两次一致", a1.Headline == a2.Headline && a1.Items.Count == a2.Items.Count);

        // ---- 9) 接线：体检真的挂在开跑前 ----
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("接线/开跑前调体检", mainCs.Contains("var report = SafePreflight();", StringComparison.Ordinal));
        Check("接线/只有 CanRun 为假才拦", mainCs.Contains("if (report != null && !report.CanRun)", StringComparison.Ordinal));
        Check("接线/拦截后开体检窗", mainCs.Contains("OpenPreflight()", StringComparison.Ordinal));
        Check("接线/有取事实的方法", mainCs.Contains("CollectPreflight()", StringComparison.Ordinal));
        Check("接线/出错不抛", mainCs.Contains("private static PreflightReport? SafePreflight()", StringComparison.Ordinal));
        Check("接线/系统卡有入口", mainXaml.Contains("x:Name=\"BtnPreflight\"", StringComparison.Ordinal));
        Check("接线/入口挂到点击", mainXaml.Contains("Click=\"BtnPreflight_Click\"", StringComparison.Ordinal));
        Check("接线/命令面板能搜到", mainCs.Contains("case \"view.preflight\": OpenPreflight(); break;", StringComparison.Ordinal));
        Check("接线/命令已注册", CommandRegistry.All.Any(c => c.Id == "view.preflight"));
        Check("接线/体检窗只读（不含 Save）",
            !ReadRepoSources("Dragon Butler/PreflightWindow.xaml.cs").Contains("SaveSettings", StringComparison.Ordinal));

        Console.WriteLine($"RESULT preflight fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
