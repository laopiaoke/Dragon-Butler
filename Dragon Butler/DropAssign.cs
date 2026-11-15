using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DragonButler;

/// <summary>
/// v1.16：拖进来的东西该设给哪个工具。
///
/// 为什么单独成类：判定要能断言，而断言的输入不能是真实文件系统。
/// 所以 <see cref="Resolve"/> 把「文件在不在」与「目录里有什么」都变成委托传进来，
/// 纯逻辑留在里面——探针可以造一个假磁盘，不必在用户机器上创建 BetterGI 目录。
///
/// 这条链路之前是不存在的：v1.15 的三步引导写着「把 exe 直接拖进窗口」，
/// 但全项目搜不到 AllowDrop。引导语比实现先写，是这次要补的缺口。
/// </summary>
public static class DropAssign
{
    /// <summary>已知工具的候选可执行文件名（全小写，只比文件名不比路径）。</summary>
    public static readonly (string ToolId, string[] FileNames)[] Known =
    {
        ("bettergi", new[] { "bettergi.exe" }),
        ("march7th", new[] { "march7thassistant.exe", "march7th.exe" }),
        ("ok-ww", new[] { "ok-ww.exe", "okwu.exe" }),
    };

    /// <summary>设置入口的显示名（提示语用，与工具卡一致）。</summary>
    public static string DisplayName(string? toolId) => toolId switch
    {
        "bettergi" => "BetterGI",
        "march7th" => "三月七助手",
        "ok-ww" => "ok-ww",
        _ => toolId ?? "",
    };

    /// <summary>只看文件名，猜它属于哪个工具。猜不出返回 null（交给调用方问用户）。</summary>
    public static string? ToolOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string name;
        try { name = Path.GetFileName(fileName.Trim()); }
        catch { return null; }
        if (name.Length == 0) return null;

        foreach (var (toolId, names) in Known)
            if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return toolId;
        return null;
    }

    /// <summary>
    /// 一次拖放解析出「设给谁、设哪个路径」。
    ///
    /// 认得的情况：exe 文件名命中已知工具；拖进来的是目录且目录里有已知 exe。
    /// 认不出返回 null——**不猜**。把 BetterGI 的路径写给别人，用户下次点开会得到一个
    /// 起不来的工具，而他并不知道是自己拖错了。
    /// </summary>
    /// <param name="path">拖进来的完整路径</param>
    /// <param name="fileExists">文件/目录是否存在</param>
    /// <param name="isDirectory">是不是目录（单独传，避免为了判断而碰磁盘两次）</param>
    /// <param name="listFiles">列目录下的文件名；目录不可读时返回空</param>
    public static (string ToolId, string Path)? Resolve(
        string? path,
        Func<string, bool> fileExists,
        bool isDirectory,
        Func<string, IEnumerable<string>> listFiles)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.Trim().Trim('"');
        if (p.Length == 0 || !fileExists(p)) return null;

        if (!isDirectory)
        {
            string? tool = ToolOf(p);
            return tool == null ? null : (tool, p);
        }

        var files = (listFiles(p) ?? Array.Empty<string>()).ToList();

        // 目录：先按目录名猜，再按里面的 exe 猜。
        // 顺序有讲究——「BetterGI」这个目录名比它里面恰好有个 ok-ww.exe 更能说明问题。
        string? byName = ToolOf(p.TrimEnd('\\', '/') + ".exe");
        if (byName != null)
        {
            foreach (var (toolId, names) in Known)
            {
                if (!string.Equals(toolId, byName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string n in names)
                {
                    // 优先返回目录里真实存在的那一项（保住磁盘上的大小写），
                    // 拼出来的名字在 Windows 上也能用，但日志里显示「bettergi.exe」
                    // 而磁盘上是「BetterGI.exe」会让人怀疑是不是写错了文件。
                    var real = files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase));
                    if (real != null) return (toolId, Path.Combine(p, real));

                    string candidate = Path.Combine(p, n);
                    if (fileExists(candidate)) return (toolId, candidate);
                }
            }
        }

        foreach (var (toolId, names) in Known)
            foreach (string n in names)
            {
                var hit = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return (toolId, Path.Combine(p, hit));
            }

        return null;
    }

    /// <summary>
    /// 拖放失败时给人看的一句话。分三种，因为处理方式完全不同：
    /// 拖了快捷方式、拖了认不出的 exe、拖了别的东西。
    /// </summary>
    public static string Refusal(string? path)
    {
        string ext = "";
        try { ext = Path.GetExtension(path ?? "").ToLowerInvariant(); } catch { }

        if (ext == ".lnk")
            return "拖进来的是快捷方式。请拖它指向的 exe（在快捷方式上右键 → 打开文件所在的位置）。";
        if (ext == ".exe")
            return "这个 exe 我不认识。去「工具设置」里给对应的工具单独选一次路径。";
        if (ext.Length == 0)
            return "拖进来一个文件夹，但里面没找到认识的主程序。";
        return "只认 exe 文件或装着 exe 的文件夹。";
    }
}
