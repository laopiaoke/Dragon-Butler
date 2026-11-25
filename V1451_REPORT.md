# v1.45.1 大扫除交付报告

## 〇 一句话结论

同一件事写两遍的地方收了四处，从没被调用的方法删了三个。**零功能变化、零界面变化**，主工程 0 警告 0 错误，26 组探针全绿，`ui rows` 的零滚动硬闸门仍放行。

## 一 规矩收拢

| 规矩 | 收到哪 | 原来几处 | 做法 |
| --- | --- | --- | --- |
| 星期几位号（周一=0…周日=6） | `Dragon Butler/QueueService.cs:904` | 2（`QueueService` + `SchedulePlanner`） | `SchedulePlanner.WeekdayBit` 改成 `=> QueueService.WeekdayBit(day)` 转发。**两个入口都保留**——探针 `tests/CoordinatorProbe/Program.cs:4422` 调前者、`:6777` 调后者，删任何一个都会让探针编译不过。 |
| 耗时文案（`4小时0分` / `12分` / `不到 1 分钟`） | `Dragon Butler/MorningReport.cs` `MorningReportService.FormatDuration(TimeSpan)` | 2（队列收尾 + 晨报/周报） | 规则只写在 `TimeSpan` 版；`double` 版改成 `=> FormatDuration(TimeSpan.FromMinutes(Math.Round(totalMinutes)))`；`QueueService` 私有版改成单行转发。周报断言 `总耗时 4小时0分` / `平均每次 2小时0分` 仍绿。 |
| 按主题取画刷（取不到兜底色） | `Dragon Butler/Theme.cs` `Theme.BrushOf(key, fallback)` | 3（日志规则 / 消息中心 / 今日运行，逐字相同） | 三处私有方法各改成单行转发，各自的调用点（4 / 16 / 20 处）一行没动。 |
| 按主题取画刷（取不到返回空） | `Dragon Butler/MainWindow.xaml.cs:301` | 1 | **不并**：返回 `Brush?` 而非兜底色，171 处调用依赖「取不到就是 null」的语义，强行合并会改变行为。 |

## 二 删掉的死代码

三个方法在 `*.cs` + `*.xaml`（排除 `obj\` `bin\`）里全库零调用，逐个核实方法体后删除，各留一行说明：

- `CenterOnActiveMonitor()`（`MainWindow.xaml.cs` 原 :181）—— 空方法，方法体只有一句注释。真正管窗口位置的是 `RestoreWindowBounds()`。
- `BuildQueueTip(QueueEntry, QueueItemResult)`（原 :1078）—— 格子时代的悬停提示，v1.42 换成横向工具行后改由行内徽章与右键菜单承担。它引用的 `QueueGraph.DependenciesOf` / `GroupSlicing.UsesGlobal` 都还有别的调用点。
- `NaturalToolOrder()`（原 :2528）—— 读 `AppState.Settings.Queue` 生成工具表顺序，已被 `CurrentToolOrder()`（读 `ToolPages`）取代。

### 扫出来但**没删**的（都是有意保留，删了会坏事）

- `Dragon Butler/PluginContract.cs`（86 行）：`docs/插件计划.md:27-55` 与 `docs/路线图.md:2449-2457` 写明它是 v1.20 起给插件作者的公开契约，`PluginLoader.cs` 按它校验 `Plugin.dll`。**当前无人实现 ≠ 死代码。**
- 探针里的「必须不存在」断言（`TrayConfigWindow`、`BtnQueueCancelShutdown`、`BtnQueueView`、`WeeklyPlanWindow` 等）：删了断言会空转。
- `SelectedGroups` 的 16 处引用：v17 迁移与 v1.2→v1.3 兼容路径要读它，`ProfileRegistry.cs:229` 的 `hasLegacy` 判据依赖它。
- 156 个「私有方法全库只出现 1 次」候选里，绝大部分是 XAML 事件处理器（在 `.xaml` 里以字符串引用，不在 `*.cs` 的计数范围内）—— 逐个核过，只有上面 3 个是真死。

## 三 验证

- `dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release` → **0 个警告 0 个错误**；探针工程同样 0/0。
- **26 组探针全绿**：schedule / resume / bundle / explain / retry / api / weekly / hotkey / progress / command / dag / groups / bgiupdate / harvest / mcp / uirules / pager / morning / recap / toolgrid / logpattern / migrate / afterrun / signals / wrapup / m12fix。
- `ui rows` → `ZERO-SCROLL fold summary=True start=True add=True`、`WRAPUP bar=Visible left=60`、`rowsExit=0`。
- `git --no-pager diff --numstat`（提交前）：8 文件 `+62 / −78`，最大的 `MainWindow.xaml.cs` 是 `9/48` —— **没有整文件重写**。
- 五个 LF 文件 `git ls-files --eol` 提交后仍是 `i/lf w/lf attr/text`，与索引一致。

## 四 施工中发现、但没动手的问题

1. **13 个源文件超过 5000 token**（`MainWindow.xaml.cs` 397 KB / 9153 行、`MainWindow.xaml` 251 KB / 3175 行、`QueueService.cs` 82 KB…）。本次只做「删与并」，拆文件会改变 40 多处调用点的可见性，风险与收益不成比例。
2. **`docs/proto/` 有 87 张图没人引用**（5.6 MB，走 LFS）。删除会动历史版本截图，需要产品确认哪些还要留。
3. **10 份 `*_REPORT.md` 里 4 份是 10 月的一次性报告**（`BUILD_REPORT` / `HOTFIX0_REPORT` / `PHASE1_REPORT` / `PHASE2_REPORT`），信息已过时，但它们是当时的验收记录。
4. **没有 AGENTS.md**，也没有提交前钩子。agent 每次仍会读到 397 KB 的 `MainWindow.xaml.cs`。

## 五 需产品确认

- 上面第 1–4 条要不要做、按什么顺序做。
- `Theme.BrushOf` 是否需要把 `HandoverWindow.xaml.cs:117`、`PreflightWindow.xaml.cs:148` 那两处「写法不同但同义」的也收进来（它们不逐字相同，收进来要改语义细节）。
