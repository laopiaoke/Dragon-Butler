# v1.42 交付报告：任务清单卡整合改造（任务书 v1.42 / M8-1 ～ M8-8）

> 开工前状态：`main` HEAD `366470b`（v1.41 已发布），工作树干净。
> 完工后：主工程与探针工程均 `0 警告 0 错误`；22 组探针逐组 `fails=0 exit=0`。

---

## 〇、先废后立：本次废除的 V141 成果

任务书 §0 要求明确写出被废除的旧成果，这里逐条对上：

| V141 成果 | 本次处置 | 依据 |
| --- | --- | --- |
| **M3-3** 五个并列视图 chip 收成「当前视图名 + 下拉箭头」 | **整体废除**。下拉按钮 `BtnQueueView`、`QueueViewMenu` 与五个 `MenuItem` 全部删除 | D10 / M8-3 |
| **M3-4** 「跑完后」下拉列出全部可选项 | **保留**。`CbAfterRun` 五项原样，只是位置挪到工具行下方那行 `跑完后` / `收拾一下` | M8-5 |
| V141 的视图体系（计划 / 日历 / 今日 / 依赖 / 配置组五个窗口入口） | **入口全删**，`TodayWindow` 因为日志视图的「今日详情」还要用而保留 | D10 / M8-3 |
| V141 M3-2「托管配置」改名「任务清单」 | **保留并延续**。这张卡现在就是工具行列表本身 | D9 |
| V141 M3-5 工具卡状态摘要行 | **保留语义、换了形状**。原来在格子里第三行，现在是工具行第二行，仍显示全局间隔与全局跑完动作 | §2-④ |
| V141 的 6 格分页网格（`ToolGridHost` / `ToolPagerClip` / `PageDotsHost` / `ToolDragLayer`） | **不再渲染**。XAML 里保留为 `Visibility="Collapsed"` 的隐藏区，只为兜住后台老引用 | D12 |

---

## 一、改动文件清单

### 删除

| 文件 | 说明 |
| --- | --- |
| `Dragon Butler/TrayConfigWindow.xaml` | 「托管配置」独立窗口，166 行 |
| `Dragon Butler/TrayConfigWindow.xaml.cs` | 848 行 |

### 修改

| 文件 | 行数变化 | 要点 |
| --- | --- | --- |
| `Dragon Butler/MainWindow.xaml` | 3186 → 3138 行 | 标题行右侧换「已配 N 个」；提示行换 §4 那行；「配置」大按钮 → `ToolRowHost`；底部动作区按 §4 重排；新增 `TbQueueCount` / `TbTonightSummary`；主页底部留白 22 → 78 |
| `Dragon Butler/MainWindow.xaml.cs` | 8380 → 9073 行 | 新增工具行渲染全套（约 470 行）；`RenderQueue` 改走 `SyncToolRows`；删除「配置」按钮、五个视图入口、`RevealQueueFromOutside`；右键菜单扩到九项；新增 `RemoveFromList` / `MoveToolRow` / `TonightGroups` / `TonightMinutes` / `RefreshTonightSummary` / `RefreshQueueCount` |
| `Dragon Butler/App.xaml` | 3 → 72 行 | 应用级 `ContextMenu` / `MenuItem` 模板，去掉图标列（M8-6） |
| `docs/VOICE.md` | 123 → 137 行 | 新增「标杆句（v1.42 新增，非灵魂句）」一节 |
| `docs/PATTERNS.md` | 86 → 122 行 | 新增第 5 条「删除管理界面：先列能力迁移清单，再删」 |
| `docs/使用说明.txt` | 305 → 286 行 | 删掉四个视图的说明段与重复的日报段；改写任务清单卡、双击/右键、添加工具、配置组、FAQ |
| `README.md` | 306 行 | 工具卡措辞改工具行；两处 FAQ 换成 v1.42 的说法 |
| `tests/CoordinatorProbe/Program.cs` | — | 删 `TrayConfigWindow` 源码读取；重写 11d)/11j) 两段断言；`uirules` 与「五个视图入口」断言反向化 |
| `tests/CoordinatorProbe/RenderProbe.cs` | — | 删 `tray` 模式；新增 `rows` 模式（收起 / 展开 / 添加面板 / 移除确认框四张） |

---

## 二、新增文案清单

严格对齐 `docs/VOICE.md`。全部为本次新写，逐条列出：

### 任务清单卡头部与提示（M8-2 / M8-5）

| 文案 | 位置 |
| --- | --- |
| `已配 {N} 个` / `今晚没有工具` | 卡片标题行右侧 `TbQueueCount` |
| `拖 ≡ 换顺序 · 双击立即跑 · 右键更多` | 卡片唯一提示行 `TbGridHint` |

### 工具行（M8-2）

| 文案 | 位置 |
| --- | --- |
| `✓ 已就绪` / `未配置路径` / `今晚不跑` / `运行中` | 行内徽章 |
| `按住往上/往下拖，换今晚跑的顺序` | `≡` 手柄 ToolTip |

### 展开区（M8-4）

| 文案 | 位置 |
| --- | --- |
| `这个工具没有配置组。程序位置和完成方式在「编辑…」里改。` | 非 BetterGI 工具展开时 |
| `还没识别到配置组。确认 BetterGI 的位置后点「重新识别」。` | 扫不到组时 |
| `今晚 0 项 · 不勾＝沿用全局勾选（{N} 个）` | 一个组都没勾时 |
| `今晚 {N} 项` | 勾了组时 |
| `全选` / `全部取消` | 展开区底部 |

### 添加（M8-2 ⑤）

| 文案 | 位置 |
| --- | --- |
| `＋ 添加工具` | 列表末尾虚线行 |
| `从内置清单里挑一个，或者添加任意 exe` | 虚线行 ToolTip |
| `加一个工具。已经在清单里的会打勾——点它直接去改路径。` | 添加面板说明 |
| `其它 exe…` / `收起` | 添加面板底部 |

### 今晚摘要（M8-7）

| 文案 | 位置 |
| --- | --- |
| `今晚还没有要跑的工具。` | 摘要行空态 |
| `今晚要跑：{组A} + {组B} · {N} 项 · 约 {X} 分钟` | 摘要行正常态 |
| `今晚要跑：用配置名跑 · {N} 个工具` | 没有勾任何组时 |
| `今晚要跑：{组A} + {组B} ... 等 {N} 个组 · {N} 项` | 组数超过 2 个时 |

### 右键菜单（M8-2 / D13）

| 文案 | 位置 |
| --- | --- |
| `立即跑这一个` / `跳过这次` / `这次还是跑` / `今晚不跑它` / `今晚跑它` / `改间隔…` / `编辑…` / `查看日志` / `移除…` | 菜单九项 |

### 移除确认框（M8-8 标杆句）

> `把 {工具名} 从今晚清单移除？配置会保留，随时可以再加回来。`

按钮：`移除`（主）/ `算了`（次）。

### 运行日志（内部，非界面）

| 文案 | 位置 |
| --- | --- |
| `工具顺序：{toolId} 第 {a} → 第 {b}` | `MoveToolRow` |
| `移出清单：{toolId}` | `RemoveFromList` |
| `✓ 已从今晚清单移除：{name}` | `RemoveFromList` |
| `✓ 已加入清单：{name}` | `BuildAddPanel` |

---

## 三、自检表（任务书 §7 全局验收，逐条）

| # | 验收项 | 结果 | 依据 |
| --- | --- | --- | --- |
| ① | 「配置」大按钮、托管配置弹窗、五视图下拉全部不可见且全局无残留引用 | **通过** | `md5`/`Select-String` 全库搜索 `BtnOpenTrayConfig` / `TrayConfigWindow` / `BtnQueueView` / `QueueViewMenu` / `BtnWeeklyPlan` / `BtnCalendar` / `BtnDepends` / `BtnGroupSlice` 全 0 命中；探针断言 `入口/托管配置窗口没了`、`入口/视图下拉整个撤掉` 通过 |
| ② | 工具行六项能力全部可用 | **通过** | 探针 `工具行/*` 十四条断言全绿；截图三张（收起 / 展开 / 添加） |
| ③ | 右键六项功能生效，`跳过这次` 与 `今晚不跑它` 语义符合 D13 | **通过** | `跳过这次` → `QueueService.ToggleSkipOnce`（进程内 `_skipOnce`，`FinishQueue` 末尾 `ClearSkipOnce()`）；`今晚不跑它` → `ToggleQueueEnabled` 写 `QueueEntry.Enabled` 并持久化 |
| ④ | 配置组勾选写在原有数据上、重启保留、BGI 禁用组置灰 | **部分通过，已记入待人工确认** | 读写的是 `bettergi` 的 `QueueEntry.Groups`（与 `GroupsWindow` 同一份，也是 `BuildLaunchArguments` 优先级最靠前那层），无第二存储、`SaveSettings()` 落盘；**「BGI 侧已禁用的组置灰」不可实现**——见第四节 ① |
| ⑤ | 展开勾选 → 今晚摘要实时变化；无耗时数据不显示 `约 X 分钟` | **通过** | `Persist()` 里 `Renumber()` + `RefreshTonightSummary()`；`TonightMinutes` 里「任何一个组没样本就整体返回 null」 |
| ⑥ | 卡片结构与 §4 图一致、无重复说明文案 | **通过** | 顺序为 提示行 → 工具行 → 展开区 → 添加行 → 跑完后/收拾一下同行 → 一键开跑 → 今晚摘要；`TbGridHint` 只保留一行 |
| ⑦ | 所有菜单/下拉左侧无空白占位 | **通过** | `App.xaml` 应用级 `MenuItem` 模板不含 Icon 列；探针 `菜单/模板里不画图标列` 通过 |
| ⑧ | VOICE / PATTERNS / SOUL 三文档已同步 | **通过** | VOICE 增标杆句、PATTERNS 增第 5 条；SOUL 未改（六句灵魂句一字未动，逐条 `Contains` 复检全 True） |
| ⑨ | 编译 0 警告 0 错误、探针全量通过 | **通过** | 主工程与探针均 0/0；22 组逐组 `fails=0 exit=0` |
| ⑩ | 截图核验四项 | **通过** | `tasklist_collapsed_v142.png` / `tasklist_expanded_v142.png` / `tasklist_add_v142.png` / `removedialog_v142.png` |

### 逐条核对 M 项

| 项 | 状态 | 关键实现 |
| --- | --- | --- |
| M8-1 删托管配置弹窗 | 完成 | 两个文件删除；`BtnOpenTrayConfig_Click` / `RevealQueueFromOutside` 删除；`view.*` 命令表清理 |
| M8-2 能力迁移 + 工具行 | 完成 | 六项迁移逐条落地（见 PATTERNS 第 5 条那张表）；右键九项 |
| M8-3 删视图系统 UI | 完成 | 下拉与五入口删除；`view.calendar` / `view.depends` / `view.groups` 三条命令撤掉；四个窗口类与数据模型保留 |
| M8-4 配置组整合 | 完成（置灰除外） | `BuildGroupPanel`，读写 `QueueEntry.Groups` |
| M8-5 布局重排 | 完成 | §4 顺序；`TbPlanSummary` / `TbQueueHint` / `QueueEmptyHint` 三个控件删除 |
| M8-6 菜单空白 | 完成 | `App.xaml` 应用级模板 |
| M8-7 今晚摘要 | 完成 | `RefreshTonightSummary` + `TonightGroups` + `TonightMinutes` |
| M8-8 文档同步 | 完成 | VOICE / PATTERNS 已改；本报告第二节写明废除项 |

---

## 四、待人工确认清单

1. **M8-4「BGI 侧已禁用的组置灰」不可实现。**
   `Dragon Butler/BgiDiscovery.cs` 的 `Scan()` 走 `ScanJsonNames()`，它只读 `{BGI}\User\ScriptGroup\*.json` 的**文件名**（去扩展名即组名），从不解析 JSON 内容；全库也没有任何读取 ScriptGroup 字段的代码（`BgiConfigWriter` 只碰 `User\OneDragon`）。也就是说系统里根本不存在「某个组被禁用」这个信息。
   现在的实现是：扫到的组全部可勾。若确实要做置灰，需要先确定「禁用」在 BGI 配置文件里的确切字段与语义，再加一层解析——这属于新增能力，本次不做。

2. **配置组的数据归属：选了 `QueueEntry.Groups`，不是全局 `SelectedGroups`。**
   `Dragon Butler/QueueService.cs:960-978` 的 `BuildLaunchArguments` 优先级是「本项 `QueueEntry.Groups` > 配置组合 `Preset.Groups` > 全局 `Settings.SelectedGroups`」。展开区天然只对一个工具，写最靠前那层才对得上「我今晚就跑这几个组」的直觉；写全局会绕开优先级链、语义也变成「影响所有工具」。这与 V141 的「配置组」视图（`GroupsWindow`）是同一份存储，符合任务书「沿用原配置组视图的数据」。
   代价：展开区里勾的组**不会**同步到主界面那份全局勾选。若要双向同步，需要另立一条决策。

3. **§4 布局没覆盖「卡片比可视区高」的情况。**
   内容按 §4 排下来后，`QueueCard` 在默认 720 高的窗口里会略微超出，需要滚动才能看全「今晚摘要」。本次没改 §4 规定的顺序，只把 `PageHome` 的底部留白从 22 加到 78（避开悬浮标签栏 58 + 14）。若希望「整卡一屏放得下」，得压缩工具行高度或折叠某一段——这会动 §4，故未擅自做。

4. **`TodayWindow` 与四个视图窗口类还在仓库里。**
   任务书 D10 说「四个视图 UI 不可达」，本次只撤入口与路由，`WeeklyPlanWindow` / `CalendarWindow` / `DependsWindow` / `GroupsWindow` 四个类和它们的数据结构原样保留（§2-① 要求数据模型不动）。`TodayWindow` 仍被日志视图的「今日详情」使用，必须保留。
   若下一步要物理删除前四个窗口，请先确认「它们的配置写入路径是否已完全由别处承担」（尤其 `DependsWindow` 写的是 `QueueEntry.DependsOn`，目前没有任何 UI 能改依赖了）。

5. **`ToolGridHost` 等六个网格控件保留为隐藏区。**
   V141 的 6 格分页网格（`ToolPagerClip` / `ToolGridHost` / `ToolDragLayer` / `PageDotsHost` / `TbGridOverflow` / `QueueHost`）现在 `Visibility="Collapsed"` 且不再渲染内容。保留原因：`RenderToolGrid()`、`SyncToolGrid()`、`PageWidth()`、`PlaceToolPages()` 等约 30 处后台代码仍按名字引用它们，一次性拆掉风险高于收益。它们不占布局、不响应操作，对用户不可见。

6. **探针 `clipboard` 仍失败（与本版无关）。**
   `CLIP busy ok=True elapsed=2ms`，探针要求阻塞约 1 秒后失败；`Dragon Butler/ClipboardHelper.cs` 本次未改动（`git diff` 为空），属环境侧现象。V141 报告已记过同一条。

---

## 五、截图（`C:\Users\Administrator\AppData\Local\Temp\dragon-ui`）

| 文件 | 内容 |
| --- | --- |
| `toolrows.png` → `docs/proto/tasklist_collapsed_v142.png` | 工具行收起态：两行工具、徽章、摘要、虚线添加行、跑完后 / 收拾一下同行、一键开跑、今晚摘要 |
| `toolrows_open.png` → `docs/proto/tasklist_expanded_v142.png` | BetterGI 展开态：`2. 活动` / `1. 日常` 两个勾选组、全选 / 全部取消、右侧「今晚 2 项」 |
| `toolrows_add.png` → `docs/proto/tasklist_add_v142.png` | 添加面板：内联图标网格，已加过的打勾并高亮 |
| `removedialog.png` → `docs/proto/removedialog_v142.png` | 移除确认框，标杆句与双按钮 |

---

## 六、未做与建议

- **未发版**：版本号仍是 `1.41.0`，`发布/` 目录与 GitHub Release 均未动（用户本次只下发任务书，未要求发版）。若要发版，需要：`csproj` 三处版本号 → `RELEASE_NOTES.md` 加 v1.42 小节 → 跑 `./scripts/package.ps1 -Variant all` → 提交 → 打 tag → push。
- **`docs/使用说明.txt` 与 `README.md` 已按 v1.42 改过文字**，但里面的截图仍是 v1.41 的（`tasklist_*_v142.png` 已入库但还没被引用）。若要一并换图，需要在两份文档里插这四张。
- M8-6 只覆盖了「全部无图标」这一条路线。若将来某个菜单确实需要图标，得同时给**所有**菜单项补上，否则空白又会回来——这条已写进 `App.xaml` 的注释里。
