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
    // ---- 共享状态：原来是 RunToolGrid 里的顶层局部变量 ----
    private List<string> fails = null!;
    private List<string> natural = null!;
    private List<string> o1 = null!;
    private List<string> o2 = null!;
    private List<string> o3 = null!;
    private List<string> o4 = null!;
    private List<string> o5 = null!;
    private List<string> order = null!;
    private List<string>? right;
    private List<string>? left;
    private List<string> nine = null!;
    private List<string> abc = null!;
    private List<string>? m1;
    private List<string>? m2;
    private List<string>? m3;
    private List<string>? m4;
    private List<string> knownAll = null!;
    private List<List<string>> np1 = null!;
    private List<List<string>> np2 = null!;
    private List<List<string>> np3 = null!;
    private (int Page, int Slot) loc;
    private (int Page, int Slot) loc2;
    private List<List<string>> ins = null!;
    private List<List<string>> fullPage = null!;
    private List<List<string>> ins2 = null!;
    private List<List<string>> ins3 = null!;
    private List<List<string>> repacked = null!;
    private List<List<string>> srcPages = null!;
    private List<List<string>> cloned = null!;
    private string mainXaml = null!;
    private string mainCs = null!;
    private string appStateCs = null!;
    private string logCs = null!;
    private string profileCs = null!;
    private string pluginCs = null!;
    private string backupCs = null!;
    private string iconCs = null!;
    private string soundCs = null!;
    private string pathCs = null!;
    private string wizardCs = null!;
    private string wizardXaml = null!;
    private string appXaml = null!;
    private string navStyle = null!;
    private string autoCs = null!;
    private string scheduleCs = null!;
    private string appCsV138 = null!;
    private string settingsPane = null!;
    private string pagerCs = null!;
    private string durCs = null!;
    private string queueCs = null!;
    private string durSandbox = null!;
    private string? durOld;

    /// <summary>按原来的顺序跑一遍；退出码口径与拆分前一致。</summary>
    internal int Run()
    {
        Step0();
        Step1();
        Step2();
        Step3();
        Step4();
        Step5();
        Step6();
        Step7();
        Step8();
        Step9();
        Step10();
        Step11();
        Step12();
        Step13();
        Step14();
        Step15();
        Step16();
        Step17();
        Step18();
        Step19();
        Step20();
        Step21();
        Step22();
        Step23();
        Step24();
        Step25();
        Step26();
        Step27();
        Step28();
        Console.WriteLine($"RESULT toolgrid fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }
    private void Step0()
    {
        fails = new List<string>();

        natural = new List<string> { "bettergi", "march7th", "ok-ww" };
    }

    private void Step1()
    {
        // ---- 1) 常量 ----
        // v1.30：桌面改成 3 列 2 行（一页 6 格）——主窗那张卡缩了一半宽，2 列会把每格拉得很宽。
        // 4 行要 216 高，翻页之后没必要为了少翻一页把「一键开跑」挤出屏幕。
        Check("格子/3 列", ToolGrid.Columns == 3, ToolGrid.Columns.ToString());
        Check("格子/2 行", ToolGrid.Rows == 2, ToolGrid.Rows.ToString());
        Check("格子/一页 6 格", ToolGrid.Capacity == 6, ToolGrid.Capacity.ToString());
        Check("分页/常量转发到 ToolPages", ToolGrid.Columns == ToolPages.Columns && ToolGrid.Rows == ToolPages.Rows);
    }

    private void Step2()
    {
        // ---- 2) Order：没排过就按自然序 ----
        o1 = ToolGrid.Order(natural, null);
        Check("排序/没排过按自然序", o1.Count == 3 && o1[0] == "bettergi" && o1[2] == "ok-ww",
            string.Join(",", o1));
        Check("排序/null 自然序", ToolGrid.Order(null, null).Count == 0);
        Check("排序/null saved", ToolGrid.Order(natural, null).Count == 3);
    }

    private void Step3()
    {
        // ---- 3) Order：用户排过的优先且保持原序 ----
        o2 = ToolGrid.Order(natural, new[] { "ok-ww", "bettergi", "march7th" });
        Check("排序/按用户排的来", string.Join(",", o2) == "ok-ww,bettergi,march7th", string.Join(",", o2));
    }

    private void Step4()
    {
        // ---- 4) Order：已删除的 id 静默剔除，不留空位 ----
        o3 = ToolGrid.Order(natural, new[] { "gone", "ok-ww", "bettergi" });
        Check("排序/删掉的工具不留空位", string.Join(",", o3) == "ok-ww,bettergi,march7th", string.Join(",", o3));
        Check("排序/总数不变", o3.Count == 3, o3.Count.ToString());
    }

    private void Step5()
    {
        // ---- 5) Order：排过的在前、没排过的按自然序补后 ----
        o4 = ToolGrid.Order(natural, new[] { "march7th" });
        Check("排序/排过的在前", string.Join(",", o4) == "march7th,bettergi,ok-ww", string.Join(",", o4));
    }

    private void Step6()
    {
        // ---- 6) Order：大小写不敏感、重复不重复加 ----
        o5 = ToolGrid.Order(natural, new[] { "BETTERGI", "bettergi" });
        Check("排序/大小写不敏感且不重复", string.Join(",", o5) == "BETTERGI,march7th,ok-ww", string.Join(",", o5));
    }

    private void Step7()
    {
        // ---- 7) Move：正常左右挪 ----
        order = new List<string> { "bettergi", "march7th", "ok-ww" };
        right = ToolGrid.Move(order, "bettergi", 1);
        Check("挪动/往右一格", right != null && string.Join(",", right) == "march7th,bettergi,ok-ww",
            right == null ? "null" : string.Join(",", right));
        left = ToolGrid.Move(order, "ok-ww", -1);
        Check("挪动/往左一格", left != null && string.Join(",", left) == "bettergi,ok-ww,march7th",
            left == null ? "null" : string.Join(",", left));
        Check("挪动/原列表没被改", string.Join(",", order) == "bettergi,march7th,ok-ww", string.Join(",", order));
    }

    private void Step8()
    {
        // ---- 8) Move：越界不动（不循环） ----
        Check("挪动/第一个再往左不动", ToolGrid.Move(order, "bettergi", -1) == null);
        Check("挪动/最后一个再往右不动", ToolGrid.Move(order, "ok-ww", 1) == null);
        Check("挪动/delta 为 0 不动", ToolGrid.Move(order, "bettergi", 0) == null);
        Check("挪动/找不到的工具不动", ToolGrid.Move(order, "nope", 1) == null);
        Check("挪动/空 id 不动", ToolGrid.Move(order, "", 1) == null);
        Check("挪动/null 列表不动", ToolGrid.Move(null, "bettergi", 1) == null);
        Check("挪动/空列表不动", ToolGrid.Move(new List<string>(), "bettergi", 1) == null);
    }

    private void Step9()
    {
        // ---- 9) Remove / Append ----
        Check("删除/去掉一个", string.Join(",", ToolGrid.Remove(order, "march7th")) == "bettergi,ok-ww");
        Check("删除/大小写不敏感", ToolGrid.Remove(order, "MARCH7TH").Count == 2);
        Check("删除/不存在的安全", ToolGrid.Remove(order, "nope").Count == 3);
        Check("删除/null 返回空表", ToolGrid.Remove(null, "bettergi").Count == 0);
        Check("追加/排最后", string.Join(",", ToolGrid.Append(order, "zzz-od")) == "bettergi,march7th,ok-ww,zzz-od");
        Check("追加/已存在不重复", ToolGrid.Append(order, "bettergi").Count == 3);
        Check("追加/大小写不敏感不重复", ToolGrid.Append(order, "BETTERGI").Count == 3);
        Check("追加/空 id 不加", ToolGrid.Append(order, "").Count == 3);
        Check("追加/null 返回单元素", ToolGrid.Append(null, "bettergi").Count == 1);
    }

    private void Step10()
    {
        // ---- 10) Visible / OverflowCount（v1.27：改由分页决定容量） ----
        nine = Enumerable.Range(0, 9).Select(i => "t" + i).ToList();
        Check("上桌/只取前 6 个", ToolGrid.Visible(nine).Count == 6, ToolGrid.Visible(nine).Count.ToString());
        Check("上桌/顺序不变", ToolGrid.Visible(nine)[0] == "t0" && ToolGrid.Visible(nine)[5] == "t5");
        Check("溢出/9 个在一页里溢出 3 个", ToolGrid.OverflowCount(nine) == 3, ToolGrid.OverflowCount(nine).ToString());
        Check("溢出/6 个正好不溢出", ToolGrid.OverflowCount(nine.Take(6).ToList()) == 0);
        Check("溢出/3 个不溢出", ToolGrid.OverflowCount(order) == 0);
        Check("溢出/null 是 0", ToolGrid.OverflowCount(null) == 0);
        Check("上桌/null 空表", ToolGrid.Visible(null).Count == 0);
    }

    private void Step11()
    {
        // ---- 10b) MoveTo：长按拖拽落点（v1.22） ----
        // 拖拽是「插到目标位置」，不是「和那一格交换」——交换会让用户抓的那一格之后
        // 所有格子的顺序全变，而拖拽的本意只是「把它挪到这儿」。
        abc = new List<string> { "a", "b", "c", "d" };
        m1 = ToolGrid.MoveTo(abc, "a", 2);
        Check("拖到/往后插", m1 != null && string.Join(",", m1) == "b,c,a,d", string.Join(",", m1 ?? new List<string>()));
        m2 = ToolGrid.MoveTo(abc, "d", 0);
        Check("拖到/往前插", m2 != null && string.Join(",", m2) == "d,a,b,c", string.Join(",", m2 ?? new List<string>()));
        Check("拖到/原表没被改", string.Join(",", abc) == "a,b,c,d", string.Join(",", abc));
        Check("拖到/挪到自己身上不动", ToolGrid.MoveTo(abc, "b", 1) == null);
        Check("拖到/找不到不动", ToolGrid.MoveTo(abc, "zz", 2) == null);
        Check("拖到/空 id 不动", ToolGrid.MoveTo(abc, "", 2) == null);
        Check("拖到/null 表不动", ToolGrid.MoveTo(null, "a", 0) == null);
        m3 = ToolGrid.MoveTo(abc, "b", 99);
        Check("拖到/越界夹到末尾", m3 != null && string.Join(",", m3) == "a,c,d,b", string.Join(",", m3 ?? new List<string>()));
        m4 = ToolGrid.MoveTo(abc, "c", -5);
        Check("拖到/负数夹到开头", m4 != null && string.Join(",", m4) == "c,a,b,d", string.Join(",", m4 ?? new List<string>()));
        Check("拖到/总数不变", (m1?.Count ?? -1) == abc.Count);
    }

    private void Step12()
    {
        // ---- 10c) ToolPages：可翻页桌面（v1.27） ----
        // 桌面从「固定 8 格」变成「2 列 3 行，可以有很多页」。规则全部抽在这个纯函数类里，
        // 界面上那层只负责画和拖——规则能被断言，界面就不会偷偷变形。
        Check("分页/一页 3 列 2 行", ToolPages.Columns == 3 && ToolPages.Rows == 2 && ToolPages.PerPage == 6);
        Check("分页/最多 9 页", ToolPages.MaxPages == 9);

        knownAll = new List<string> { "a", "b", "c", "d", "e", "f", "g", "h" };
        np1 = ToolPages.Normalize(new[] { new[] { "a", "b" }, new[] { "c" } }, knownAll);
        // v1.29：Normalize 末尾会过一遍 Repack，所以「两页共 3 个工具」会被收成一页。
        // 这是新的语义：页数是数量的函数，用户手工留下的空页/半页会被合并。
        Check("分页/整理后按容量收页", np1.Count == 1, np1.Count.ToString());
        Check("分页/整理保留顺序", string.Join(",", np1[0]) == "a,b,c",
            string.Join("|", np1.Select(p => string.Join(",", p))));
        // 关键一条：不存在的工具不补回来。否则「删掉一整页」删不干净——
        // 重新渲染时 Normalize 又把它补回去，页面看着又满了。
        Check("分页/已删工具不补回来", !np1.SelectMany(p => p).Contains("g"),
            string.Join("|", np1.Select(p => string.Join(",", p))));

        np2 = ToolPages.Normalize(new[] { new[] { "a", "a", "b" } }, knownAll);
        Check("分页/同页重复只留一个", string.Join(",", np2[0]) == "a,b", string.Join(",", np2[0]));

        np3 = ToolPages.Normalize(new[] { new[] { "a", "b", "c", "d", "e", "f", "g" } }, knownAll);
        Check("分页/超 6 格顺延下一页", np3.Count == 2 && np3[1].Count == 1 && np3[1][0] == "g",
            string.Join("|", np3.Select(p => string.Join(",", p))));

        Check("分页/空输入也给一页", ToolPages.Normalize(null, knownAll).Count == 1);
        Check("分页/剔掉已不存在的工具",
            ToolPages.Normalize(new[] { new[] { "a", "gone" } }, knownAll)[0].Count == 1);

        Check("分页/拍平成一条顺序表",
            string.Join(",", ToolPages.Flatten(new[] { new[] { "a", "b" }, new[] { "c" } })) == "a,b,c");
        Check("分页/拍平 null 是空表", ToolPages.Flatten(null).Count == 0);

        loc = ToolPages.Locate(new[] { new[] { "a", "b" }, new[] { "c" } }, "c");
        Check("分页/定位到第 2 页第 1 格", loc.Page == 1 && loc.Slot == 0, $"{loc.Page},{loc.Slot}");
        loc2 = ToolPages.Locate(new[] { new[] { "a", "b" } }, "zz");
        Check("分页/找不到返回 -1", loc2.Page == -1 && loc2.Slot == -1, $"{loc2.Page},{loc2.Slot}");
        Check("分页/空 id 定位不炸", ToolPages.Locate(new[] { new[] { "a" } }, "").Page == -1);

        ins = ToolPages.Insert(new[] { new[] { "a", "b", "c" } }, "c", 0, 0);
        Check("分页/拖到开头是插入不是交换", string.Join(",", ins[0]) == "c,a,b", string.Join(",", ins[0]));
        Check("分页/插入不丢东西", ins[0].Count == 3, ins[0].Count.ToString());

        fullPage = new List<List<string>> { new() { "a", "b", "c", "d", "e", "f" } };
        ins2 = ToolPages.Insert(fullPage, "zz", 0, 6);
        Check("分页/插满一格就顺延下一页", ins2.Count == 2 && ins2[1][0] == "f",
            string.Join("|", ins2.Select(p => string.Join(",", p))));
        Check("分页/插入不改原数组", fullPage.Count == 1 && fullPage[0].Count == 6,
            fullPage.Count + "/" + fullPage[0].Count);

        ins3 = ToolPages.Insert(new[] { new[] { "a" }, new[] { "b" } }, "b", 0, 1);
        Check("分页/跨页拖动只有一份", ins3.SelectMany(p => p).Count(id => id == "b") == 1,
            string.Join("|", ins3.Select(p => string.Join(",", p))));
        Check("分页/跨页拖到第 1 页末尾", string.Join(",", ins3[0]) == "a,b", string.Join(",", ins3[0]));

        Check("分页/删掉一页", ToolPages.RemovePageAt(new[] { new[] { "a" }, new[] { "b" } }, 0).Count == 1);
        Check("分页/只剩一页不许删", ToolPages.RemovePageAt(new[] { new[] { "a" } }, 0).Count == 1);
        Check("分页/删越界页不动", ToolPages.RemovePageAt(new[] { new[] { "a" }, new[] { "b" } }, 9).Count == 2);
        Check("分页/加一页", ToolPages.AddPage(new[] { new[] { "a" } }).Count == 2);
        Check("分页/到 9 页不再加", ToolPages.AddPage(Enumerable.Repeat(new[] { "a" }, 9).ToList()).Count == 9);
        Check("分页/去掉尾巴空页", ToolPages.TrimEmptyTail(new[] { new[] { "a" }, Array.Empty<string>() }).Count == 1);
        Check("分页/最后一页空也留着", ToolPages.TrimEmptyTail(new[] { Array.Empty<string>() }).Count == 1);

        // v1.29：Repack——页数是数量的函数，不是用户手动维护的东西。
        repacked = ToolPages.Repack(new List<List<string>>
        {
            new() { "a", "b", "c", "d" },
            new(),
            new() { "e", "f", "g" },
        });
        Check("分页/重切按容量切满", repacked.Count == 2, repacked.Count.ToString());
        Check("分页/重切第一页塞满 6 个", repacked[0].Count == 6, repacked[0].Count.ToString());
        Check("分页/重切第二页只剩多的", repacked[1].SequenceEqual(new[] { "g" }));
        Check("分页/重切丢掉空页",
            ToolPages.Repack(new List<List<string>> { new() { "a" }, new() }).Count == 1);
        Check("分页/重切空输入也给一页",
            ToolPages.Repack(null).Count == 1 && ToolPages.Repack(null)[0].Count == 0);

        // Clone 必须真复制：拖拽时是「在副本上算完再换上去」，
        // 如果 Clone 返回的是同一批 List 引用，中途一次异常就把用户桌面改坏了。
        srcPages = new List<List<string>> { new() { "a" } };
        cloned = ToolPages.Clone(srcPages);
        cloned[0].Add("b");
        Check("分页/复制是真复制", srcPages[0].Count == 1, srcPages[0].Count.ToString());
    }

}
