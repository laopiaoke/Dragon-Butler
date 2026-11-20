using System;

namespace DragonButler
{
    /// <summary>
    /// 四页滑动的纯计算部分（v1.22，v1.34 从三页扩到四页）。
    ///
    /// 界面里那套（Grid + TranslateTransform + 动画）没法断言，
    /// 但「滑到哪一页」「每页该摆在哪」「这一下算不算翻页」是纯算术，可以离线验。
    /// 所以把这三件事抽出来，窗口那边只负责把结果喂给 TranslateTransform。
    ///
    /// 四页的顺序本身就是语义：
    ///   0 = 日志（刚才怎么样了）　1 = 主页（今晚跑什么，打开就在这）
    ///   2 = 设置（配一次的东西）　3 = 插件（往里放东西的地方）
    /// 打开落在 1 而不是 0：绝大多数时候人打开它是为了「今晚跑不跑」，
    /// 落到日志页会先看到一句「还没有记录」，等于先道了个歉。
    ///
    /// v1.34 为什么加第四页：插件原来是设置页里一张默认收起的折叠卡。
    /// 「要往里放东西」的入口折叠着，等于让人自己猜它存在——
    /// 而它偏偏又是唯一一处「你需要动手放文件进去」的地方，最不该藏。
    /// </summary>
    public static class Pager
    {
        public const int Count = 4;
        public const int Home = 1;

        /// <summary>翻页阈值：可视宽的 12%，但不小于 80px。低于这个距离当作手抖，不翻。</summary>
        public static double SwipeThreshold(double width)
            => Math.Max(80, width * 0.12);

        /// <summary>把页码夹进合法区间。越界不环绕——从最后一页再往右滑应该停住，而不是跳回第一页。</summary>
        public static int Clamp(int index) => index < 0 ? 0 : index >= Count ? Count - 1 : index;

        /// <summary>
        /// 每一页相对可视区该偏移几屏。当前页永远是 0，左边的页是负、右边是正。
        /// 返回长度 Count 的数组，下标就是页码。
        /// </summary>
        public static int[] Offsets(int current)
        {
            int c = Clamp(current);
            var result = new int[Count];
            for (int i = 0; i < Count; i++) result[i] = i - c;
            return result;
        }

        /// <summary>
        /// 一次横滑落到哪一页。dx 是手指位移（屏幕坐标，往右为正），
        /// 所以往左滑（dx 为负）= 去下一页。滑动距离不够就原地不动，返回 current。
        /// </summary>
        public static int Swipe(int current, double dx, double width)
        {
            int c = Clamp(current);
            if (Math.Abs(dx) < SwipeThreshold(width)) return c;
            return Clamp(dx < 0 ? c + 1 : c - 1);
        }

        /// <summary>页码对应的中文名，写日志和状态提示时用。</summary>
        public static string Name(int index) => Clamp(index) switch
        {
            0 => "日志",
            2 => "设置",
            3 => "插件",
            _ => "主页",
        };
    }
}
