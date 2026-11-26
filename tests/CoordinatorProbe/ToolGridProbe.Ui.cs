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
    private void Step13()
    {
        // ---- 11) 接线 ----
        mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        // v1.42 / M8-1：TrayConfigWindow 已经整体删除，这里不再读它的源码。
        appStateCs = ReadRepoSources("Dragon Butler/AppState.cs");
        logCs = ReadRepoSources("Dragon Butler/LogService.cs");
        profileCs = ReadRepoSources("Dragon Butler/ProfileRegistry.cs");
        pluginCs = ReadRepoSources("Dragon Butler/PluginLoader.cs");
        backupCs = ReadRepoSources("Dragon Butler/SettingsBackup.cs");
        iconCs = ReadRepoSources("Dragon Butler/ToolIconService.cs");
        soundCs = ReadRepoSources("Dragon Butler/SoundService.cs");
        pathCs = ReadRepoSources("Dragon Butler/Paths.cs");
        wizardCs = ReadRepoSources("Dragon Butler/FirstRunWindow.xaml.cs");
        wizardXaml = ReadRepoSources("Dragon Butler/FirstRunWindow.xaml");
        Check("接线/有 ToolGridHost", mainXaml.Contains("\"ToolGridHost\""));
        Check("接线/ToolGridHost 是 Grid", mainXaml.Contains("<Grid x:Name=\"ToolGridHost\""));
        // v1.26：队列卡标题行那个「＋ 添加」长条按钮删了——格子里本来就有一格「＋ 添加」，
        // 一上一下两个入口只会让人犹豫点哪个。判「格子里有那个位」而不是「XAML 里有那个按钮」。
        Check("接线/格子里有添加位", mainCs.Contains("BuildAddTile(") && mainCs.Contains("BtnAddCustomTool_Click("));
        Check("接线/有溢出提示", mainXaml.Contains("TbGridOverflow"));
        // v1.26：格子搬进启动页那张卡（原来的「睡前队列」），设置页那张「我的工具」整块删掉——
        // 两张卡摆同一批工具，用户要点一个工具得先想清楚它在哪一页。
        Check("接线/格子只在启动页出现一次",
            mainXaml.Split("x:Name=\"ToolGridHost\"").Length == 2);
        // v1.27：标题从「我的工具」改成「托管配置」——说的是「它替你干什么」而不是「它是你的东西」。
        // v1.41 / M3-2：叫「托管配置」像在说这软件的设置；它其实是「你要它干哪些活」的清单。
        Check("接线/队列卡标题是任务清单", mainXaml.Contains("Text=\"任务清单\""));
        Check("接线/界面上不再叫我的工具", !mainXaml.Contains("Text=\"我的工具\""));
        // 判「界面上不再叫这个名字」而不是「源码里没有这四个字」：
        // 注释里保留「原来叫睡前队列、为什么改」是有用的记录，不该被断言逼着删。
        Check("接线/界面上不再叫睡前队列", !mainXaml.Contains("Text=\"睡前队列\""));
        Check("接线/点格子进启动设置", mainCs.Contains("OpenToolLaunch("));
        Check("接线/右键菜单", mainCs.Contains("ToolTile_RightClick"));
        Check("接线/格子里按宽度设 MaxWidth", mainCs.Contains("nameText.MaxWidth"));
        Check("接线/订单存进设置", mainCs.Contains("SaveToolOrder("));
        Check("接线/刷新时重画网格", mainCs.Contains("RenderToolGrid()"));
        Check("接线/工具表里有 ToolOrder", ReadRepoSources("Dragon Butler/AppState.cs").Contains("ToolOrder"));
        Check("接线/长按才拖（350ms）", mainCs.Contains("_dragHold"));
        // v1.27：拖拽改成跨页插入。ToolGrid.MoveTo 只管「一维列表里挪位置」，
        // 表达不了「从第 1 页拖到第 3 页第 4 格」，所以落点走 ToolPages.Insert。
        Check("接线/拖动用 ToolPages.Insert", mainCs.Contains("ToolPages.Insert("));
        Check("接线/落点算格子算到页", mainCs.Contains("SlotAtPage("));
        // v1.26：格子＝队列顺序。摆什么、按什么顺序摆，答案必须来自队列而不是「装了哪些工具」。
        Check("接线/格子顺序来自队列", mainCs.Contains("AppState.Settings.Queue"));
        Check("接线/排序会重排队列", mainCs.Contains("rebuilt"));
        Check("接线/能切今晚跑不跑", mainCs.Contains("ToggleQueueEnabled("));
        Check("接线/右键里有那一项", mainCs.Contains("今晚不跑它"));
        Check("接线/不跑的那格压暗", mainCs.Contains("Opacity = onDuty ? 1.0 : 0.55"));
        // 判「没挂接」而不是「没出现」：注释里仍会提到这个名字（说明为什么改掉），
        // 那是有用的记录，不该被断言逼着删。
        Check("接线/只挂一套 Up（不抢事件）",
            !mainCs.Contains("+= ToolTile_Click") && !mainCs.Contains("void ToolTile_Click"));
        Check("接线/空格子有虚线", mainCs.Contains("StrokeDashArray"));
    }

    private void Step14()
    {
        // ---- 11b) v1.27 可翻页桌面：翻页、页码点、跨页拖拽 ----
        Check("翻页/有裁切容器", mainXaml.Contains("x:Name=\"ToolPagerClip\"") && mainXaml.Contains("ClipToBounds=\"True\""));
        Check("翻页/裁切容器能触摸横滑", mainXaml.Contains("ToolPager_ManipulationCompleted"));
        Check("翻页/拖拽层在裁切外面", mainXaml.Contains("x:Name=\"ToolDragLayer\""));
        Check("翻页/有页码点容器", mainXaml.Contains("x:Name=\"PageDotsHost\""));
        // v1.29：「＋ 新页」按钮删了。它的问题是只能加不能删——加出来的空页会一直挂在页码条上，
        // 于是「页」变成了一个用户要自己整理的东西。现在页数由工具数量决定。
        Check("翻页/不再有加页按钮",
            !mainXaml.Contains("x:Name=\"BtnAddPage\"") && !mainCs.Contains("void BtnAddPage_Click("));
        Check("翻页/页数由数量决定", ReadRepoSources("Dragon Butler/ToolPages.cs").Contains("public static List<List<string>> Repack("));
        Check("翻页/切页走 GoToolPage", mainCs.Contains("void GoToolPage("));
        Check("翻页/摆位走 PlaceToolPages", mainCs.Contains("void PlaceToolPages("));
        Check("翻页/拖到边缘会翻页", mainCs.Contains("CheckEdgeFlip("));
        Check("翻页/落点能算出目标页", mainCs.Contains("private (int Page, int Slot) SlotAtPage("));
        Check("翻页/页码点能点", mainCs.Contains("PageDot_Click("));
        Check("翻页/页码点长按能删", mainCs.Contains("PageDot_PressDown(") && mainCs.Contains("AskDeletePage("));
        // 删页要说清撤不回来。这条是产品要求不是文案洁癖：
        // 「删掉一整页」一次带走最多 6 个工具，没有撤销就得在确认框里说清楚。
        Check("翻页/删页前说清撤不回来", mainCs.Contains("撤不回来"));
        Check("翻页/只剩一页时拒绝删", mainCs.Contains("ToolPages.RemovePageAt("));
        Check("翻页/新工具落到它所在的那页", mainCs.Contains("ToolPages.Locate("));
        Check("翻页/页面存进设置", ReadRepoSources("Dragon Butler/AppState.cs").Contains("ToolPages"));
        // v1.29：页码故意不落盘。存了的话每次启动都要先划两下才看得到「今晚跑什么」，
        // 而那正是九成人打开它的理由。这条盯的是「别哪天顺手把它持久化」。
        Check("翻页/页码不落盘",
            !ReadRepoSources("Dragon Butler/AppState.cs").Contains("ToolPageIndex"));
    }

    private void Step15()
    {
        // ---- 11c) v1.29 底部导航栏 + 两卡合并 + 插件卡 ----
        // v1.34：三项变四项，NavBtn 的出现次数从 5（1 处定义 + 3 处引用 + 1）变成 6。
        // v1.40：加「汇报」后变五项 → 7（1 处定义 + 5 处引用 + 1）。
        // v1.41：五页变四页，NavBtn 从 6 处（1 处定义 + 5 处引用）变成 5 处，Split 后 6 段。
        Check("导航/四个按钮搬到底部",
            mainXaml.Contains("Style=\"{StaticResource NavBtn}\"") &&
            mainXaml.Split("NavBtn").Length == 6);
        Check("导航/标题栏不再有三页切换",
            !mainXaml.Contains("Style=\"{StaticResource SegBtn}\" Checked=\"SegPage_Checked\"")); 
        // v1.32 那条横贯的 Border 换成了悬浮的 Grid，判据跟着改：底栏占第 2 行、状态条在第 3 行
        // v1.33：导航栏不再占自己一行，它浮在内容区（Row 1）底部；状态条退到 Row 2。
        // 判据从「两者的行号」改成「导航栏跟内容区同一行、状态条在它下面一行」——
        // 它们是「浮在上面」而不是「排在上面的」，行号相等才是这一段想要的样子。
        Check("导航/底栏浮在内容区上",
            mainXaml.Contains("x:Name=\"NavShell\"") &&
            mainXaml.Contains("Grid.Row=\"1\" Panel.ZIndex=\"20\"") &&
            mainXaml.Contains("<Border Grid.Row=\"2\" x:Name=\"StatusBarBorder\""));
        Check("合并/工具设置搬进启动页",
            mainXaml.IndexOf("x:Name=\"ToolButtonHost\"", StringComparison.Ordinal) <
            mainXaml.IndexOf("x:Name=\"PageSettings\"", StringComparison.Ordinal));
        Check("合并/设置页不再有工具设置壳", !mainXaml.Contains("第二层：工具设置"));
        // v1.34：插件从折叠卡变成独立一页，折叠头没了，但它那一整套「读得懂/读不懂」都还在。
        Check("插件/有自己的一页", mainXaml.Contains("x:Name=\"PagePlugins\"") && mainXaml.Contains("x:Name=\"PluginsHost\""));
        Check("插件/不再挂在折叠头下面", !mainXaml.Contains("x:Name=\"BtnFoldPlugins\"") && !mainCs.Contains("=> PluginsBody"));
        Check("插件/读得懂的与读不懂的都列出来", mainCs.Contains("PluginLoader.Scan()") && mainCs.Contains("没加载"));
        Check("插件/空着也说清不影响功能", mainCs.Contains("这不影响任何功能"));
    }

    private void Step16()
    {
        // ---- 11d) v1.30 托管配置缩成入口 + 独立窗口 + 目录收拾 ----
    }

    private void Step17()
    {
        // ---- 11d) v1.42：托管配置窗口与五个视图入口双双撤掉 ----
        Check("入口/「配置」大按钮没了", !mainXaml.Contains("BtnOpenTrayConfig"));
        Check("入口/托管配置窗口没了", !mainCs.Contains("TrayConfigWindow"));
        Check("入口/卡中间那格换成了工具行宿主", mainXaml.Contains("x:Name=\"ToolRowHost\""));
        Check("入口/标题行有「已配 N 个」", mainXaml.Contains("x:Name=\"TbQueueCount\""));
        Check("入口/视图下拉整个撤掉", !mainXaml.Contains("BtnQueueView") && !mainCs.Contains("BtnQueueView_Click"));
        // v1.42.1 / M9-4（D18）：四个窗口类本身也删了。留着入口是隐患，留着类更是——
        // 六个月后有人「顺手接回去」，被撤掉的视图系统就还魂了。
        Check("入口/四个视图窗口类已从仓库删除",
            !mainCs.Contains("WeeklyPlanWindow") && !mainCs.Contains("CalendarWindow") &&
            !mainCs.Contains("DependsWindow") && !mainCs.Contains("GroupsWindow"));
        // v1.42 / M8-2：格子墙改成横向工具行，六项能力一个不少。
        Check("工具行/有渲染入口", mainCs.Contains("RenderToolRows()") && mainCs.Contains("SyncToolRows()"));
        // v1.42.1 / M9-3（D17）：56 → 52。零滚动契约是硬预算，一行省 4px 正好把它抹平。
        Check("工具行/单行 52 高", mainCs.Contains("Height = 52"));
        Check("工具行/带拖拽手柄", mainCs.Contains("≡") && mainCs.Contains("MoveToolRow("));
        Check("工具行/双击立即跑", mainCs.Contains("e2.ClickCount >= 2") && mainCs.Contains("RunToolNow("));
        Check("工具行/点一下展开配置组", mainCs.Contains("_expandedRows") && mainCs.Contains("BuildGroupPanel("));
        Check("工具行/行内徽章", mainCs.Contains("_rowBadges") && mainCs.Contains("✓ 已就绪") && mainCs.Contains("未配置路径"));
        Check("工具行/添加行是虚线", mainCs.Contains("BuildAddRow(") && mainCs.Contains("StrokeDashArray"));
        Check("工具行/添加面板是内联格子", mainCs.Contains("BuildAddPanel(") && !mainCs.Contains("ToolPickerWindow"));
        // v1.42 / M8-4：展开区读写的就是 QueueEntry.Groups（与配置组视图同一份存储）。
        Check("展开区/读写 QueueEntry.Groups", mainCs.Contains("QueueEntryOf(\"bettergi\")") && mainCs.Contains("target.Groups = order2.ToList();"));
        Check("展开区/有全选与全部取消", mainCs.Contains("全选") && mainCs.Contains("全部取消"));
        Check("展开区/有今晚 N 项计数", mainCs.Contains("footerCount") && mainCs.Contains("今晚 {n} 项"));
        // v1.42 / M8-7：今晚摘要。
        Check("今晚摘要/有那一行", mainXaml.Contains("x:Name=\"TbTonightSummary\""));
        Check("今晚摘要/按组耗时算时长", mainCs.Contains("TonightMinutes(") && mainCs.Contains("MedianMinutes("));
        Check("今晚摘要/没有样本就不报时长", mainCs.Contains("if (median == null) return null;"));
        // v1.42 / M8-6：菜单统一走应用级模板，不留 Icon 列。
        appXaml = ReadRepoSources("Dragon Butler/App.xaml");
        Check("菜单/应用级有 MenuItem 模板", appXaml.Contains("<Style TargetType=\"MenuItem\">"));
        Check("菜单/模板里不画图标列", !appXaml.Contains("ContentSource=\"Icon\""));
        Check("默认/不再自己塞三个工具", !appStateCs.Contains("Settings.Queue.Add(new QueueEntry\n            {\n                ToolId = p.ToolId,"));
        Check("默认/队列只保证非 null", appStateCs.Contains("Settings.Queue ??= new List<QueueEntry>();"));
        Check("引导/桌面照着勾选重建", wizardCs.Contains("AppState.Settings.ToolPages = ToolPages.Repack("));
        Check("引导/一个都不勾就空着", wizardCs.Contains("if (picked.Count > 0)"));
    }

    private void Step18()
    {
        // ---- 11e) v1.30 目录收拾成两个 ----
        Check("目录/配置根叫所有配置", pathCs.Contains("\"所有配置\""));
        Check("目录/日志根叫全部日志", pathCs.Contains("\"全部日志\""));
        Check("目录/静态构造先建目录", pathCs.Contains("static Paths() => Prepare();"));
        Check("目录/老 config 摊平搬过来", pathCs.Contains("flatten: true"));
        Check("目录/搬完打标记不再扫", pathCs.Contains(".moved-to-所有配置"));
        Check("目录/设置走新根", appStateCs.Contains("Paths.Config(\"settings.json\")"));
        Check("目录/日志走新根", logCs.Contains("public static readonly string Dir = Paths.LogRoot;"));
        Check("目录/工具档案走新根", profileCs.Contains("public static string ToolsDir => Paths.ToolsDir;"));
        Check("目录/插件走新根", pluginCs.Contains("public static string Root => RootOverride ?? Paths.PluginsDir;"));
        Check("目录/备份走新根", backupCs.Contains("public static string Dir => Paths.BackupDir;"));
        Check("目录/图标缓存走新根", iconCs.Contains("public static string CacheDir => Paths.CacheDir;"));
        Check("目录/音效走新根", soundCs.Contains("Paths.SoundsDir"));
        Check("目录/不再散着建 config",
            !mainCs.Contains("Path.Combine(AppContext.BaseDirectory, \"config\"") &&
            !appStateCs.Contains("Path.Combine(AppContext.BaseDirectory, \"config\""));
    }

    private void Step19()
    {
        // ---- 11f) v1.32 一个工具一个按键 / 引导页图标格 ----
        Check("按键/有那一排", mainXaml.Contains("x:Name=\"ToolButtonHost\""));
        Check("按键/排不下会换行", mainXaml.Contains("<WrapPanel x:Name=\"ToolButtonHost\""));
        Check("按键/旧的折叠头撤掉了",
            !mainXaml.Contains("x:Name=\"BtnFoldTools\"") && !mainXaml.Contains("x:Name=\"ChevTools\""));
        Check("按键/长列表留着但不再显示",
            mainXaml.Contains("<StackPanel x:Name=\"ToolCardsHost\" Visibility=\"Collapsed\">"));
        Check("按键/点一下开那个工具的配置窗", mainCs.Contains("btn.MouseLeftButtonDown += (_, _) => OpenToolLaunch(toolId);"));
        Check("按键/名字太长会截断", mainCs.Contains("TextTrimming.CharacterEllipsis,") && mainCs.Contains("MaxWidth = 96"));
        Check("按键/没配好的用警示色圈出来", mainCs.Contains("ready ? \"BorderBrush\" : \"WarningBrush\""));
        // 每秒轮询会走到这里，签名没变就一个控件都不碰——否则手指按着的那颗按钮会被换掉
        Check("按键/没变就不重造", mainCs.Contains("_toolButtonSignature") && mainCs.Contains("if (signature == _toolButtonSignature) return;"));

        Check("引导/图标格不是一行一个", wizardXaml.Contains("<WrapPanel x:Name=\"ToolPickHost\""));
        Check("引导/有压在角上的勾选框", wizardXaml.Contains("x:Key=\"WizTileCheck\""));
        Check("引导/勾上周围变绿", wizardCs.Contains("tile.BorderBrush = (Brush?)TryFindResource(\"BrandBrush\")"));
        Check("引导/一整格都能点", wizardCs.Contains("tile.MouseLeftButtonUp += (_, _) => check.IsChecked = check.IsChecked != true;"));
        Check("引导/名字两行封顶", wizardCs.Contains("MaxHeight = 30") && wizardCs.Contains("TextWrapping = TextWrapping.Wrap"));

        Check("图标/缺的那五个有图了",
            System.IO.File.Exists("Dragon Butler/Assets/icons/baah.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/hsr.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/m9a.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/mss.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/src.png"));
        // m9a 的原文件其实是 ICO 冒名 png，重存过；比 10 KB 大说明还是那个两百多 K 的壳
        Check("图标/m9a 是真 PNG",
            System.IO.File.Exists("Dragon Butler/Assets/icons/m9a.png") &&
            new System.IO.FileInfo("Dragon Butler/Assets/icons/m9a.png").Length < 120_000);
    }

}
