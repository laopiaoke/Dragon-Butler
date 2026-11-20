using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// v1.27：把「桌面格子」从一个固定 8 格改成可翻页的 2×3 桌面。
///
/// 为什么是 2 列 3 行：卡只有半屏宽，2 列每格约 150px 名字才写得全；
/// 3 行是因为一格 54 高，4 行会把下面的「一键开跑」挤出屏幕，3 行刚好收在视线里，
/// 多出来的工具交给翻页——翻页比缩格子诚实，格子缩到看不清等于没放。
///
/// 这一层只有纯逻辑（不碰任何 WPF 控件），所以探针能整片断言它：
/// - <see cref="Normalize"/> 负责把存盘的页面修好（剔除已删工具、补进新工具、每页不超 6 格）
/// - <see cref="Insert"/> 负责拖拽落点（插入不是交换，被顶出去的那一格顺延到下一页）
/// - <see cref="RemovePageAt"/> 负责「删掉一整页」
/// </summary>
public static class ToolPages
{
    /// <summary>一页 3 列。v1.30：主窗那张卡缩了一半，2 列会把每格拉得很宽、四行又太高。</summary>
    public const int Columns = 3;

    /// <summary>一页 2 行。横 3 竖 2 就是「一屏六格」，和独立窗口那张桌子同一个排法。</summary>
    public const int Rows = 2;

    /// <summary>一页 6 格。</summary>
    public const int PerPage = Columns * Rows;

    /// <summary>
    /// 最多 9 页。不是技术上限，是「页码点」的上限——
    /// 超过 9 个点就没人去数了，那时候该做的是删工具而不是加页。
    /// </summary>
    public const int MaxPages = 9;

    /// <summary>
    /// 把存下来的页面整理成可用的形状。
    /// <para>
    /// 三件事：①丢掉已经不存在的工具（删过的工具不该在桌面上留空位）
    /// ②同一页里重复的只留第一个 ③每页超出的顺延到下一页。
    /// </para>
    /// <para>
    /// 注意：**不会**把 <paramref name="known"/> 里没出现在页面上的工具补回来。
    /// 页面是「桌面上摆什么」的唯一真相——「删掉一整页」之所以能删干净，靠的就是这条。
    /// </para>
    /// </summary>
    public static List<List<string>> Normalize(IEnumerable<IEnumerable<string>?>? pages, IEnumerable<string>? known)
    {
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (known != null)
            foreach (string id in known)
                if (!string.IsNullOrWhiteSpace(id)) exists.Add(id);

        var result = new List<List<string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (pages != null)
        {
            foreach (var raw in pages)
            {
                var page = new List<string>();
                if (raw != null)
                {
                    foreach (string id in raw)
                    {
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        if (!exists.Contains(id)) continue;   // 已删除的工具不留空位
                        if (!seen.Add(id)) continue;          // 同一页重复的只留一个
                        page.Add(id);
                    }
                }
                result.Add(page);
            }
        }

        if (result.Count == 0) result.Add(new List<string>());

        // 每页最多 PerPage；超出的顺延到下一页（逐页推，推到需要就开新页）
        for (int i = 0; i < result.Count; i++)
        {
            while (result[i].Count > PerPage)
            {
                string overflowId = result[i][PerPage];
                result[i].RemoveAt(PerPage);
                if (i + 1 >= result.Count) result.Add(new List<string>());
                result[i + 1].Insert(0, overflowId);
            }
        }

        while (result.Count > MaxPages)
        {
            result[MaxPages - 1].AddRange(result[MaxPages]);
            result.RemoveAt(MaxPages);
        }

        // v1.29：页数不再是「用户摆出来的形状」，而是数量的一个函数————
        // 收尾统一按 6 个一页重切一遍，空页、半空的页都会被收掉。
        // 这一段必须放在最后：前面的「删掉已不存在的工具」会让某一页空出来，
        // 不重切的话那一页会一直挂在那儿，页码点比实际内容多。
        return Repack(result);
    }

    /// <summary>把页面拍平成一条顺序表（执行顺序 = 「页 0 左→右、上→下，再页 1……」）。</summary>
    public static List<string> Flatten(IEnumerable<IEnumerable<string>?>? pages)
    {
        var list = new List<string>();
        if (pages == null) return list;
        foreach (var page in pages)
            if (page != null)
                foreach (string id in page)
                    if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
        return list;
    }

    /// <summary>某个工具在第几页第几格；不在桌面上返回 (-1, -1)。</summary>
    public static (int Page, int Slot) Locate(IEnumerable<IEnumerable<string>?>? pages, string? toolId)
    {
        if (pages == null || string.IsNullOrWhiteSpace(toolId)) return (-1, -1);
        int p = 0;
        foreach (var page in pages)
        {
            if (page != null)
            {
                int s = 0;
                foreach (string id in page)
                {
                    if (string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase)) return (p, s);
                    s++;
                }
            }
            p++;
        }
        return (-1, -1);
    }

    /// <summary>
    /// 拖拽落点：把 <paramref name="toolId"/> 放到第 <paramref name="page"/> 页第 <paramref name="slot"/> 格。
    /// <para>
    /// 插入不是交换——交换会让「我把它拖到第二格」变成「它和第二格互换」，
    /// 后面的顺序全不动，看着像没生效（v1.22 就定下的规矩，这里继续）。
    /// 被顶出去的那一格顺延到下一页第一位，不是丢掉。
    /// </para>
    /// </summary>
    public static List<List<string>> Insert(IEnumerable<IEnumerable<string>?>? pages, string? toolId, int page, int slot)
    {
        var list = Clone(pages);
        if (string.IsNullOrWhiteSpace(toolId)) return list;
        if (list.Count == 0) list.Add(new List<string>());

        // 先把它从原处摘掉（同页内移动也走这条路，避免出现两份）
        foreach (var pg in list) pg.RemoveAll(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase));

        if (page < 0) page = 0;
        if (page > list.Count) page = list.Count;
        if (page == list.Count)
        {
            if (list.Count >= MaxPages) page = list.Count - 1;
            else list.Add(new List<string>());
        }
        if (slot < 0) slot = 0;
        if (slot > list[page].Count) slot = list[page].Count;
        if (slot > PerPage - 1) slot = PerPage - 1;

        list[page].Insert(slot, toolId);

        // 越界的一格往后推，推到需要就开新页
        for (int i = page; i < list.Count; i++)
        {
            while (list[i].Count > PerPage)
            {
                string overflowId = list[i][PerPage];
                list[i].RemoveAt(PerPage);
                if (i + 1 >= list.Count)
                {
                    if (list.Count >= MaxPages) return list;   // 到顶了就停，多的留在本页最后
                    list.Add(new List<string>());
                }
                list[i + 1].Insert(0, overflowId);
            }
        }
        return list;
    }

    /// <summary>从桌面上拿走一个工具（页面跟着收拢）。</summary>
    public static List<List<string>> Remove(IEnumerable<IEnumerable<string>?>? pages, string? toolId)
    {
        var list = Clone(pages);
        if (string.IsNullOrWhiteSpace(toolId)) return list;
        foreach (var pg in list) pg.RemoveAll(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// 删掉一整页。里面的工具一并从桌面上消失（它们的档案还在，只是不再摆出来）。
    /// 只剩一页时不允许删——桌面上必须至少留一页，否则「＋」都没处可点。
    /// </summary>
    public static List<List<string>> RemovePageAt(IEnumerable<IEnumerable<string>?>? pages, int index)
    {
        var list = Clone(pages);
        if (list.Count <= 1) return list;
        if (index < 0 || index >= list.Count) return list;
        list.RemoveAt(index);
        return list;
    }

    /// <summary>加一页空页（到 9 页上限就不加）。</summary>
    public static List<List<string>> AddPage(IEnumerable<IEnumerable<string>?>? pages)
    {
        var list = Clone(pages);
        if (list.Count == 0) list.Add(new List<string>());
        if (list.Count < MaxPages) list.Add(new List<string>());
        return list;
    }

    /// <summary>去掉尾巴上没人用的空页（新建了又没用，不该一直留着）。最后一页除外。</summary>
    public static List<List<string>> TrimEmptyTail(IEnumerable<IEnumerable<string>?>? pages)
    {
        var list = Clone(pages);
        if (list.Count == 0) list.Add(new List<string>());
        while (list.Count > 1 && list[^1].Count == 0) list.RemoveAt(list.Count - 1);
        return list;
    }

    /// <summary>整页搬走（页码点长按删除、或者整理顺序时用）。</summary>
    public static List<List<string>> Clone(IEnumerable<IEnumerable<string>?>? pages)
    {
        var list = new List<List<string>>();
        if (pages == null) return list;
        foreach (var page in pages) list.Add(page == null ? new List<string>() : page.Where(id => !string.IsNullOrWhiteSpace(id)).ToList());
        return list;
    }

    /// <summary>
    /// 按 Flatten 顺序把工具重新切页：每页 <see cref="PerPage"/> 个，满了才开下一页。
    /// <para>
    /// v1.29：页面不再是一个「可以手动加、可以留白的东西」，而是数量的一个函数——
    /// 8 个工具就是两页（6 + 2），删掉三个就自己收回一页。
    /// 原来那个「＋ 新页」按钮的毛病是：加了页不会自动删，用户手上有几页空页自己都不知道，
    /// 而且翻到第二页看到的常是六个空格子——那页除了让人多点两下，没有任何信息。
    /// </para>
    /// <para>
    /// 一行空白（最后一页没满）是允许的：「＋」要占一格，摆不满的那格本来就是空的。
    /// </para>
    /// </summary>
    public static List<List<string>> Repack(IEnumerable<IEnumerable<string>?>? pages)
    {
        var flat = Flatten(pages);
        var result = new List<List<string>>();
        for (int i = 0; i < flat.Count; i += PerPage)
        {
            var page = new List<string>();
            for (int j = i; j < flat.Count && j < i + PerPage; j++) page.Add(flat[j]);
            result.Add(page);
        }
        if (result.Count == 0) result.Add(new List<string>());
        while (result.Count > MaxPages) result.RemoveAt(result.Count - 1);
        return result;
    }
}
