using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 它会在你回来之前收拾好（v1.20 / 奇思妙想 #7）。
///
/// <para>
/// <b>为什么这件事以前没人做</b>：自动化工具的终点通常是「跑完了」，
/// 但用户回来时面对的是——游戏还开着、BetterGI 还挂着、窗口被拉到奇怪的位置。
/// 这些不是故障，是「有人用过这台机器」的痕迹，而留下痕迹的正是我们。
/// </para>
/// <para>
/// <b>为什么默认全关</b>：关掉别人的程序是不可逆的。哪怕只有 1% 的情况关错了
/// （比如你其实正想继续玩），代价也远大于收益。所以这里的一切都必须用户亲口打开，
/// 而且<b>关游戏单独一个开关</b>——「关掉工具」和「关掉我在玩的游戏」不是一件事。
/// </para>
/// <para>纯函数：只算「该做什么」，不做。窗口的读写由 <see cref="TidyService"/> 负责。</para>
/// </summary>
public static class Tidy
{
    /// <summary>一个被记住的窗口位置。</summary>
    public sealed record WindowSpot(string ProcessName, string Title, int Left, int Top, int Width, int Height);

    public enum Action { CloseGame, CloseTool, RestoreWindows }

    public sealed record Step(Action Kind, string Text, bool Destructive);

    public sealed record Input(
        bool Enabled,
        bool CloseGameEnabled,
        bool CloseToolEnabled,
        bool RestoreWindowsEnabled,
        bool GameRunning,
        bool ToolRunning,
        int RememberedWindows,
        bool AnyFailure,
        bool DryRun);

    public sealed record Plan(IReadOnlyList<Step> Steps, bool Anything, string Summary);

    /// <summary>
    /// 算一份收拾计划。<paramref name="input"/>.Enabled 为 false 时返回空计划——
    /// 「没开」和「开了但没东西可收拾」在界面上是两句话，所以 Summary 区分。
    /// </summary>
    public static Plan Build(Input input)
    {
        var steps = new List<Step>();

        if (!input.Enabled)
            return new Plan(steps, false, "没开「跑完收拾一下」。");

        if (input.CloseGameEnabled && input.GameRunning)
            steps.Add(new(Action.CloseGame, "关掉游戏", Destructive: true));

        if (input.CloseToolEnabled && input.ToolRunning)
            steps.Add(new(Action.CloseTool, "关掉 BetterGI", Destructive: false));

        if (input.RestoreWindowsEnabled && input.RememberedWindows > 0)
            steps.Add(new(Action.RestoreWindows,
                input.RememberedWindows == 1 ? "把 1 个窗口放回原处" : $"把 {input.RememberedWindows} 个窗口放回原处",
                Destructive: false));

        if (steps.Count == 0)
            return new Plan(steps, false, "没什么要收拾的。");

        // 有失败时不关游戏：那时候用户多半想自己看一眼现场，
        // 把游戏关了等于把证据也关了。
        if (input.AnyFailure && steps.Any(s => s.Kind == Action.CloseGame))
        {
            steps.RemoveAll(s => s.Kind == Action.CloseGame);
            steps.Insert(0, new(Action.CloseGame, "这次不关游戏（上一轮没跑成，留着让你看）", Destructive: false));
            return new Plan(steps, steps.Any(s => s.Kind != Action.CloseGame), Summary(steps, input));
        }

        return new Plan(steps, true, Summary(steps, input));
    }

    private static string Summary(List<Step> steps, Input input)
    {
        var real = steps.Where(s => !(s.Kind == Action.CloseGame && s.Text.StartsWith("这次不关"))).ToList();
        if (real.Count == 0) return "这次不收拾——上一轮没跑成。";
        string body = string.Join("、", real.Select(s => s.Text));
        return input.DryRun ? $"（演练）会{body}。" : $"已{body}。";
    }

    /// <summary>给通知正文用的一句话：只报「做了什么」，不报步骤清单。</summary>
    public static string ShortSummary(Plan plan)
    {
        if (plan == null || !plan.Anything) return "";
        return plan.Summary;
    }

    /// <summary>
    /// 判断一个窗口值不值得记。极小/极大/在屏幕外的都不记——
    /// 那些多半是隐藏窗口、工具提示或已经离屏的旧窗口，记住它们只会让还原看起来像乱跳。
    /// </summary>
    public static bool WorthRemembering(WindowSpot? spot, int screenWidth, int screenHeight)
    {
        if (spot == null) return false;
        if (spot.Width < 160 || spot.Height < 120) return false;
        if (spot.Width > screenWidth * 2 || spot.Height > screenHeight * 2) return false;
        // 整个窗口都在屏幕外 = 用户看不见它，记了也没意义。
        if (spot.Left + spot.Width < 0 || spot.Top + spot.Height < 0) return false;
        if (spot.Left > screenWidth || spot.Top > screenHeight) return false;
        return true;
    }

    /// <summary>
    /// 两个位置算不算「已经归位」。差值 ≤ <paramref name="tolerance"/> 就当没动过——
    /// 差两个像素还去挪一次，反而会打断正在用的人。
    /// </summary>
    public static bool SamePlace(WindowSpot? a, WindowSpot? b, int tolerance = 8)
    {
        if (a == null || b == null) return false;
        return Math.Abs(a.Left - b.Left) <= tolerance
               && Math.Abs(a.Top - b.Top) <= tolerance
               && Math.Abs(a.Width - b.Width) <= tolerance
               && Math.Abs(a.Height - b.Height) <= tolerance;
    }
}
