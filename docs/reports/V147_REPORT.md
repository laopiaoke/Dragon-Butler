# V147 交付报告

版本 **v1.47.0**，对应 V145 合并任务书的 M11 批（七项）。M12 批已随 v1.45 交付。

## 〇 一句话结论

主页任务清单卡只剩一件事（今晚跑什么），插件页从「先读两段话」变成「先看见能装什么」，
工具配置整块搬进设置页第六张卡；主题切换补了 250ms 截图淡出。
七项全部落地，26 组探针 + 零滚动闸门 + 文件大小闸门全绿，源码断言与灵魂句做了字节级核对。

## 一 M11-3 · 设置页「工具配置」入口（先立）

落点：`Dragon Butler/MainWindow.xaml.cs` 的 `SettingsPane` 枚举、`SettingsEntries` 表、
`ShowSettingsPane` 路由、`PaneOfSettingsTarget`；`Dragon Butler/MainWindow.xaml` 的二级容器。

- 枚举加 `Tools`（`Api` 与 `Ui` 之间）；`SettingsEntries` 插一格格
  `(SettingsPane.Tools, "工具配置", "主程序与完成方式", "M4,6 L20,6 L20,17 L4,17 Z M4,10 L20,10 M7,14 L11,14 M15,14 L17,14")`。
- 新增 `SecTools` 容器（与 `SecGuard`/`SecApi`/`SecUi` 同构：卡 + 标题行 + 分隔线 + `SetCardBody` 滚动区），
  内含提示行 `TbToolSetupHint` 与宿主 `SecToolsHost`。
- 新增 `RefreshToolSettingsList()` 与 `BuildToolSettingsRow(string toolId, bool last)`：
  28×28 圆角 8 图标、名称 12.5 号、说明 10.5 号（已配显示 `主程序：<路径>`，未配用 `WarningBrush` 显示
  「还没找到主程序。点这一行填上，不然「一键开跑」会跳过它。」）、右侧「已就绪 / 未配置」胶囊 + 箭头；
  整行 `MouseLeftButtonUp → OpenToolLaunch(toolId)`，即**原来那个工具配置窗，内容一字未改**。
- 去抖签名 `_toolSettingsSignature`（含主题模式与每项「配没配」），签名没变就一个控件都不重建。
- **接线确认（防功能无效）**：`RefreshToolSettingsList()` 有 4 个调用点 —— 三处刷新（队列重画、工具汇总刷新、
  首屏主卡刷新）+ 一处 `ShowSettingsPane` 进 `Tools` 时的显式调度。

**生效证明**：`pane_tools.png`（1120×648，`ui panes` 产出）目视有：返回「‹ 工具配置」、卡标题「工具配置」
+ 右侧「点一行改一个工具的配置」、说明行、两行工具（各带首字图标、「还没找到主程序…」橙色说明、
右侧「未配置」胶囊 + 箭头）；探针断言 `设置页/六个分区一次只显示一个`、`设置页/六个分区竖排铺开`、
`设置页/六张卡都还在` 全 PASS。

## 二 M11-4 · 主页撤掉「每个工具怎么跑」（后破）

- XAML 删掉：标题 Grid（`TextBlock Text="每个工具怎么跑"` + `TbToolsSummary`）、说明 TextBlock、
  `<WrapPanel x:Name="ToolButtonHost"/>`、`ToolSetupDivider`，以及原本闭合 `ToolSetupBlock` 的那一层 `</StackPanel>`。
- **保留** `ToolCardsHost` 长列表（17,165 字符、33 个 `x:Name`、9 个 Click 处理器）：它从 v1.32 起就是
  `Visibility="Collapsed"`，代码侧还有 61 处引用，按任务书「不重构无关代码」不动。
- 代码侧删掉：`RenderToolButtons()`、`BuildToolButton(string)`、`RefreshToolsSummary()`、字段 `_toolButtonSignature`
  与三处调用点；`RefreshHomeHero()` 里为首次运行收起那两行一并删掉。
- **引用清零自检**：`ToolSetupBlock` 0 / `ToolSetupDivider` 0 / `TbToolsSummary` 0 / `ToolButtonHost` 0 /
  `BuildToolButton` 0 / `RenderToolButtons` 0 / `RefreshToolsSummary` 0（只剩说明性注释）；
  `每个工具怎么跑` 只剩 3 处注释。

**生效证明**：`toolrows.png`（1120×720）目视：卡标题 → 「今晚要跑：日常 + 活动 · 2 项」→ 提示行 →
两行工具 → 虚线「＋ 添加工具」→「跑完后」+「收拾一下」同行 → 「一键开跑」；「每个工具怎么跑」整块不见。
探针 `按键/主页那排按键已撤`、`按键/设置页有一列工具`、`首运行/提示行跟着收起`、`半配/文案用警示色`、
`半配/配齐了才用三级灰`（判据切到 `BuildToolSettingsRow` 方法体）全 PASS。

## 三 M11-1 · 插件页头部重构

- 头部：插件图标 + 「插件」15 号 SemiBold + 右侧「刷新」「＋ 添加插件 ▾」（下拉里是「打开插件目录」）。
- 副标题：「安装、启用和配置插件」13 号 + `ⓘ`；点 `ⓘ` 弹出 `AppDialog.Show`，
  detail 是三段原文（怎么装 / 能做 / 不能做），**一字未删、一处未改**。
- 原两张整卡（「插件」长说明卡、「插件能做什么、不能做什么」卡）从 XAML 删除。

**生效证明**：探针 `插件/头部有刷新和添加`、`插件/说明收进 ⓘ`、`插件/打开目录在添加下拉里`、
`插件页/说清能做什么不能做什么`、`插件页/装坏了也不拖垮主程序`（后两条判据改到代码侧弹窗文案）全 PASS。

## 四 M11-2 · 插件页列表三分组

- 「已加载插件」段：逐条 `BuildPluginRow(f, false)`。
- 「加载失败」段：**只在真有失败项时出现**（注释：「一段永远空着的分组标题，比不分组更吵」）。
- **「内置插件配置」这一段不建**（§9 #1）：`Dragon Butler/PluginContract.cs` 只定义外部插件接口
  （`IPlugin` / `IPluginHost` / `PluginStatus` / `PluginNotifyLevel`），`PluginLoader.Scan()` 只扫 `插件\` 目录，
  没有内置插件注册表，也没有任何可配置的内置项。硬造一段就是空段，故按任务书「没有 → 不建组」处理。
- 行样式：图标 40×40 圆角 8、名称 15 号 SemiBold、说明 13 号辅助色、`MinHeight = 64`。
- 没有「启用/停用」开关：加载器扫到就加载、坏了就不加载，摆开关等于骗用户按。

**顺手修掉一个真 bug**：`f.Meta?.Name ?? f.DirName` 在 `Name` 是空串时不接手（`??` 只挡 null），
坏插件那行标题会显示为空；改成 `string.IsNullOrWhiteSpace(f.Meta?.Name) ? f.DirName : f.Meta!.Name!`。

**空态灵魂句字节校验**：新代码里那句与 `git show HEAD:"Dragon Butler/MainWindow.xaml.cs"` 的同名切片
逐一比对，两边 SHA256 同为 `B619A7A02DE9133705B14A846B551A749AC06475D997C4239F0F743C2C4E6AE9`，
长 93 字节，`identical=True`。

**生效证明**：三张截图 —— `plugins.png`（已加载 1 + 加载失败 1，两段标题都在）、
`plugins_ok.png`（只有「已加载插件」一段，无「加载失败」空标题）、`plugins_empty.png`（只有灵魂句一行）。
探针 `插件/列表分两段`、`插件/没有内置插件配置那一段`、`插件/行样式达标`、`插件/空态那句一字没改` 全 PASS。

## 五 M11-5 · 主题切换截图淡出（D24）

新文件 `Dragon Butler/ThemeFade.cs`（152 行，CRLF）：截旧画面 → 换字典 → 截图作 `Image` 覆盖全窗 → 250ms
`CubicEase EaseOut` 退掉 → `Completed` 里摘层并释放 `Source`。

- **连点防护**：`_running` 为真时只 `swap()`、不叠第二层。
- **亮度没变不做动画**：`wasLight == Theme.LastSchemeLight` 时直接 return。
- **覆盖层 `IsHitTestVisible = false`**：动画期间点击落到下面真正的控件上。
- **圆角 16px 裁切**：与窗口外框一致，否则圆角处会冒出直角。
- **探针模式禁用**：`RenderProbe.RunUi` 首行 `ThemeFade.Enabled = false;` —— 无头环境动画时钟不推进，
  250ms 的 `Completed` 可能永不回来，那层截图就会一直盖着窗口，出的图全是旧的。
- 两条切换路径（`ApplyTheme` 与 `ToggleTheme`）都走 `ThemeFade.Run(this, () => …)`。

**生效证明**：探针 8 条 —— `主题/有截图那一步`、`主题/截图作覆盖层且不吃点击`、`主题/250ms 的 EaseOut`、
`主题/退完就摘掉`、`主题/连点不叠第二层`、`主题/亮度没变就不动`、`主题/探针模式关掉`、
`主题/两条切换路径都走它`（`CountOf(mainCs, "ThemeFade.Run(this, () =>") == 2`）全 PASS。

## 六 M11-6 · 探针同步

改动 10 条旧断言 + 新增 16 条（插件 8、主题 8），并把 `ToolGridProbe.Ui.cs` 顶到 20 KB 后的
`Step19`（v1.32 工具按键与图标格）拆成 `ToolGridProbe.Ui2.cs`。

- 旧断言改写方式：**判据搬到新落点**，不是删掉。例：`合并/工具设置搬进启动页` →
  `合并/工具配置卡在设置页里`（`PageSettings` 出现在 `SecTools` 之前）；
  `半配/文案用警示色` 的切片从 `RefreshToolsSummary()` 改到 `BuildToolSettingsRow()`。
- `Doc/size` 闸门：`check-file-size.ps1 -All` → `文件大小：OK（上限 20 KB，白名单 14 个存量文件）`、exit=0。
- 全量：**26 组探针全绿**（`ran=26 bad=`）；`CANARY groups=84/84 ci=22 missing=0`；
  `ui rows` → `ZERO-SCROLL fold summary=True start=True add=True`、`rowsExitCode=0`；`ui all` → `allExit=0`。

## 七 M11-7 · 文档同步

- `docs/PATTERNS.md`：3,465 → 5,142 字符，新增四条（低频调试入口不进主页 / 说明性信息收成 ⓘ /
  要断言的逻辑抽 public 纯函数 / 大文件改完先核对完整性）。
- `docs/VOICE.md`：2,632 → 2,985 字符，追加「标杆句（v1.45 新增，非灵魂句）：出错之后先给出口」，
  含一条标准句与三条反例。`docs/SOUL.md` **未动**。
- 「点一下就能改它的程序位置…」在 `docs/` 已 0 命中（`程序位置`/`点一下`/`完成方式` 各 0）；
  只剩设置页新卡自己的提示 `TbToolSetupHint` 用同一句话，属正常。
- `docs/使用说明.txt`（UTF-16LE + BOM + CRLF，9,755 字符 / 262 CRLF）：设置入口从「五类」改「六类」并补
  「工具配置」一句话；插件页补列表两段与 ⓘ、刷新、添加下拉；任务清单卡那句「卡片右上角显示已配置数量」
  改成指向 设置 → 工具配置；插件目录的打开路径改到「＋ 添加插件 ▾ → 打开插件目录」。

## 八 验证总账

| 项 | 结果 |
|---|---|
| 主工程 `dotnet build -c Release` | 0 个警告 0 个错误 |
| 探针 `dotnet build` | 0 个警告 0 个错误 |
| 26 组功能断言 | `ran=26 bad=` 全绿 |
| 金丝雀 | `CANARY groups=84/84 ci=22 missing=0` |
| `ui rows` 零滚动硬闸门 | `fold summary=True start=True add=True` / `rowsExitCode=0` |
| `ui all` 全量出图 | `allExit=0` |
| 文件大小闸门 | `OK（上限 20 KB，白名单 14 个存量文件）` |
| 灵魂句字节 | SHA256 两侧一致（93 字节） |
| 主页收起态量测 | `viewport=573 extent=607 scrollable=34.0`，超出全在折叠线以下 |

## 九 施工中发现但没动手的

1. `ToolCardsHost` 那个 17 KB 的 Collapsed 长列表仍在 `MainWindow.xaml` 里，代码侧 61 处引用。
   它已经是死界面，但拆掉要动配置窗与分组卡，超出 M11 范围 —— 建议单独立项清理。
2. `MainWindow.xaml` 242 KB / 3,280 行，仍在文件大小白名单里。M11 又往里加了一块。
3. 插件没有图标机制（`metadata.json` 无图标字段），当前用名字首字占位。要做真图标得改插件契约。
4. 主题淡出只处理「亮度真变了」的情况；亮→亮换配色仍是硬切，观感上可以接受，但与淡出并存略显不一致。

## 十 需产品确认的

1. **主页收起态仍超 34px**：超出的全在折叠线以下（底部动作区 + 添加行），按 v1.45 定的「三块 AboveFold」
   判据不算违规。若要求「整页零滚动」，得再压底部动作区，那会动到一键开跑和跑完后两行的间距。
2. **插件「启用/停用」**：当前没有这个概念。若要做，得先在 `PluginContract` 加状态并在加载器里落实，
   否则开关是假的。
3. **主题淡出要不要给亮→亮也做**：现在是靠「亮度变了没」判断，做法保守。要做需要区分「只换配色」的场景。

## 十一 改动文件清单

改：`Dragon Butler/MainWindow.xaml`、`Dragon Butler/MainWindow.xaml.cs`、
`Dragon Butler/Dragon Butler.csproj`、`tests/CoordinatorProbe/ToolGridProbe.Ui.cs`、
`tests/CoordinatorProbe/ToolGridProbe.Ui2.cs`（新）、`tests/CoordinatorProbe/ToolGridProbe.Flow.cs`、
`tests/CoordinatorProbe/ToolGridProbe.Pages.cs`、`tests/CoordinatorProbe/RenderProbe.cs`、
`tests/CoordinatorProbe/RenderProbe.Ui.B.cs`、`tests/CoordinatorProbe/RenderProbe.Ui.D.cs`、
`docs/PATTERNS.md`、`docs/VOICE.md`、`docs/使用说明.txt`、`RELEASE_NOTES.md`、`README.md`、`README.en.md`。
新增：`Dragon Butler/ThemeFade.cs`。
