using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// v1.21：工具网格的排列逻辑。
///
/// 用户要的是「像手机桌面那样」——一格一个工具、点开就是这个工具自己的设置。
/// 这里只放纯逻辑（顺序、增删、排位），不碰任何 WPF 控件，
/// 所以它能在探针里被完整断言；界面那一层只负责把结果画成方块。
///
/// 三条不肯让步：
/// - 顺序是用户排的，就永远按用户排的来。新加进来的工具排到最后，不插队。
/// - 已经不在工具表里的 id 要静默剔除（删过的工具不该在桌面上留个空位）。
/// - 排到底了就不动（往右移最后一格＝没反应），不循环——循环会让人以为自己点错了。
/// </summary>
public static class ToolGrid
{
    /// <summary>
    /// 桌面一共 8 格。
    /// <para>
    /// v1.26：从 4 列 2 行改成 **2 列 4 行**。格子从设置页搬进启动页那张卡以后，
    /// 卡本身只占半屏宽（和定时卡并排），4 列会把每个格子压到 60px 上下——
    /// 名字只剩三个字、图标和状态字挤成一团。2 列之后每格约 150px，
    /// 名字能写全，行数多出来的两行反正有滚动条。
    /// </para>
    /// </summary>
    public const int Columns = 2;
    public const int Rows = 4;
    public const int Capacity = Columns * Rows;

    /// <summary>
    /// 算出桌面上该按什么顺序摆哪些工具。
    /// <para>saved 里排过序的优先且保持原序；没排过的按 <paramref name="naturalOrder"/> 的先后补在后面；saved 里已失效的剔除。</para>
    /// </summary>
    public static List<string> Order(IEnumerable<string>? naturalOrder, IReadOnlyList<string>? saved)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var known = new List<string>();
        if (naturalOrder != null)
        {
            foreach (string id in naturalOrder)
            {
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                known.Add(id);
            }
        }
        // seen 现在装着「存在的工具」，下面重新用它做去重
        var exists = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

        if (saved != null)
        {
            foreach (string id in saved)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!exists.Contains(id)) continue;      // 已删除的工具不留空位
                if (result.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(id);
            }
        }
        foreach (string id in known)
        {
            if (result.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// 把 <paramref name="toolId"/> 往左/右挪一格。越界不动，返回 null（调用方据此跳过保存）。
    /// </summary>
    public static List<string>? Move(IReadOnlyList<string>? order, string? toolId, int delta)
    {
        if (order == null || order.Count == 0 || string.IsNullOrWhiteSpace(toolId) || delta == 0) return null;
        var list = order.ToList();
        int from = IndexOf(list, toolId);
        if (from < 0) return null;
        int to = from + delta;
        if (to < 0 || to >= list.Count) return null;
        (list[from], list[to]) = (list[to], list[from]);
        return list;
    }

    /// <summary>
    /// v1.22：长按拖拽用。把 <paramref name="toolId"/> 搬到 <paramref name="targetIndex"/> 那个位置
    /// （其余元素依次让位，不是交换——交换会让用户抓的那一格之后顺序全变）。
    /// 目标越界就夹到两端，返回 null 表示什么都没变。
    /// </summary>
    public static List<string>? MoveTo(IReadOnlyList<string>? order, string? toolId, int targetIndex)
    {
        if (order == null || order.Count == 0 || string.IsNullOrWhiteSpace(toolId)) return null;
        var list = order.ToList();
        int from = IndexOf(list, toolId);
        if (from < 0) return null;
        int to = Math.Clamp(targetIndex, 0, list.Count - 1);
        if (to == from) return null;
        list.RemoveAt(from);
        list.Insert(to, toolId);
        return list;
    }

    /// <summary>从排列里去掉一个（删除工具后调用）。</summary>
    public static List<string> Remove(IReadOnlyList<string>? order, string? toolId)
    {
        var list = order?.ToList() ?? new List<string>();
        if (string.IsNullOrWhiteSpace(toolId)) return list;
        list.RemoveAll(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>补到桌面末尾（新加工具时调用）；已存在则不重复加。</summary>
    public static List<string> Append(IReadOnlyList<string>? order, string? toolId)
    {
        var list = order?.ToList() ?? new List<string>();
        if (string.IsNullOrWhiteSpace(toolId)) return list;
        if (list.Contains(toolId, StringComparer.OrdinalIgnoreCase)) return list;
        list.Add(toolId);
        return list;
    }

    /// <summary>能上桌的前 <see cref="Capacity"/> 个。</summary>
    public static List<string> Visible(IReadOnlyList<string>? order)
        => (order ?? Array.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Take(Capacity).ToList();

    /// <summary>排不进桌面的（第 9 个及以后）。界面据此提示「还有 N 个没上桌面」。</summary>
    public static int OverflowCount(IReadOnlyList<string>? order)
        => Math.Max(0, (order?.Count ?? 0) - Capacity);

    private static int IndexOf(IReadOnlyList<string> list, string toolId)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], toolId, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
