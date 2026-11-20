using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonButler.Plugins;

/// <summary>插件的 <c>metadata.json</c>。字段故意少——每多一个字段都是一处「谁来校验」。</summary>
public sealed class PluginMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    /// <summary>要访问的域名（只展示给用户看，不做运行时拦截——做不到就别假装做到）。</summary>
    [JsonPropertyName("hosts")] public List<string> Hosts { get; set; } = new();
    /// <summary>一句话说清它要什么，会原样显示在插件列表里。</summary>
    [JsonPropertyName("needs")] public string Needs { get; set; } = "";
    /// <summary>入口程序集文件名；留空默认 Plugin.dll。</summary>
    [JsonPropertyName("assembly")] public string Assembly { get; set; } = "";
}

/// <summary>
/// v1.20：插件加载器。
///
/// 目标只有一个：<b>一个坏插件不能让主程序打不开</b>。
/// 所以这里的每个判定都是「先看能不能读懂，读不懂就跳过并说清为什么」，
/// 宁可少加载一个，也不要为了加载它而冒抛异常的风险。
/// </summary>
public static class PluginLoader
{
    /// <summary>插件根目录：<c>plugins\</c>（exe 旁边）。</summary>
    public static string Root => Paths.PluginsDir;

    /// <summary>单个插件目录里默认的入口文件名。</summary>
    public const string DefaultAssembly = "Plugin.dll";

    /// <summary>元数据文件名。</summary>
    public const string MetaName = "metadata.json";

    private static readonly JsonSerializerOptions ReadOpt = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ===== 纯函数部分（可断言，不碰磁盘）=====

    /// <summary>
    /// 校验元数据。<paramref name="dirName"/> 是插件所在目录名，用来兜底 id。
    /// 返回 null = 通过；否则返回一句人话。
    /// </summary>
    public static string? RefuseReason(PluginMeta? meta, string? dirName)
    {
        if (meta == null) return "metadata.json 读不出来。";
        if (string.IsNullOrWhiteSpace(meta.Name)) return "没写插件名字（name）。";
        if (string.IsNullOrWhiteSpace(meta.Version)) return "没写版本号（version）。";

        string id = EffectiveId(meta, dirName);
        if (id.Length == 0) return "目录名不能当 id 用，元数据里也没写 id。";
        if (!IsSafeId(id)) return "id 里只能有字母、数字、- 和 _。";

        string asm = EffectiveAssembly(meta);
        if (asm.Length == 0) return "入口程序集名不合法。";
        if (!asm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return "入口程序集必须是 .dll。";

        return null;
    }

    /// <summary>插件 id：元数据里写了就用它，否则用目录名。</summary>
    public static string EffectiveId(PluginMeta? meta, string? dirName)
    {
        string fromMeta = (meta?.Id ?? "").Trim();
        if (fromMeta.Length > 0) return fromMeta;
        return (dirName ?? "").Trim();
    }

    /// <summary>入口程序集文件名；留空用默认值。</summary>
    public static string EffectiveAssembly(PluginMeta? meta)
    {
        string a = (meta?.Assembly ?? "").Trim();
        if (a.Length == 0) return DefaultAssembly;
        // 只收平铺文件名：带路径的一律当成没写（防止指到别的目录去）
        if (a.Contains('\\') || a.Contains('/') || a.Contains(':')) return "";
        return a;
    }

    /// <summary>id 只允许字母数字与 <c>-</c> <c>_</c>；同时挡住 <c>..</c> 之类。</summary>
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        foreach (char c in id)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>解析元数据文本；坏 JSON 返回 null（不抛）。</summary>
    public static PluginMeta? ParseMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginMeta>(json, ReadOpt);
        }
        catch
        {
            return null;
        }
    }

    // ===== 磁盘部分 =====

    /// <summary>扫描到的一个插件目录（还没加载程序集）。</summary>
    public sealed record Found(string Dir, string DirName, PluginMeta? Meta, string? Refuse, bool HasAssembly);

    /// <summary>
    /// 扫 <c>plugins\</c> 下的一层子目录。任何一步出错都只记在返回值里，不抛。
    /// 目录不存在返回空列表——没装插件的用户不该看到任何异常。
    /// </summary>
    public static List<Found> Scan(string? root = null)
    {
        var list = new List<Found>();
        string baseDir = root ?? Root;
        try
        {
            if (!Directory.Exists(baseDir)) return list;
            foreach (string dir in Directory.GetDirectories(baseDir))
            {
                string dirName = Path.GetFileName(dir);
                PluginMeta? meta = null;
                string? refuse;

                string metaPath = Path.Combine(dir, MetaName);
                if (!File.Exists(metaPath))
                {
                    refuse = "没有 metadata.json。";
                }
                else
                {
                    string? text = null;
                    try { text = File.ReadAllText(metaPath); } catch { }
                    meta = ParseMeta(text);
                    refuse = text == null ? "metadata.json 读不出来。" : RefuseReason(meta, dirName);
                }

                string asmName = EffectiveAssembly(meta);
                bool hasAsm = asmName.Length > 0 && File.Exists(Path.Combine(dir, asmName));

                list.Add(new Found(dir, dirName, meta, refuse, hasAsm));
            }
        }
        catch
        {
            // 目录读不了就当没有插件
        }
        return list;
    }
}
