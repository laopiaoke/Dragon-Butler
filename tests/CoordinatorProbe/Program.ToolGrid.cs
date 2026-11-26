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

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。
internal static partial class Program
{
    /// <summary>
    /// v1.21：桌面网格（2 行 4 列，可自由排序）。
    ///
    /// 这块最容易写错的三处，正好就是断言的重点：
    /// ① 已删除的工具不能留空位；② 排到底不能循环；③ 新工具排最后不插队。
    /// </summary>
    ///
    /// v1.45.3 / #15：方法体搬进 <see cref="ToolGridProbe"/>——原来 66 KB 的单方法一个文件放不下。
    /// 断言正文一字未改，只是每段成了一个小方法，共享状态改成字段。
    /// </summary>
    private static int RunToolGrid() => new ToolGridProbe().Run();
}
