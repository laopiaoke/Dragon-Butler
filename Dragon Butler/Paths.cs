using System;
using System.IO;

namespace DragonButler;

/// <summary>
/// 全程序的数据目录。
///
/// v1.30：exe 旁边原来会散出六七个文件夹（config / tools / plugins / cache / sounds / 备份 / 全部日志），
/// 第一次打开的人看到的是一堆不知道哪来的目录。现在收成两个：
///
///   exe 旁\
///     所有配置\         ← 要备份、要带走的东西：设置、台账、工具档案、图标缓存、音效
///     全部日志\         ← 只用来看、可以删的东西：运行日志、崩溃转储、截图、现场包
///     插件\             ← 用户自己往里放东西的地方（v1.35 从「所有配置」里挪出来，
///                          因为「所有配置」听起来像程序自己的，没人会想到往那儿放插件）
///
/// 为什么缓存和音效也算「配置」：它们都是程序自己生成、删了下次会重建的东西，
/// 但重建要读配置（图标是从档案里的路径提取的，音效版本号存在配置里）。
/// 把它们跟日志放一起，用户删日志时顺手就把图标缓存删了，下次打开八格全变首字母。
/// 所以按「删了会怎样」分，不按「是不是配置」分。
///
/// 老版本的目录不删也不认：第一次启动时搬进新位置，搬完留一个 .moved 标记，
/// 之后再也不看它——万一用户自己在老 config 里放了别的东西，那不该被我们清掉。
/// </summary>
public static class Paths
{
    /// <summary>
    /// 类型初始化时就把目录建好、把老目录搬完。
    /// 放在静态构造里而不是等 App.OnStartup 调：任何一处代码第一次碰到
    /// <see cref="Paths"/> 都会先跑完它，不需要依赖谁的启动顺序。
    /// </summary>
    static Paths() => Prepare();

    /// <summary>要备份、要带走的东西。</summary>
    public static string ConfigRoot => Path.Combine(AppContext.BaseDirectory, "所有配置");

    /// <summary>只用来看、可以删的东西。</summary>
    public static string LogRoot => Path.Combine(AppContext.BaseDirectory, "全部日志");

    /// <summary><c>所有配置\xxx.json</c>。</summary>
    public static string Config(string fileName) => Path.Combine(ConfigRoot, fileName);

    /// <summary>工具档案目录。</summary>
    public static string ToolsDir => Path.Combine(ConfigRoot, "tools");

    /// <summary>
    /// 插件目录：exe 旁边的「插件」文件夹。
    ///
    /// v1.35：原来放在 <c>所有配置\plugins</c> 里面。那个位置的问题是——
    /// 插件是用户要自己往里丢东西的地方，而「所有配置」听起来像程序自己的东西，
    /// 没人会想到要去那儿放东西。而且导航第四项就叫「插件」，点进去却说
    /// 「打开插件目录」，结果目录藏在两级子文件夹里。
    /// 放在 exe 旁边，「插件」两个字明摆着，跟导航那一项对得上。
    /// </summary>
    public static string PluginsDir => Path.Combine(AppContext.BaseDirectory, "插件");

    /// <summary>图标缓存目录。</summary>
    public static string CacheDir => Path.Combine(ConfigRoot, "cache", "icons");

    /// <summary>音效目录（首次启动时生成）。</summary>
    public static string SoundsDir => Path.Combine(ConfigRoot, "sounds");

    /// <summary>设置备份目录。</summary>
    public static string BackupDir => Path.Combine(ConfigRoot, "备份");

    /// <summary>截图目录。</summary>
    public static string SnapshotsDir => Path.Combine(LogRoot, "snapshots");

    /// <summary>现场包目录。</summary>
    public static string IncidentsDir => Path.Combine(LogRoot, "incidents");

    /// <summary>迁移完成标记：写在老 config 目录里，表示「已经搬过了」。</summary>
    private const string MovedMarker = ".moved-to-所有配置";

    /// <summary>
    /// 建好两个根目录，并把老版本的散装目录搬进来。
    /// 幂等；任何一步失败都只记一条日志，绝不让程序起不来。
    /// </summary>
    public static void Prepare()
    {
        try
        {
            Directory.CreateDirectory(ConfigRoot);
            Directory.CreateDirectory(LogRoot);
            // 插件目录也在这里建：第一次运行时它就该存在，
            // 而不是等用户点「打开插件目录」才被建出来。
            Directory.CreateDirectory(PluginsDir);
        }
        catch { /* 建不出目录就等各服务自己再试，不在这里挡路 */ }

        ImportLegacy();
    }

    /// <summary>
    /// 把 v1.29 及以前的散装目录搬进新位置。
    /// 规则：老目录存在、新位置还没有 → 整块搬过去（Move，同盘是改名，不会复制几十兆）。
    /// 新位置已经有了 → 老目录原样留着，不动它。
    /// </summary>
    private static void ImportLegacy()
    {
        string baseDir = AppContext.BaseDirectory;

        // ---- 配置类：整目录搬 ----
        MoveDir(Path.Combine(baseDir, "config"), ConfigRoot, flatten: true);
        MoveDir(Path.Combine(baseDir, "tools"), ToolsDir);
        MoveDir(Path.Combine(baseDir, "plugins"), PluginsDir);
        // v1.35：插件目录从「所有配置\plugins」搬到 exe 旁边的「插件」。
        // 搬的是内容（flatten 到「插件」根），不是整个目录——那边原来就叫 plugins，
        // 搬过去会变成 插件\plugins\，多看一层。
        MoveDir(Path.Combine(ConfigRoot, "plugins"), PluginsDir, flatten: true);
        MoveDir(Path.Combine(baseDir, "cache"), Path.Combine(ConfigRoot, "cache"));
        MoveDir(Path.Combine(baseDir, "sounds"), SoundsDir);
        MoveDir(Path.Combine(baseDir, "备份"), BackupDir);

        // ---- 日志类：老代码里 snapshots / incidents 已经在「全部日志」下面了，不用搬 ----

        // 搬完打标记：以后不再扫老 config 目录（用户可能在里面放了自己的东西）
        try
        {
            string oldConfig = Path.Combine(baseDir, "config");
            if (Directory.Exists(oldConfig) && !File.Exists(Path.Combine(oldConfig, MovedMarker)))
                File.WriteAllText(Path.Combine(oldConfig, MovedMarker), "");
        }
        catch { }
    }

    /// <summary>
    /// 搬一个目录。<paramref name="flatten"/> 为 true 时把里面的文件直接倒进目标根
    /// （老的 <c>config\settings.json</c> 要变成 <c>所有配置\settings.json</c>，不多一层 config）。
    /// </summary>
    private static void MoveDir(string from, string to, bool flatten = false)
    {
        try
        {
            if (!Directory.Exists(from)) return;

            if (!flatten)
            {
                // 目标已存在就不动（用户可能已经有新数据了）
                if (Directory.Exists(to)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(to) ?? to);
                Directory.Move(from, to);
                return;
            }

            // 摊平：逐个文件/子目录搬，目标已存在的跳过
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                string name = Path.GetFileName(file);
                if (name == MovedMarker) continue;
                string dest = Path.Combine(to, name);
                if (File.Exists(dest)) continue;
                try { File.Move(file, dest); } catch { }
            }
            foreach (string dir in Directory.GetDirectories(from))
            {
                string name = Path.GetFileName(dir);
                string dest = Path.Combine(to, name);
                if (Directory.Exists(dest)) continue;
                try { Directory.Move(dir, dest); } catch { }
            }
        }
        catch { /* 搬不动就让它待在原地：老路径的读取退路还在，不至于丢数据 */ }
    }
}
