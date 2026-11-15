using System;

namespace DragonButler
{
    /// <summary>
    /// 首屏主卡的状态。
    /// 加一个状态的成本很高（要做文案、要写断言、要出渲染图），所以这里只留五种——
    /// 再多就变成「状态机自娱自乐」，用户其实只需要知道「现在该点哪」。
    /// </summary>
    public enum HomePhase
    {
        /// <summary>一个工具都没配好：只能引导。</summary>
        FirstRun,
        /// <summary>正在跑：说清跑到哪、还要多久。</summary>
        Running,
        /// <summary>刚出过错（卡住/崩溃/任务失败）：先处理这个，别管别的。</summary>
        Trouble,
        /// <summary>今天跑完且成功：给结论，给下一步。</summary>
        Finished,
        /// <summary>配好了但没在跑：说清下次什么时候、还剩多久。</summary>
        Waiting,
    }

    /// <summary>
    /// 主卡要显示的一屏文案。全部是字符串和布尔，不含任何 WPF 类型——
    /// 这样判定逻辑可以在探针里逐条断言，不需要起窗口。
    /// </summary>
    public sealed record HomeView(
        HomePhase Phase,
        string Title,
        string Detail,
        string PrimaryText,
        string PrimaryCommand,
        string SecondaryText,
        string SecondaryCommand,
        bool ShowSteps);

    /// <summary>
    /// 首屏判定。纯函数，输入全是原始事实，输出全是文案。
    /// 设计约束：<b>不读 AppState、不碰磁盘、不看时钟</b>——now 由调用方传进来。
    /// </summary>
    public static class HomeState
    {
        /// <summary>「今天跑完了」的有效期：超过这个时长就退回 Waiting，免得第二天还挂着昨天的结论。</summary>
        public static readonly TimeSpan FreshHours = TimeSpan.FromHours(18);

        public static HomeView Build(
            bool anyToolReady,
            bool running,
            string? toolName,
            string? groupName,
            TimeSpan toolElapsed,
            TimeSpan? groupElapsed,
            double? medianMinutes,
            int done,
            int total,
            DateTime? nextDue,
            bool lastRunOk,
            DateTime? lastRunAt,
            bool trouble,
            DateTime now)
        {
            // 1) 在跑的时候，「跑得怎么样」压过一切——哪怕一个工具都没配好
            //    （比如工具被手动删了但队列还在跑，这时最该说的是进度而不是引导）
            if (running)
            {
                return new HomeView(
                    HomePhase.Running,
                    RunningTitle(groupName, toolName, groupElapsed ?? toolElapsed, done, total),
                    RunningDetail(groupElapsed, medianMinutes, done, total, now),
                    "停下", "run.stop",
                    "看今天", "view.today",
                    false);
            }

            // 2) 出过事：先让人知道哪儿不对，再谈下一步
            if (trouble)
            {
                return new HomeView(
                    HomePhase.Trouble,
                    "刚才有点不对劲",
                    "我停下动作了，没继续往下跑。点「看现场」能看到当时的日志和截图。",
                    "看现场", "tool.snapshot",
                    "重试这一轮", "run.queue",
                    false);
            }

            // 3) 一个工具都没配好：唯一该做的就是引导，不给别的选项分散注意力
            if (!anyToolReady)
            {
                return new HomeView(
                    HomePhase.FirstRun,
                    "先告诉我在哪",
                    "找到 BetterGI 的 exe，选一下就好。剩下的我按默认值配。",
                    "选择 BetterGI.exe", "pick.bettergi",
                    "我先看看", "view.schedule",
                    true);
            }

            // 4) 今天跑完且成功
            if (lastRunOk && lastRunAt.HasValue && now - lastRunAt.Value <= FreshHours)
            {
                // 刚跑完的人最想知道「明天还跑不跑」。定时没开时，把「定个时间」
                // 提到主按钮——这比再看一遍今天干了什么更能留住人。
                bool hasNext = nextDue.HasValue;
                return new HomeView(
                    HomePhase.Finished,
                    "今天跑完了",
                    FinishedDetail(lastRunAt.Value, nextDue),
                    hasNext ? "看今天" : "定个时间", hasNext ? "view.today" : "view.schedule",
                    hasNext ? "明天几点跑" : "看今天", hasNext ? "view.schedule" : "view.today",
                    false);
            }

            // 5) 待跑：说清下次什么时候、还剩多久。
            //
            // 注意这里两个按钮必须<strong>不一样的意思</strong>：
            // 定了时间 → 「现在就开跑」+「改时间」；没定时间 → 「现在就开跑」+「定个时间」。
            // 早先的写法在没定时间时主按钮也给「定个时间」，两个按钮都指向同一件事，
            // 等于逼人做一次无意义的选择。
            return new HomeView(
                HomePhase.Waiting,
                WaitingTitle(nextDue, now),
                WaitingDetail(nextDue, now),
                "现在就开跑", "run.queue",
                nextDue.HasValue ? "改时间" : "定个时间", "view.schedule",
                false);
        }

        private static string RunningTitle(string? groupName, string? toolName, TimeSpan elapsed, int done, int total)
        {
            string who = !string.IsNullOrWhiteSpace(groupName)
                ? groupName!
                : (!string.IsNullOrWhiteSpace(toolName) ? toolName! : "正在跑");
            string tail = total > 0 ? $"　{done}/{total}" : "";
            return $"{who} · 已跑 {LiveStatus.Short(elapsed)}{tail}";
        }

        private static string RunningDetail(TimeSpan? groupElapsed, double? medianMinutes, int done, int total, DateTime now)
        {
            if (!groupElapsed.HasValue || medianMinutes is not > 0)
                return total > 0 ? $"队列整体 {done}/{total}，还差 {Math.Max(total - done, 0)} 项。" : "刚开始，还说不好要多久。";

            var eta = groupElapsed.Value.TotalMinutes > medianMinutes.Value
                ? null
                : (DateTime?)now.AddMinutes(medianMinutes.Value - groupElapsed.Value.TotalMinutes);

            if (eta == null)
                return $"已经超过往常的 {LiveStatus.Short(TimeSpan.FromMinutes(medianMinutes.Value))}，可能卡住了，也可能只是今天慢。";

            return $"通常这时候差不多了，预计 {eta.Value:HH:mm} 结束。";
        }

        private static string FinishedDetail(DateTime lastRunAt, DateTime? nextDue)
        {
            string at = lastRunAt.ToString("HH:mm");
            return nextDue.HasValue
                ? $"{at} 那轮全部成功。下次 {nextDue.Value:MM-dd HH:mm} 自己开跑。"
                : $"{at} 那轮全部成功。定时没开，想每天自动跑就去定个时间。";
        }

        private static string WaitingTitle(DateTime? nextDue, DateTime now)
        {
            if (!nextDue.HasValue) return "还没定几点跑";
            var due = nextDue.Value;
            bool today = due.Date == now.Date;
            bool tomorrow = due.Date == now.Date.AddDays(1);
            string day = today ? "今天" : tomorrow ? "明天" : due.ToString("MM-dd");
            return $"{day} {due:HH:mm} 自己开跑";
        }

        private static string WaitingDetail(DateTime? nextDue, DateTime now)
        {
            if (!nextDue.HasValue) return "定个时间，到点我自己开跑；你在玩游戏的话我会等你退出。";
            var left = nextDue.Value - now;
            if (left <= TimeSpan.Zero) return "到点了，随时可以开始。";
            string span = LiveStatus.Short(left);
            return $"还有 {span}。到点如果你在玩游戏，我会等你退出再开始。";
        }
    }
}
