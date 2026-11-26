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
    /// 周报（F8）：默认关；BuildWeekly 是纯函数，拿固定输入验固定输出；
    /// 有数据才播报、同一天不重复、七天没记录不播。
    /// </summary>
    private static int RunWeekly()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        bool defaultOff = new AppSettings().WeeklyReportEnabled == false;
        Check("周报默认关闭", defaultOff);

        // --- 纯函数：SummarizeDay ---
        var day = new DateTime(2026, 11, 5);
        var recs = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = "02:30:00", Source = "log" },
            new() { Name = "B", Start = "02:30:00", End = "04:12:00", Source = "log" },
        };
        var stat = WeeklyReportService.SummarizeDay(recs, day);
        Check("日统计/次数", stat.Runs == 2, stat.Runs.ToString());
        Check("日统计/完成数", stat.Finished == 2, stat.Finished.ToString());
        // A: 01:00→02:30 = 90 分；B: 02:30→04:12 = 102 分；合计 192 分
        Check("日统计/耗时 192 分钟", Math.Abs(stat.Minutes - 192) < 0.01, stat.Minutes.ToString("F1"));
        Check("日统计/全成功", stat.AllOk);
        Check("日统计/日期归零到日", stat.Day == day.Date, stat.Day.ToString("yyyy-MM-dd"));

        var half = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = "02:00:00", Source = "log" },
            new() { Name = "B", Start = "02:00:00", End = null, Source = "log" },
        };
        var halfStat = WeeklyReportService.SummarizeDay(half, day);
        Check("日统计/未结束不算完成", halfStat.Finished == 1 && halfStat.Runs == 2, $"{halfStat.Finished}/{halfStat.Runs}");
        Check("日统计/未结束不算全成功", !halfStat.AllOk);

        var empty = WeeklyReportService.SummarizeDay(new List<GroupRecord>(), day);
        Check("日统计/空日 Ran=false", !empty.Ran && empty.Runs == 0 && empty.Minutes == 0);

        // --- 纯函数：BuildWeekly ---
        var week = new List<DayStat>
        {
            new(new DateTime(2026, 11, 2), 1, 1, 60),
            new(new DateTime(2026, 11, 3), 0, 0, 0),
            new(new DateTime(2026, 11, 4), 3, 3, 180),
            new(new DateTime(2026, 11, 5), 0, 0, 0),
            new(new DateTime(2026, 11, 6), 0, 0, 0),
            new(new DateTime(2026, 11, 7), 0, 0, 0),
            new(new DateTime(2026, 11, 8), 0, 0, 0),
        };
        string text = WeeklyReportService.BuildWeekly(week);
        Console.WriteLine("WEEKLY text=" + text);
        Check("周报/含天数", text.Contains("7 天里跑了 2 天"), text);
        Check("周报/含总次数", text.Contains("共 4 次运行"), text);
        Check("周报/全成功措辞", text.Contains("全部成功"), text);
        Check("周报/含总耗时", text.Contains("总耗时 4小时0分"), text);
        Check("周报/含平均", text.Contains("平均每次 2小时0分"), text);
        Check("周报/含最多的一天", text.Contains("跑得最多的是周三（3 次）"), text);

        var partial = new List<DayStat>
        {
            new(new DateTime(2026, 11, 2), 2, 1, 90),
            new(new DateTime(2026, 11, 3), 1, 1, 30),
        };
        string partialText = WeeklyReportService.BuildWeekly(partial);
        Console.WriteLine("WEEKLY partial=" + partialText);
        Check("周报/有失败时报成功率", partialText.Contains("成功 2 次（66%）"), partialText);
        Check("周报/全勤时改口", partialText.Contains("每天都跑了"), partialText);
        Check("周报/并列时取较早的一天", partialText.Contains("跑得最多的是周一（2 次）"), partialText);

        // 每天各跑一次：没有「最多的一天」，改用平铺说法
        var flat = new List<DayStat>
        {
            new(new DateTime(2026, 11, 2), 1, 1, 30),
            new(new DateTime(2026, 11, 3), 1, 1, 30),
        };
        string flatText = WeeklyReportService.BuildWeekly(flat);
        Console.WriteLine("WEEKLY flat=" + flatText);
        Check("周报/每天都一次时改口", flatText.Contains("每天都只跑了一次"), flatText);

        Check("周报/空输入有兜底", WeeklyReportService.BuildWeekly(new List<DayStat>()).Contains("没有可统计"));
        Check("周报/全零不崩",
            WeeklyReportService.BuildWeekly(new List<DayStat> { new(new DateTime(2026, 11, 2), 0, 0, 0) })
                .Contains("一次都没跑"));

        // --- 端到端：Collect 读归档 + MaybeShow 只播一次 ---
        var today = DateTime.Today;
        var archive = new Dictionary<string, List<GroupRecord>>();
        for (int i = 0; i < WeeklyReportService.WindowDays; i++)
            archive[today.AddDays(-i).ToString("yyyy-MM-dd")] = new()
            {
                new GroupRecord { Name = "G", Start = "01:00:00", End = "02:00:00", Source = "log" },
            };
        SaveArchive(archive);
        GroupArchive.Load();

        var collected = WeeklyReportService.Collect();
        Check("Collect 七天", collected.Count == WeeklyReportService.WindowDays, collected.Count.ToString());
        Check("Collect 按时间升序", collected[0].Day < collected[^1].Day,
            $"{collected[0].Day:MM-dd} < {collected[^1].Day:MM-dd}");
        Check("Collect 最后一天是今天", collected[^1].Day == today.Date);
        Check("Collect 每天都有记录", collected.All(d => d.Runs == 1 && d.Finished == 1));

        AppState.Settings.WeeklyReportEnabled = true;
        AppState.Settings.WeeklyReportDate = "";
        int before = CountLog("[周报]");
        WeeklyReportService.MaybeShow();
        int after1 = CountLog("[周报]");
        string date1 = AppState.Settings.WeeklyReportDate;
        WeeklyReportService.MaybeShow();
        int after2 = CountLog("[周报]");
        Check("周报播报一次", after1 - before == 1, (after1 - before).ToString());
        Check("周报同日不重复", after2 == after1, (after2 - after1).ToString());
        Check("周报记下日期", date1 == today.ToString("yyyy-MM-dd"), date1);

        // 关掉开关：不再播
        AppState.Settings.WeeklyReportEnabled = false;
        AppState.Settings.WeeklyReportDate = "";
        int beforeOff = CountLog("[周报]");
        WeeklyReportService.MaybeShow();
        Check("关掉后不播", CountLog("[周报]") == beforeOff);

        // 清空归档：开了也不播（但日期仍记下）
        SaveArchive(new Dictionary<string, List<GroupRecord>>());
        GroupArchive.Load();
        AppState.Settings.WeeklyReportEnabled = true;
        AppState.Settings.WeeklyReportDate = "";
        int beforeEmpty = CountLog("[周报]");
        WeeklyReportService.MaybeShow();
        Check("七天全空不播", CountLog("[周报]") == beforeEmpty);
        Check("空周也记日期", AppState.Settings.WeeklyReportDate == today.ToString("yyyy-MM-dd"));

        AppState.Settings.WeeklyReportEnabled = false;
        AppState.Settings.WeeklyReportDate = "";

        Console.WriteLine("RESULT weekly fails=" + fails.Count + (fails.Count > 0 ? " [" + string.Join(", ", fails) + "]" : ""));
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// 控制 API（F6）+ 只读面板（F7）：路由判定是纯函数，直接断言；
    /// 面板 HTML 也只验结构（200 / Content-Type / 含关键字 / 转义），不要求起监听器——
    /// CI 里没有桌面会话，起 HttpListener 会因为端口与权限不稳。
    /// </summary>
    private static int RunApi()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        const string token = "0123456789abcdef0123456789abcdef";

        // --- 令牌 ---
        Check("令牌/32 位随机", ControlApi.NewToken().Length == ControlApi.TokenLength);
        Check("令牌/两次不同", ControlApi.NewToken() != ControlApi.NewToken());
        Check("令牌/合规判断", ControlApi.IsUsableToken(token));
        Check("令牌/空不合规", !ControlApi.IsUsableToken(""));
        Check("令牌/长度不对不合规", !ControlApi.IsUsableToken("0123"));
        Check("令牌/非十六进制不合规", !ControlApi.IsUsableToken("zz23456789abcdef0123456789abcdef"));
        Check("令牌/缺省时生成", ControlApi.IsUsableToken(ControlApi.EnsureToken("")));
        Check("令牌/已有则沿用", ControlApi.EnsureToken(token) == token);
        Check("令牌/不合规则换新", ControlApi.EnsureToken("bad") != "bad");

        // --- 开关关着：一律 404（不暴露 API 存在） ---
        var off = ControlApi.Plan("/api/status", token, apiEnabled: false, token);
        Check("关闭时状态也 404", off.Status == 404 && off.Action == ApiAction.None, off.Status.ToString());
        var offStart = ControlApi.Plan("/api/start", token, apiEnabled: false, token);
        Check("关闭时开跑 404", offStart.Status == 404);

        // --- 开着但令牌不对 ---
        var noToken = ControlApi.Plan("/api/status", null, true, token);
        Check("缺令牌 401", noToken.Status == 401, noToken.Status.ToString());
        var wrong = ControlApi.Plan("/api/status", "ffffffffffffffffffffffffffffffff", true, token);
        Check("错令牌 401", wrong.Status == 401, wrong.Status.ToString());
        var noExpected = ControlApi.Plan("/api/status", token, true, "");
        Check("服务端无令牌 503", noExpected.Status == 503, noExpected.Status.ToString());

        // --- 正常路由 ---
        var status = ControlApi.Plan("/api/status", token, true, token);
        Check("状态 200", status.Status == 200 && status.Action == ApiAction.None);
        Check("开跑 200", ControlApi.Plan("/api/start", token, true, token).Action == ApiAction.Start);
        Check("停止 200", ControlApi.Plan("/api/stop", token, true, token).Action == ApiAction.Stop);
        Check("单跑 200", ControlApi.Plan("/api/run", token, true, token).Action == ApiAction.Run);
        Check("末尾斜杠等价", ControlApi.Plan("/api/status/", token, true, token).Status == 200);
        Check("大小写不敏感", ControlApi.Plan("/API/STATUS", token, true, token).Status == 200);

        // --- 未知路径 ---
        Check("未知 api 路径 404", ControlApi.Plan("/api/hack", token, true, token).Status == 404);
        Check("非 api 路径 404", ControlApi.Plan("/webhook2", token, true, token).Status == 404);

        // --- tool 参数 ---
        Check("tool 参数解析", ControlApi.ToolIdFromQuery("?tool=bettergi") == "bettergi");
        Check("tool 参数无问号", ControlApi.ToolIdFromQuery("tool=bettergi") == "bettergi");
        Check("tool 参数多参数", ControlApi.ToolIdFromQuery("?x=1&tool=okww&y=2") == "okww");
        Check("tool 参数大写键", ControlApi.ToolIdFromQuery("?TOOL=bettergi") == "bettergi");
        Check("tool 参数解码", ControlApi.ToolIdFromQuery("?tool=a%20b") == "a b");
        Check("tool 参数缺失", ControlApi.ToolIdFromQuery("?x=1") == "");
        Check("tool 参数空串查询", ControlApi.ToolIdFromQuery(null) == "");

        // --- 状态 JSON ---
        string json = ControlApi.StatusJson(true, "bettergi", 2, 1, 4, "组开始：A");
        Check("状态 JSON 含 running", json.Contains("\"running\": true"), json.Replace("\n", " "));
        Check("状态 JSON 含工具", json.Contains("\"currentTool\": \"bettergi\""));
        Check("状态 JSON 含进度", json.Contains("\"doneTools\": 2") && json.Contains("\"totalTools\": 4"));
        Check("状态 JSON 含事件", json.Contains("组开始：A"));
        Check("状态 JSON 中文不转义", !json.Contains("\\u"));

        string actionJson = ControlApi.ActionJson(true, "start", "已请求开跑");
        Check("动作 JSON 含 ok", actionJson.Contains("\"ok\": true"));
        Check("动作 JSON 含 action", actionJson.Contains("\"action\": \"start\""));
        Check("错误体也是 JSON", ControlApi.Fail("令牌不正确").Contains("\"ok\": false"));

        // --- 面板 HTML（F7） ---
        string html = ControlApi.PanelHtml("一条龙管家", true, "bettergi", 3, 1, 5, "组结束：A", 5);
        Check("面板有 doctype", html.StartsWith("<!DOCTYPE html>"));
        Check("面板有 charset", html.Contains("charset=\"utf-8\""));
        Check("面板有自动刷新", html.Contains("http-equiv=\"refresh\""));
        Check("面板显示状态", html.Contains("正在跑"));
        Check("面板显示进度", html.Contains("3 / 5"));
        Check("面板显示事件", html.Contains("组结束：A"));
        Check("面板无控制按钮", !html.Contains("<button") && !html.Contains("<form"));
        Check("面板无令牌字段", !html.Contains("ControlApiToken") && !html.Contains("X-Butler-Token"));
        Check("面板刷新间隔有下限", ControlApi.PanelHtml("t", false, "", 0, 0, 0, "", 1).Contains("content=\"2\""));
        Check("面板刷新间隔有上限", ControlApi.PanelHtml("t", false, "", 0, 0, 0, "", 9999).Contains("content=\"60\""));
        Check("面板空闲文案", ControlApi.PanelHtml("t", false, "", 0, 0, 0, "", 5).Contains("空闲"));

        string evil = ControlApi.PanelHtml("<script>alert(1)</script>", false, "<img onerror=x>", 0, 0, 0, "", 5);
        Check("面板转义尖括号", !evil.Contains("<script>alert(1)</script>"));
        Check("面板转义引号", ControlApi.Escape("\"x\"").Contains("&quot;"));
        Check("面板转义单引号", ControlApi.Escape("'x'").Contains("&#39;"));
        Check("面板转义与号", ControlApi.Escape("a&b") == "a&amp;b");

        // --- 全库唯一监听器（WebhookHub 里写下的铁律） ---
        // 用 Dragon Butler 主程序集定位仓库根：...\Dragon Butler\bin\Release\net8.0-windows\ → 上溯 5 级
        string root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(QueueService).Assembly.Location)!,
            "..", "..", "..", "..", ".."));
        string srcDir = Path.Combine(root, "Dragon Butler");
        int listenerFiles = 0;
        if (Directory.Exists(srcDir))
        {
            foreach (var f in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                try
                {
                    // 同一文件里两处是正常的：wildcard 失败会重建一个回退到 127.0.0.1，生命周期仍只有一处
                    if (File.ReadLines(f).Any(line => line.Contains("new HttpListener(", StringComparison.Ordinal)))
                        listenerFiles++;
                }
                catch { }
            }
        }
        else
        {
            Console.WriteLine("  SKIP 找不到源码目录：" + srcDir);
        }
        Check("全库只有一个文件创建 HttpListener", listenerFiles == 1, "找到 " + listenerFiles + " 个文件");

        // ===== v1.43 / 任务四：报文分类与应答状态码 =====
        //
        // 这一段是任务书四的核心验收：非法报文不能拿到和成功事件一样的 200。
        // Parse 被特意做成静态纯函数，就是为了让这组断言不必真起 HttpListener
        // （CI 里没有桌面会话，起监听会因为端口和权限不稳）。
        var okEvent = WebhookListener.Parse("{\"event\":\"dragon.end\",\"name\":\"一条龙\"}");
        Check("报文/合法事件 200", okEvent.Status == 200 && okEvent.Accepted, okEvent.Status.ToString());
        Check("报文/合法事件带出事件", okEvent.Event != null && okEvent.Event!.Kind == SentinelEventKind.DragonEnded);
        Check("报文/合法事件带出名字", okEvent.Event != null && okEvent.Event!.Payload == "一条龙");

        var badJson = WebhookListener.Parse("{not json");
        Check("报文/非法 JSON 400", badJson.Status == 400 && !badJson.Accepted, badJson.Status.ToString());
        Check("报文/非法 JSON 不带事件", badJson.Event == null);

        Check("报文/空报体 400", WebhookListener.Parse("").Status == 400);
        Check("报文/null 报体 400", WebhookListener.Parse(null).Status == 400);
        Check("报文/JSON 数组 400", WebhookListener.Parse("[1,2,3]").Status == 400);
        Check("报文/JSON 字符串 400", WebhookListener.Parse("\"dragon.end\"").Status == 400);
        Check("报文/缺 event 400", WebhookListener.Parse("{\"name\":\"x\"}").Status == 400);
        Check("报文/空 event 400", WebhookListener.Parse("{\"event\":\"\"}").Status == 400);

        // 未知事件必须被明确拒绝，不能映射成 DragonEnded 伪装成功
        var unknown = WebhookListener.Parse("{\"event\":\"dragon.exploded\"}");
        Check("报文/未知事件 400", unknown.Status == 400 && !unknown.Accepted, unknown.Status.ToString());
        Check("报文/未知事件不带事件", unknown.Event == null);
        // 回应里不能把请求方输入整段吐回去（限长）
        Check("报文/拒绝正文限长", unknown.Body.Length <= 80, unknown.Body.Length.ToString());

        // 六个已知事件都得认得
        foreach (var pair in new (string Ev, SentinelEventKind Kind)[]
                 {
                     ("dragon.start", SentinelEventKind.DragonStarted),
                     ("dragon.end", SentinelEventKind.DragonEnded),
                     ("group.start", SentinelEventKind.GroupStarted),
                     ("group.end", SentinelEventKind.GroupEnded),
                     ("task.error", SentinelEventKind.GroupError),
                     ("task.cancel", SentinelEventKind.GroupError),
                 })
        {
            var r = WebhookListener.Parse("{\"event\":\"" + pair.Ev + "\"}");
            Check("报文/认得 " + pair.Ev, r.Status == 200 && r.Event != null && r.Event!.Kind == pair.Kind);
        }

        // 坏报文不会影响下一次好报文（Parse 无状态，这里是显式确认）
        for (int i = 0; i < 5; i++) WebhookListener.Parse("{bad");
        Check("报文/坏报文之后仍能收", WebhookListener.Parse("{\"event\":\"dragon.end\"}").Status == 200);

        // 绑定结果自身的口径
        var notStarted = WebhookBindResult.NotStarted(18235, "端口被占用");
        Check("绑定/未启动的口径", !notStarted.Started && !notStarted.Wildcard && notStarted.Error.Length > 0);
        Console.WriteLine("RESULT api fails=" + fails.Count + (fails.Count > 0 ? " [" + string.Join(", ", fails) + "]" : ""));
        return fails.Count == 0 ? 0 : 1;
    }

}
