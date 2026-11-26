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
    /// v1.20 / 奇思妙想 #7：跑完收拾一下。
    ///
    /// 这块真正要挡的是「默认全关」和「有失败就不关游戏」两件事：
    /// 前者是不可逆动作的分寸，后者是别把现场也一起关了。
    /// </summary>
    /// <summary>
    /// v1.22：三页滑动的纯计算。
    ///
    /// 界面那套（Grid + TranslateTransform + 260ms 动画）没法断言，
    /// 但「翻到哪一页」「每页摆在哪」「这一下算不算翻页」全是算术，
    /// 抽成 <see cref="Pager"/> 就是为了能在这里一条条钉住。
    /// 重点挡三个容易写反的地方：
    ///   1) 往左滑到底是下一页还是上一页（手指往左 = 内容往左 = 看右边那页）；
    ///   2) 滑到最后一页再滑到底会不会绕回第一页（不该绕）；
    ///   3) 滑动距离不够时会不会被当成翻页（不该）。
    /// </summary>
    private static int RunPager()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { Console.WriteLine("  PASS " + name + (detail.Length > 0 ? " :: " + detail : "")); return; }
            fails.Add(name);
            Console.WriteLine("  FAIL " + name + (detail.Length > 0 ? " :: " + detail : ""));
        }

        // v1.41：日志不再是页，改从各处的入口带着过滤条件打开。
        // ---- 1) 基本常量 ----
        Check("常量/共四页", Pager.Count == 4, Pager.Count.ToString());
        Check("常量/打开落在主页", Pager.Home == 1, Pager.Home.ToString());
        Check("常量/日报站最左", Pager.Report == 0);
        Check("常量/设置与插件在最后", Pager.Settings == 2 && Pager.Plugins == 3);

        // ---- 2) 夹取：不环绕 ----
        Check("夹取/负数变 0", Pager.Clamp(-3) == 0);
        Check("夹取/超界变 3", Pager.Clamp(9) == 3);
        Check("夹取/合法值不动", Pager.Clamp(1) == 1);

        // ---- 3) 每页摆位 ----
        var atHome = Pager.Offsets(1);
        Check("摆位/主页时自己为 0", atHome[1] == 0, atHome[1].ToString());
        Check("摆位/主页时日报页为 -1", atHome[0] == -1, atHome[0].ToString());
        Check("摆位/主页时设置页为 +1", atHome[2] == 1, atHome[2].ToString());
        Check("摆位/主页时插件页为 +2", atHome[3] == 2, atHome[3].ToString());
        var atReport = Pager.Offsets(0);
        Check("摆位/日报页时主页在 +1", atReport[1] == 1, atReport[1].ToString());
        Check("摆位/日报页时设置在 +2", atReport[2] == 2, atReport[2].ToString());
        Check("摆位/日报页时插件在 +3", atReport[3] == 3, atReport[3].ToString());
        var atSettings = Pager.Offsets(2);
        Check("摆位/设置页时主页在 -1", atSettings[1] == -1, atSettings[1].ToString());
        Check("摆位/设置页时日报在 -2", atSettings[0] == -2, atSettings[0].ToString());
        Check("摆位/设置页时插件在 +1", atSettings[3] == 1, atSettings[3].ToString());
        Check("摆位/越界按夹取算", Pager.Offsets(99)[3] == 0, Pager.Offsets(99)[3].ToString());

        // ---- 4) 横滑 ----
        const double W = 1000;   // 阈值 = max(80, 120) = 120
        Check("滑/往左滑去下一页", Pager.Swipe(1, -300, W) == 2, Pager.Swipe(1, -300, W).ToString());
        Check("滑/往右滑回上一页", Pager.Swipe(1, 300, W) == 0, Pager.Swipe(1, 300, W).ToString());
        Check("滑/距离不够不动", Pager.Swipe(1, -50, W) == 1, Pager.Swipe(1, -50, W).ToString());
        Check("滑/刚好到阈值算翻页", Pager.Swipe(1, -120, W) == 2, Pager.Swipe(1, -120, W).ToString());
        Check("滑/差一点点不算", Pager.Swipe(1, -119, W) == 1, Pager.Swipe(1, -119, W).ToString());
        Check("滑/第一页往右滑停住", Pager.Swipe(0, 500, W) == 0, Pager.Swipe(0, 500, W).ToString());
        Check("滑/最后一页往左滑停住", Pager.Swipe(3, -500, W) == 3, Pager.Swipe(3, -500, W).ToString());
        Check("滑/日报页往左到主页", Pager.Swipe(0, -300, W) == 1, Pager.Swipe(0, -300, W).ToString());
        Check("滑/设置页往左到插件页", Pager.Swipe(2, -300, W) == 3, Pager.Swipe(2, -300, W).ToString());
        Check("滑/窄窗口阈值地板 80", Pager.Swipe(1, -100, 400) == 2, Pager.Swipe(1, -100, 400).ToString());
        Check("滑/窄窗口 79 不算", Pager.Swipe(1, -79, 400) == 1, Pager.Swipe(1, -79, 400).ToString());
        Check("滑/宽度为 0 不崩", Pager.Swipe(1, -200, 0) == 2, Pager.Swipe(1, -200, 0).ToString());

        // ---- 5) 阈值函数本身 ----
        Check("阈值/宽窗口按 12%", Math.Abs(Pager.SwipeThreshold(1000) - 120) < 0.01, Pager.SwipeThreshold(1000).ToString());
        Check("阈值/窄窗口不小于 80", Pager.SwipeThreshold(400) >= 80, Pager.SwipeThreshold(400).ToString());
        Check("阈值/宽度 0 也不小于 80", Pager.SwipeThreshold(0) >= 80, Pager.SwipeThreshold(0).ToString());

        // ---- 6) 页码名字 ----
        Check("名字/0 是日报", Pager.Name(0) == "日报", Pager.Name(0));
        Check("名字/1 是主页", Pager.Name(1) == "主页", Pager.Name(1));
        Check("名字/2 是设置", Pager.Name(2) == "设置", Pager.Name(2));
        Check("名字/3 是插件", Pager.Name(3) == "插件", Pager.Name(3));
        Check("名字/越界也认", Pager.Name(99) == "插件", Pager.Name(99));

        // ---- 7) 接线：窗口必须真的用这几样东西 ----
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/底栏有四个页面按钮", mainXaml.Contains("SegPageReport") && mainXaml.Contains("SegPageHome")
                                          && mainXaml.Contains("SegPageSettings") && mainXaml.Contains("SegPagePlugins"));
        Check("接线/日志按钮已经摘掉", !mainXaml.Contains("SegPageRun"));
        Check("接线/四页各有一个平移量", mainXaml.Contains("ShiftReport") && mainXaml.Contains("ShiftHome")
                                         && mainXaml.Contains("ShiftSettings") && mainXaml.Contains("ShiftPlugins"));
        Check("接线/日志页的平移量也摘掉了", !mainXaml.Contains("ShiftRun"));
        Check("接线/切页走 Pager.Clamp", mainCs.Contains("Pager.Clamp("));
        Check("接线/摆位走 Pager.Offsets", mainCs.Contains("Pager.Offsets("));
        Check("接线/横滑走 Pager.Swipe", mainCs.Contains("Pager.Swipe("));
        Check("接线/有触摸横滑", mainXaml.Contains("IsManipulationEnabled=\"True\""));
        // v1.41：日志改成带过滤条件的覆盖视图，命令面板走 OpenLogView。
        Check("接线/命令面板能跨页定位", mainCs.Contains("OpenLogView(") && mainCs.Contains("ShowPage(Pager.Settings)"));
        Check("接线/初始页与 Pager.Home 一致", mainCs.Contains("private int _page = Pager.Home;"));
        Console.WriteLine($"RESULT pager fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.28：消息中心。
    ///
    /// 这块要挡住的是一件很具体的事：**崩溃提示不许再挡住启动流程**。
    /// 上一版把「检测到上次为异常退出」做成了模态框，而它弹在自动开跑之前，
    /// 人不在电脑前就等于整晚没跑。所以这里的断言分两半：
    /// 一半验「翻成人话」翻得对不对，一半验「记录这件事永远不弹窗」。
    /// </summary>
    private static int RunNotice()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 翻成人话：按异常类型 ----
        var denied = ErrorExplain.Explain("System.UnauthorizedAccessException: 对路径\"C:\\Program Files\\x\"的访问被拒绝。");
        Check("解释/权限：说没权限", denied.What.Contains("权限"), denied.What);
        Check("解释/权限：提到 Program Files", denied.Why.Contains("Program Files"), denied.Why);
        Check("解释/权限：给出可做的事", denied.Todo.Length > 0, denied.Todo);

        var missing = ErrorExplain.Explain("System.IO.FileNotFoundException: 未能加载文件或程序集");
        Check("解释/找不到文件", missing.What.Contains("找不到"), missing.What);

        var net = ErrorExplain.Explain("System.Net.Sockets.SocketException: 由于目标计算机积极拒绝");
        Check("解释/网络", net.What.Contains("网络"), net.What);

        var timeout = ErrorExplain.Explain("System.TimeoutException: 操作已超时");
        Check("解释/超时", timeout.What.Contains("放弃") || timeout.What.Contains("等太久"), timeout.What);

        // ---- 2) 类型名压过关键词：带「权限」二字的普通异常不该被关键词抢走 ----
        var ours = ErrorExplain.Explain("System.NullReferenceException: 未将对象引用设置到对象的实例。");
        Check("解释/空引用认成程序自己的问题", ours.Why.Contains("不是你配错"), ours.Why);
        Check("解释/空引用权重高过关键词", !ours.What.Contains("权限"), ours.What);

        // ---- 3) 翻不出来就承认 ----
        var weird = ErrorExplain.Explain("SomeVeryWeirdError: qwertyuiop");
        Check("解释/没见过就说没见过", weird.What.Contains("没见过的"), weird.What);
        Check("解释/不硬猜原因", weird.Why.Contains("硬猜"), weird.Why);
        Check("解释/空输入走兜底", ErrorExplain.Explain(null).What == weird.What);
        Check("解释/三段能拼成一段", denied.Text.Contains(denied.What) && denied.Text.Contains('\n'));

        // ---- 4) 标题：从堆栈里挑根因那一行 ----
        string stack = "System.InvalidOperationException: 序列包含多个元素\n   在 System.Linq.Enumerable.Single(...)\n   在 DragonButler.QueueService.StartAsync()";
        string head = ErrorExplain.Headline(stack);
        Check("标题/挑到异常那一行", head.Contains("序列包含多个元素"), head);
        Check("标题/去掉 System. 前缀", !head.Contains("System."), head);
        Check("标题/不带堆栈行", !head.Contains("QueueService"), head);
        string longHead = ErrorExplain.Headline("System.Exception: " + new string('x', 300));
        Check("标题/太长会截断", longHead.Length <= ErrorExplain.Headline("x", 92).Length + 200 && longHead.EndsWith("…"), "len=" + longHead.Length);
        Check("标题/空输入也不留空", ErrorExplain.Headline("").Length > 0);

        // ---- 5) 是不是管家自己的锅 ----
        Check("归因/空引用是", ErrorExplain.IsOurFault("System.NullReferenceException: x"));
        Check("归因/索引越界是", ErrorExplain.IsOurFault("System.IndexOutOfRangeException: x"));
        Check("归因/权限不是", !ErrorExplain.IsOurFault("System.UnauthorizedAccessException: x"));
        Check("归因/空输入不是", !ErrorExplain.IsOurFault(null));

        // ---- 6) 存与读：换到临时文件，别碰用户真实的 notices.json ----
        string sandbox = Path.Combine(Path.GetTempPath(), "dragon-notice-probe.json");
        string? oldPath = NoticeCenter.PathOverride;
        try
        {
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
            NoticeCenter.PathOverride = sandbox;
            NoticeCenter.Reload();
            NoticeCenter.Clear();
            Check("存取/清空后是 0 条", NoticeCenter.Count == 0, NoticeCenter.Count.ToString());
            Check("存取/没消息时红点不显示", NoticeCenter.BadgeText().Length == 0, NoticeCenter.BadgeText());

            var n1 = NoticeCenter.AddError("第一个", "正文", "raw");
            Check("存取/记完有 1 条", NoticeCenter.Count == 1, NoticeCenter.Count.ToString());
            Check("存取/新消息算未读", NoticeCenter.UnreadCount == 1, NoticeCenter.UnreadCount.ToString());
            Check("存取/红点显示 1", NoticeCenter.BadgeText() == "1", NoticeCenter.BadgeText());
            Check("存取/落盘了", File.Exists(sandbox));
            Check("存取/标题被保留", n1.Title == "第一个", n1.Title);
            Check("存取/时间是刚刚", (DateTime.Now - n1.At).TotalSeconds < 30, n1.At.ToString("HH:mm:ss"));

            // 最新在前
            Thread.Sleep(20);
            NoticeCenter.AddCrash("场景", "System.NullReferenceException: x", "第二个");
            var all = NoticeCenter.All();
            Check("存取/最新的排在最前", all[0].Title.Contains("第二个"), all[0].Title);
            Check("存取/Recent 取最近", NoticeCenter.Recent(1).Count == 1);

            // 崩溃那条要带上人话解释
            var crash = all[0];
            Check("存取/崩溃自动翻成人话", crash.Body.Contains("不是你配错"), crash.Body);
            Check("存取/崩溃标成程序的问题", crash.OurFault);
            Check("存取/原始内容也留着", crash.Raw.Contains("NullReferenceException"), crash.Raw);

            // 已读
            NoticeCenter.MarkAllRead();
            Check("存取/全读之后未读归零", NoticeCenter.UnreadCount == 0, NoticeCenter.UnreadCount.ToString());
            Check("存取/全读之后红点消失", NoticeCenter.BadgeText().Length == 0);
            Check("存取/读过不等于删掉", NoticeCenter.Count == 2, NoticeCenter.Count.ToString());

            // 删一条 / 清空
            NoticeCenter.Remove(all[0]);
            Check("存取/删掉一条剩一条", NoticeCenter.Count == 1, NoticeCenter.Count.ToString());
            NoticeCenter.Remove(null);
            Check("存取/删 null 不炸", NoticeCenter.Count == 1);
            NoticeCenter.Clear();
            Check("存取/清空归零", NoticeCenter.Count == 0 && NoticeCenter.UnreadCount == 0);

            // 环形上限
            for (int i = 0; i < NoticeCenter.Capacity + 12; i++) NoticeCenter.Add(NoticeKind.Info, "第 " + i + " 条");
            Check("存取/最多留 Capacity 条", NoticeCenter.Count == NoticeCenter.Capacity, NoticeCenter.Count.ToString());
            Check("存取/挤掉的是最老的", NoticeCenter.All()[NoticeCenter.Capacity - 1].Title == "第 12 条",
                NoticeCenter.All()[NoticeCenter.Capacity - 1].Title);

            // 重启后还在
            NoticeCenter.Reload();
            Check("存取/重开程序还记得", NoticeCenter.Count == NoticeCenter.Capacity, NoticeCenter.Count.ToString());

            // 可粘贴文字
            var one = NoticeCenter.Add(NoticeKind.Warn, "提醒标题", "提醒正文", "原始");
            string clip = NoticeCenter.ToClipboard(one);
            Check("粘贴/带类型", clip.Contains("提醒"), clip);
            Check("粘贴/带标题", clip.Contains("提醒标题"));
            Check("粘贴/带原始内容", clip.Contains("原始"));
            Check("粘贴/空值返回空串", NoticeCenter.ToClipboard(null).Length == 0);

            // 类型中文名
            Check("名字/崩溃", NoticeCenter.KindName(NoticeKind.Crash) == "崩溃", NoticeCenter.KindName(NoticeKind.Crash));
            Check("名字/出错", NoticeCenter.KindName(NoticeKind.Error) == "出错", NoticeCenter.KindName(NoticeKind.Error));
        }
        finally
        {
            NoticeCenter.PathOverride = oldPath;
            NoticeCenter.Reload();
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
        }

        // ---- 7) 接线：这一整块存在的理由是「不许弹窗挡住启动」 ----
        string crashCs = ReadRepoSources("Dragon Butler/CrashGuard.cs");
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string appCs = ReadRepoSources("Dragon Butler/App.xaml.cs");
        string queueCs = ReadRepoSources("Dragon Butler/QueueService.cs");
        string noticeWinCs = ReadRepoSources("Dragon Butler/NoticeWindow.xaml.cs");

        // 判「没调用」而不是「没这几个字」——注释里留着「这里原来调 AppDialog.Show」是有用的记录
        Check("接线/崩溃不再走模态框", !crashCs.Contains("AppDialog.Show("), "CrashGuard.cs 里不该再调用 AppDialog.Show");
        Check("接线/崩溃记进消息中心", crashCs.Contains("NoticeCenter.AddCrash("));
        Check("接线/上次没收尾也记一条", crashCs.Contains("上次没有正常收尾"));
        Check("接线/标题栏有消息按钮", mainXaml.Contains("BtnNotice") && mainXaml.Contains("Click=\"BtnNotice_Click\""));
        Check("接线/标题栏有红点", mainXaml.Contains("NoticeBadge") && mainXaml.Contains("TbNoticeBadge"));
        Check("接线/红点不吃点击", mainXaml.Contains("IsHitTestVisible=\"False\""));
        // 窗口自己不负责「打开自己」，打开的人是主窗（OpenNotices），所以判主窗那边用的是 Show
        Check("接线/消息窗口不模态",
              mainCs.Contains("new NoticeWindow { Owner = this };") && mainCs.Contains("win.Show();")
              && !mainCs.Contains("win.ShowDialog()"), "开消息中心只能 Show()");
        Check("接线/只开一份消息中心", mainCs.Contains("NoticeWindow") && mainCs.Contains("Activate()"));
        Check("接线/启动后刷一次红点", appCs.Contains("RefreshNoticeBadge()"));
        Check("接线/单工具失败进消息中心", queueCs.Contains("NoticeCenter.AddError("));
        Check("接线/整队中断进消息中心", queueCs.Contains("NoticeCenter.AddCrash("));
        Check("接线/消息中心有自己的滚动条", ReadRepoSources("Dragon Butler/NoticeWindow.xaml").Contains("NoticeScrollBar"));

        Console.WriteLine($"RESULT notice fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
