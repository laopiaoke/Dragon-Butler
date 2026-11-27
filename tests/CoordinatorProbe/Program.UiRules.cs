using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// v1.12：观感规则断言。
    /// 这一轮只调字号、对比度、间距，最怕的是「调着调着顺手加了个色键、加了一行、加了个区块」——
    /// 那就不再是「修看得见的地方」，而是又往臃肿走一步。
    /// 所以把当年口头约定的硬约束固化成断言，改坏了 CI 会喊停。
    /// </summary>
    private static int RunUiRules()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        const string MiniBar = "Dragon Butler/MiniBarWindow.xaml";
        string mini = ReadRepoSources(MiniBar);
        Check("浮条仍是 180×44", mini.Contains("Width=\"180\" Height=\"44\"", StringComparison.Ordinal));
        Check("浮条字号提到 13 / 11.5",
            mini.Contains("FontSize=\"13\"", StringComparison.Ordinal)
            && mini.Contains("FontSize=\"11.5\"", StringComparison.Ordinal));
        Check("浮条次行透明度提到 0.75", mini.Contains("Opacity=\"0.75\"", StringComparison.Ordinal));
        // 只允许四个色键：多出来的就是新配色，一多就不叫「跟着主题走」了
        var allowed = new[] { "CardBgBrush", "BorderBrush", "TextPrimaryBrush", "BrandBrush" };
        var used = System.Text.RegularExpressions.Regex
            .Matches(mini, @"\{DynamicResource (\w+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var extra = used.Where(u => !allowed.Contains(u, StringComparer.Ordinal)).ToList();
        Check("浮条只用四个允许的色键", extra.Count == 0, string.Join(",", extra));

        const string Main = "Dragon Butler/MainWindow.xaml";
        string main = ReadRepoSources(Main);
        Check("主窗默认宽度是 1080", main.Contains("Width=\"1080\"", StringComparison.Ordinal));
        // v1.35：高级设置与系统不再是两张折叠长表，而是拆成「守护与恢复 / 启动与界面 / 节奏与报告 / 接口与维护」
        // 四张卡两两并排。所以「某个 Body 里有几行」这个判据本身失效了。
        // 换成守「一个都不能丢」：这些开关必须还在界面上，而且必须在新卡里——加行的人照样要来这里改数字。
        string keepGuard = SliceBetween(main, "守护与恢复", "节奏与报告");
        string keepRhythm = SliceBetween(main, "定时与节奏", "接口与维护");
        string keepNotifySection = SliceBetween(main, "跑完怎么告诉你", "接口与维护");
        string keepApi = SliceBetween(main, "接口与维护", "</Grid>\n                    </ScrollViewer>");
        Check("设置页/原来的高级设置还在新卡里",
            keepGuard.Contains("TgSentinel") && keepGuard.Contains("TgStallAutoKill") && keepGuard.Contains("TgAutoKill"),
            "守护与恢复");
        // v1.41 / M4-1：原来那张「节奏与报告」拆成了三处——
        // 排期与节奏留在「定时与节奏」，两份报告搬进「通知」下的「报告」卡，
        // 日志规则按「出事怎么办」归到「守护」。一个开关都没丢，只是各自回到它该在的那一类里。
        Check("设置页/节奏与报告拆开后每一项都还在",
            keepRhythm.Contains("CbFailStrategy") && keepRhythm.Contains("CbInterval")
            && keepNotifySection.Contains("TgMorningReport") && keepNotifySection.Contains("TgWeeklyReport")
            && keepGuard.Contains("BtnLogRules"),
            "定时与节奏 / 通知 / 守护");
        Check("设置页/接口与维护装下了 API、面板、热键、备份、体检、交接单",
            keepApi.Contains("TgControlApi") && keepApi.Contains("TgLanPanel") && keepApi.Contains("TgHotkey")
            && keepApi.Contains("BtnBackupNow") && keepApi.Contains("BtnPreflight") && keepApi.Contains("BtnHandover"),
            "接口与维护");
        Check("系统区有交接单一行", main.Contains("BtnHandover"), "MainWindow.xaml");
        Check("系统区有备份一行",
            main.Contains("x:Name=\"BtnBackupNow\"", StringComparison.Ordinal)
            && main.Contains("x:Name=\"BtnBackupRestore\"", StringComparison.Ordinal)
            && main.Contains("x:Name=\"TbBackupHint\"", StringComparison.Ordinal));
        Check("系统区有体检一行",
            main.Contains("x:Name=\"BtnPreflight\"", StringComparison.Ordinal)
            && main.Contains("x:Name=\"TbPreflightHint\"", StringComparison.Ordinal));
        // v1.41 / M3-3：标题行原来并排五个 chip、中间靠这条 1px 竖线分组。
        // v1.42 / M8-3：那个「配置组 ▾」下拉按 D10 一起撤掉了，标题行右边现在只剩「已配 N 个」。
        // 这里改成守「下拉不在、计数在」——既证明它真删了，也证明那片位置没留空。
        Check("队列卡不再有视图下拉",
            !main.Contains("x:Name=\"BtnQueueView\"", StringComparison.Ordinal)
            && !main.Contains("x:Name=\"QueueViewMenu\"", StringComparison.Ordinal)
            && main.Contains("x:Name=\"TbQueueCount\"", StringComparison.Ordinal));

        string palette = ReadRepoSources("Dragon Butler/CommandPalette.xaml");
        Check("命令面板选中态有 Brand 竖条",
            palette.Contains("x:Name=\"Bar\"", StringComparison.Ordinal)
            && palette.Contains("Value=\"{DynamicResource BrandBrush}\"", StringComparison.Ordinal));
        Check("命令面板提示字号 11.5 / 透明度 0.7",
            palette.Contains("FontSize=\"11.5\" Opacity=\"0.7\"", StringComparison.Ordinal));

        // v1.42.1 / M9-4：CalendarWindow / DependsWindow 已删，读源码的断言跟着撤。
        // 这里留一行反向断言：它们的 XAML 不该再出现在仓库里。
        Check("日历与依赖窗口的源文件确实没了",
            !RepoFileExists("Dragon Butler/CalendarWindow.xaml")
            && !RepoFileExists("Dragon Butler/DependsWindow.xaml.cs")
            && !RepoFileExists("Dragon Butler/WeeklyPlanWindow.xaml.cs")
            && !RepoFileExists("Dragon Butler/GroupsWindow.xaml.cs")
            && !RepoFileExists("Dragon Butler/CalendarModel.cs"));

        string probe = ReadRepoSourcesGroup("tests/CoordinatorProbe", "RenderProbe*.cs");
        Check("渲染工具支持滚到顶（ui top）",
            probe.Contains("ScrollToTop()", StringComparison.Ordinal)
            && probe.Contains("\"top\"", StringComparison.Ordinal));
        Check("渲染工具会造日历样例历史",
            probe.Contains("SeedSampleArchive", StringComparison.Ordinal));

        // v1.16：首次向导是每个新用户看到的第一个界面，之前它的路径框是白底黑字——
        // 代码里写了 Style="{StaticResource InputBox}"，但那是主窗的窗口级资源，
        // 向导窗口取不到就静默退回系统默认浅色。这类问题只有真渲染才看得见。
        string wizard = ReadRepoSources("Dragon Butler/FirstRunWindow.xaml");
        Check("向导有自己的 InputBox 样式",
            wizard.Contains("x:Key=\"InputBox\"", StringComparison.Ordinal));
        Check("向导输入框不是系统默认浅色",
            wizard.Contains("TargetType=\"TextBox\"", StringComparison.Ordinal)
            && wizard.Contains("{DynamicResource InsetBrush}", StringComparison.Ordinal));
        Check("向导勾选框自己画（不退回系统方框）",
            wizard.Contains("x:Key=\"WizCheck\"", StringComparison.Ordinal)
            && wizard.Contains("TargetType=\"CheckBox\"", StringComparison.Ordinal)
            && wizard.Contains("Data=\"M 3.5 8 L 6.6 11 L 12.4 5\"", StringComparison.Ordinal));
        Check("向导三步都有渲染用例", probe.Contains("\"wizard\"", StringComparison.Ordinal));

        // v1.25：默认不推送，但引导页得把选择权交出去——否则等于「这软件没有通知」。
        Check("向导有渠道胶囊样式（主窗 NotifyChip 拿不到）",
            wizard.Contains("x:Key=\"WizChip\"", StringComparison.Ordinal)
            && wizard.Contains("TargetType=\"RadioButton\"", StringComparison.Ordinal));
        // v1.38：中间插了「开机自启」那一页，推送选择从第 3 步挪到第 4 步、完成页到第 5 步。
        Check("向导说清是五步", wizard.Contains("共 5 步", StringComparison.Ordinal));
        Check("向导有第 5 步（完成页）",
            wizard.Contains("x:Name=\"Step5\"", StringComparison.Ordinal));
        // v1.41 / M2-8：单一列表拆成「不推送」置顶 + 本机 / IM / 手机三组。
        Check("向导第 4 步是推送选择",
            wizard.Contains("x:Name=\"NotifyTopHost\"", StringComparison.Ordinal)
            && wizard.Contains("x:Name=\"NotifyGroupsHost\"", StringComparison.Ordinal)
            && wizard.Contains("x:Name=\"TbNotifyField\"", StringComparison.Ordinal)
            && wizard.Contains("x:Name=\"BtnNotifyTest\"", StringComparison.Ordinal));
        Check("向导渲染用例已改成五步", probe.Contains("step <= 5", StringComparison.Ordinal));

        // v1.25：换渠道必须把上一个渠道打在框里的字收走。
        // 不然从飞书切到企业微信，框里还是飞书的地址，手一滑就存成了企业微信的地址。
        string wizardCs = ReadRepoSources("Dragon Butler/FirstRunWindow.xaml.cs");
        string notifyCs = ReadRepoSources("Dragon Butler/NotifyService.cs");
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("向导/换渠道先收草稿",
            wizardCs.Contains("_notifyTyped[_notifyShownMode] = TbNotifyField.Text", StringComparison.Ordinal));
        Check("向导/带出的是这个渠道自己的地址",
            wizardCs.Contains("TbNotifyField.Text = FieldFor(mode);", StringComparison.Ordinal));
        Check("向导/第 3 步存了才前进",
            wizardCs.Contains("SaveNotifyChoice();", StringComparison.Ordinal)
            && wizardCs.Contains("_step = 4;", StringComparison.Ordinal));
        Check("向导/完成时也存一次",
            System.Text.RegularExpressions.Regex.IsMatch(wizardCs, @"SavePaths\(\);\s+SaveNotifyChoice\(\);"));

        // v1.35：关窗口那句「还在后台」整条删了，不是换条路弹。
        //
        // v1.28 给它加过闸，但闸的判据是「通知渠道 != 不推送」——
        // 而「关窗口」跟「跑完了要不要通知我」是两件事：选了 Windows 通知的人，
        // 在意的是跑完的结果，不是每次关窗都被系统弹一次「还在后台」。
        // 这一刻用户就坐在屏幕前、看着窗口消失，他知道自己关了什么。
        Check("关窗提示/整条删了，不是绕道弹",
            !mainCs.Contains("NotifyService.ShowTrayNote(", StringComparison.Ordinal)
            && !notifyCs.Contains("public static void ShowTrayNote(", StringComparison.Ordinal)
            && !notifyCs.Contains("public static void ResetTrayNote(", StringComparison.Ordinal));
        Check("关窗提示/那三个字段与调用一起清了",
            !notifyCs.Contains("TrayNoticeShown", StringComparison.Ordinal)
            && !mainCs.Contains("ResetTrayNote(", StringComparison.Ordinal));
        // 在关窗回调内分别查隐藏动作与提前放行的条件，不依赖同一行里条件的顺序。
        string closing = SliceBetween(mainCs, "private void Window_Closing(", "\n    }");
        var hide = Regex.Match(closing, @"\be\.Cancel\s*=\s*true\s*;\s*Hide\(\s*\)\s*;");
        var bypasses = Regex.Matches(closing[..hide.Index], @"\bif\s*\(([^()]*)\)\s*(?:\{\s*)?return\s*;")
            .SelectMany(m => Regex.Replace(m.Groups[1].Value, @"\s+", "").Split("||"))
            .ToHashSet(StringComparer.Ordinal);
        Check("关窗提示/仍然收进托盘不退出",
            hide.Success && bypasses.Contains("App.ForceExit") && bypasses.Contains("!AppState.Settings.StayInTray"));
        Check("托盘精简/允许关闭主窗", bypasses.Contains("App.Instance?.IsTrimming==true"));

        // v1.46.1 / S15：启动时最小化必须「首帧之前」就藏好。
        //
        // 老写法是先 Show() 再 Hide()，中间隔着刷红点、两张报告、建托盘、起调度定时器——
        // 那段距离足够首帧画到屏幕上，现象是「闪一下才消失」。
        // 这里只守「顺序」这一件事：四样属性全部出现在 Show() 之前，且不再有 Show 之后的 Hide。
        var appXaml = ReadRepoSources("Dragon Butler/App.xaml");
        var appCs = ReadRepoSources("Dragon Butler/App.xaml.cs");
        var presentation = ReadRepoSources("Dragon Butler/StartupPresentation.cs");

        Check("隐藏启动/App.xaml 没有 StartupUri（主窗只能由代码建）",
            !appXaml.Contains("StartupUri", StringComparison.Ordinal));

        // 找 OnStartup 里那一段：从「算 hiddenStart」到 Show() 为止。
        string startup = SliceBetween(appCs, "bool hiddenStart =", "_win.Show();");
        Check("隐藏启动/先算 hiddenStart 再建窗", startup.Length > 0);
        // 四个属性必须在 Show() 之前设好——写成一个方法调用，顺序由 StartupPresentation 保证。
        var beforeShow = Regex.Match(startup, @"if\s*\(\s*hiddenStart\s*\)\s*StartupPresentation\.PrepareHidden\(\s*_win\s*\)\s*;");
        Check("隐藏启动/属性全部在 Show() 之前摆好", beforeShow.Success);
        // Show() 之前不许出现 Hide()：真要藏，四样属性就够，Hide 只允许紧跟在 Show() 之后。
        Check("隐藏启动/Show() 之前没有 Hide()",
            !startup.Contains("_win.Hide(", StringComparison.Ordinal));
        // 老写法把 Hide() 放在 RefreshNoticeBadge / 两张报告 / BuildTray 之后，
        // 那是「首帧已出」的证据。后来那段里不许再有 Hide。
        string lateStartup = SliceBetween(appCs, "BuildTray();\r\n        _scheduleTimer.Tick", "if (AppState.Settings.SentinelEnabled)");
        Check("隐藏启动/建托盘之后不再补一次 Hide()",
            lateStartup.Length > 0 && !lateStartup.Contains("_win.Hide(", StringComparison.Ordinal),
            lateStartup.Length == 0 ? "切成空片" : "");

        // StartupPresentation 里四个属性一个都不能少，且都必须早于任何 Show 调用。
        string prepare = SliceBetween(presentation, "public static void PrepareHidden(Window window)", "}");
        foreach (string prop in new[] { "ShowActivated", "WindowState", "Visibility", "ShowInTaskbar" })
        {
            Check($"隐藏启动/PrepareHidden 设了 {prop}",
                prepare.Contains(prop + " = ", StringComparison.Ordinal));
        }
        Check("隐藏启动/PrepareHidden 里没有 Show（属性必须先于展示）",
            !prepare.Contains(".Show(", StringComparison.Ordinal));
        Check("隐藏启动/恢复时按相反方向还原",
            SliceBetween(presentation, "public static void RestoreShown(Window window)", "}")
                .Contains("ShowInTaskbar = true", StringComparison.Ordinal)
            && presentation.Contains("Visibility = Visibility.Visible", StringComparison.Ordinal));
        Check("隐藏启动/手动建窗后登记 Application.MainWindow",
            Regex.IsMatch(appCs, @"MainWindow\s*=\s*_win\s*;"));
        // OnStartup 里调度器的接线不许被这次改动碰到（v1.46.0 刚修好）。
        Check("隐藏启动/没动 ScheduleTrigger 的接线",
            appCs.Contains("_scheduleTimer.Tick += (_, _) => ScheduleTrigger.Check(DateTime.Now);", StringComparison.Ordinal)
            && appCs.Contains("_scheduleTimer.Start();", StringComparison.Ordinal));
        // 值域：开关两个都开才藏，缺一个都不藏。
        Check("隐藏启动/两个开关都开才算数",
            presentation.Contains("startMinimized && stayInTray", StringComparison.Ordinal));

        Console.WriteLine($"RESULT uirules fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
