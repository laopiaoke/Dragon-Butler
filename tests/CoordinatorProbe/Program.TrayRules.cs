using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CoordinatorProbe;

// v1.46.2 / 任务书 #18：托盘菜单的观感规则单独一个文件。
//
// 为什么从 Program.UiRules.cs 里挪出来：那一份加完这条之后 20.1 KB，撞上了文件大小闸门
// （上限 20 KB，白名单只减不增）。断言一条没改，只换了所在文件——与 v1.45.3 拆探针同一个理由。
internal static partial class Program
{
    /// <summary>
    /// 守三件事：
    /// ① 托盘菜单已换成 WPF 的 ContextMenu（不是把 WinForms 那条空白条调窄）；
    /// ② 样式文件只含 TrayMenu.* 前缀的键，不许碰任何既有键；
    /// ③ 颜色全走 DynamicResource（写死 hex 或 StaticResource，切主题就不跟了）。
    /// 全部读源码做宽容匹配，不依赖控件实例。
    /// </summary>
    private static void TrayMenuChecks(Action<string, bool, string> check)
    {
        const string TrayStyle = "Dragon Butler/Themes/TrayMenuStyle.xaml";
        check("托盘菜单/样式文件存在", RepoFileExists(TrayStyle), "");
        string tray = ReadRepoSources(TrayStyle);
        var trayKeys = Regex.Matches(tray, @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var badKeys = trayKeys.Where(k => !k.StartsWith("TrayMenu.", StringComparison.Ordinal)).ToList();
        check("托盘菜单/只含 TrayMenu.* 前缀的键", trayKeys.Count > 0 && badKeys.Count == 0,
            badKeys.Count > 0 ? string.Join(",", badKeys) : $"共 {trayKeys.Count} 个");

        string trayCs = ReadRepoSources("Dragon Butler/App.Tray.cs");
        // 残留一处旧菜单类就意味着两套菜单并存；ImageMargin 是那条空白的来源
        check("托盘菜单/已无旧菜单控件", !trayCs.Contains("ContextMenuStrip", StringComparison.Ordinal), "");
        check("托盘菜单/已无 ShowImageMargin",
            !trayCs.Contains("ShowImageMargin", StringComparison.Ordinal)
            && !ReadRepoSources("Dragon Butler/App.xaml").Contains("ShowImageMargin", StringComparison.Ordinal), "");
        check("托盘菜单/项模板不画图标列",
            tray.Contains("TrayMenu.ItemStyle", StringComparison.Ordinal)
            && !Regex.IsMatch(tray, @"ContentSource\s*=\s*""Icon"""), "");
        var staticRefs = Regex.Matches(tray, @"\{StaticResource ([^}]+)\}")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(k => !k.StartsWith("TrayMenu.", StringComparison.Ordinal)).ToList();
        check("托盘菜单/颜色只用 DynamicResource 或本文件的键", staticRefs.Count == 0,
            string.Join(",", staticRefs));
        check("托盘菜单/悬停与文字走既有色键",
            tray.Contains("{DynamicResource HoverBrush}", StringComparison.Ordinal)
            && tray.Contains("{DynamicResource TextPrimaryBrush}", StringComparison.Ordinal)
            && tray.Contains("{DynamicResource TextSecondaryBrush}", StringComparison.Ordinal)
            && tray.Contains("{DynamicResource TextTertiaryBrush}", StringComparison.Ordinal), "");
        check("托盘菜单/小字号用 Display 模式",
            tray.Contains("TextFormattingMode", StringComparison.Ordinal)
            && tray.Contains("ClearType", StringComparison.Ordinal), "");
        // 五个项一个都不能少（文案保持原样，任务书禁止改）
        var missing = new[] { "打开主界面", "一键开跑", "接着跑", "现在就到", "退出" }
            .Where(l => !trayCs.Contains(l, StringComparison.Ordinal)).ToList();
        check("托盘菜单/五个项都在", missing.Count == 0, string.Join(",", missing));
        // 弹菜单走托盘右键；点完先收菜单再干活（否则弹层可能挡住关机/退出）
        check("托盘菜单/右键才弹出",
            Regex.IsMatch(trayCs, @"MouseButtons\.Right[\s\S]{0,120}ShowTrayMenu\(\)"), "");
        check("托盘菜单/点完先收菜单再执行",
            Regex.IsMatch(trayCs, @"CloseTrayMenu\(\);[\s\S]{0,700}\bact\(\)"), "");
        // 收菜单还不够：Click 跑在弹出层的嵌套消息循环里，同步 Shutdown() 退不干净。
        // 断言只查「动作被排到了下一轮消息泵」，不查那几行注释怎么写。
        check("托盘菜单/动作排到下一轮消息泵（否则退出退不干净）",
            Regex.IsMatch(trayCs, @"Dispatcher\.BeginInvoke\([\s\S]{0,200}\bact\(\)"), "");
    }
}
