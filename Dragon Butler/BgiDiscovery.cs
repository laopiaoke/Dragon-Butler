using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DragonButler;

/// <summary>一次扫描结果：配置组、一条龙配置、BGI 根目录与失败原因</summary>
public sealed class BgiScanResult
{
    public List<string> Groups { get; set; } = new();
    public List<string> DragonConfigs { get; set; } = new();
    public string Root { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTime ScannedAt { get; set; } = DateTime.Now;

    /// <summary>是否成功读到 BGI 目录（即使 0 组也算成功）</summary>
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// BGI 配置自动发现（参考 autoBGI）：不依赖 BGI 的推送地址，直接读安装目录——
/// 配置组：{BGI}\User\ScriptGroup\*.json（一个组一个 JSON，文件名即组名）
/// 一条龙：{BGI}\User\OneDragon\*.json（Name 字段等于文件名）
/// 只读文件、不写 BGI 目录；路径无效/无权限时返回失败原因，界面据此提示。
/// </summary>
public static class BgiDiscovery
{
    private static readonly object Gate = new();
    private static BgiScanResult _cache = new();
    private static string _cacheRoot = "\u0000";

    /// <summary>BGI 根目录（BetterGI.exe 所在目录）；路径为空/非法返回 ""</summary>
    public static string BgiRoot
    {
        get
        {
            try
            {
                string path = AppState.Settings.BgiPath;
                if (string.IsNullOrWhiteSpace(path)) return "";
                string? dir = Path.GetDirectoryName(path);
                return string.IsNullOrWhiteSpace(dir) ? "" : dir!;
            }
            catch { return ""; }
        }
    }

    /// <summary>
    /// 扫描配置组与一条龙配置。force=false 时同根目录 5 秒内复用缓存（避免界面频繁刷新时反复读盘）。
    /// </summary>
    public static BgiScanResult Scan(bool force = false)
    {
        lock (Gate)
        {
            string root = BgiRoot;
            if (!force && root == _cacheRoot && (DateTime.Now - _cache.ScannedAt).TotalSeconds < 5)
                return _cache;

            var result = new BgiScanResult { Root = root, ScannedAt = DateTime.Now };

            if (root.Length == 0)
            {
                result.Error = "尚未设置 BetterGI 路径";
            }
            else if (!Directory.Exists(root))
            {
                result.Error = "BetterGI 目录不存在：" + root;
            }
            else
            {
                // 全新 BGI 可能还没有 User 目录（未启动过），这属于正常的 0 组状态，不是错误
                result.Groups = ScanJsonNames(Path.Combine(root, "User", "ScriptGroup"));
                result.DragonConfigs = ScanJsonNames(Path.Combine(root, "User", "OneDragon"));
            }

            _cache = result;
            _cacheRoot = root;
            return result;
        }
    }

    /// <summary>读取目录下所有 .json 文件名（升序、去重、忽略大小写）</summary>
    private static List<string> ScanJsonNames(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("扫描 BGI 配置目录失败：" + ex.Message);
            return new List<string>();
        }
    }
}
