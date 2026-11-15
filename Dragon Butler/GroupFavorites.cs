using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler
{
    /// <summary>
    /// v1.15 / L4：配置组的「常用」。
    ///
    /// <para>
    /// 只做一件事：把常跑的那几个组排到最前面，让它们在十来个组里不用找。
    /// 不做拖拽、不做分组、不存权重——收藏就是个是非题，多点一次就是取消。
    /// </para>
    /// <para>
    /// 这里全是纯函数，不读 AppState、不碰磁盘：排序错了只可能是这里错了，
    /// 界面那边没有别的地方能解释它。
    /// </para>
    /// </summary>
    public static class GroupFavorites
    {
        /// <summary>按「常用优先」重排，其余保持原来的相对顺序（稳定排序）。</summary>
        public static List<string> Sort(IReadOnlyList<string> names, IReadOnlyList<string>? favorites)
        {
            var result = new List<string>(names?.Count ?? 0);
            if (names == null) return result;

            var fav = ToSet(favorites);
            if (fav.Count == 0)
            {
                result.AddRange(names);
                return result;
            }

            foreach (string n in names)
                if (fav.Contains(n)) result.Add(n);
            foreach (string n in names)
                if (!fav.Contains(n)) result.Add(n);
            return result;
        }

        /// <summary>加/取消一条，返回新数组。名字做去空白处理，空白名直接忽略。</summary>
        public static string[] Toggle(IReadOnlyList<string>? favorites, string? name)
        {
            string key = (name ?? "").Trim();
            var list = new List<string>();
            if (favorites != null)
                foreach (string f in favorites)
                    if (!string.IsNullOrWhiteSpace(f) && !list.Contains(f, StringComparer.OrdinalIgnoreCase))
                        list.Add(f);

            if (key.Length == 0) return list.ToArray();

            int at = list.FindIndex(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) list.RemoveAt(at);
            else list.Add(key);
            return list.ToArray();
        }

        public static bool IsFavorite(IReadOnlyList<string>? favorites, string? name)
        {
            string key = (name ?? "").Trim();
            if (key.Length == 0 || favorites == null) return false;
            return favorites.Any(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>去掉已经不存在的组名（BetterGI 里删掉了配置组时用）。</summary>
        public static string[] Prune(IReadOnlyList<string>? favorites, IReadOnlyList<string>? known)
        {
            if (favorites == null) return Array.Empty<string>();
            var valid = ToSet(known);
            return favorites
                .Where(f => valid.Contains(f))
                .ToArray();
        }

        private static HashSet<string> ToSet(IReadOnlyList<string>? items)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (items == null) return set;
            foreach (string s in items)
                if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
            return set;
        }
    }
}
