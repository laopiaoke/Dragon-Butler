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
using static CoordinatorProbe.Program;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。

// v1.45.3 / #15：ToolGrid 的断言从 Program.ToolGrid.cs 里搬出来，按段拆进几个 partial 文件。
// 五十二个局部变量成了字段；每段成了一个 Step 方法。断言正文一字未改。
internal sealed partial class ToolGridProbe
{
    private void Step20()
    {
        // ---- 11g) v1.32 悬浮标签栏 + 设置页卡片墙 ----
        // 参考的是 Figma 那套做法：选中底是一整块独立的圆角矩形在滑，
        // 不是每一项各画各的底轮流亮。这一条改回去，整个动效就没了。
        // v1.34：外壳从「实色 + 细边」换成液态玻璃（渐变底 + 投影），圆角 25 -> 27。
        Check("标签栏/是浮起来的一块胶囊", mainXaml.Contains("x:Name=\"NavShell\"") && mainXaml.Contains("CornerRadius=\"27\""));
        Check("标签栏/那块底是独立的一层", mainXaml.Contains("x:Name=\"NavPill\"") && mainXaml.Contains("x:Name=\"NavPillShift\""));
        Check("标签栏/底不挡点击", mainXaml.Contains("IsHitTestVisible=\"False\""));
        // 单项自己不再画底：NavBtn 模板里连命名容器都没有了，只剩 ContentPresenter。
        // 判据要切开来看——全文里别的样式也有叫 Bd 的 Border，整段搜会误判。
        navStyle = SliceBetween(mainXaml, "x:Key=\"NavBtn\"", "</Style>");
        Check("标签栏/单项不再各画各的底", navStyle.Length > 0 && !navStyle.Contains("x:Name=\"Bd\""));
        Check("标签栏/底会滑", mainCs.Contains("MoveNavPill("));
        Check("标签栏/滑到底带一点回弹", mainCs.Contains("new BackEase") && mainCs.Contains("Amplitude = 0.32"));
        Check("标签栏/点按钮也让底动", mainCs.Contains("MoveNavPill(index, animate: true);"));
        // 拖动：小于 4px 算点击，否则手上抖一下就把页翻走了
        Check("标签栏/拖起来能换位置", mainCs.Contains("NavShell_PreviewMouseMove"));
        Check("标签栏/抖一下不算拖", mainCs.Contains("< 4) return;"));
        Check("标签栏/松手吸附到最近一项", mainCs.Contains("NavIndexAt(") && mainCs.Contains("MoveNavPill(want, animate: true);"));
        Check("标签栏/拖了就等于点了", mainCs.Contains("if (want != _page) ShowPage(want);"));

        // 设置页：v1.34 起四块全占整行（米游社搬成插件、插件搬去独立一页，没有并排的了）。
        // 位置由 Grid.Row/Column 决定，跟标签在文件里的先后没关系——判标签顺序会自找麻烦。
        // v1.35：设置页从「四条等宽长条」改成两列卡片墙。判据跟着换：
        // 不再看 Row 序号（卡会挪），只看「两列存在 + 四张卡两两并排 + 通知占满第一排」。
        Check("设置页/两列卡片墙", mainXaml.Contains("<ColumnDefinition Width=\"*\"") && SliceBetween(mainXaml, "x:Name=\"PageSettings\"", "</Grid.ColumnDefinitions>").Contains("<ColumnDefinition Width=\"*\""));
        Check("设置页/通知卡还在", mainXaml.Contains("x:Name=\"NotifyCard\""));
        // v1.41 / M4-1：卡片不再按 Grid 行列摆，改成竖排分区，一次只显示一个。
        // v1.47 / M11-3：多了「工具配置」这一区，从五变六。
        Check("设置页/六个分区一次只显示一个",
            new[] { "SecGuard", "SecTiming", "SecNotify", "SecApi", "SecTools", "SecUi" }.All(n => mainXaml.Contains("x:Name=\"" + n + "\" Visibility=\"Collapsed\"")));
        Check("设置页/一级只有入口卡没有人开关",
            mainXaml.Contains("x:Name=\"SettingsEntryHost\"") && SliceBetween(mainXaml, "x:Name=\"SettingsHome\"", "x:Name=\"SettingsSection\"").IndexOf("TgSchedule", StringComparison.Ordinal) < 0);
        Check("设置页/五张卡各有标题", new[] { "守护与恢复", "定时与节奏", "接口与维护", "启动与界面", "报告" }.All(t => mainXaml.Contains("Text=\"" + t + "\"")));
        Check("设置页/不再是一条条浅底方块", !mainXaml.Contains("SetRow\"><Border Style=\"{StaticResource SettingCard}"));
        Check("设置页/米游社那张已经搬走", !mainXaml.Contains("BtnMiyousheLogin") && !mainXaml.Contains("BtnMiyousheSign"));
    }

    private void Step21()
    {
        // ---- 11h) v1.33 导航栏点得动了 + 主题开关 + 引导页不塞满 ----

        // ① 导航栏点不动的病根：按下就抢鼠标，RadioButton 收不到完整的按下/松开。
        // 判「没有那一句调用」而不是「没有这个词」——注释里正在解释这件事，会被自己绊倒。
        Check("导航/按下不抢鼠标", !SliceBetween(mainCs, "NavShell_PreviewMouseLeftButtonDown", "}").Contains("NavShell.CaptureMouse()"));
        Check("导航/拖起来才抢", SliceBetween(mainCs, "_navDragging = true;", "}").Contains("NavShell.CaptureMouse()"));
        Check("导航/松手才放开", mainCs.Contains("if (NavShell.IsMouseCaptured) NavShell.ReleaseMouseCapture();"));
        Check("导航/拖完别让按钮再补一次", SliceBetween(mainCs, "int want = NavIndexAt(_navPillX);", "}").Contains("e.Handled = true;"));

        // ② 主题开关：v1.34 收成两档、缩小、点得动
        Check("主题/只剩深色和浅色两档", mainCs.Contains("private static readonly string[] ThemeModes = { \"Dark\", \"Light\" };"));
        Check("主题/没有跟随系统那一档了", !mainXaml.Contains("x:Name=\"IconSystem\""));
        Check("主题/旧配置按实际深浅落到一格", mainCs.Contains("if (i < 0) i = Theme.LastSchemeLight ? 1 : 0;"));
        Check("主题/有那块会滑的玻璃", mainXaml.Contains("x:Name=\"ThemeThumb\"") && mainXaml.Contains("x:Name=\"ThemeThumbShift\""));
        Check("主题/玻璃比轨道高（探出去才看得出玻璃）", mainXaml.Contains("Width=\"52\" Height=\"32\" CornerRadius=\"16\""));
        Check("主题/整体缩小了", mainXaml.Contains("x:Name=\"ThemeSwitchRoot\" Width=\"112\" Height=\"38\""));
        Check("主题/边缘有色散", mainXaml.Contains("#66FF8FA3") && mainXaml.Contains("#668FD8FF"));
        Check("主题/投影用蓝色不用黑", mainXaml.Contains("Color=\"#FF1B3A6B\""));
        Check("主题/点图标能切", mainCs.Contains("Array.FindIndex(ThemeModes"));
        Check("主题/拖动也能切", mainCs.Contains("ThemeSlotAt(_themeThumbX)"));
        // v1.36：图标里的每一条笔画都是「画上去的图案」，不是分层素材。
        // 让星星、光芒单独位移，屏幕上读起来就是「图标自己在抖」——比原来整组飘更难懂。
        // 所以切换只做两件事：滑块滑过去、两条笔画换上不同的灰度。位移一律不碰。
        Check("主题/切换时图标一动不动",
            !mainCs.Contains("PlayThemeParallax") &&
            !mainCs.Contains("Push(Star1,") &&
            !mainCs.Contains("Push(Star2,") &&
            !mainCs.Contains("Push(SunRays,") &&
            !mainCs.Contains("Push(MoonPath,") &&
            !mainCs.Contains("Push(IconDark,"));
        // 这些元素上也不该留位移层 —— 留着就是「以后有人顺手往里推一下」的口子。
        Check("主题/图标上没有挂位移层",
            !SliceBetween(mainXaml, "x:Name=\"MoonPath\"", "/>").Contains("RenderTransform") &&
            !SliceBetween(mainXaml, "x:Name=\"SunRays\"", "/>").Contains("RenderTransform"));
        // 换档靠上色：选中的那档用主文字色，另一档退成三级灰。
        Check("主题/换档靠上色",
            SliceBetween(mainCs, "private void PaintThemeIcons", "catch").Contains("index == 0 ? on : off") &&
            SliceBetween(mainCs, "private void PaintThemeIcons", "catch").Contains("index == 1 ? on : off"));// 「点了没反应」的根治：标题栏那层挂着 DragMove，按下必须在这一层吃掉，否则窗口跟着手跑
        Check("主题/按下不把窗口拖走", SliceBetween(mainCs, "private void ThemeSwitch_MouseLeftButtonDown", "\n    }").Contains("e.Handled = true;"));
        Check("主题/松开也不把窗口拖走", SliceBetween(mainCs, "private void ThemeSwitch_MouseLeftButtonUp", "\n    }").Contains("e.Handled = true;"));
        Check("主题/自定义搬去设置页", !mainXaml.Contains("Content=\"自定义\"") && mainXaml.Contains("x:Name=\"BtnCustomTheme\""));
        Check("主题/外观卡说清现在用的是哪套", mainCs.Contains("private void RefreshAppearanceSummary") && mainCs.Contains("现在用的就是自定义配色"));
        Check("主题/自定义状态下点那格真能切走", mainCs.Contains("bool sameAsNow = to == from"));

        // ③ 引导页：没勾的不进队列
        Check("引导/只勾了的才进队列", wizardCs.Contains("if (on) queue.Add(new QueueEntry { ToolId = kv.Key, Enabled = true });"));
        Check("引导/没勾的不再无条件写一行", !wizardCs.Contains("foreach (var kv in _picks) Set(kv.Key, kv.Value.IsChecked == true);"));
    }

    private void Step22()
    {
        // ---- 11i) v1.34 四页导航 + 插件独立成页 + 米游社搬出本体 ----
        Check("导航/四项", mainXaml.Contains("SegPageReport") && mainXaml.Contains("SegPageHome")
                          && mainXaml.Contains("SegPageSettings") && mainXaml.Contains("SegPagePlugins"));
        Check("导航/第四页有自己的平移量", mainXaml.Contains("x:Name=\"ShiftPlugins\""));
        // v1.41：日志不再是页，四页从 0 起；插件仍是最右边那一页，但下标从 4 变成 3。
        Check("导航/摆位认得第四页", mainCs.Contains("(ShiftPlugins, offsets[3])"));
        Check("导航/同步认得第四页", mainCs.Contains("(SegPagePlugins, Pager.Plugins)"));
        Check("导航/「汇报」改叫「日报」", mainXaml.Contains("Text=\"日报\"") && !mainXaml.Contains("Text=\"汇报\""));
        Check("导航/「启动」改叫「主页」", mainXaml.Contains("Text=\"主页\"") && !mainXaml.Contains("Text=\"启动\""));
        // v1.35：日志图标从「两半页」改成「带折角的纸 + 三行字」，具体在下面的 11m 段逐条盯着。
        Check("导航/插件用的是拼图", mainXaml.Contains("A1.6,1.6 0 0 1 11,3"));
        Check("导航/外壳是玻璃不是实色块", mainXaml.Contains("#D9181C22") && mainXaml.Contains("BlurRadius=\"22\""));
        Check("分页/一共四页", Pager.Count == 4);
        Check("分页/最右一页叫插件", Pager.Name(3) == "插件");
        Check("分页/最左一页叫日报", Pager.Name(0) == "日报");
        Check("分页/第二页叫主页", Pager.Name(1) == "主页");
        Check("分页/翻到第四页就到头", Pager.Clamp(9) == 3);
        Check("插件页/有自己的一页", mainXaml.Contains("x:Name=\"PagePlugins\"") && mainXaml.Contains("x:Name=\"PluginsHost\""));
        Check("插件页/从设置页撤走了", !mainXaml.Contains("x:Name=\"BtnFoldPlugins\""));
        // v1.47 / M11-1（D22）：两段说明从两张卡收进 ⓘ 弹窗，话一句没少，只是换了落点——判据跟着搬到代码侧。
        Check("插件页/说清能做什么不能做什么", mainCs.Contains("不能做：改界面、停队列、动设置、读文件、访问任意网址。"));
        Check("插件页/装坏了也不拖垮主程序", mainCs.Contains("装坏了也不会让主程序起不来，只会在下面列成一条「没加载」并写明原因。"));
        bool archived;
        try { archived = ReadRepoSources("docs/plugin-samples/miyoushe/MiyousheBbs.cs").Length > 1000; }
        catch { archived = false; }
        Check("米游社/源码留档在插件样例里", archived);
        Check("米游社/本体里没有那段签到了", !mainCs.Contains("MiyousheService.SignNowAsync"));
        Check("米游社/设置里不再有那八个字段", !appStateCs.Contains("MiyousheAutoSign"));
    }

    private void Step23()
    {
        // ---- 11j) v1.42：任务清单卡内部按「工具行 → 开关 → 一键开跑 → 今晚摘要」重排 ----
        // 原来那张卡里塞了：五个视图小按钮、一个「配置」大按钮、一堵 3×2 格子墙、
        // 一条「按日计划」和一条「依次执行勾选的工具」。现在只留一行提示 + 工具行列表。
        Check("任务清单/只剩一行提示",
            mainXaml.Contains("拖 ≡ 换顺序 · 双击立即跑 · 右键更多") &&
            !mainXaml.Contains("x:Name=\"TbPlanSummary\"") && !mainXaml.Contains("x:Name=\"TbQueueHint\"") &&
            !mainXaml.Contains("x:Name=\"QueueEmptyHint\""));
        Check("任务清单/加载行挂在工具行下面", mainCs.Contains("ToolRowHost.Children.Add(BuildAddRow())") ||
              mainCs.Contains("BuildAddRow();"));
        // v1.42.1 / M9-3（D17）：摘要从卡片底部搬到头条，方向掉了个头——
        // 摘要现在必须排在「一键开跑」前面，否则又回到「要滚动才看得见」。
        Check("任务清单/今晚摘要在卡片头条",
            mainXaml.IndexOf("x:Name=\"TbTonightSummary\"") >= 0 &&
            mainXaml.IndexOf("x:Name=\"TbTonightSummary\"") < mainXaml.IndexOf("x:Name=\"BtnQueueStart\""),
            "摘要要排在标题行下面、一键开跑上面");
        Check("任务清单/摘要挨着标题行",
            mainXaml.IndexOf("x:Name=\"TbTonightSummary\"") < mainXaml.IndexOf("x:Name=\"TbGridHint\""));
        // 插件目录：v1.35 起是 exe 旁边的「插件」，不是「所有配置\plugins」。
        // 理由是这一项在导航里就叫「插件」，点进去却指向两级子文件夹里的 plugins。
        Check("插件目录/在 exe 旁边叫插件", pathCs.Contains("public static string PluginsDir => Path.Combine(AppContext.BaseDirectory, \"插件\");"));
        Check("插件目录/第一次启动就建好", SliceBetween(pathCs, "public static void Prepare()", "\n    }").Contains("Directory.CreateDirectory(PluginsDir);"));
        Check("插件目录/老位置的内容会搬过来", pathCs.Contains("MoveDir(Path.Combine(ConfigRoot, \"plugins\"), PluginsDir, flatten: true);"));
    }

}
