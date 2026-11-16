using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DragonButler
{
    /// <summary>
    /// 一个候选值，以及「哪些配置正在用它」。
    ///
    /// 为什么不给每个值配一句中文解释：`settings.completionAction` 的合法取值集合
    /// 我们没有权威来源（ABGI 文档只写「结束后的行为，包括关机等高风险动作」，没列枚举）。
    /// 猜一个映射写进界面，猜错了比不写更糟——用户会以为「选这个就是关机」，
    /// 而实际可能是别的。所以界面上只呈现**事实**：这个值长什么样、哪些配置用过。
    /// 用户自己认得哪个是他设过关机的。
    /// </summary>
    public sealed class CompletionOption
    {
        public string Value { get; set; } = "";

        /// <summary>用这个值的配置名（不含扩展名），按发现顺序。</summary>
        public List<string> UsedBy { get; set; } = new();
    }

    /// <summary>
    /// v1.17 / M1-B：一条龙「跑完后 BGI 自己做什么」的读写。
    ///
    /// 与 CbAfterRun（队列跑完本软件做什么）**不是一回事**，别混：
    /// 那个是我们自己的动作，这个是写进 BGI 配置文件、由 BGI 在它自己跑完时执行的。
    /// 所以这一层多两道闸：候选值只能来自用户自己的配置（不硬编码），
    /// 写入前必须过 <see cref="BgiConfigWriter.RefuseReason"/>。
    ///
    /// 全部是纯函数，磁盘访问用委托传进来，探针能在假目录上跑完所有分支。
    /// </summary>
    public static class DragonCompletion
    {
        /// <summary>被读写的那一个键（完整路径，与 ABGI 文档一致）。</summary>
        public const string Key = BgiConfigWriter.KeyCompletionAction;

        /// <summary>取值长度上限。BGI 那边是枚举，正常不会有长串。</summary>
        public const int MaxValueChars = 64;

        /// <summary>
        /// 清洗一个候选值：去首尾空白、拒控制字符、超长截断。
        /// 不清洗就写回去的东西，下次 BGI 读到的可能是一个带换行的怪值。
        /// </summary>
        public static string CleanValue(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (c < 0x20) continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            if (s.Length > MaxValueChars) s = s.Substring(0, MaxValueChars);
            return s;
        }

        /// <summary>
        /// 要改的是哪份 JSON。`DragonConfigName` 留空时返回 null——
        /// 留空意味着「用 BGI 当前选中的那份」，那是运行时的选择，我们不知道，
        /// 更不该随便挑一个文件去改。
        /// </summary>
        public static string? TargetJson(string? oneDragonDir, string? dragonConfigName)
        {
            if (string.IsNullOrWhiteSpace(oneDragonDir)) return null;
            if (!BgiConfigWriter.IsSafeName(dragonConfigName)) return null;
            try { return Path.Combine(oneDragonDir!.Trim(), dragonConfigName!.Trim() + ".json"); }
            catch { return null; }
        }

        /// <summary>
        /// 扫描目录下所有一条龙配置，收集 `settings.completionAction` 出现过的取值。
        ///
        /// 去重按 **Ordinal**（区分大小写）：`Sleep` 与 `sleep` 在 JSON 里是两个不同的值，
        /// BGI 可能只认其中一个。合并成一个会让用户以为没区别，而写错了就是跑完不关机。
        /// 读不出来的文件（坏 JSON、没权限）直接跳过，不参与候选，也不报错——
        /// 一个坏文件不该让整个候选列表消失。
        /// </summary>
        public static List<CompletionOption> Scan(string? oneDragonDir,
            Func<string, string?> readFile, Func<string, IEnumerable<string>> listFiles)
        {
            var result = new List<CompletionOption>();
            if (string.IsNullOrWhiteSpace(oneDragonDir)) return result;

            IEnumerable<string> files;
            try { files = listFiles(oneDragonDir!.Trim()) ?? Enumerable.Empty<string>(); }
            catch { return result; }

            var index = new Dictionary<string, CompletionOption>(StringComparer.Ordinal);
            foreach (string file in files)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                string name = "";
                try { name = Path.GetFileNameWithoutExtension(file) ?? ""; } catch { continue; }
                if (name.Length == 0) continue;

                string? json;
                try { json = readFile(file); } catch { continue; }
                if (string.IsNullOrWhiteSpace(json)) continue;

                string value = CleanValue(BgiConfigWriter.GetString(json, Key));
                if (value.Length == 0) continue;

                if (!index.TryGetValue(value, out var opt))
                {
                    opt = new CompletionOption { Value = value };
                    index[value] = opt;
                    result.Add(opt);
                }
                if (!opt.UsedBy.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    opt.UsedBy.Add(name);
            }

            return result
                .OrderBy(o => o.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Value, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>这份配置现在是什么值；读不到返回 ""。</summary>
        public static string CurrentValue(string? json)
        {
            return CleanValue(BgiConfigWriter.GetString(json, Key));
        }

        /// <summary>两个值是不是同一个（Ordinal，与去重口径一致）。</summary>
        public static bool IsSameValue(string? a, string? b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        }

        /// <summary>
        /// 改成目标值。返回 null = 结构不认识（比如根本没有 settings 对象），
        /// 这时候界面必须拒绝并告诉用户「去 BetterGI 里改」，而不是新建一个它可能不认的结构。
        /// </summary>
        public static string? Apply(string? json, string? value)
        {
            string v = CleanValue(value);
            if (v.Length == 0) return null;
            return BgiConfigWriter.SetString(json, Key, v);
        }

        /// <summary>下拉里每一行显示什么：「值 · 用在 日常、深渊」。</summary>
        public static string OptionText(CompletionOption? o)
        {
            if (o == null || o.Value.Length == 0) return "";
            if (o.UsedBy.Count == 0) return o.Value;
            return o.Value + " · 用在 " + string.Join("、", o.UsedBy);
        }

        /// <summary>
        /// 改动确认弹窗的正文：说清改哪个文件、从什么改成什么、以及我们不知道这个值的含义。
        /// 最后一句是刻意的——用户有权知道我们在猜之外什么都没做。
        /// </summary>
        public static string ConfirmText(string? configName, string? from, string? to)
        {
            string file = string.IsNullOrWhiteSpace(configName) ? "（未命名）" : configName!.Trim();
            string a = CleanValue(from);
            string b = CleanValue(to);
            return "会改这条配置：" + file + ".json\n"
                 + "跑完后的动作：" + (a.Length == 0 ? "（现在是空的）" : a) + "  →  " + (b.Length == 0 ? "（清空）" : b) + "\n\n"
                 + "改之前会把原文件备份到 config\\bgi-backup，改坏了能拿回来。\n"
                 + "这个值会让它做什么（比如关机），取决于 BetterGI 怎么解释它——"
                 + "所以在 BetterGI 里先试过一次，再从这里改。";
        }
    }
}
