# M11 人工复核清单（v1.47）

Kimi 未跑（这台机器原本没有 Kimi CLI，已装好 `~/.kimi-code/bin/kimi-code.exe` 但未登录，你选了「自己看」）。
下面每一行都给了**判据**和**怎么看**，照着点即可。带 🔴 的是最容易看走眼的地方。

## 一 逐任务书核对（§8 第 ① 项）

### M11-3 设置页「工具配置」

| 判据 | 怎么核 | 已自动核过 |
|---|---|---|
| 一级入口从 5 张变 6 张，顺序 守护/定时/通知/接口/**工具配置**/界面与启动 | 设置页顶部的卡片顺序，`SettingsEntries` 表位于 `Dragon Butler/MainWindow.xaml.cs` | ✅ `Api@188425 < Tools@188535 < Ui@188660` |
| 二级页容器顺序同样插在 Api 与 Ui 之间 | XAML 里 `SecApi` / `SecTools` / `SecUi` 三块 | ✅ `SecApi@177553 < SecTools@190164 < SecUi@193589` |
| 点一行弹出的是**原来那个**工具配置窗，内容一字未改 | 设置 → 工具配置 → 点任意一行 | 代码侧 `OpenToolLaunch` 有 7 处引用、`RefreshToolSettingsList` 5 处；弹窗本体 `ToolLaunchWindow` 本版未改 |
| 刷新有去抖，不会把正按着的手指弄丢 | `_toolSettingsSignature` 变化才重建（4 处引用） | ✅ |
| 未配好的工具必须一眼看得出，且不能和配好的同样淡 | 未配那行有橙色「还没找到主程序…」，右侧「未配置」胶囊 | ✅ 探针 `半配/文案用警示色` + `半配/配齐了才用三级灰`（判据切到 `BuildToolSettingsRow` 方法体） |

🔴 需要你亲眼确认的：打开设置 → 工具配置 → 点一行，看弹出的是不是熟悉的那个配置窗（有「主程序路径」「完成方式」那些），而不是空窗。

### M11-4 主页撤掉「每个工具怎么跑」

| 判据 | 结果 |
|---|---|
| `ToolSetupBlock` / `ToolSetupDivider` / `TbToolsSummary` / `ToolButtonHost` / `BuildToolButton` / `RenderToolButtons` 全部 **0 引用** | ✅ cs 与 xaml 两侧都是 0 |
| `RefreshToolsSummary` 只剩 1 处（一行说明性注释，不是代码） | ✅ |
| `ToolCardsHost` 那个 75% 不可见的长列表**保留**（任务书「不重构无关代码」） | ✅ 仍在，`Visibility="Collapsed"` |
| 首次运行不再为这块单独收起 | ✅ 探针 `首运行/提示行跟着收起` 只剩提示行 |

🔴 需要你亲眼确认的：主页那张卡从上到下是 —— 标题行 → 「今晚要跑：…」→ 提示行 → 工具行 → 虚线添加行 → 「跑完后 / 收拾一下」→ 「一键开跑」。中间不该有任何「每个工具怎么跑」的痕迹。

### M11-1 插件页头部

| 判据 | 结果 |
|---|---|
| 头部四件：标题 15 号 + 副标题「安装、启用和配置插件」+ 刷新 + ＋ 添加插件 ▾ | ✅ 截图 `plugins.png` 可见 |
| ⓘ 弹窗包含原两卡的**全部**说明文字 | ✅ 三段原文都在代码里（「把一个插件文件夹放进插件目录…」「能做：看当前跑没跑…」「不能做：改界面、停队列、动设置…」） |
| 原两张整卡从 XAML 删除 | ✅ `插件能做什么、不能做什么` / `装坏了也不会让主程序起不来` / `插件出问题时最坏的结果` 在 xaml 里全部为 False |
| 「打开插件目录」从独立按钮改成下拉项 | ✅ `AddPluginMenu` 在 xaml 里 1 处、`Header="打开插件目录"` 在它内部 |

🔴 需要你亲眼确认的：点 ⓘ，通读弹窗里的三段，对照 `git show c00397a^:"Dragon Butler/MainWindow.xaml"` 里那两张卡的原文，看有没有少句子。

### M11-2 插件页列表三分组

| 判据 | 结果 |
|---|---|
| 分两段：已加载插件 / 加载失败 | ✅ `BuildPluginGroupHeader("已加载插件")` 与 `("加载失败")` |
| 失败段**只在真有失败项时**出现 | ✅ `if (badList.Count > 0)` 包住 |
| 「内置插件配置」不建组 | ✅ 代码里没有这一段的构造；理由：`PluginContract` 只有外部接口、`PluginLoader.Scan()` 只扫目录 |
| 行样式：图标 40×40 圆角 8、名称 15 号、说明 13 号、行高 ≥64 | ✅ 探针 `插件/行样式达标` 五项逐一断言 |
| 空态灵魂句**字节级原样** | ✅ SHA256 与 `c00397a^` 版一致（`B619A7A0…`，93 字节） |
| 没有假的「启用/停用」开关 | ✅ `BuildPluginRow` 方法体内 `ToggleButton` / `SwitchStyle` 均为 False |

🔴 需要你亲眼确认的：三张图对照 —— `plugins.png`（两段都在）、`plugins_ok.png`（只有一段，没有空的「加载失败」标题）、`plugins_empty.png`（只有灵魂句那一行，居中）。

### M11-5 主题切换截图淡出

| 判据 | 结果 |
|---|---|
| 五步齐：截图 → 换字典 → 覆盖 → 250ms 淡出 → 摘层 | ✅ `bmp.Render(window)` / `Freeze()` / `IsHitTestVisible = false` / `TimeSpan.FromMilliseconds(DurationMs)` / `host.Children.Remove(layer)` / `image.Source = null` 全在 |
| 250ms + CubicEase EaseOut | ✅ `DurationMs = 250`、`EasingMode.EaseOut` |
| 连点不叠第二层 | ✅ `if (_running || !Enabled …) { swap(); return; }` |
| 亮度没变不动画 | ✅ `wasLight == Theme.LastSchemeLight` 时提前 return |
| 覆盖层不吃点击 | ✅ `IsHitTestVisible = false` |
| 探针模式禁用 | ✅ `RenderProbe.cs` 的 `RunUi` 首行 `ThemeFade.Enabled = false;` |
| 两条切换路径都走它 | ✅ `ThemeFade.Run(` 在 MainWindow.xaml.cs 里 2 处（`ApplyTheme` @625 行、`ToggleTheme` @9522 行） |
| 圆角与外框一致（否则圆角处冒直角） | ✅ `CornerRadius = 16` |
| 没引入跨线程 UI 直写 | ✅ diff 里无新增 `Dispatcher` / `Invoke` |

🔴 需要你亲手点：暗→亮、亮→暗、暗→暗（换配色）三种。前两种应平滑无闪、无叠影，第三种应直接切换（无动画）。连点两下切换，不该出现叠在一起的两层。

## 二 diff 是否引入新问题（§8 第 ② 项）

| 检查 | 结果 |
|---|---|
| 接线遗漏 | ✅ `RefreshToolSettingsList` 4 个调用点（三处刷新 + 进二级页时显式调度） |
| 异常吞噬 | ⚠️ 新增 4 处 `try { RefreshToolSettingsList(); } catch { }` —— 与文件里既有的 `try { RenderToolButtons(); } catch { }` 同风格（界面刷新失败不该拖垮队列），但确实不记日志。见第四节。 |
| UI 线程违规 | ✅ 无新增跨线程直写 |
| 新的死字符串 / 魔法数字 | ⚠️ 新增代码里有 `MinHeight = 64`、`Width/Height = 40`、`MaxWidth = 320`、`FontSize 10.5/12/12.5/13/15/16`、`LineHeight = 21` 等裸数字 —— 这些是布局规格值（任务书 §3.1 直接给的），项目里既有代码也是这个风格（`SetRow` 等都是内联数字），不算新债。 |
| 大文件完整性 | ✅ `MainWindow.xaml` `XmlDocument.Load` 通过；`MainWindow.xaml.cs` 编译 0/0；文件大小闸门 OK |
| 灵魂句 | ✅ 未改动 |

## 三 随机抽 3 条生效证明核对（§8 第 ③ 项）

我抽了这三条，你可以复核：

1. **`插件/空态那句一字没改`** —— 核法：`git show c00397a^:"Dragon Butler/MainWindow.xaml.cs"` 里也有这句吗？答案是**没有**（原来在 `MainWindow.xaml.cs:8860` 附近的旧 `RefreshPlugins` 里，而旧 `RefreshPlugins` 在本版被整段替换了）。所以「没改」的准确含义是**新文件里那一串字节与旧文件里那一串字节相同**，SHA256 都是 `B619A7A02DE9133705B14A846B551A749AC06475D997C4239F0F743C2C4E6AE9`，93 字节。
2. **`主题/两条切换路径都走它`** —— 核法：`Select-String 'ThemeFade.Run\(' "Dragon Butler/MainWindow.xaml.cs"`，应正好 2 行，分别在 625 与 9522 行。
3. **`按键/设置页有一列工具`** —— 核法：`Select-String 'x:Name="SecToolsHost"' "Dragon Butler/MainWindow.xaml"`，应 1 行（第 2598 行）。

## 四 我建议你重点盯的三件事

1. **4 处空 catch**（`try { RefreshToolSettingsList(); } catch { }`）。界面刷新失败静默，与既有风格一致，但如果工具列表因为某个 `ToolIconService` 异常一直刷不出来，现场只有「列表是空的」这一个症状、日志里什么都没有。**要不要给它加一行日志**，你定。
2. **`MaxWidth = 320`**：插件名超过这个宽度会被截断加省略号。中文名一般在 10 字以内，但英文长名（比如 `steam-achievement-tracker`）会被切。是否改宽由你定。
3. **M11-4 的 34px**：主页收起态仍比视口高 34px，全在折叠线以下（底部动作区 + 添加行）。按 v1.45 定的「三块 AboveFold」判据不算违规，但**整页确实还能滚一点**。要做到严格零滚动，得再压底部动作区间距，那会动到「一键开跑」和「跑完后」两行 —— 属于设计取舍，我没动。

## 五 复跑命令（想自己再验一遍）

```powershell
dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release -v q
dotnet build "tests\CoordinatorProbe\CoordinatorProbe.csproj" -c Release
$exe = "tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.exe"
$stub = "tests\StubTool\bin\Release\net8.0\StubTool.exe"

& $exe toolgrid $stub      # 含 M11 全部新断言，末尾应有 CANARY groups=84/84 ci=22 missing=0
& $exe ui rows             # 零滚动硬闸门，末尾 fold summary=True start=True add=True，退出码 0
& $exe ui plugins          # 出 plugins.png（两段）
& $exe ui plugins_ok       # 出 plugins_ok.png（只有已加载）
& $exe ui plugins_empty    # 出 plugins_empty.png（灵魂句）
& $exe ui panes            # 出 pane_tools.png 等六张
```

截图落在 `%TEMP%\dragon-ui\`。
