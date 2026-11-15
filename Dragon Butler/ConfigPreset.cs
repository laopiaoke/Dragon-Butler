using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler
{
    /// <summary>
    /// v1.15：一套「跑什么」的组合 —— 一条龙配置名 + 勾选的配置组。
    ///
    /// 参考 AUTO-MAS 的多配置管理：它让一个脚本存多套配置并依次注入。
    /// 我们不需要注入（BGI 自己从磁盘读配置），需要的只是「记住几套组合，开跑时挑一套」。
    /// 于是最简模型就是三样东西：一个名字、一个配置名、一串组名。
    ///
    /// 存名字不存索引：BGI 重识别之后组名不变、索引会变。
    /// 这套东西**不写 BetterGI 的任何文件**——它只改我们传给 BGI 的命令行参数。
    /// </summary>
    public sealed class ConfigPreset
    {
        /// <summary>稳定标识，改名不变（队列项靠它找预设，改名不会把队列打断）。</summary>
        public string Id { get; set; } = "";

        /// <summary>给人看的名字，如「大号日常」「小号只跑周本」。</summary>
        public string Name { get; set; } = "";

        /// <summary>一条龙配置名（对应 BGI 的 OneDragon 文件名，可空）。</summary>
        public string DragonConfigName { get; set; } = "";

        /// <summary>这套组合勾选的配置组；空 = 不指定，交给 BGI 的默认流程。</summary>
        public string[] Groups { get; set; } = Array.Empty<string>();

        /// <summary>最后一次用它开跑的时间（ISO 字符串，空 = 没用过）。列表排序用。</summary>
        public string LastUsedAt { get; set; } = "";

        public ConfigPreset Clone() => new()
        {
            Id = Id,
            Name = Name,
            DragonConfigName = DragonConfigName,
            Groups = (string[])Groups.Clone(),
            LastUsedAt = LastUsedAt,
        };
    }

    /// <summary>
    /// 配置组合的纯逻辑。全部无副作用、不读磁盘、不看时钟——时钟由调用方传。
    ///
    /// 刻意不做的三件事：
    /// ① 不做「自动切换」（换账号、换登录态）——那属于另一个风险档，这层只管传参；
    /// ② 不做「组名不存在就自动删掉」——静默丢东西是最难排查的一类 bug，只标出来；
    /// ③ 不做嵌套引用（预设引用预设）——用户看不出最后到底跑了什么。
    /// </summary>
    public static class ConfigPresets
    {
        /// <summary>名字最长字符数。</summary>
        public const int MaxNameChars = 24;

        /// <summary>最多允许存在几套。挡住「一键导入几百套」这类把界面撑爆的输入。</summary>
        public const int MaxCount = 32;

        /// <summary>清洗名字：去首尾空白、换行/制表转空格、压连续空格、超长截断。</summary>
        public static string NormalizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
            s = s.Trim();
            return s.Length > MaxNameChars ? s.Substring(0, MaxNameChars).TrimEnd() : s;
        }

        /// <summary>
        /// 校验一套组合能不能存。返回 null = 可以；返回字符串 = 拒绝原因（直接给用户看）。
        /// 判重不看 Id，只看名字——两个都叫「日常」的预设，用户在建第二个的时候就已经分不清了。
        /// </summary>
        public static string? RefuseReason(ConfigPreset? preset, IEnumerable<ConfigPreset>? existing)
        {
            if (preset == null) return "这套组合读不出来，换一个。";
            string name = NormalizeName(preset.Name);
            if (name.Length == 0) return "给这套组合起个名字，比如「大号日常」。";

            var others = (existing ?? Array.Empty<ConfigPreset>())
                .Where(p => p != null && !string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (others.Any(p => string.Equals(NormalizeName(p.Name), name, StringComparison.OrdinalIgnoreCase)))
                return "已经有一套叫「" + name + "」了，换个名字。";

            if (others.Count >= MaxCount) return "最多存 " + MaxCount + " 套，先删掉几套不用的。";

            if (string.IsNullOrWhiteSpace(preset.DragonConfigName) && (preset.Groups?.Length ?? 0) == 0)
                return "这套组合什么都没选：至少填一个一条龙配置名，或者勾一个配置组。";

            return null;
        }

        /// <summary>
        /// 从当前设置抓一套，用于「把现在这组设置存下来」。
        /// 名字留空的话给一个能直接用的默认名（「配置 1」「配置 2」…），不用用户先想名字。
        /// </summary>
        public static ConfigPreset Capture(IEnumerable<ConfigPreset>? existing, string? dragonConfigName,
            IReadOnlyList<string>? groups, string? name, Func<string> newId)
        {
            var list = (existing ?? Array.Empty<ConfigPreset>()).Where(p => p != null).ToList();
            string final = NormalizeName(name);
            if (final.Length == 0) final = SuggestName(list);

            return new ConfigPreset
            {
                Id = newId(),
                Name = final,
                DragonConfigName = (dragonConfigName ?? "").Trim(),
                Groups = (groups ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).ToArray(),
            };
        }

        /// <summary>「配置 1」「配置 2」…取第一个没被占用的编号。</summary>
        public static string SuggestName(IEnumerable<ConfigPreset>? existing)
        {
            var used = new HashSet<string>(
                (existing ?? Array.Empty<ConfigPreset>()).Where(p => p != null).Select(p => NormalizeName(p.Name)),
                StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i <= MaxCount + 1; i++)
            {
                string candidate = "配置 " + i;
                if (!used.Contains(candidate)) return candidate;
            }
            return "配置";
        }

        /// <summary>按 Id 找；找不到返回 null（调用方据此退回全局设置，而不是拿一套空预设去跑）。</summary>
        public static ConfigPreset? FindById(IEnumerable<ConfigPreset>? presets, string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return (presets ?? Array.Empty<ConfigPreset>())
                .FirstOrDefault(p => p != null && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 列表排序：用过的按最近在前，没用过的排后面、保持原有相对顺序。
        /// 理由与配置组常用一致——用户改不了拖拽顺序，那就按「你上次用了什么」来排。
        /// </summary>
        public static List<ConfigPreset> Sort(IEnumerable<ConfigPreset>? presets)
        {
            var list = (presets ?? Array.Empty<ConfigPreset>()).Where(p => p != null).ToList();
            return list
                .Select((p, i) => (p, i, at: ParseTime(p.LastUsedAt)))
                .OrderByDescending(x => x.at ?? DateTime.MinValue)
                .ThenBy(x => x.at.HasValue ? 0 : 1)
                .ThenBy(x => x.i)
                .Select(x => x.p)
                .ToList();
        }

        private static DateTime? ParseTime(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return null;
            return DateTime.TryParse(iso, out var dt) ? dt : null;
        }

        /// <summary>
        /// 标出这套组合里已经不存在于 BGI 的组名（重识别后组被删/改名）。
        /// 只报告不修改：悄悄删掉的话，用户下次开跑才发现少跑了一组，而且不知道为什么。
        /// known 为空表示「还没识别到」，此时不判定为失效。
        /// </summary>
        public static List<string> MissingGroups(ConfigPreset? preset, IReadOnlyList<string>? known)
        {
            var result = new List<string>();
            if (preset == null || known == null || known.Count == 0) return result;

            var set = new HashSet<string>(known.Where(g => !string.IsNullOrWhiteSpace(g)), StringComparer.OrdinalIgnoreCase);
            foreach (string g in preset.Groups ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                if (!set.Contains(g)) result.Add(g);
            }
            return result;
        }

        /// <summary>把一套组合展开成「这次要用的配置名 + 组名」，供启动参数使用。</summary>
        public static (string DragonConfigName, IReadOnlyList<string> Groups) Expand(ConfigPreset? preset)
        {
            if (preset == null) return ("", Array.Empty<string>());
            return ((preset.DragonConfigName ?? "").Trim(),
                (preset.Groups ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).ToList());
        }

        /// <summary>一句话描述这套组合，用在列表行和提示里：`日常 · 3 组` 或 `日常 · 默认组`。</summary>
        public static string Describe(ConfigPreset? preset)
        {
            if (preset == null) return "";
            string cfg = string.IsNullOrWhiteSpace(preset.DragonConfigName) ? "" : preset.DragonConfigName!.Trim();
            int n = (preset.Groups ?? Array.Empty<string>()).Count(g => !string.IsNullOrWhiteSpace(g));
            string tail = n == 0 ? "默认组" : n + " 组";
            return cfg.Length == 0 ? tail : cfg + " · " + tail;
        }

        /// <summary>写回使用时间；只给一个能存进 JSON 的 ISO 字符串，不碰时钟来源。</summary>
        public static string StampUsed(DateTime now) => now.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
