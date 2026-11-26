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
    /// v1.8 / F9：全局热键的解析层。
    /// 不验真实注册——CI 没有桌面会话，RegisterHotKey 必失败；
    /// 但「字符串怎么变成 修饰键+虚拟键」是纯函数，能全分支断言。
    /// </summary>
    private static int RunHotkey()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 默认档位：默认关，默认组合开跑 Ctrl+Alt+R、停止 Ctrl+Alt+S
        var def = new AppSettings();
        Check("热键默认关闭", !def.HotkeyEnabled);
        Check("默认开跑组合", def.HotkeyStart == "Ctrl+Alt+R", def.HotkeyStart);
        Check("默认停止组合", def.HotkeyStop == "Ctrl+Alt+S", def.HotkeyStop);

        // --- 解析：常见写法 ---
        var p = HotkeyService.Parse("Ctrl+Alt+R");
        Check("解析 Ctrl+Alt+R 成功", p != null);
        Check("Ctrl+Alt+R 修饰键=Control|Alt", p != null && p.Value.Modifiers == (HotkeyService.ModControl | HotkeyService.ModAlt),
            p?.Modifiers.ToString() ?? "null");
        Check("Ctrl+Alt+R 主键='R'(0x52)", p != null && p.Value.VirtualKey == 0x52, p?.VirtualKey.ToString() ?? "null");

        // 大小写、顺序、别名都要认
        Check("小写也认", HotkeyService.Parse("ctrl+alt+r") != null);
        Check("顺序无关", HotkeyService.Parse("Alt+Ctrl+R") != null);
        Check("control 别名", HotkeyService.Parse("control+shift+f5") != null);
        Check("win 别名", HotkeyService.Parse("Win+R") != null);
        Check("空格容忍", HotkeyService.Parse(" Ctrl + Alt + R ") != null);

        // --- 非法输入一律 null：裸主键会霸占打字，必须拒绝 ---
        Check("空串拒绝", HotkeyService.Parse("") == null);
        Check("null 拒绝", HotkeyService.Parse(null) == null);
        Check("裸主键拒绝", HotkeyService.Parse("R") == null);
        Check("只有修饰键拒绝", HotkeyService.Parse("Ctrl+Alt") == null);
        Check("未知主键拒绝", HotkeyService.Parse("Ctrl+Alt+NotAKey") == null);
        Check("未知修饰键拒绝", HotkeyService.Parse("Hyper+R") == null);

        // --- Normalize：回填输入框时用的规范写法 ---
        Check("Normalize 规范大小写", HotkeyService.Normalize("ctrl+alt+r").Contains("Ctrl"), HotkeyService.Normalize("ctrl+alt+r"));
        Check("Normalize 空串不抛", HotkeyService.Normalize("") == "");
        Check("Normalize null 不抛", HotkeyService.Normalize(null) == "");

        // --- 特殊主键 ---
        foreach (var key in new[] { "F1", "F12", "Space", "Escape", "Tab", "Enter", "0", "9" })
            Check("主键 " + key + " 可解析", HotkeyService.Parse("Ctrl+" + key) != null);

        Console.WriteLine($"RESULT hotkey fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.8 / F10：任务栏进度与托盘文字的纯函数。
    /// 这一段最容易写错的地方是「分母」，所以断言重点在 0 项、1 项、全跑成三种边界。
    /// </summary>
    private static int RunProgress()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        Check("0/0 进度为 0", QueueProgress.Fraction(0, 0) == 0);
        Check("负数不外溢", QueueProgress.Fraction(-1, 5) == 0);
        Check("0/3 为 0", QueueProgress.Fraction(0, 3) == 0);
        Check("1/3 约 0.333", Math.Abs(QueueProgress.Fraction(1, 3) - 1.0 / 3.0) < 0.0001);
        Check("3/3 为 1", QueueProgress.Fraction(3, 3) == 1);
        Check("超量夹到 1", QueueProgress.Fraction(5, 3) == 1);
        Check("分母为负按 0", QueueProgress.Fraction(2, -3) == 0);

        Check("总数 0 时文案是准备中", QueueProgress.Describe(0, 0, null) == "准备中", QueueProgress.Describe(0, 0, null));
        Check("无工具名只给计数", QueueProgress.Describe(1, 3, null) == "1/3", QueueProgress.Describe(1, 3, null));
        Check("空工具名只给计数", QueueProgress.Describe(1, 3, "  ") == "1/3", QueueProgress.Describe(1, 3, "  "));
        Check("有工具名带分隔符", QueueProgress.Describe(1, 3, "BetterGI") == "1/3 · BetterGI", QueueProgress.Describe(1, 3, "BetterGI"));

        Check("空闲时用缺省名", QueueProgress.TrayText(false, 0, 0, null) == "一条龙管家", QueueProgress.TrayText(false, 0, 0, null));
        Check("空闲时用自定义名", QueueProgress.TrayText(false, 1, 3, null, "自定义") == "自定义", QueueProgress.TrayText(false, 1, 3, null, "自定义"));
        Check("运行中有进度", QueueProgress.TrayText(true, 1, 3, "BetterGI").Contains("1/3"), QueueProgress.TrayText(true, 1, 3, "BetterGI"));

        // 托盘 tooltip 有长度上限（约 63 字符），超了必须截断加省略号
        string longName = new string('长', 100);
        string tray = QueueProgress.TrayText(true, 1, 3, longName);
        Check("托盘文字超长截断", tray.Length <= 64, tray.Length.ToString());
        Check("托盘文字截断加省略号", tray.EndsWith("…", StringComparison.Ordinal), tray.Substring(Math.Max(0, tray.Length - 3)));

        Console.WriteLine($"RESULT progress fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.8 / F15：命令面板的匹配与分发表。
    /// 面板本身是窗口，CI 里开不了；但「有哪些命令、怎么筛、id 撞不撞」全在 CommandRegistry。
    /// </summary>
    private static int RunCommand()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var all = CommandRegistry.All;
        Check("命令表非空", all.Count >= 20, all.Count.ToString());

        // id 必须唯一，否则 Executed 回传时分不清是哪条
        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool dup = false;
        foreach (var c in all) if (!ids.Add(c.Id)) dup = true;
        Check("命令 id 唯一", !dup);

        // 每条都得有标题和分组，空标题会渲染成一行空白
        Check("每条都有标题", all.All(c => c.Title.Length > 0));
        Check("每条都有分组", all.All(c => c.Group.Length > 0));
        Check("每条 id 不含空白", all.All(c => c.Id.IndexOf(' ') < 0));

        // 空查询：面板刚打开要看到内容
        var empty = CommandRegistry.Search("", 8);
        Check("空查询返回 8 条", empty.Count == 8, empty.Count.ToString());
        Check("空查询按原序", empty.Count > 0 && empty[0].Id == all[0].Id, empty.Count > 0 ? empty[0].Id : "none");

        // 子串命中：标题优先于关键字
        var q1 = CommandRegistry.Search("日历", 8);
        Check("搜『日历』命中挂机日历", q1.Count > 0 && q1[0].Id == "view.calendar", q1.Count > 0 ? q1[0].Id : "none");

        var q2 = CommandRegistry.Search("token", 8);
        Check("搜 token 命中复制令牌", q2.Count > 0 && q2[0].Id == "tool.copyToken", q2.Count > 0 ? q2[0].Id : "none");

        var q3 = CommandRegistry.Search("浮条", 8);
        Check("搜『浮条』命中迷你浮条", q3.Count > 0 && q3[0].Id == "ui.miniBar", q3.Count > 0 ? q3[0].Id : "none");

        Check("无匹配返回空", CommandRegistry.Search("zzz-not-a-command", 8).Count == 0);
        Check("limit 生效", CommandRegistry.Search("", 3).Count == 3);

        // 标题命中的排序必须在关键字命中之前
        var mixed = CommandRegistry.Search("开跑", 8);
        Check("标题命中排在前面", mixed.Count > 0 && mixed[0].Id == "run.queue", mixed.Count > 0 ? mixed[0].Id : "none");

        // 动态命令：单跑某工具，id 带工具 id，且不与固定表撞
        var extra = CommandRegistry.ToolCommands(new[] { ("bettergi", "BetterGI"), ("ok-ww", "ok-ww") });
        Check("动态命令数量", extra.Count == 2, extra.Count.ToString());
        Check("动态命令 id 带前缀", extra[0].Id.StartsWith("run.tool:", StringComparison.Ordinal), extra[0].Id);
        Check("动态命令 id 含工具 id", extra[0].Id == "run.tool:bettergi", extra[0].Id);
        Check("动态命令不撞固定表", extra.All(e => !ids.Contains(e.Id)));
        Check("动态命令跳过空名", CommandRegistry.ToolCommands(new[] { ("", "") }).Count == 0);

        // 动态命令要能一起被搜到
        var merged = new List<CommandRegistry.Command>(CommandRegistry.All);
        merged.AddRange(extra);
        var hitTool = CommandRegistry.Search(merged, "BetterGI", 8);
        Check("合并后能搜到自定义工具", hitTool.Count > 0 && hitTool[0].Id == "run.tool:bettergi",
            hitTool.Count > 0 ? hitTool[0].Id : "none");

        // 面板里所有 id 都必须被分发处理（漏一条就是点了没反应）
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "run.queue", "run.continue", "run.stop", "run.single",
            "view.schedule", "view.queue", "view.tools", "view.advanced", "view.log", "view.calendar",
            "view.today", "view.logrules", "view.depends", "view.groups",
            "view.preflight", "view.presets", "view.notify",
            "view.handover",
            "tool.copyToken", "tool.apiToggle", "tool.lanToggle", "tool.snapshot",
            "ui.miniBar", "ui.theme", "ui.hotkey",
            "sys.openFolder", "sys.check", "sys.about",
        };
        var missing = all.Where(c => !known.Contains(c.Id)).Select(c => c.Id).ToList();
        Check("命令表没有未实现的分支", missing.Count == 0, string.Join(",", missing));

        Console.WriteLine($"RESULT command fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
