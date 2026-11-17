using System;

namespace DragonButler.Plugins;

/// <summary>
/// v1.20：插件能看到的主程序状态。<b>全部只读</b>——插件不该能停队列、改设置、动界面。
/// </summary>
public sealed class PluginStatus
{
    /// <summary>队列正在跑。</summary>
    public bool Running { get; init; }

    /// <summary>当前工具 id（空 = 没有）。</summary>
    public string CurrentToolId { get; init; } = "";

    /// <summary>今天已经跑完的工具数。</summary>
    public int DoneToday { get; init; }

    /// <summary>今天还没跑的工具数。</summary>
    public int LeftToday { get; init; }

    /// <summary>主程序版本，形如 <c>1.20.0</c>。</summary>
    public string HostVersion { get; init; } = "";
}

/// <summary>通知级别。字符串是为了让插件不必引用主程序的枚举。</summary>
public static class PluginNotifyLevel
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
}

/// <summary>
/// 交给插件的那只手。
///
/// 设计立场：给「说一句话」「记一行日志」「存自己的设置」这三件小事，
/// 以及一个 <see cref="StartQueue"/>；不给界面、不给 AppState、不给任意路径。
/// 插件能造成的最大破坏是「自己坏了」。
/// </summary>
public interface IPluginHost
{
    /// <summary>这个插件的 id（目录名）。</summary>
    string PluginId { get; }

    /// <summary>主程序当前状态快照。</summary>
    PluginStatus Status { get; }

    /// <summary>走主程序已配好的通知渠道发一条。<paramref name="level"/> 取 <see cref="PluginNotifyLevel"/>。</summary>
    void Notify(string level, string title, string message);

    /// <summary>写这个插件自己的日志（<c>全部日志\plugin-{id}.log</c>）。</summary>
    void Log(string text);

    /// <summary>读自己的设置（存 <c>config\plugins\{id}.json</c>）。没有就返回空串。</summary>
    string GetSetting(string key);

    /// <summary>写自己的设置。</summary>
    void SetSetting(string key, string value);

    /// <summary>相当于用户点了一次「一键开跑」。队列正在跑时返回 false。</summary>
    bool StartQueue();

    /// <summary>
    /// 读今天的 BGI 日志末尾若干字符（只读，最多 <paramref name="maxChars"/> 字符）。
    /// 找不到日志返回空串——插件自己判断怎么处理。
    /// </summary>
    string ReadBgiLog(int maxChars);
}

/// <summary>
/// 插件要实现的东西。
///
/// 约束（写在这里是因为插件作者未必会去读文档）：
/// 1. <see cref="Initialize"/> 与 <see cref="Tick"/> 都在后台线程调用，不要碰 UI。
/// 2. 两个方法都会带超时（10 秒），超时后本插件被标记为无响应并不再调用。
/// 3. 抛异常不会让主程序崩，只会让这个插件被停用并记一行日志。
/// </summary>
public interface IPlugin : IDisposable
{
    /// <summary>启动时调一次。<paramref name="host"/> 之后一直可用，不需要自己保存以外的处理。</summary>
    void Initialize(IPluginHost host);

    /// <summary>主程序每隔几秒调一次。做轮询、定时任务都放这里。</summary>
    void Tick();
}
