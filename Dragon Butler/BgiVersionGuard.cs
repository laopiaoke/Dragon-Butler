using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DragonButler;

/// <summary>版本比对结论。</summary>
public enum VersionVerdict
{
    /// <summary>没记录过（第一次跑）——不提醒，免得开门第一句就是警告。</summary>
    FirstSeen = 0,
    /// <summary>和上次跑成时一模一样——什么都不用说。</summary>
    Unchanged = 1,
    /// <summary>版本变了——可能是 BGI 自己更新了，也可能是用户换了安装目录。</summary>
    Changed = 2,
    /// <summary>读不到版本（路径没设、文件不在、无权限）——不知道就别乱报。</summary>
    Unknown = 3,
}

/// <summary>
/// v1.9 / P2：BGI 更新前置检查。
///
/// 要解决的问题不是"帮用户更新 BGI"，而是"BGI 更新之后脚本容易失灵，而那时候
/// 用户往往在睡觉"。所以这里不做任何联网、不做下载、不替用户做决定，只做一件事：
/// 记住上一次**跑成功**时用的是哪个版本，下次开跑前发现版本变了就提醒一句
/// "BGI 从 X 变成 Y，如果这次跑得不顺，多半是版本变化引起的"。
///
/// 三条约束：
/// 1. 完全离线：只读本地 exe 的文件版本号，不发一个请求。
/// 2. 只在成功后记录：失败的版本不写进台账，否则更新坏掉的那一版会被当成"基线"。
/// 3. 读不到版本时保持沉默（Unknown），不要拿"未知"去覆盖用户的已知版本。
/// </summary>
public static class BgiVersionGuard
{
    /// <summary>从 BetterGI.exe 读文件版本；读不到返回空串。</summary>
    public static string ReadVersion(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return "";
        try
        {
            if (!File.Exists(exePath)) return "";
            var info = FileVersionInfo.GetVersionInfo(exePath);
            string text = (info.ProductVersion ?? "").Trim();
            if (text.Length == 0) text = (info.FileVersion ?? "").Trim();
            return Normalize(text);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("读取 BetterGI 版本失败：" + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 去掉构建哈希之类的尾巴，只留主版本段（1.2.3+abc → 1.2.3），
    /// 否则同一个版本重新编译一次就会被当成"变了"。
    /// </summary>
    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        string text = version.Trim();
        int plus = text.IndexOf('+');
        if (plus >= 0) text = text.Substring(0, plus);
        int dash = text.IndexOf('-');
        if (dash > 0) text = text.Substring(0, dash);
        return text.Trim();
    }

    /// <summary>纯函数：拿"上次跑成的版本"和"这次读到的版本"比一比。</summary>
    public static VersionVerdict Judge(string? seenVersion, string? currentVersion)
    {
        string seen = Normalize(seenVersion);
        string current = Normalize(currentVersion);
        if (current.Length == 0) return VersionVerdict.Unknown;
        if (seen.Length == 0) return VersionVerdict.FirstSeen;
        return string.Equals(seen, current, StringComparison.OrdinalIgnoreCase)
            ? VersionVerdict.Unchanged
            : VersionVerdict.Changed;
    }

    /// <summary>给人看的一句话；不需要说话时返回空串。</summary>
    public static string Describe(VersionVerdict verdict, string? seenVersion, string? currentVersion)
    {
        switch (verdict)
        {
            case VersionVerdict.Changed:
                return $"BetterGI 已从 {Normalize(seenVersion)} 变成 {Normalize(currentVersion)}。"
                     + "如果这次跑得不顺，多半是版本变化引起的——先去 BGI 里跑一次看看是否正常。";
            case VersionVerdict.FirstSeen:
                return "第一次记录 BetterGI 版本：" + Normalize(currentVersion);
            default:
                return "";
        }
    }

    /// <summary>
    /// 用当前读到的版本更新台账；只有"跑成"的那次才调用它。
    /// 返回是否真的改了（界面据此判断要不要存盘）。
    /// </summary>
    public static bool Remember(string? currentVersion)
    {
        string current = Normalize(currentVersion);
        if (current.Length == 0) return false;
        if (string.Equals(Normalize(AppState.Settings.LastBgiVersion), current, StringComparison.OrdinalIgnoreCase))
            return false;
        AppState.Settings.LastBgiVersion = current;
        return true;
    }

    /// <summary>
    /// 开跑前的一站式检查：读版本 → 判断 → 返回要不要提醒。
    /// <paramref name="exePath"/> 为空时按当前设置里的 BGI 路径读。
    /// </summary>
    public static (VersionVerdict Verdict, string Current, string Message) Preflight(string? exePath = null)
    {
        string path = string.IsNullOrWhiteSpace(exePath) ? AppState.Settings.BgiPath : exePath;
        string current = ReadVersion(path);
        var verdict = Judge(AppState.Settings.LastBgiVersion, current);
        return (verdict, current, Describe(verdict, AppState.Settings.LastBgiVersion, current));
    }
}
