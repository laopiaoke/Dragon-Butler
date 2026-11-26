# V1.45.3 交付报告：探针拆分 + 入口金丝雀

## 〇 一句话结论

`tests/CoordinatorProbe/Program.cs` 从 **458,146 B / 7,290 行**拆成 **46 个文件，最大 19,816 B**；
断言一条没改（`toolgrid` 364 条、`ui all` 51 张图都对得上），入口从 `switch` 换成显式表并配了
「CI 组名单 + 入口总数」两道金丝雀，少注册一组当场红。文件大小闸门的白名单从 17 项降到 14 项——
这是它第一次真的减。

## 一 #14：失败模式签名独立成档（提交 `7a801b4`）

| 文件 | 改动 |
|---|---|
| `docs/SIGNATURES.md` | 新建。S1–S14 逐条（现象 / 怎么认出来 / 怎么防），开头写明用途与「新增签名必须附本项目已证实案例，纯理论推测不收录」，末尾一张「生成时自查速查」两列表 |
| `AGENTS.md` | 加一行指针「动代码前还要过一遍失败模式签名」；坑列表加第 9 条：「判断没人引用必须全库扫 `.md` / `.txt` / `.cs`，包括各版本报告的产出物表」 |

用户原稿里 S13 写的是「webhook 端口冲突（任务书 #5 修复）、剪贴板复制失败（#11 修复）」，
**按原样落地，没有自己补 `v1.44` / `v1.45` 版本前缀**——版本号会漂，任务书编号不会。

`S1–S12` 这个说法在仓库里原本没有成文清单（全库扫描只命中 `V144_REPORT.md` 的「P0 / S1」优先级标注），
所以这份档是按用户 m07228 给的原文新建的，不是从别处搬的。

## 二 #15：探针拆分

### 2.1 拆分前后的体量

| 项 | 拆前 | 拆后 |
|---|---|---|
| `Program.cs` | 458,146 B / 7,290 行 | 7,072 B / 198 行（只剩入口表 + `ResolveStub`） |
| 探针文件数 | 3 个（`Program.cs` / `RenderProbe.cs` / `MemoryProbe.cs`） | 46 个 |
| 单文件最大 | 458,146 B | 19,816 B（`Program.Weekly.cs`） |
| 超过 20 KB 的探针文件 | 3 个 | 0 个 |

**拆分口径**：按功能组切，不按行数切。一个组一个文件，名字就是组名
（`Program.Schedule.cs` 放 `RunSchedule` / `RunResume` / `RunExplain` / `RunRetry`）。
小组合并（如 `Program.Bundle.cs` = `RunBundle` + `Migrate`），大组一个占一个文件。
全部用 `internal static partial class Program`，**成员写法、断言正文、缩进一字未改**。

分组表（30 组）：

- `Program.cs` = `ResolveStub` / `Main`
- `Program.Core.cs` = `Stub` / `Run` / `FeedLogPattern` / `After` / `WaitHolderExit` / `Compact` / `PowerRequests` / `SystemParametersInfo` / `GetScreensaver` / `SetScreensaver` / `GetGuiResources` / `GdiCount` / `SaveArchive` / `WritePng` / `CountLog` / `LogHas` / `CountOf` / `RepoFileExists` / `ReadRepoSources` / `SliceBetween` / `CountRowsBefore` / `Request` / `Call` / `Help`
- `Program.Clipboard.cs` / `Program.Signals.cs` / `Program.WrapUp.cs` / `Program.Queue.cs` /
  `Program.QueueFlow.cs` / `Program.Backup.cs` / `Program.Presets.cs` / `Program.Completion.cs` /
  `Program.Preflight.cs` / `Program.Decision.cs` / `Program.Away.cs` / `Program.Troubleshoot.cs` /
  `Program.Pager.cs` / `Program.ToolCatalog.cs` / `Program.Suggest.cs` / `Program.Hardware.cs` /
  `Program.WeekPlan.cs` / `Program.Weekly.cs` / `Program.Schedule.cs` / `Program.Bundle.cs` /
  `Program.Hotkey.cs` / `Program.Live.cs` / `Program.Favorites.cs` / `Program.Dag.cs` /
  `Program.BgiVersion.cs` / `Program.UiRules.cs` / `Program.Mcp.cs` / `Program.ToolGrid.cs`

### 2.2 `RunToolGrid`：唯一拆不开的那个

它一个方法 66,809 B，内部 30 个 `// ---- N) ----` 分段，共享 52 个顶层局部变量
（`mainXaml` / `mainCs` / `order` / `m1`~`m4` / `np1`~`np3` ……）。按组拆不够，按分段拆文件也不行——
跨段共享的变量没有归属。

**方案**：新建命名空间级 `internal sealed partial class ToolGridProbe`，分 5 个文件：

| 文件 | 字节 | 内容 |
|---|---|---|
| `ToolGridProbe.cs` | 17,264 | 52 个字段 + `Run()`（`Step0();`…`Step28();` + RESULT + return）+ `Check` 实例方法 + `Step0()`–`Step12()` |
| `ToolGridProbe.Ui.cs` | 19,071 | `Step13()`–`Step19()` |
| `ToolGridProbe.Flow.cs` | 14,444 | `Step20()`–`Step23()` |
| `ToolGridProbe.Pages.cs` | 13,254 | `Step24()`–`Step26()` |
| `ToolGridProbe.Check.cs` | 9,795 | `Step27()`–`Step28()` |
| `Program.ToolGrid.cs` | 1,288 | 薄壳：`private static int RunToolGrid() => new ToolGridProbe().Run();` |

52 个局部变量 → 字段（值类型 `(int Page, int Slot) loc;` 不加 `= null!;`，其余加），
局部函数 `Check` → 实例方法，30 个分段 → 实例方法 `StepN()`。
**分段方法体保持 8 格缩进不变**——这是选「命名空间级类」而不是「嵌套类」的原因：
嵌套类的缩进要多一层，756 行全部重排，diff 会变成整个文件。

### 2.3 `RenderProbe`（93,456 B）：按截图分组拆

`RunUi(string mode)` 一个方法里 25 个 `if (mode == "…")` 块。改成：

| 文件 | 字节 | 内容 |
|---|---|---|
| `RenderProbe.cs` | 10,406 | `Save` / `SwitchToSettingsPage` / `SwitchToPluginsPage` / `ResetHomeFixture` / `SettleNav` / `SwitchToReportPage` / `RunUi => new UiRunner(mode).Run()` |
| `RenderProbe.Ui.cs` | 4,621 | `UiRunner` 字段 + `Run()`（`Step0();`…`Step24();`） |
| `RenderProbe.Ui.A.cs` | 18,937 | step 0–11 |
| `RenderProbe.Ui.B.cs` | 18,081 | step 12–13 |
| `RenderProbe.Ui.C.cs` | 18,532 | step 14–23 |
| `RenderProbe.Ui.D.cs` | 2,594 | step 24 |
| `RenderProbe.Seeds.cs` | 19,533 | 14 个 `Seed*` / `SetStatic` / `SetBacking` / `WriteIfMissing` |

### 2.4 `MemoryProbe`（25,432 B）

| 文件 | 字节 | 内容 |
|---|---|---|
| `MemoryProbe.cs` | 9,476 | `Run()` + `Measure` / `MeasureChild` / `MeasureOpenCost` / `Collect` / `Pump` / 计数工具 |
| `MemoryProbe.Core.cs` | 13,198 | `_trimTarget` + `ShowAndMeasure` / `HideAndMeasure` / `TrimAndMeasure` |
| `MemoryProbe.Trim.cs` | 4,152 | `RunTrim()` |

`MemoryProbe` 原声明是 `public static class`（不是 internal），三个文件统一成
`public static partial class MemoryProbe`——写成 internal 会报 CS0260 / CS0262。

### 2.5 断言等价性证明

| 证据 | 拆前（HEAD） | 拆后 |
|---|---|---|
| `toolgrid` 组 `Check("` 出现次数 | 364 | 364（实跑输出 364 行 PASS/FAIL） |
| `toolgrid` 结果 | PASS | `RESULT toolgrid fails=0` / exit=0 |
| `ui all` 出图 | 51 张 | 51 张，`fail=0` / exit=0 |
| `ui rows` 零滚动闸门 | `fold summary=True start=True add=True` | 同左，`rowsExit=0` |
| 26 组 | 全绿 | 全绿（`ran=26 bad=0`） |

## 三 入口金丝雀

**要防的不是「写错」，是「少跑」**：拆分之后「忘了把分支加回去」不报错，只会安静地少测一条。
少测的那条可能是最危险的那条（`signals` 组守的就是「崩溃不许当成功」）。

**做法**（`tests/CoordinatorProbe/Program.cs`）：

1. `switch` → `Dictionary<string, Func<int>> modes`，84 个 key→lambda。
2. `private static readonly string[] CiGroups`：CI 点名的 21 组。
3. `private const int ExpectedModeCount = 84;`
4. 建表后立刻核一次「CI 组是否都在表里」，跑完再核一次，并核总数。
5. 两处都 `return 1`，**用退出码红，不只打日志**——日志会被 `| Select-Object -Last 3` 截掉。

每次运行都打印一行：

```
CANARY groups=84/84 ci=21 missing=0
```

**反向实测**：把 `["m12fix"] = () => RunM12Fix(),` 临时注释掉 → 编译 0/0 →
跑 `toolgrid` 输出 `CANARY-FAIL 组没注册：m12fix`、`exit=1`。还原后重新编译 0/0。

## 四 顺带修的两件真事

### 4.1 `uirules` 组 4 条断言因拆分误报

`Program.UiRules.cs:109` 原本是 `ReadRepoSources("tests/CoordinatorProbe/RenderProbe.cs")`——
读**单个文件**的源码文本，然后在里面搜 `ScrollToTop()` / `SeedSampleArchive` / `"wizard"` / `step <= 5`。
代码搬到 `RenderProbe.Ui.*.cs` 与 `RenderProbe.Seeds.cs` 之后，这四条全红。

修法：`Program.Core.cs` 新增 `ReadRepoSourcesGroup(string relativeDir, string pattern)`，
读同前缀的一组文件；`UiRules.cs:109` 改成
`ReadRepoSourcesGroup("tests/CoordinatorProbe", "RenderProbe*.cs")`。
**判据一字未改**，只改了「从哪儿读」。

### 4.2 闸门白名单第一次真的减

`scripts/check-file-size.ps1` 的 `$Whitelist` 删掉三行：

```
-    'tests/CoordinatorProbe/Program.cs'
-    'tests/CoordinatorProbe/RenderProbe.cs'
-    'tests/CoordinatorProbe/MemoryProbe.cs'
```

17 项 → 14 项。`& .\scripts\check-file-size.ps1 -All` → `文件大小：OK（上限 20 KB，白名单 14 个存量文件）`、exit=0。
第一次替换留下一个多余空行，diff 里出现一行只含 `+` 的变更，已修掉；最终 diff 只有那三行删除。

## 五 验证总账

| 项 | 结果 |
|---|---|
| 主工程编译 | 0 警告 0 错误 |
| 探针编译 | 0 警告 0 错误 |
| 26 组探针 | 全绿（`ran=26 bad=0`） |
| `ui rows` | `fold summary=True start=True add=True` / `WRAPUP shown=True cleared=True` / `rowsExit=0` |
| `ui all` | 51 张，`fail=0`，exit=0 |
| `mem` / `trim` | exit=0，尾部有 `CANARY groups=84/84 ci=21 missing=0` |
| 文件大小闸门 | `-All` → OK，白名单 14 |
| 探针单文件最大 | 19,816 B（限 20,480 B） |

## 六 施工中发现但没动手的

1. **主工程还有 13 个超 20 KB 的文件**（`MainWindow.xaml.cs` 404,957 / `MainWindow.xaml` 257,446 /
   `QueueService.cs` 84,427 / `FirstRunWindow.xaml.cs` 49,602 / `TimePickerControl.xaml.cs` 41,104 /
   `FirstRunWindow.xaml` 37,461 / `AppState.cs` 35,029 / `NotifyService.cs` 33,599 /
   `SentinelService.cs` 25,804 / `IncidentBundle.cs` 25,513 / `ToolCatalog.cs` 22,898 /
   `CustomToolWindow.xaml` 22,658 / `CustomThemeWindow.xaml` 20,498）。都在白名单里。按 D29 现在不拆。
2. **`docs/使用说明.txt` 也在白名单里**（UTF-16LE，22,358 B）。它是给人看的连续文档，拆开更糟。
3. **`uirules` 这类「读源码文本做断言」的组天生脆**。这次靠「读一组文件」绕过，但下一个人再搬代码还会红。
   根治要改成「读编译后的类型/成员」，成本不低，没动。
4. **`ExpectedModeCount = 84` 是个手动维护的数字**。加组时会红一次，这是设计意图（逼你确认），
   但它不会自己变——别忘了改。

## 七 需产品确认

1. **`RenderProbe.Ui.A/B/C/D` 的切分点**是按字节装箱定的（≤19,000 B），不是按语义。
   `Ui.B` 只有 step 12–13 两个块却 18 KB，`Ui.D` 只有一个块 2.5 KB。若要按语义重划，可以，但断言不受影响。
2. **金丝雀要不要也管住「多了的组」**。现在只管「CI 点名的组必须存在」+「总数不变」。
   如果想让「存在但没进 CI 的新组」也红一次，需要再加一条断言——那是新规则，不在本次范围。
3. **`ReadRepoSourcesGroup` 的匹配模式**写死 `"RenderProbe*.cs"`。以后 `RenderProbe` 再改名，这条要跟着改。

## 八 改动文件清单

**修改（5）**：`docs/PATTERNS.md`（+21 行，第 6 条）、`scripts/check-file-size.ps1`（−3 行白名单）、
`tests/CoordinatorProbe/Program.cs`（入口表 + 金丝雀）、`tests/CoordinatorProbe/RenderProbe.cs`、
`tests/CoordinatorProbe/MemoryProbe.cs`。

**新增（43）**：`Program.*.cs` 29 个、`ToolGridProbe*.cs` 5 个、`RenderProbe.Ui*.cs` 5 个、
`RenderProbe.Seeds.cs`、`MemoryProbe.Core.cs`、`MemoryProbe.Trim.cs`、`docs/SIGNATURES.md`（#14）。

**文档**：`RELEASE_NOTES.md`（顶部 v1.45.3 节）、`README.md`、`README.en.md`、
`docs/使用说明.txt`（三处版本号）、`Dragon Butler/Dragon Butler.csproj`（三处版本号）。
