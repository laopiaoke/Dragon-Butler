# Phase 2 交付报告：多游戏排队 + 管家功能 + UI 改版（v1.3.0）

> 前置：热修 0（剪贴板）已完成并推送；Phase 1 两处偏差按裁决保留（门面已按 §8.4 物理删除）。
> 当前状态：Phase 2 自查清单全部跑过（下方逐条），**附录 B 人工验收未做**；tag / GitHub Release **未创建**，等你验收。

## 一、结论

- 队列引擎落地：`QueueService` 按勾选顺序执行，间隔、失败策略、汇总通知、跑完动作全部实现。
- 完成判定收敛：全库只剩 `WebhookListener` 内一处 `new HttpListener`；`SentinelService` 不再做任何完成判定（只保留存活/内存/截图/会话）。
- 门面退役：`BgiService` 已物理删除，`git grep -i "BgiService"` 源码零结果。
- UI 改为三层：队列卡（Hero）+ 三张工具卡（默认折叠）+ 高级设置（默认折叠）；旧哨兵卡删除，改为底部盾牌（绿/灰/黄，悬停出详情）。
- 版本 1.3.0；Build **0 error 0 warning**。

## 二、文件级变更清单

**新增（8）**

| 文件 | 作用 |
|---|---|
| `Dragon Butler/AppState.cs` | 设置读写 / 日志广播 / 运行状态轮询（承接原 `BgiService` 的数据职责）+ v1.3 队列设置字段 |
| `Dragon Butler/QueueService.cs` | 队列引擎、前置检查、防打扰、通知模板、跑完动作（含 DRYRUN） |
| `Dragon Butler/WebhookHub.cs` | 全局唯一 Webhook 监听 + 事件扇出；`GroupArchiveWatcher` 组归档接驳 |
| `Dragon Butler/ToolDiscovery.cs` | 四级自动寻找：运行进程 → 开始菜单 .lnk → CandidatePaths → 注册表 Uninstall |
| `Dragon Butler/FirstRunWindow.xaml(.cs)` | 首启向导三步（选工具 / 找路径 / 完成），可跳过 |
| `tests/StubClipboardHolder/` | 剪贴板占用替身（热修 0 遗留，本阶段回归用） |
| `PHASE2_REPORT.md` / `RELEASE_NOTES.md` | 本报告与发布说明草稿 |

**删除（1）**：`Dragon Butler/BgiService.cs`（340 行，§8.4 授权的门面退役）。

**修改（主要）**

| 文件 | 改动 |
|---|---|
| `MainWindow.xaml` / `.cs` | 三层布局重写；队列卡代码生成行（勾选/状态/拖拽排序/间隔节点）；工具卡（路径/浏览/自动寻找+确认/测试启动；BGI 保留配置名与配置组）；高级设置；底部盾牌；首启向导入口 |
| `SentinelService.cs` | 卸下 webhook/日志完成判定与完成通知；只留进程/内存/截图/会话；崩溃重启改读 profile，队列运行时不重复启动 |
| `CompletionSignalSources.cs` | `WebhookSignalSource` 改订阅 `WebhookHub`（不再自建监听器）；`LogPatternSignalSource` 支持多候选锚点 |
| `ToolProfile.cs` | BGI 主信号按勘误 F 改为 `LogPattern`（锚点"一条龙和配置组任务结束"），副信号 `GameProcessExit;Webhook`；三工具加 `candidatePaths`/`extraLogPatterns`（默认空，待人工核对） |
| `App.xaml.cs` | 启动 WebhookHub + GroupArchiveWatcher；托盘「一键开跑」走队列；退出时停总线 |
| `ProfileRegistry.cs` / `NotifyService.cs` / `LogWatcher.cs` / `Theme.cs` / `SoundService.cs` / `ScreenshotMonitor.cs` / `BgiDiscovery.cs` / `AppDialog.xaml.cs` / `CustomThemeWindow.xaml.cs` | 设置引用迁移到 `AppState`；截图监测的游戏进程读 profile |

## 三、验收清单（逐条）

| 验收项 | 结果 | 如何验证 / 证据 |
|---|---|---|
| 两工具队列全流程 | ✅ | `CoordinatorProbe queue` → `RESULT stub-a Done / stub-b Done`，8.0s（2s + 1s 间隔 + 3s） |
| 失败重试 1 次后跳过 | ✅ | `queuefail` → `stub-b Failed attempts=2`，随后 `stub-c Done`（Skip 生效） |
| 策略=中止 | ✅ | `queueabort` → 只有 `stub-a Failed`，第二项未出现在结果里 |
| 超时 + 无锚点 WARNING | ✅ | `timeoutanchor` → `Timeout（超时 1 分钟未完成）`；日志 `WARNING 完成锚点未命中，请检查 BGI 版本是否更新` |
| 悬置 → 队列停 | ✅ | `queuehalted` → `stub-log Halted`，`secondRan=False` |
| 锚点防误判 | ✅ | `anchor` → 裸"任务结束"命中 `0`；完整锚点命中 `1` |
| IdleGuard | ✅ | `idle` → 先推迟、改空闲后放行 `ok=True`（2.3s）；无输入直接跑由 `IdleFor>=阈值` 分支保证 |
| 残留清理（定时杀 / 手动跳过） | ✅ 定时路径 | `residual` → 预启 `--hang` 后定时触发：先杀再跑，`state=Done`；手动路径为 `PreCheck` 同一函数的 `else` 分支（代码审查） |
| 游戏冲突 | ✅ | `conflict` → `skipped=True`，原因"检测到游戏客户端正在运行…" |
| 度假模式 | ✅ | `vacation` → 定时触发 `ran=False`；手动不受限由 `StartAsync` 分支保证 |
| AfterRunAction DRYRUN | ✅ | 日志 `DRYRUN: 将执行关机`；有失败项时 `跑完动作：队列有失败项，跳过 Shutdown（仅通知）` |
| 剪贴板回归 | ✅ | `CoordinatorProbe clipboard` → `busy ok=False 962ms` / `free ok=True`（热修 0） |
| 自动寻找 | ✅ | `discover`：把替身命名为 `March7 Launcher.exe` 运行 → `found=… source=running` |
| 完成判定收敛 | ✅ | `grep "new HttpListener"` 仅 `WebhookListener.cs` 一处；`SentinelService` 无 webhook/log 完成判定 |
| 门面退役 | ✅ | `grep -rni BgiService "Dragon Butler"` → 0 结果 |
| 首启向导 / 空状态 / 盾牌 | ✅ 向导+盾牌；空状态代码就绪 | 向导截图（步骤 1，可跳过）；盾牌绿/灰/黄三态由 `SentinelEnabled` + `ShieldAlertAt` 驱动；队列无勾选时显示"第一步…"提示 |
| Build 0/0 | ✅ | 主工程与三个测试工程均 0 error 0 warning |

复跑命令（仓库根）：

```bash
S="%CD%\tests\StubTool\bin\Release\net8.0\StubTool.exe"
P="tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.exe"
%P% queue "%S%"      & rem Done / Done
%P% queuefail "%S%"  & rem Done / Failed(2 次) / Done
%P% queueabort "%S%" & rem 只跑第一项
%P% queuehalted "%S%"& rem Halted 且第二项不跑
%P% anchor "%S%"     & rem 裸"任务结束"=0，完整锚点=1
%P% timeoutanchor "%S%" & rem 1 分钟后 Timeout + WARNING
%P% residual "%S%"   & rem 定时路径先杀残留再跑
%P% conflict "%S%"   & rem 游戏进程在跑 → 跳过
%P% vacation         & rem 定时被抑制
%P% idle             & rem 推迟后放行
%P% afterrun         & rem 只有 DRYRUN 日志
%P% discover "%S%"   & rem 命中运行中的进程
```

## 四、需要你知道的偏差与取舍

1. **BGI 完成判定的实际风险（最重要）**：按勘误 F，BGI 主信号是日志锚点，副信号是 `GameProcessExit; Webhook`（**v1.3.1 已追加 `ProcessExit0` 解决**）。若某次 BGI 跑完既不关游戏、也没配 Webhook，状态机会进 `Halted` 并停下队列（宁等勿进）。真机跑一次即可确认；若频繁出现，建议在 `tools/bettergi.json` 的 `SecondarySignals` 追加 `ProcessExit0`（BGI 自身退出也是强证据），我可以改。
2. **通知不可点击**：任务书写的"点此立即开始/跳过本次""点此取消"在 Windows 气泡/Toast 上不可实现（现有 `NotifyService` 无动作按钮）。改为：等待空闲时队列卡出现「立即开始（跳过等待）」，关机倒计时出现「取消关机」；通知文案保留说明。
3. **"多用户自启"未复活**：该模块在 v1.2 清理中已整体删除（178 行 WMI 实现，零调用）。本阶段没有重新实现，避免把刚删掉的死代码加回来。需要的话单独提。
4. **哨兵崩溃重启**：队列运行中进程消失时，交由队列/协调器判定，哨兵只记日志不重复拉起（否则会双开）。非队列场景仍按原逻辑自动重启。
5. **候选路径为空**：三个工具的 `candidatePaths` 全部留空并标注"待人工核对"（按纪律不臆造官方安装位置）。自动寻找仍可用"运行进程 / 开始菜单 / 注册表"三级命中。
6. **DRYRUN 仍开着**：`AfterRunDryRun=true`（默认）。附录 B 里真实睡眠/关机各验证一次后，把 `settings.json` 的 `AfterRunDryRun` 改成 `false`（或我改默认值再发版）。
7. **提交粒度**：本轮拆成 feat(queue) / feat(ui) / test(queue) / docs(v1.3) 四个提交，未做巨型单提交。

## 五、待人工（附录 B）

1. 真机三月七 `main -e` 跑通一次；2. 真机 ok-ww `-t 1 -e` 跑通一次；3. 任务管理器核对 `GameProcessNames`；4. 双工具真机排队一次；5. BGI 经队列跑通且配置组数 >0（组归档照常累计）；6. BGI 完成通知在锚点行后数秒内出现；7. 关闭 DRYRUN 后真实睡眠/关机各一次；8. 核对 RELEASE_NOTES 与 zip 解压即用；9. 附录 C-1 已确认（用户两次点击，非缺陷）。
