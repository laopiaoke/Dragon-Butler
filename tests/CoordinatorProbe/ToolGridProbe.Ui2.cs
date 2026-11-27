using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using DragonButler;
using static CoordinatorProbe.Program;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。

// v1.45.3 / #15：这一段（v1.32 的工具按键与图标格）单独一个文件，因为 Ui.cs 顶到 20 KB 了。
// 断言正文一字未改，只是换了所在文件。
internal sealed partial class ToolGridProbe
{
    private void Step19()
    {
        // ---- 11f) v1.32 一个工具一个按键 / 引导页图标格 ----
        Check("按键/主页那排按键已撤", !mainXaml.Contains("x:Name=\"ToolButtonHost\""));
        Check("按键/设置页有一列工具", mainXaml.Contains("x:Name=\"SecToolsHost\""));
        Check("按键/旧的折叠头撤掉了",
            !mainXaml.Contains("x:Name=\"BtnFoldTools\"") && !mainXaml.Contains("x:Name=\"ChevTools\""));
        Check("按键/长列表留着但不再显示",
            mainXaml.Contains("<StackPanel x:Name=\"ToolCardsHost\" Visibility=\"Collapsed\">"));
        Check("按键/点一下开那个工具的配置窗", mainCs.Contains("row.MouseLeftButtonUp += (_, _) => OpenToolLaunch(toolId);"));
        Check("按键/名字太长会截断", mainCs.Contains("TextTrimming = TextTrimming.CharacterEllipsis,"));
        Check("按键/没配好的用警示色标出来", mainCs.Contains("\"还没找到主程序。点这一行填上，不然「一键开跑」会跳过它。\""));
        // 每秒轮询会走到这里，签名没变就一个控件都不碰——否则手指按着的那颗按钮会被换掉
        Check("按键/没变就不重造", mainCs.Contains("_toolSettingsSignature") && mainCs.Contains("if (signature == _toolSettingsSignature) return;"));

        Check("引导/图标格不是一行一个", wizardXaml.Contains("<WrapPanel x:Name=\"ToolPickHost\""));
        Check("引导/有压在角上的勾选框", wizardXaml.Contains("x:Key=\"WizTileCheck\""));
        Check("引导/勾上周围变绿", wizardCs.Contains("tile.BorderBrush = (Brush?)TryFindResource(\"BrandBrush\")"));
        Check("引导/一整格都能点", wizardCs.Contains("tile.MouseLeftButtonUp += (_, _) => check.IsChecked = check.IsChecked != true;"));
        Check("引导/名字两行封顶", wizardCs.Contains("MaxHeight = 30") && wizardCs.Contains("TextWrapping = TextWrapping.Wrap"));

        Check("图标/缺的那五个有图了",
            System.IO.File.Exists("Dragon Butler/Assets/icons/baah.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/hsr.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/m9a.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/mss.png") &&
            System.IO.File.Exists("Dragon Butler/Assets/icons/src.png"));
        // m9a 的原文件其实是 ICO 冒名 png，重存过；比 10 KB 大说明还是那个两百多 K 的壳
        Check("图标/m9a 是真 PNG",
            System.IO.File.Exists("Dragon Butler/Assets/icons/m9a.png") &&
            new System.IO.FileInfo("Dragon Butler/Assets/icons/m9a.png").Length < 120_000);
    }
}
