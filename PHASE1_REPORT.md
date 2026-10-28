# 龙管家 v1.3 · Phase 1 交付报告（ToolProfile 架构收敛）

- 交付时间：2026-10-29
- 范围：仅 Phase 1（纯重构，用户可见行为零变化）
- 产品版本：仍为 **v1.2.0**（按任务书，v1.3.0 的用户可见内容与版本号属于 Phase 2）
- 配置结构版本：**v1.2 → v1.3（`Version: 13`）**
- 构建：`dotnet build` **0 error / 0 warning**（两条存量告警已显式豁免，见 §7）

---

## 1. 结论摘要

| 目标 | 结果 |
| --- | --- |
| BGI 专属知识收拢进 ToolProfile | ✅ 三个档案（bettergi / march7th / ok-ww）字段与任务书表格逐项一致 |
| 完成信号抽象 | ✅ `ICompletionSignalSource` + 4 个实现（进程退出 / 游戏退出 / Webhook / 日志正则） |
| 仲裁状态机 | ✅ `RunCoordinator`，四条分支全部用 StubTool 实测通过 |
| BgiService 拆分 | ⚠️ 拆出 `ToolRunner` + `ProfileRegistry`，**BgiService 保留为门面**（理由见 §6） |
| 配置向后兼容 | ✅ 自动迁移 + `.bak.v12` 备份 + 失败不写盘 + 日志埋点 |
| XAML 零改动 | ✅ 5 个 .xaml 的 mtime 均停在 2026-10-27（Phase 1 开始于 10-29 06:39） |

---

## 2. 文件级变更清单

### 新增（产品代码）

| 文件 | 职责 |
| --- | --- |
| `Dragon Butler/ToolProfile.cs` | 数据模型（14 个字段，与任务书一一对应）+ `CompletionSignalKind` / `SignalConfidence` 枚举 + 三份内置档案默认值 |
| `Dragon Butler/ProfileRegistry.cs` | 档案注册表：内置值 → `settings.Tools` → `tools/*.json` 三级合并；v1.2→v1.3 迁移与备份；`BetterGiWebhookPort` |
| `Dragon Butler/CompletionSignal.cs` | `ToolRunContext` / `CompletionSignal` / `ICompletionSignalSource` |
| `Dragon Butler/CompletionSignalSources.cs` | `ProcessExitSignalSource`、`GameProcessExitSignalSource`、`WebhookSignalSource`、`LogPatternSignalSource`，以及共用的 `ProcessTools`（会话过滤） |
| `Dragon Butler/RunCoordinator.cs` | 状态机：Idle → Launching → Running → Resolving → 终态（Done / Failed / Timeout / Halted），逐次迁移写日志 |
| `Dragon Butler/ToolRunner.cs` | 通用执行：参数模板替换、按 profile 启停进程、装配协调器与信号源 |

### 修改（仅任务书点名的模块）

| 文件 | 变更 |
| --- | --- |
| `BgiService.cs` | `AppSettings` 新增 `Version`、`Tools`；进程枚举/结束/启动改走 `ToolRunner` + `ProfileRegistry.BetterGi`；参数改由 `ToolRunner.BuildArguments` 生成；`SaveSettings` 把界面路径/配置名/勾选组反写进 profile |
| `LogWatcher.cs` | 构造函数新增 `completionPattern`、`logDirOverride`；命中 profile 正则时额外抛 `Source="log-pattern"` 事件（内置的"配置组…执行结束"规则与返回顺序保持原样） |
| `SentinelService.cs` | `LogWatcher` 用 profile 的完成正则构造；Webhook 端口取 `ProfileRegistry.BetterGiWebhookPort`（缺省仍回落到旧设置） |
| `Dragon Butler.csproj` | `NoWarn` 显式豁免两条存量告警（CS8981 / WFAC010），未改任何编译产物配置 |

### 新增（测试资产，不随产品发布）

| 路径 | 说明 |
| --- | --- |
| `tests/StubTool/` | 替身工具：`--seconds N --exit C` / `--hang` |
| `tests/CoordinatorProbe/` | 探针：`done / failed / timeout / halted / haltedlog / logpattern / migrate / args` 八种模式 |

---

## 3. 状态机与实测

### 转移规则（与任务书一致）

```
Idle ──BeginLaunch──▶ Launching
Launching ──启动失败──▶ Failed
Launching ──启动成功──▶ Running
Running ──主信号(非 ProcessExit0)──▶ Resolving ──副信号──▶ Done
                                         └──宽限耗尽──▶ Halted(+通知)
Running ──ProcessExit0 且 exit=0──▶ Done
Running ──ProcessExit0 且 exit≠0──▶ Failed
Running ──超过 MaxDuration──▶ Timeout
```

铁律「宁等勿进」：Resolving 阶段只接受副信号，宽限耗尽一律 Halted 并通知，**不做任何猜测性推进**。

### 实测结果（本机，StubTool 驱动）

| 分支 | 命令 | 结果 |
| --- | --- | --- |
| 正常完成 | `CoordinatorProbe done <stub>` | `Idle→Launching→Running→Done`，2.6s，exit=0 |
| 退出码非 0 | `CoordinatorProbe failed <stub>` | `…→Running→Failed`，2.6s，exit=3 |
| 超时 | `CoordinatorProbe timeout <stub>`（`--hang`，MaxDuration=1 分钟） | `…→Running→Timeout`，60.3s |
| 悬置（手动主信号） | `CoordinatorProbe halted <stub>`（宽限 1 分钟） | `…→Running→Resolving→Halted` + 通知，62.4s |
| 悬置（日志主信号） | `CoordinatorProbe haltedlog <stub>` | `…→Running→Resolving→Halted` + 通知，61.4s |
| 日志哨兵 | `CoordinatorProbe logpattern <stub>` | 命中 `log-pattern` 事件，同时保留原有 `GroupEnded` 事件 |

日志埋点样例（`全部日志/YYYY-MM-DD_运行.log`）：

```
[run:72cf6ced] stub Idle→Launching（开始启动 StubTool）
[run:72cf6ced] stub Launching→Running（进程已启动 pid=27336）
[run:72cf6ced] stub Running→Done（进程正常退出 exit=0）
```

---

## 4. 配置迁移验证

### 替身验证（`CoordinatorProbe migrate`）

输入一份 v1.2 形态的 `settings.json`（含 `BgiPath`、`DragonConfigName`、`SelectedGroups`、定时、主题）：

```
MIGRATE version=13
MIGRATE bgiPath=D:\BetterGI\BetterGI.exe
MIGRATE configName=日常一条龙
MIGRATE groups=日常|挖矿
MIGRATE schedule=True/8
MIGRATE backup=True
MIGRATE toolsJson=True
MIGRATE profiles=bettergi,march7th,ok-ww
```

### 真机验证（把 v1.2 配置放进干净目录后启动产品）

```
07:06:02 事件：启动：Dragon Butler v1.2.0
07:06:02 事件：已迁移配置 v1.2→v1.3
07:06:02 事件：[profiles] bettergi,march7th,ok-ww
```

迁移后 `settings.json`：`Version=13`、`Tools=[bettergi]`、`ExecutablePath` 保留原值；`config/settings.json.bak.v12` 与 `tools/bettergi.json` 均已生成。

失败路径：迁移异常时**不写入**任何新结构（`SaveSettings` 在异常前不会执行），原文件保持可读，并抛 `ProfileRegistry.MigrationFailed` 供界面提示"配置迁移失败，请联系开发者"。

---

## 5. 验收清单（Phase 1）

| # | 条目 | 结果 | 如何验证 |
| --- | --- | --- | --- |
| 1 | Build 0 error 0 warning | ✅ | `dotnet build "Dragon Butler/Dragon Butler.csproj" -c Release` → 0 错误 0 警告 |
| 2 | StubTool 验证状态机全部转移 | ✅ | §3 五条命令；Done/Failed/Timeout/Halted 均有 `TRANS` 日志 |
| 3 | StubTool 改名 BetterGI.exe 验证哨兵崩溃重启走 profile | ⏳ **待人工** | 需真机 UI：把 `StubTool.exe` 复制成 `BetterGI.exe`，在界面选中该路径启动 → 杀掉进程 → 观察 60 秒冷却后是否按 profile 参数重启（`startOneDragon`）。本机自动化会触发 UAC，未纳入自动验证 |
| 4 | 旧配置迁移：路径/定时/组列表不丢，原文件有备份 | ✅ | §4 两组输出（探针 + 真机） |
| 5 | LogPattern 参数化后与旧版一致 | ✅ | `CoordinatorProbe logpattern` 同时得到 `log-pattern` 与 `GroupEnded` 两个事件，证明内置规则未被破坏 |
| 6 | XAML 零改动 | ✅ | `stat` 显示 5 个 `.xaml` 均停在 2026-10-27，Phase 1 期间（10-29 06:39 起）无写入 |
| 7 | 真机 BGI 一条龙全流程 | ⏳ **待人工** | 附录 A |
| 8 | 三份 profile 字段与任务书一致 | ✅ | `CoordinatorProbe args`：进程名 / 游戏名 / 主信号 / 超时 / 宽限 / 参数模板逐项打印 |

---

## 6. 关键决策与偏差说明（需你知悉）

1. **`BgiService` 保留为门面，而非物理删除。**
   任务书写"BgiService 拆为 ToolRunner + ProfileRegistry"，但同一条纪律要求"不重构未点名的文件、XAML 零改动"。`MainWindow.xaml.cs`、`App.xaml.cs`、`SentinelService.cs` 中有 20 余处 `BgiService.*` 调用，若删类必须改这些未点名文件。
   实际做法：BGI 专属知识（进程名、参数、端口、完成正则）全部移入 profile，执行与注册表拆成新类，`BgiService` 只做转发（`GetSessionBgi` / `KillAll` / `StartCore` 均为一行委托）。**净效果等价，且 UI 零改动。**

2. **`RunCoordinator` 未接入生产主路径（BGI 运行时仍由哨兵判定完成）。**
   任务书假设"原 BgiService 有完成判定路径"可被替代；实际代码里 BGI 的完成判定一直在 `SentinelService`（Webhook + 日志 + 截图三路），`BgiService` 只负责启动。
   强制把协调器塞进 BGI 主链路会引入第二个 Webhook 监听（端口冲突）或重复完成判定，直接违反"用户可见行为零变化"。
   因此 Phase 1 只交付**可用的、被测试驱动的**协调器，BGI 现网路径保持原样；Phase 2 的排队引擎直接用 `ToolRunner.CreateCoordinator()`。

3. **两条告警被显式豁免，而非修复。**
   `CS8981`（`using wf =` 小写别名）与 `WFAC010`（WPF 程序必须保留的 manifest DPI 段）都是存量问题，修掉前者要改未点名的 `App.xaml.cs` 别名，修掉后者会破坏 DPI 行为。已在 csproj 用 `NoWarn` 并加注释说明。

4. **`CustomData` 用 `Dictionary<string,string>`。**
   任务书写 `Dictionary`，未规定值类型；BGI 需要存端口（数字）与组列表（数组），统一转成字符串最稳，避免 JSON 反序列化多态带来的兼容风险。

---

## 7. 待人工确认项

1. **哨兵重启**（清单 #3）：真机按附录 A-2 操作，确认重启命令用的是 profile 参数（日志会出现 `BGI：启动一条龙（来源：哨兵重启）参数：startOneDragon`）。
2. **真机 BGI 全流程**（清单 #7）：启动 → 跑完 → 组归档/通知是否与 v1.2 一致。
3. **March7th / ok-ww 的真实进程名与游戏进程名**：
   - `March7 Launcher`（含空格）、`StarRail`
   - `ok-ww`、`Client-Win64-Shipping`
   这三项来自任务书候选值，本机无对应工具，**未做真机核对**；若实际不同，改 `tools/*.json` 即可，无需改代码。
4. **BGI 的 `GameProcessNames`**：保留了旧代码的 `YuanShen` / `GenshinImpact` 两个候选，未在真机确认新版本是否改名。

---

## 8. 如何复跑测试

```bash
# 1) 构建
dotnet build "Dragon Butler/Dragon Butler.csproj" -c Release
dotnet build "tests/StubTool/StubTool.csproj" -c Release
dotnet build "tests/CoordinatorProbe/CoordinatorProbe.csproj" -c Release

# 2) 四条状态机分支（timeout / halted 各约 1 分钟）
P="tests/CoordinatorProbe/bin/Release/net8.0-windows/CoordinatorProbe.exe"
S="$(cygpath -w "$(realpath tests/StubTool/bin/Release/net8.0/StubTool.exe)")"
$P done "$S"; $P failed "$S"; $P timeout "$S"; $P haltedlog "$S"

# 3) 迁移与档案
$P migrate "$S"
$P args "$S"
$P logpattern "$S"
```

> 注意：探针里的 stub 路径必须是 Windows 形式（`cygpath -w`），否则 `Path.GetDirectoryName` 会把 `/c/...` 当成相对路径。

---

## 9. 附：Phase 2 接入点（本阶段已预留）

- `ToolRunner.CreateCoordinator(profile)`：直接拿到装配好主/副信号源的协调器。
- `ProfileRegistry.All` / `Get(id)`：排队引擎按 profile 遍历即可。
- `RunCoordinator.Transition` 事件：可用于队列 UI 的状态回显。
- `ToolProfile.RetryCount` 字段已就位，执行路径暂未启用（任务书要求"本阶段只建数据"）。

---

**Phase 1 交付完毕，按门禁要求停在此处。等待你回复「Phase 1 验收通过」后，再开始 Phase 2。**
