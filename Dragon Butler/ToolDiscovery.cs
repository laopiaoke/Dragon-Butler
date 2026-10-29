using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 工具 exe 自动寻找：四级按序，命中即停。
/// 1) 运行中的进程 MainModule 路径（文件名需与 profile 的进程名一致）
/// 2) 开始菜单 .lnk 解析（ProgramData + %APPDATA%）
/// 3) profile.CustomData["candidatePaths"]（| 分隔，默认为空=待人工核对）
/// 4) 注册表 Uninstall 键的 DisplayIcon / InstallLocation
/// 找不到返回 null，由界面提示"未自动找到，请手动选择文件夹"。
/// </summary>
public static class ToolDiscovery
{
    /// <summary>在当前机器上寻找该工具的 exe。</summary>
    /// <param name="profile">工具档案。</param>
    /// <param name="source">命中的层级（running/startmenu/candidate/registry/已配置）。</param>
    public static string? TryAutoDiscover(ToolProfile profile, out string source)
    {
        source = "";

        // 0) 已配置且存在，直接返回
        try
        {
            if (!string.IsNullOrWhiteSpace(profile.ExecutablePath) && File.Exists(profile.ExecutablePath))
            {
                source = "已配置";
                return profile.ExecutablePath;
            }
        }
        catch { }

        // 1) 运行中的进程
        string? p = FromRunningProcess(profile);
        if (p != null) { source = "running"; return p; }

        // 2) 开始菜单快捷方式
        p = FromStartMenu(profile);
        if (p != null) { source = "startmenu"; return p; }

        // 3) 候选路径
        p = FromCandidates(profile);
        if (p != null) { source = "candidate"; return p; }

        // 4) 注册表卸载信息
        p = FromRegistry(profile);
        if (p != null) { source = "registry"; return p; }

        return null;
    }

    private static string? FromRunningProcess(ToolProfile profile)
    {
        try
        {
            string name = profile.ManagedProcessName;
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = proc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch { }
                finally { try { proc.Dispose(); } catch { } }
            }
        }
        catch { }
        return null;
    }

    private static string? FromStartMenu(ToolProfile profile)
    {
        var roots = new List<string>();
        try { roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")); } catch { }
        try { roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")); } catch { }

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (string lnk in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(lnk);
                if (!Matches(profile, fileName)) continue;
                string? target = ResolveShortcut(lnk);
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target)) return target;
            }
        }
        return null;
    }

    private static string? FromCandidates(ToolProfile profile)
    {
        try
        {
            if (!profile.CustomData.TryGetValue("candidatePaths", out string? raw) || string.IsNullOrWhiteSpace(raw))
                return null;
            foreach (string item in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string path = Environment.ExpandEnvironmentVariables(item.Trim());
                if (File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    private static string? FromRegistry(ToolProfile profile)
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string root in roots)
            {
                try
                {
                    using var key = hive.OpenSubKey(root);
                    if (key == null) continue;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = key.OpenSubKey(sub);
                            if (app == null) continue;
                            string display = app.GetValue("DisplayName") as string ?? "";
                            if (!Matches(profile, display)) continue;

                            string icon = app.GetValue("DisplayIcon") as string ?? "";
                            string? exe = PickExe(icon);
                            if (exe != null) return exe;

                            string loc = app.GetValue("InstallLocation") as string ?? "";
                            if (Directory.Exists(loc))
                            {
                                foreach (string file in Directory.EnumerateFiles(loc, "*.exe", SearchOption.TopDirectoryOnly))
                                {
                                    if (string.Equals(Path.GetFileNameWithoutExtension(file), profile.ManagedProcessName, StringComparison.OrdinalIgnoreCase))
                                        return file;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static string? PickExe(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        string path = displayIcon.Trim().Trim('"');
        int comma = path.LastIndexOf(',');
        if (comma > 2) path = path[..comma].Trim('"');
        return File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>名称匹配：显示名、进程名、ToolId 任一包含即算（大小写不敏感）。</summary>
    private static bool Matches(ToolProfile profile, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string token in new[] { profile.DisplayName, profile.ManagedProcessName, profile.ToolId })
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // 常见别名（官方中文名/英文名差异）
        return profile.ToolId switch
        {
            "march7th" => text.Contains("三月七", StringComparison.Ordinal) || text.Contains("March7th", StringComparison.OrdinalIgnoreCase),
            "ok-ww" => text.Contains("okww", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>解析 .lnk 目标路径（WScript.Shell，失败返回 null）。</summary>
    public static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = (string)shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }
}
