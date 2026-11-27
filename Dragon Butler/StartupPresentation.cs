using System.Windows;

namespace DragonButler;

/// <summary>
/// v1.46.1：「启动时最小化」这一条设置的**全部**时序。
///
/// 修之前是 Show() 之后再 Hide()——中间隔着刷红点、两张报告、建托盘、起调度定时器，
/// 首帧早就画到屏幕上了，用户看到的是「闪一下才消失」。窗口不是不想藏，
/// 是藏晚了一整段启动流程。
///
/// 所以这条修复的核心不是「加个 Hide」，而是**把状态在 Show() 之前摆好**：
/// ShowActivated=false（不抢当前前台焦点）、WindowState=Minimized（框架按最小化建窗口）、
/// Visibility=Hidden（不给它上屏的机会）、ShowInTaskbar=false（跟收进托盘时的观感一致）。
/// 四个都先于 Show()，顺序换一下就等于没修——这也是它单独成一个方法的原因：
/// 谁来改都得一起改，改不了其中一个。
/// </summary>
internal static class StartupPresentation
{
    /// <summary>
    /// 该不该藏起来启动。两个条件缺一不可：用户要「启动时最小化」，
    /// 且「关闭时驻留托盘」开着——不驻留托盘的话藏起来就没有图标能点回来，
    /// 那是启动即失踪，不算最小化。
    /// </summary>
    public static bool ShouldStartHidden(bool startMinimized, bool stayInTray)
        => startMinimized && stayInTray;

    /// <summary>
    /// 在 Show() **之前**调用。四个属性缺一个都不算数，顺序也不可换。
    /// </summary>
    public static void PrepareHidden(Window window)
    {
        window.ShowActivated = false;
        window.WindowState = WindowState.Minimized;
        window.Visibility = Visibility.Hidden;
        window.ShowInTaskbar = false;
    }

    /// <summary>
    /// 用户从托盘点回来时调用：把藏起来时改掉的四样东西按相反方向还原。
    /// 只 Show() 不还原，会得到一个「窗口出现了、但没任务栏图标、也不响应点击」的怪东西。
    /// </summary>
    public static void RestoreShown(Window window)
    {
        window.ShowInTaskbar = true;
        window.ShowActivated = true;
        window.Visibility = Visibility.Visible;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
    }
}
