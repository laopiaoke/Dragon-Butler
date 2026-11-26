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
    /// v1.44 / 修复 #4 #5 #6：三份修复任务书的可测面。
    ///
    /// <para>
    /// 这里都不真睡、不真关：反悔窗口的「关机」分支会真发 shutdown，所以只断言
    /// 「没开窗时取消返回 false」这种不产生副作用的路径，以及纯函数与常量。
    /// 倒计时本体在渲染探针里靠截图与源码断言覆盖。
    /// </para>
    /// </summary>
    private static int RunWrapUp()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // #4：没开窗的时候三条入口都必须什么都不做。
        Check("倒计时/默认没开窗", !WrapUpCountdown.IsActive);
        Check("倒计时/没开窗时取消返回 false", !WrapUpCountdown.Cancel());
        Check("倒计时/没开窗时立即执行返回 false", !WrapUpCountdown.RunNow());
        Check("倒计时/没开窗时剩余秒数为 0", WrapUpCountdown.RemainingSeconds == 0);
        Check("倒计时/没开窗时没有动作类型", WrapUpCountdown.ActiveKind == null);

        // 60 秒是任务书钉死的，不做配置项；这里守住它别被人顺手改成 30。
        Check("倒计时/窗口就是 60 秒", WrapUpCountdown.WindowSeconds == 60);

        Check("倒计时/名字说人话", WrapUpCountdown.NameOf(WrapUpKind.Sleep) == "睡眠"
            && WrapUpCountdown.NameOf(WrapUpKind.Shutdown) == "关机");

        Check("倒计时/没有待收尾时允许开跑", WrapUpCountdown.TryCancelForNewRun());
        string queueCs = ReadRepoSources("Dragon Butler/QueueService.cs");
        Check("倒计时/整队、接着跑和单跑都走撤销门槛",
            CountOf(queueCs, "if (!TryBeginRun()) return false;") == 3 &&
            queueCs.IndexOf("!WrapUpCountdown.TryCancelForNewRun()", StringComparison.Ordinal) >= 0);

        var lan = System.Net.IPAddress.Parse("192.0.2.10");
        var loopback = System.Net.IPAddress.Loopback;
        Check("局域网/只读首页可访问", WebhookListener.CanAccessPath(lan, "", "GET")
            && !WebhookListener.CanAccessPath(lan, "", "POST"));
        Check("局域网/拒绝 Webhook、控制 API 和 MCP", !WebhookListener.CanAccessPath(lan, "/webhook", "POST")
            && !WebhookListener.CanAccessPath(lan, "/api/start", "GET")
            && !WebhookListener.CanAccessPath(lan, "/mcp", "POST"));
        Check("回环/保留原有接口", WebhookListener.CanAccessPath(loopback, "/webhook", "POST")
            && WebhookListener.CanAccessPath(loopback, "/api/start", "GET")
            && WebhookListener.CanAccessPath(loopback.MapToIPv6(), "/mcp", "POST"));

        // #5：端口校验的每一条挡板。
        Check("端口/正常值放行", WebhookHub.ValidatePortText("18235") == null);
        Check("端口/上下边界放行", WebhookHub.ValidatePortText("1024") == null && WebhookHub.ValidatePortText("65535") == null);
        Check("端口/非数字拦住", WebhookHub.ValidatePortText("abc") != null);
        Check("端口/空值拦住", WebhookHub.ValidatePortText("") != null && WebhookHub.ValidatePortText(null) != null);
        Check("端口/低于 1024 拦住", WebhookHub.ValidatePortText("80") != null);
        Check("端口/高于 65535 拦住", WebhookHub.ValidatePortText("70000") != null);
        Check("端口/前后空格能忍", WebhookHub.ValidatePortText("  18235  ") == null);

        // #6：重试节奏收敛成单点常量之后，数值本身不许动（任务书 D21）。
        Check("重试/节奏还是 1-3-7 秒",
            NotifyService.PushRetryDelaysMs.Length == 3
            && NotifyService.PushRetryDelaysMs[0] == 1000
            && NotifyService.PushRetryDelaysMs[1] == 3000
            && NotifyService.PushRetryDelaysMs[2] == 7000);

        // 三次用尽必须留痕：常量之外还有 DecisionLog 那条，靠源码断言守住它不被删。
        string notifyCs = ReadRepoSources("Dragon Butler/NotifyService.cs");
        Check("重试/耗尽后进今日留痕", notifyCs.Contains("推送没送到（") && notifyCs.Contains("DecisionLog.Add"));
        Check("重试/耗尽后仍进消息中心", notifyCs.Contains("NoticeCenter.AddError"));
        Check("重试/循环读常量不写死数字", notifyCs.Contains("PushRetryDelaysMs[attempt]"));

        // 界面侧：倒计时条得真在 XAML 里，而且两个按钮都接着事件。
        string mainXamlForWrap = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("倒计时/界面上有那条提示", mainXamlForWrap.Contains("x:Name=\"WrapUpBar\""));
        Check("倒计时/有剩余秒数那一格", mainXamlForWrap.Contains("x:Name=\"TbWrapUpLeft\""));
        Check("倒计时/有取消与立即执行",
            mainXamlForWrap.Contains("BtnWrapUpCancel_Click") && mainXamlForWrap.Contains("BtnWrapUpNow_Click"));
        string mainCsForWrap = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        // 节流在上游文件里：200ms 一跳，但只有秒数变了才通知界面。
        string wrapCs = ReadRepoSources("Dragon Butler/WrapUpCountdown.cs");
        Check("倒计时/改秒数才刷界面",
            wrapCs.Contains("_lastNotifiedSecond") && wrapCs.Contains("if (left != _lastNotifiedSecond)"));
        Check("倒计时/显隐只有一处说了算",
            mainCsForWrap.Contains("RefreshWrapUpBar()") && !mainCsForWrap.Contains("BtnQueueCancelShutdown"));
        // 「取消关机」老按钮已经撤掉：它只管关机、词还对不上睡眠，留着就是第二个入口。
        Check("倒计时/没有第二个取消入口", !mainXamlForWrap.Contains("BtnQueueCancelShutdown"));
        Check("端口/输入弹窗走统一校验", mainCsForWrap.Contains("Validate = WebhookHub.ValidatePortText"));
        Console.WriteLine("RESULT wrapup fails=" + fails.Count);
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.45 / M12：六项行为修缮的验收。
    ///
    /// <para>
    /// 这里量的大多是「接线」而不是「算法」——M12 六项栽的都是同一类跟头：
    /// 代码写了，但没人调用；或者调用点写了两份，慢慢长歪。所以断言一半是
    /// 纯函数行为，一半是源码里的接线顺序与唯一性。
    /// </para>
    /// </summary>
    private static int RunM12Fix()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- M12-1：确认框必须挡在写盘之前 ----
        string themeCs = ReadRepoSources("Dragon Butler/CustomThemeWindow.xaml.cs");
        int askAt = themeCs.IndexOf("AppDialog.Show(", StringComparison.Ordinal);
        int writeAt = themeCs.IndexOf("s.Theme = \"System\";", StringComparison.Ordinal);
        Check("确认/先问再改", askAt >= 0 && writeAt > askAt,
            askAt < 0 ? "没找到确认框" : writeAt < 0 ? "没找到重置赋值" : $"问@{askAt} 写@{writeAt}");
        Check("确认/取消就什么都不做", themeCs.Contains("if (!resetOk) return;"));
        Check("确认/正文列全六项",
            themeCs.Contains("· 主题") && themeCs.Contains("· 背景类型") && themeCs.Contains("· 背景色")
            && themeCs.Contains("· 背景图") && themeCs.Contains("· 遮罩比例") && themeCs.Contains("· 配色方案"));
        Check("确认/说了不可撤销", themeCs.Contains("这个操作不可撤销"));
        // 「重置」这个方法体里只许写一次盘，而且必须在确认之后。
        // 用方法名到下一个方法声明之间切段，别让旁边那个「应用」按钮的写盘混进来。
        int resetAt = themeCs.IndexOf("private void BtnReset_Click", StringComparison.Ordinal);
        int resetEnd = resetAt < 0 ? -1 : themeCs.IndexOf("private void ", resetAt + 10, StringComparison.Ordinal);
        string resetBody = resetAt >= 0 && resetEnd > resetAt ? themeCs.Substring(resetAt, resetEnd - resetAt) : "";
        Check("确认/重置里只写一次盘", CountOf(resetBody, "AppState.SaveSettings();") == 1,
            "出现 " + CountOf(resetBody, "AppState.SaveSettings();") + " 次");
        Check("确认/重置里也是先问后写",
            resetBody.IndexOf("AppDialog.Show(", StringComparison.Ordinal) >= 0
            && resetBody.IndexOf("AppDialog.Show(", StringComparison.Ordinal) < resetBody.IndexOf("AppState.SaveSettings();", StringComparison.Ordinal));

        // ---- M12-2：重发走既有链路，不平行实现 ----
        string notifyCs = ReadRepoSources("Dragon Butler/NotifyService.cs");
        Check("重发/有共用的发送内核", notifyCs.Contains("SendCoreAsync("));
        // 重试循环只许有一份。同一份循环里日志和 Delay 各引用一次常量是正常的，
        // 要守的是「循环本身没被抄到两个方法里」——抄了就是两套节奏。
        Check("重发/重试循环只有一份", CountOf(notifyCs, "for (int attempt = 0; attempt <= PushRetryDelaysMs.Length; attempt++)") == 1,
            "出现 " + CountOf(notifyCs, "for (int attempt = 0; attempt <= PushRetryDelaysMs.Length; attempt++)") + " 次");
        int resendAt = notifyCs.IndexOf("public static async Task<(bool ok, string err)> ResendAsync", StringComparison.Ordinal);
        int resendCore = resendAt < 0 ? -1 : notifyCs.IndexOf("SendCoreAsync(", resendAt, StringComparison.Ordinal);
        Check("重发/重发也走这个内核", resendAt >= 0 && resendCore > resendAt);
        Check("重发/首次发送也走这个内核",
            notifyCs.Contains("await SendCoreAsync(") && CountOf(notifyCs, "await SendCoreAsync(") == 2,
            "出现 " + CountOf(notifyCs, "await SendCoreAsync(") + " 次（首次 + 重发）");
        // 载荷只在内存、每渠道一份：用 PushChannel 作键的字典就是这条的落地形态。
        Check("重发/载荷每渠道一份", notifyCs.Contains("Dictionary<PushChannel, ResendPayload>"));
        Check("重发/载荷不落盘", !notifyCs.Contains("PendingResend.*Write") && !notifyCs.Contains("Serialize(PendingResend"));
        Check("重发/重入有闸门", notifyCs.Contains("Resending") && notifyCs.Contains("这条正在重发"));

        string noticeWin = ReadRepoSources("Dragon Butler/NoticeWindow.xaml.cs");
        Check("重发/通知窗口有按钮", noticeWin.Contains("HasResendPayload") && noticeWin.Contains("\"重发\""));
        Check("重发/重发中禁用", noticeWin.Contains("resend.IsEnabled = false"));
        Check("重发/没载荷时说清原因", noticeWin.Contains("ResendHintFor"));
        Check("重发/失败文案指向重发", notifyCs.Contains("在这条上点「重发」就行"));
        // 重启后的老实说明：别让人对着一个没有按钮的条目等。
        Check("重发/重启后如实说明", NotifyService.ResendHintFor(new Notice { At = DateTime.Now.AddDays(-1) }) == "重启过，这条只能留档了");
        Check("重发/本轮失败可重发", NotifyService.ResendHintFor(new Notice { At = DateTime.Now.AddSeconds(5) }).Contains("只有刚失败的那条"));
        Check("重发/没有载荷就别假装能发", !NotifyService.HasResendPayload(new Notice { At = DateTime.Now }));
        Check("重发/初始没有待重发", NotifyService.PendingResendCount == 0);

        // ---- M12-3：文案里不许再死写阈值 ----
        string queueCs = ReadRepoSources("Dragon Butler/QueueService.cs");
        Check("挂起/两处都改插值", CountOf(queueCs, "{SentinelService.StallThreshold.TotalMinutes:F0} 分钟无任何活动") == 2,
            "出现 " + CountOf(queueCs, "{SentinelService.StallThreshold.TotalMinutes:F0} 分钟无任何活动") + " 次");
        Check("挂起/不再有死写的 3 分钟", !queueCs.Contains("疑似挂起（3 分钟"));
        string mainCsM12 = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("挂起/设置页也跟着变", mainCsM12.Contains("RefreshStallHint()") && mainCsM12.Contains("SentinelService.StallThreshold.TotalMinutes"));

        // ---- M12-4：删之前先进回收站，进不去要说出来 ----
        string bundleCs = ReadRepoSources("Dragon Butler/IncidentBundle.cs");
        Check("现场包/先进回收站", bundleCs.Contains("RecycleOption.SendToRecycleBin"));
        Check("现场包/回收站失败才直删", bundleCs.Contains("TryRecycle(") && bundleCs.Contains("Directory.Delete(d, true)"));
        Check("现场包/回退留痕", bundleCs.Contains("没能放进回收站"));
        string mainXamlM12 = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("现场包/界面上写着规则", mainXamlM12.Contains("x:Name=\"TbBundleKeepHint\""));
        Check("现场包/规则数字来自设置", mainCsM12.Contains("TbBundleKeepHint.Text") && mainCsM12.Contains("s.IncidentBundleKeep"));

        // ---- M12-5：复制失败能就地重试，且只有一条复制路径 ----
        string todayXaml = ReadRepoSources("Dragon Butler/TodayWindow.xaml");
        string todayCs = ReadRepoSources("Dragon Butler/TodayWindow.xaml.cs");
        Check("剪贴板/有重试按钮", todayXaml.Contains("x:Name=\"BtnCopyRetry\""));
        Check("剪贴板/两个入口同一个函数",
            todayCs.Contains("private void TryCopyReport()")
            && CountOf(todayCs, "TryCopyReport();") >= 2,
            "调用 " + CountOf(todayCs, "TryCopyReport();") + " 次");
        Check("剪贴板/剪贴板只碰一次", CountOf(todayCs, "Clipboard.SetText(") == 1,
            "出现 " + CountOf(todayCs, "Clipboard.SetText(") + " 次");
        Check("剪贴板/重试中不重复进", todayCs.Contains("_copying") && todayCs.Contains("if (_copying) return;"));
        Check("剪贴板/显隐只有一处说了算", todayCs.Contains("private void ShowCopyFailure("));

        // ---- M12-6：替身路径定死成绝对路径 ----
        string probeCs = ReadRepoSources("tests/CoordinatorProbe/Program.cs");
        Check("替身路径/有归一函数", probeCs.Contains("private static string ResolveStub("));
        Check("替身路径/已经是绝对的就不再动", probeCs.Contains("Path.IsPathRooted(path)"));
        Check("替身路径/入口走归一", probeCs.Contains("ResolveStub(rawStub)") && probeCs.Contains("string rawStub"));

        Console.WriteLine("RESULT m12fix fails=" + fails.Count);
        return fails.Count == 0 ? 0 : 1;
    }

}
