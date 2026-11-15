using System;
using System.Collections.Generic;
using System.Text;

namespace DragonButler
{
    /// <summary>
    /// v1.15 / L6：通知正文模板。
    ///
    /// 只给一句话、只给一层占位符，不搞表达式、不搞条件、不搞多套模板。
    /// 理由：真正想改通知的人，诉求几乎都是「别那么啰嗦」或「把我的名字带上」，
    /// 而不是「按队列分别设不同文案」。前者一句话就能满足，后者需要一整套规则引擎，
    /// 那是 ABGI 那种给重度用户的东西，跟这一版「降门槛」的目标正好相反。
    ///
    /// 空模板 = 用程序内置的正文（现在的行为），这样老用户升级后一个字都不会变。
    /// </summary>
    public static class NotifyTemplate
    {
        /// <summary>模板里认的占位符，顺序就是界面上小字的顺序。</summary>
        public static readonly string[] Placeholders = { "结果", "成功", "总数", "耗时", "原因", "现场", "收益" };

        /// <summary>给界面用的一行提示：每个占位符什么意思。</summary>
        public static string Hint => "可用：" + string.Join("  ", Array.ConvertAll(Placeholders, p => "{" + p + "}"));

        /// <summary>示例正文，界面上当占位提示用（不是默认值——默认值必须是空）。</summary>
        public const string Sample = "今晚跑完了：{成功}/{总数}，用了 {耗时}。";

        /// <summary>
        /// 填充占位符。认不出的占位符原样留着——比替换成空白好：
        /// 用户拼错一个词，看到屏幕上还留着 {成攻} 立刻就知道错在哪；
        /// 换成空白的话，他只会觉得「这段话怎么少了一截」。
        /// </summary>
        public static string Render(string? template, IReadOnlyDictionary<string, string>? values)
        {
            if (string.IsNullOrWhiteSpace(template)) return "";
            string result = template.Trim();
            if (values == null) return result;

            var sb = new StringBuilder(result.Length + 32);
            for (int i = 0; i < result.Length; i++)
            {
                char c = result[i];
                if (c != '{')
                {
                    sb.Append(c);
                    continue;
                }
                int end = result.IndexOf('}', i + 1);
                if (end < 0)
                {
                    // 没闭合的左括号，剩下的原样输出
                    sb.Append(result, i, result.Length - i);
                    break;
                }
                string key = result.Substring(i + 1, end - i - 1).Trim();
                if (values.TryGetValue(key, out var v))
                {
                    sb.Append(v);
                    i = end;
                }
                else
                {
                    sb.Append(result, i, end - i + 1);
                    i = end;
                }
            }
            return sb.ToString();
        }

        /// <summary>把模板里的换行压成空格：通知正文里换行在部分渠道（Bark、托盘气泡）会被吞掉，看着像断句。</summary>
        public static string OneLine(string? template)
        {
            if (string.IsNullOrWhiteSpace(template)) return "";
            var raw = template.Replace("\r", " ").Replace("\n", " ").Trim();
            while (raw.Contains("  ")) raw = raw.Replace("  ", " ");
            return raw;
        }
    }
}
