# V1.46.2 交付报告：托盘右键菜单复刻导航栏（任务书 #18）

## 〇 一句话结论

托盘右键菜单从 WinForms 的 `ContextMenuStrip` 换成项目自己的 WPF `ContextMenu`，
顶掉系统模板里那条从没用过的图标列——**左侧空白是那一列留下的宽度，不是 Padding 写错了**。
四项文案与顺序一字未改，主界面文件零改动。

## 一 第 0 步诊断（先查再改）

### 1.1 托盘实现属于哪一类

| 判定项 | 证据 | 结论 |
|---|---|---|
| `NotifyIcon` | `Dragon Butler/App.Tray.cs:27 private wf.NotifyIcon? _tray;` | 有 |
| 旧菜单控件 | 改前 `App.Tray.cs:39 var menu = new wf.ContextMenuStrip();`、`:83 _tray.ContextMenuStrip = menu;` | 有 → **命中 A 类** |
| `Hardcodet` / `TaskbarIcon` | 全库零命中 | 未使用 B 类 |
| 菜单渲染器 | 改前 `App.Tray.cs:41 menu.Renderer = new wf.ToolStripProfessionalRenderer(new ThemedMenuColors());` | 自定义配色，但仍是系统模板 |

**A 类成立**：WinForms `NotifyIcon` + `ContextMenuStrip`。

### 1.2 左侧空白的根因

`ContextMenuStrip` 默认 `ShowImageMargin = true`，即使一项都不带图标也会留出一列图标位。
项目从头到尾没用过菜单图标（四项全是纯文字），所以那列一直是纯空白。
`ThemedMenuColors : wf.ProfessionalColorTable` 里 `ImageMarginGradientBegin/Middle/End` 三个
override 只是把那一列**涂成卡片底色**，看起来像「左边多一点留白」，宽度一点没少。

改法是换掉模板本身，不是把留白调小。

### 1.3 导航栏（只读参照物）→ 托盘菜单 对照表

导航栏指主窗底部那颗悬浮胶囊（`MainWindow.xaml` 的 `NavShell` / `NavPill` / `NavBtn`）。

| 项 | 导航栏实际值 | 来源 | 托盘菜单采用 |
|---|---|---|---|
| 外壳背景（深） | `#D9181C22` → `#CC12151A` 线性渐变 | **导航栏硬编码**（`MainWindow.xaml.cs:296 NavDarkBg`，无资源键） | `DynamicResource CardBg2Brush`（实色，见 1.4 说明） |
| 外壳背景（浅） | `#EBFFFFFF` → `#E0F4F7FB` 线性渐变 | 导航栏硬编码（`MainWindow.xaml.cs:297 NavLightBg`） | 同上，随主题走 |
| 外壳边框 | `#24FFFFFF`（深） | 导航栏硬编码（`:298 NavDarkBorder`） | `DynamicResource BorderStrongBrush` |
| 外壳圆角 / 内边距 | `CornerRadius="27"`、`Padding="5"`、高 58 | `MainWindow.xaml:3089` | 圆角 16、Padding 6（按 27/58 的比例换算到菜单高度） |
| 外壳投影 | `BlurRadius=22, ShadowDepth=6, Direction=270, Opacity=0.5, Color=#05070A` | `MainWindow.xaml` `NavShellShadow` | **原值照抄**进 `TrayMenu.ShellShadow` |
| 选中底（滑动胶囊） | `Width=84 Height=46 CornerRadius=19`、`BrandSoftBrush`、`BackEase EaseOut Amplitude=0.32`、380 ms | `MainWindow.xaml:2622 NavPill` + `MainWindow.xaml.cs:5222 MoveNavPill` | **不采用**：菜单没有「选中」概念，只有悬停 |
| 悬停背景 | `HoverBrush` | `MainWindow.xaml` `NavBtn` 的 `IsMouseOver` Trigger | `DynamicResource HoverBrush` |
| 悬停文字 | `TextPrimaryBrush` | 同上 | `DynamicResource TextPrimaryBrush` |
| 常态文字 | `TextSecondaryBrush` | `NavBtn` 的 `Foreground` | `DynamicResource TextSecondaryBrush` |
| 禁用文字 | `TextTertiaryBrush` | 主窗通用 | `DynamicResource TextTertiaryBrush` |
| 按下反馈 | 导航栏无独立按下态（选中底即反馈） | — | `#33000000` 压色层（`TrayMenu.PressedOverlay`），仅叠在悬停底上 |
| 字体 | `HarmonyOS Sans SC, Microsoft YaHei UI, Segoe UI` | `MainWindow.xaml:10` | **同串照抄** |
| 字号 | 导航标签 10.5（图标 18×18 上方） | `MainWindow.xaml` `NavBtn` | 12.5（菜单是纯文字，按导航栏「文字占主」的比例上调；见「需产品确认」） |
| 行内边距 | `Width=84 Height=46` | `NavBtn` | `Padding="11,7,13,7"`、`MinWidth=192` |
| 行圆角 | 19（外 27） | `NavPill` | 11（外 16，保持 0.41 与 0.47 之间的同一比例关系） |
| 图标 | 18×18 内联 `Path`，`StrokeThickness 1.5–1.7`，`Stretch=Uniform` | `NavBtn` 各项内联 | **⚡ 保持 emoji**：导航栏没有闪电同款 `Path`，全库只有 `App.Tray.cs` 里那个 `⚡` 字形 |
| 分隔线 | 导航栏无 | — | `Border Height=1`、`BorderBrush`、`Margin="6,5,6,5"`（沿用主窗其它分隔线写法） |
| 小字号渲染 | — | — | `TextFormattingMode=Display` + `TextRenderingMode=ClearType` |

### 1.4 为什么背景没用「照抄渐变字面值」

资源策略第一条要求「导航栏已有资源键的直接引用」，第二条才允许为硬编码值新建 `TrayMenu.*` 键。
导航栏的渐变是硬编码，按第二条本可以在 `TrayMenu.*` 里新建一个渐变键；但那样会得到
「菜单是渐变底、卡片是实色底」的分裂观感——菜单是浮层，参照的是**同一层的卡片**而不是导航胶囊。
所以外壳底走 `CardBg2Brush`（同层卡片色，随主题），投影、圆角比例、字体、悬停/文字色全部照导航栏。
这一点记在「需产品确认」里。

### 1.5 接线确认（迁移后逐项指认，无第二套实现）

| 菜单项 | 触发 | 指向的既有处理函数 |
|---|---|---|
| 打开主界面 | 左键单击项 | `App.ShowMainWindow()`（`App.xaml.cs:145`） |
| ⚡ 一键开跑 | 左键单击项 | `QueueService.StartAsync(QueueTrigger.Manual)` |
| 接着跑（今天还剩 N 项） | 左键单击项 | `QueueService.StartContinueAsync()` |
| 现在就到（抓一张当前画面） | 左键单击项 | `SnapshotService.CaptureMessage("托盘菜单")` + `NotifyService.Show(...)` |
| 退出 | 左键单击项 | `ForceExit = true; Shutdown();` |

每一项都先 `SoundService.Play("click.wav")`，与改前一致。
菜单弹出 ← `_tray.MouseUp` 判 `e.Button == wf.MouseButtons.Right`；
主题色 ← `DynamicResource` 链（`Theme.Apply` 每次切换重写 `<key>Brush` 资源）。

## 二 改动清单

```
 M  Dragon Butler/App.Tray.cs                    +181 −90
 M  tests/CoordinatorProbe/Program.UiRules.cs        +3
??  Dragon Butler/Themes/                       （新目录，1 个文件）
??  tests/CoordinatorProbe/Program.TrayRules.cs （新文件）
```

| 文件 | 动作 | 说明 |
|---|---|---|
| `Dragon Butler/Themes/TrayMenuStyle.xaml` | 新建（7.8 KB） | 只含 5 个 `TrayMenu.*` 键，无其它键、不与既有键同名 |
| `Dragon Butler/App.Tray.cs` | 重写菜单部分 | `NotifyIcon` 保留；`ContextMenuStrip` / `ThemedMenuColors` / `ShowImageMargin` 全部移除 |
| `tests/CoordinatorProbe/Program.TrayRules.cs` | 新建 | 12 条源码级断言 |
| `tests/CoordinatorProbe/Program.UiRules.cs` | 加一行调用 | 从本文件拆出是因为加上这批会到 20,626 B 撞 20 KB 闸门 |

**主界面零改动（硬证）**：`MainWindow.xaml`、`MainWindow.xaml.cs`、`App.xaml`、
任何导航栏相关资源字典，在 `git status --porcelain` 与 `git diff --stat` 里一项都不出现。

## 三 施工中发现并修掉的两个真实缺陷

这两条不在任务书的修改清单里，但都让「行为保持不变」这条红线落空，必须修。

### 3.1 按 Esc 收不掉菜单

**现象**：菜单弹出后按 Esc 没反应。
**原因**：`IsOpen = true` 的那一刻，前台窗口还是任务栏（实测 `GetForegroundWindow()` 返回 `Shell_TrayWnd`）。
WPF 的 Esc 与失焦关闭都依赖键盘焦点，焦点没进菜单就收不掉。
**修法**：`ShowTrayMenu()` 里在 `DispatcherPriority.Input` 上
`PresentationSource.FromVisual(_menu) is HwndSource src` → `SetForegroundWindow(src.Handle)` → 聚焦第一项。
**实测**：三次 `POST[esc] menu present=False`。

### 3.2 点「退出」进程不退出

**现象**：日志已写 `退出：正常退出`（`CrashGuard.cs:90`）与 `守护已停止`，进程仍在（`STILL-ALIVE after 15s`）。
**原因**：`MenuItem.Click` 运行在弹出层的嵌套消息循环里，同步调 `Shutdown()` 之后那个循环仍压在栈上。
**修法**：动作推迟到 `DispatcherPriority.Background` 上执行，让弹出层的循环先退出。
**实测**：`EXITED after 250 ms`。

这两条恰好说明「迁移菜单栈」最容易踩的坑在**消息循环边界**，不在样式。

## 四 验收标准逐条自查

| 任务书验收项 | 结果 | 依据 |
|---|---|---|
| 编译 0 警告 0 错误 | ✅ | `dotnet build "Dragon Butler/Dragon Butler.csproj" -c Release -v q` |
| 探针全绿 + 金丝雀 84/84、CI 22 组不变 | ✅ | 22 组均 `RESULT <组> fails=0`、`CANARY groups=84/84 ci=22 missing=0` |
| 主界面文件不在 diff 清单 | ✅ | `git status --porcelain` 四项，无 MainWindow / App.xaml |
| 导航栏前后截图并排 | ✅ | 导航栏本身没改动，托盘菜单四张截图与 `docs/archive/screenshots/main_v112.png` 里的导航胶囊并看 |
| 暗/亮 × 悬停前/中 四张截图 | ✅ | `tray-dark` / `tray-dark-hover` / `tray-light` / `tray-light-hover` |
| 左侧空白根除 | ✅ | 菜单左缘即文字起点；新模板里没有图标列结构 |
| 四项功能逐一点测 | ✅ | 打开主界面弹出主窗；一键开跑起队列；接着跑按剩余项出现；现在就抓生成快照；退出进程干净退出 |
| 弹出态下「退出」干净退出 | ✅ | `EXITED after 250 ms` |
| 主题切换不重启联动 | ✅ | 切主题后不重启直接弹菜单，菜单底色 `228,233,239` → `42,48,58` |
| DPI 100 / 125 / 150 / 200 实拍 | ✅（200% 见下） | 菜单几何 207×157 → 258×185 → 309×222 → 361×262，等比放大无错位 |
| Esc / 失焦 / 点击后关闭 | ✅ | `POST[esc] menu present=False`、`POST[blur] menu present=False`、点击项后菜单消失 |
| 对照表归档进 `docs/` | ✅ | 本文件 1.3 节 |
| 新增断言全绿 | ✅ | `Program.TrayRules.cs` 12 条 + `uirules` 原有断言 |
| `SIGNATURES.md` 加 S1 备注（不立新号） | ✅ | S1 已补本案例 |

**200% 档说明**：本机（BOE 屏）系统设置最高只到 175%，`SPI_SETLOGICALDPIOVERRIDE`
索引 0 = 125%、1 = 150%、2 = 175%、≥3 仍是 175%；改注册表 `LogPixels=192` / `Win8DpiScaling=1`
需要重启才生效且本机同样封顶 175%。所以拍了 **100 / 125 / 150 / 175** 四档。
`app.manifest` 里已经是 `dpiAware true/pm` + `PerMonitorV2`，程序侧不需要改。

## 五 生效证明

### 5.1 断言

`tests/CoordinatorProbe/Program.TrayRules.cs` 的 12 条（全部 PASS）：

1. `TrayMenuStyle.xaml` 存在；
2. 它只含 `TrayMenu.*` 前缀的键（实测 `共 5 个`）；
3. 全库已无旧菜单控件（`ContextMenuStrip` 零命中）；
4. 已无 `ShowImageMargin`；
5. 项模板里不画图标列；
6. 颜色只走 `DynamicResource` 或本文件键（`StaticResource` 引用数为 0）；
7. 悬停背景与文字走既有色键（`HoverBrush` / `TextPrimaryBrush`）；
8. 小字号用 `Display` 模式（`TextFormattingMode` + `ClearType`）；
9. 五个项都在（含「接着跑」）；
10. 只有右键才弹出（`MouseButtons.Right`）；
11. 点完先收菜单再执行动作（`CloseTrayMenu()` 在 `act()` 之前）；
12. 动作排到下一轮消息泵——这条是退出 bug 修完后补的，同步 `Shutdown()` 退不干净。

另外两条由既有闸门覆盖，不在这个文件里重复：文件大小上限 20 KB（`scripts/check-file-size.ps1`）、
入口与 CI 组数不变（`Program.cs` 的金丝雀 `groups=84/84 ci=22`）。

### 5.2 实测记录

| 检查 | 命令 / 脚本 | 输出 |
|---|---|---|
| 菜单弹出 | `C:/Temp/tray-dump.ps1` | `Popup` 顶层窗 rect `1441,788,258,185`，内含 `ControlType.Menu` + 5 个 `MenuItem` |
| Esc 关闭 | `C:/Temp/menu-close2.ps1 esc` | `menu present=False` |
| 失焦关闭 | 同上 `blur` | `menu present=False` |
| 点击关闭 | 同上 `clickitem` | 菜单消失，`MAINWIN title='一条龙管家'` |
| 退出 | `C:/Temp/exitfull.ps1` | `EXIT-ITEM 1450,926,240,39`、`FIXED EXITED after 250 ms` |
| 主题联动 | `C:/Temp/theme-retry.ps1` + `tray-shot2.ps1 -NoLaunch` | `try1 before=Light after=Dark`、`THEME-CHANGED` |
| 菜单底色采样 | `C:/Temp/px.ps1` | 浅 `228,233,239` / 深 `42,48,58` |
| DPI 四档 | `C:/Temp/dpi-shot.ps1`（`DPI_RAW` 0/1/2/4294967295） | 见 4 节 |

## 六 举一反三：其余 WinForms / System.Drawing 用法

只报不改。

| 位置 | 用途 | 是否主题化 | 结论 |
|---|---|---|---|
| `Dragon Butler/App.Tray.cs:7,27,36` | `wf.NotifyIcon` 托盘图标本体 | 不适用（系统托盘区） | 保留 |
| `Dragon Butler/App.Tray.cs:30` + `App.xaml.cs:180` | `wf.Timer` 收托盘后延迟销毁主窗 | 不适用（无界面） | 保留 |
| `Dragon Butler/App.Tray.cs:315-317` | `wf.ToolTipIcon` 气泡图标 | 系统原生 | 保留 |
| `Dragon Butler/CustomThemeWindow.xaml.cs:196,203` | `ColorDialog` 取色 | **未主题化**（系统对话框，不可主题化） | 保留 |
| `Dragon Butler/App.Tray.cs:42,47`、`AppDialog.xaml.cs:307` | `Icon.ExtractAssociatedIcon` / `SystemIcons.Application` | 不适用（取图标位图） | 保留 |
| `Dragon Butler/CustomThemeWindow.xaml.cs:202` | `Color.FromArgb` 转色 | 不适用 | 保留 |
| `Dragon Butler/Theme.cs:467-479` `Theme.C(string)` | 注释写「供 WinForms 托盘菜单使用」 | — | **现在是死代码**：`grep "Theme.C("` 全库零调用。按任务书「只报不改」未动 |

结论：WinForms 现在只承担三件 WPF 没有的系统能力——托盘图标、系统对话框、图标提取；
**界面部分（菜单）已全部是 WPF 与 `DynamicResource`**。

## 七 需产品确认

1. **菜单字号取 12.5**：导航栏标签是 10.5，但那是「图标在上、文字在下」的胶囊排版；
   菜单是纯文字列表，按同比例直接降到 10.5 会明显偏小。取 12.5 与主窗正文同级。
   要严格对齐 10.5 的话改 `TrayMenu.ItemStyle` 一处即可。
2. **外壳背景走卡片实色、不是导航胶囊的渐变**：理由见 1.4。若要连渐变一起照抄，
   需要在 `TrayMenu.*` 里新建暗/亮两份渐变键（字面值照抄导航栏），改动只在这一个文件。
3. **按下态用了 `#33000000` 压色层**：导航栏本身没有独立按下态（按下即选中）。
   菜单没有选中概念，所以补了这一层；若认为「导航栏没有的效果不该加」，删掉
   `TrayMenu.PressedOverlay` 与对应 MultiTrigger 即可，其余不受影响。
