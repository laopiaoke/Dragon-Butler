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
    private void Step24()
    {
        // ---- 11o) v1.38 开机自启、删除度假模式与「启动后自动跑」 ----
        // 开机自启这一项原来坏在「只写不校验」：写失败、路径失效、被任务管理器禁用，
        // 三种情况界面上的勾子都还是亮的。所以断言要盯住「回读」这一个动作。
        autoCs = ReadRepoSources("Dragon Butler/AutoStartHelper.cs");
        scheduleCs = ReadRepoSources("Dragon Butler/SchedulePlanner.cs");
        appCsV138 = ReadRepoSources("Dragon Butler/App.xaml.cs");
        Check("自启/写完立刻回读", SliceBetween(autoCs, "public static (bool Ok, string Message) Set(bool enable)", "\n    }").Contains("GetValue(ValueName)"));
        Check("自启/指向别的 exe 就算没开", SliceBetween(autoCs, "public static bool IsSet()", "\n    }").Contains("OrdinalIgnoreCase"));
        Check("自启/路径取不到就说清楚", autoCs.Contains("Assembly.GetEntryAssembly()?.Location"));
        Check("自启/清掉任务管理器的禁用标记", autoCs.Contains("StartupApproved") && autoCs.Contains("ClearApprovedBlock"));
        Check("自启/失败要带一句人话", SliceBetween(autoCs, "public static (bool Ok, string Message) Set(bool enable)", "\n    }").Contains("Message"));
        Check("自启/启动时会对账", autoCs.Contains("public static string? Heal(bool wanted)"));
        // 「想要自启」这个意图得自己记一份——注册表只答得出「现在有没有那条值」，
        // 答不出「是不是被系统清掉了」。没有意图就没法判断该不该补写。
        Check("自启/记下用户想要什么", appStateCs.Contains("public bool AutoStartOnBoot"));
        Check("自启/启动时按意图对账", appCsV138.Contains("AutoStartHelper.Heal(AppState.Settings.AutoStartOnBoot)"));
        Check("自启/写失败不记意图", SliceBetween(mainCs, "private void TgAutoStart_Click", "\n    }").Contains("return;"));
        // 向导在 Loaded 之后才弹，而设置页的开关是构造时按旧值摆好的。
        // 向导关掉之后必须重读一遍，否则「向导里勾了、设置里还关着」——
        // 值已经写进注册表和 settings.json，只有界面停在几分钟前。
        Check("自启/向导关掉后设置页要重读",
            SliceBetween(mainCs, "private void TryShowFirstRunWizard", "private void FlashStatusError")
                .Contains("LoadSettingsToUi();"));
        Check("自启/设置页的开关读的是注册表不是设置字段",
            mainCs.Contains("TgAutoStart.IsChecked = AutoStartHelper.IsSet();"));

        // 开关这边：拨不动的时候要把勾子拨回去，别让界面替失败圆场。
        Check("自启/写失败把勾子拨回来", SliceBetween(mainCs, "private void TgAutoStart_Click", "\n    }").Contains("_suppressToggle"));
        Check("自启/界面上说了结果", SliceBetween(mainCs, "private void TgAutoStart_Click", "\n    }").Contains("PublishLog("));

        // 删掉的两个功能：字段、开关、徽章、调度参数、启动调用，一处都不该留。
        Check("删除/没有度假模式这个字段", !appStateCs.Contains("VacationMode"));
        Check("删除/没有启动后自动跑这个字段", !appStateCs.Contains("AutoRunOnLaunch"));
        Check("删除/调度器不再收这个参数", !scheduleCs.Contains("bool vacation"));
        // 判控件名，不判「度假」两个字——v1.38 的注释里正写着「这里原来有个度假徽章」，
        // 那条注释是有用的记录，不该被断言逼着删。
        Check("删除/界面上没有度假徽章", !mainXaml.Contains("x:Name=\"VacationBadge\""));
        Check("删除/界面上没有那两个开关", !mainXaml.Contains("TgVacation") && !mainXaml.Contains("TgAutoRun"));
        Check("删除/启动时不再自动开跑", !appCsV138.Contains("AutoRunOnLaunch"));

        // 向导第 3 步换成了「开机自启 + 启动时最小化到托盘」，共五步。
        Check("向导/一共五步", wizardCs.Contains("private const int TotalSteps = 5;"));
        Check("向导/第 3 步问开机自启", wizardXaml.Contains("WizAutoStart") && wizardXaml.Contains("WizStartMin"));
        Check("向导/自启页上说了不装服务", wizardXaml.Contains("不装服务"));
        Check("向导/打开要留到最后一起写", SliceBetween(wizardCs, "private void WizAutoStart_Click", "\n    }").Contains("if (!on)"));
        Check("向导/最小化到托盘顺手打开驻留", SliceBetween(wizardCs, "private void WizStartMin_Click", "\n    }").Contains("StayInTray = true"));
        Check("向导/最后一步才真写注册表", SliceBetween(wizardCs, "private void ApplyAll()", "\n    }").Contains("AutoStartHelper.Set(true)"));

        // 这一页的两个开关，设置页里也要有，而且要在同一张卡上。
        Check("设置页/有启动时最小化这一行", mainXaml.Contains("x:Name=\"TgStartMin\""));
        Check("设置页/最小化和驻留排在一起", mainXaml.IndexOf("x:Name=\"TgStayTray\"") > 0
            && mainXaml.IndexOf("x:Name=\"TgStartMin\"") > mainXaml.IndexOf("x:Name=\"TgStayTray\""));
    }

    private void Step25()
    {
        // ---- 11n) v1.37 UI 任务书：首次运行别递刀 + 小按钮长大 + 一半没配要警示 ----
        // 任务书 P0-1：一个工具都没配好时，「一键开跑」点下去必然失败。
        // 处理是「禁用 + 压暗」，不是「整卡消失」——整卡消失会让下面的布局跳一下，
        // 用户也就看不到它在那儿等着。
        Check("首运行/一键开跑先禁用",
            SliceBetween(mainCs, "bool firstRun = view.Phase == HomePhase.FirstRun;", "RenderHomeSteps")
                .Contains("BtnQueueStart.IsEnabled = !firstRun;"));
        Check("首运行/卡片压暗但还看得见",
            mainCs.Contains("QueueCard.Opacity = firstRun ? 0.72 : 1.0;")
            && !mainCs.Contains("QueueCard.Visibility"));
        Check("首运行/队列卡有名字才改得动",
            mainXaml.Contains("x:Name=\"QueueCard\""));
        Check("首运行/事件里也挡一道",
            mainCs.Contains("if (!BtnQueueStart.IsEnabled) return;"));
        Check("首运行/禁用态有灰底",
            SliceBetween(mainXaml, "<Style x:Key=\"LaunchPrimary\"", "</Style>")
                .Contains("Value=\"{DynamicResource ControlBgBrush}\""));
        Check("首运行/禁用态的字色设在 Bd 上",
            // 模板触发器改不到模板父（Button 自己）的 Foreground，会被 Style 的 Setter 压回去，
            // 结果是深色字贴在灰底上、比原来更难读。所以字色必须落在 Bd 的 TextElement.Foreground。
            SliceBetween(mainXaml, "<Style x:Key=\"LaunchPrimary\"", "</Style>")
                .Contains("Property=\"TextElement.Foreground\""));
        Check("首运行/提示行与工具块一起收起",
            mainCs.Contains("TbGridHint.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;")
            && mainCs.Contains("ToolSetupBlock.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;"));

        // 任务书 P0-2：26 高 / 11 号字在深色底上又小又密，低于可点中尺寸。
        Check("小按钮/长到 30 高",
            SliceBetween(mainXaml, "<Style x:Key=\"MiniAction\"", "</Style>")
                .Contains("Property=\"Height\" Value=\"30\""));
        Check("小按钮/字号 11.5",
            SliceBetween(mainXaml, "<Style x:Key=\"MiniAction\"", "</Style>")
                .Contains("Property=\"FontSize\" Value=\"11.5\""));
        // v1.42 / M8-3：五个视图入口按 D10 全部撤掉——前四个用户确认用不上，
        // 「配置组」搬进了 BetterGI 工具行的展开区。这里改成守「一个不剩」。
        Check("队列卡/五个视图入口一个不剩",
            new[] { "BtnWeeklyPlan_Click", "BtnCalendar_Click", "BtnToday_Click", "BtnDepends_Click", "BtnGroupSlice_Click" }
                .All(h => !mainXaml.Contains(h)) && !mainXaml.Contains("BtnQueueView"));

        // 任务书 P1-4：只配了一部分时也是警示——那正是「一键开跑」会悄悄跳过它们的场景。
        Check("半配/文案用警示色",
            SliceBetween(mainCs, "if (missing.Count == 0)", "catch (Exception ex)")
                .Split(new[] { "WarningBrush" }, StringSplitOptions.None).Length - 1 >= 2);
        Check("半配/配齐了才用三级灰",
            SliceBetween(mainCs, "TbToolsSummary.Text = $\"已配 {ready} 个\";", "}")
                .Contains("TextTertiaryBrush"));
    }

    private void Step26()
    {
        // ---- 11m) v1.35 设置页卡片墙 + 日志图标 + 鼠标拖动换页 ----
        // 设置页：两列四卡，每张卡有图标标题副标题，行与行之间只靠留白和一条极淡的分隔线。
        // 判据全部落在「结构属性」上，不看标签在文件里的先后（v1.32 在这里绊过一跤）。
        settingsPane = SliceBetween(mainXaml, "x:Name=\"PageSettings\"", "</Grid>\n                    </ScrollViewer>");
        // v1.41 / M4-1：卡片墙拆成五个竖排分区，一次只铺一个；这里守的是「五个分区都在、都默认收起」。
        Check("设置页/五个分区竖排铺开",
            new[] { "SecGuard", "SecTiming", "SecNotify", "SecApi", "SecUi" }
                .All(n => settingsPane.Contains("x:Name=\"" + n + "\"")));
        Check("设置页/每张卡都有图标和副标题",
            System.Text.RegularExpressions.Regex.Matches(settingsPane, "<Border Width=\"26\" Height=\"26\" CornerRadius=\"8\" Background=\"\\{DynamicResource BrandSoftBrush\\}\">").Count >= 5);
        Check("设置页/行用 SetRow 而不是每行套一块浅底",
            settingsPane.Contains("Style=\"{StaticResource SetRow}\"")
            && !settingsPane.Contains("Style=\"{StaticResource SettingCard}\""));
        Check("设置页/最后一行不画分隔线", settingsPane.Contains("Style=\"{StaticResource SetRowLast}\""));
        Check("设置页/通知卡还在", settingsPane.Contains("x:Name=\"NotifyCard\""));
        Check("设置页/五张卡都还在",
            settingsPane.Contains("守护与恢复") && settingsPane.Contains("启动与界面")
            && settingsPane.Contains("定时与节奏") && settingsPane.Contains("接口与维护"));
        Check("设置页/不再有 AdvancedBody 和 SystemBody", !mainXaml.Contains("AdvancedBody") && !mainXaml.Contains("SystemBody"));

        // v1.41 / M1-1：日志不再是底栏的一页，「带折角的纸」那个图标跟着它的按钮一起退场；
        // 原来那张纸的内容整块搬进了日志视图（LogLayer），这里改成守「搬过去了」。
        Check("日志视图/有自己的一层", mainXaml.Contains("x:Name=\"LogLayer\""));
        Check("日志视图/顶上写着在筛什么", mainXaml.Contains("x:Name=\"TbLogFilter\""));
        Check("日志视图/一条条列出来", mainXaml.Contains("x:Name=\"LogListHost\""));
        Check("日志视图/没记到东西也有话说", mainXaml.Contains("x:Name=\"TbLogEmpty\""));
        Check("日志视图/能关掉回到刚才那一页", mainXaml.Contains("BtnLogClose_Click"));
        Check("旧日志按钮已经退场", !mainXaml.Contains("x:Name=\"SegPageRun\""));

        // 鼠标拖动换页：按下不抢，横向 40px 才夺鼠标，松手按 Pager.Swipe 判。
        Check("换页/鼠标按下只记坐标不抢", mainCs.Contains("PageClip_PreviewMouseLeftButtonDown"));
        Check("换页/横向 40px 才算想换页", mainCs.Contains("PageDragStart = 40"));
        Check("换页/竖着划得多就让位给滚动", mainCs.Contains("Math.Abs(dy) > Math.Abs(dx)"));
        Check("换页/别人抓着鼠标就不掺和", mainCs.Contains("Mouse.Captured != null && !ReferenceEquals(Mouse.Captured, PageClip)"));
        Check("换页/拖动时四页跟着走", mainCs.Contains("DragPages(dx * 0.85)"));
        Check("换页/松手按同一套阈值判", SliceBetween(mainCs, "private void PageClip_PreviewMouseLeftButtonUp", "\n    }").Contains("Pager.Swipe(_page, _pageDragShift, PageClip.ActualWidth)"));
    }

}
