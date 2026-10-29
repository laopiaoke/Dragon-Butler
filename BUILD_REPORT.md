# Dragon Butler / 一条龙管家 · 版本与构建记录

> 当前版本 **v1.3.1**（程序集 1.3.1.0） · 最后更新 2026-10-29
> 本文档记录三件事：版本号怎么定、每个版本改了什么、当前产物怎么构建与发布。

---

## 一、版本号规则

### 采用两段式 `主版本.次版本`

| 段位 | 什么时候 +1 | 例子 |
| --- | --- | --- |
| **主版本** | 只有"核心能力"或"用户能感知到换了个东西"时才进位 | v0.x → **v1.0**（首次对外发布）、未来 v2.0（如多方案管理这类新模块） |
| **次版本** | 每一次可交付的改动都 +1，包含新功能、体验优化、修 bug | v1.0 → **v1.1**（自定义主题）→ v1.2 → v1.3 … |

### 为什么这么定

1. **不设第三位**。`v1.1.1` 这种补丁号，是为"只换一个文件的分发渠道"准备的（npm 包、自动更新）。这个项目是单文件 exe、手动替换，没有"只发补丁"的场景，所以修复直接进下一个次版本，编号更好记。
2. **主版本是承诺，不是进度**。`v0.x` 表示开发期，交互和配置格式随时可能改；`v1.0` 表示"配置格式稳定、可以给别人用了"。之后再改 UI、加功能，只要不推翻使用方式，都留在 1.x。
3. **同日多次修复不单独占号**。一天里反复修同一个问题，合并进下一次版本记录，避免出现一长串 `v0.10.1 / v0.10.2`。项目早期确实试过三位号（v0.10.1、v0.11.1、v0.11.2），后来废弃，历史记录里把它们并入所属的次版本。
4. **开发中不预占版本号**。版本号只在"要发布"时定，避免仓库里写着 v1.2、发出来的还是 v1.1。
5. **显示号与程序集号的对应**：界面/文档显示 `v1.1`，`Dragon Butler.csproj` 里写 `<Version>1.1.0</Version>`、`<AssemblyVersion>1.1.0.0</AssemblyVersion>`。补位恒为 0，读日志时看到 `启动：Dragon Butler v1.1.0` 属于同一版本。

### 一句话判断

> 改完之后**用户还是照原来的方式用** → 次版本 +1；
> 改完之后**要么不能用了、要么多了整整一块新东西** → 主版本 +1。

---

## 二、版本一览

| 版本 | 日期 | 进位原因 | 一句话 |
| --- | --- | --- | --- |
| v0.1 | 10-25 | 起点 | 首个可运行版本：启停 BGI、托盘、定时雏形、5 个按键音 |
| v0.2 | 10-26 | 次版本 | 交互增强：按钮四态、果冻按压、循环滚筒、tick 音 |
| v0.3 | 10-26 | 次版本 | 界面重构：单页两栏 + 20 个语义颜色令牌 + 应用图标 |
| v0.4 | 10-26 | 次版本 | 四项反馈修复：去大下巴、滚筒无限循环、三视图、折叠卡死 |
| v0.5 | 10-26 | 次版本 | 定时选择器改版：键盘输入放大、24 小时圆盘重做 |
| v0.6 | 10-26 | 次版本 | 守护明细透明化、11 种推送渠道、配置组自动识别 |
| v0.7 | 10-26 | 次版本 | 定时卡双页切换、心跳无缝、配置组严格同步 |
| v0.8 | 10-27 | 次版本 | 启动卡内联配置组、拟真心电图、时间页切换重构 |
| v0.9 | 10-27 | 次版本 | 信息权重调整：启动台上移，守护卡降级为可折叠条 |
| v0.10 | 10-27 | 次版本 | 返回后尺寸错位修复、iOS 风格按键音体系 |
| v0.11 | 10-27 | 次版本 | 监护仪心电图、音效"听不见"根因、统一弹窗、启动居中 |
| **v1.0** | 10-27 | **主版本** | **首个对外发布**：命名规范、双产物（直装/便携）、README |
| **v1.1** | 10-27 | 次版本 | 自定义主题：调色板取色 / 图片背景 + 遮罩 + 实时预览 |
| **v1.2** | 10-27 | 次版本 | 代码清理：删除无用模块与重复入口，直装版体积 -40% |
| **v1.3** | 10-29 | 次版本 | 多游戏排队 / 跑完动作 / 防打扰 / 自动寻找 / 三层 UI 改版 / 完成判定收敛 |
| **v1.3.1** | 10-29 | 次版本 | 完成判定补强：BGI 进程退出也作为互证，老配置自动升级 |

---

## 三、各版本改了什么

### v0.1 · 首个可运行版本（10-25）

- WPF（net8.0-windows）+ WinForms 托盘，21 个源文件、约 4300 行；单文件发布。
- 能力：一颗按钮启动/停止 BGI、托盘常驻、基础设置、定时雏形、5 个程序化合成音效。
- 守护三路监测打底：BGI Webhook（18235 端口）、日志 tail 解析、截图灰度比对，由 5 秒心跳统一调度。
- 关键坑：`HttpListener` 前缀必须以 `/` 结尾，改为注册根前缀 + 路径路由。

### v0.2 · 交互增强（10-26）

- 大按钮四态（未就绪/就绪/运行中/处理中）统一入口，300ms 就绪防抖。
- 果冻按压：RenderTransform 缩放 + 位移，替换原来的 IsPressed 触发器。
- 时间选择器改 iOS 式双滚筒（阶梯字号、渐隐遮罩、中心标线、滚轮/拖动/点选）。
- 新增 tick 音（40ms 节流）。
- 修两个真 bug：滚筒值整体偏移两行、超长 StackPanel 在裁剪父级里只渲染 220px。

### v0.3 · 界面重构（10-26）

- 主题系统重写：20 个语义令牌 + 旧键别名 + 系统深浅色实时跟随；XAML 里不再出现硬编码色值。
- 主界面改为单页两栏（980×760 起），标题栏自绘（图标 + 名称 + 主题分段 + 最小化/关闭）。
- 新增星期掩码定时（`ScheduleWeekdays`）与"上次执行"记录。
- 用 PIL 生成多尺寸 ICO，托盘/任务栏/窗口统一。
- 修环境坑：`TextFormattingMode=Display` 导致大字号数字不绘制。

### v0.4 · 四项反馈修复（10-26）

- 去掉窗口底部空白：改 `SizeToContent=Height`，状态栏无消息时收起。
- 滚筒改 9 槽位循环复用 + 惯性 fling，滑到头会绕回。
- 选择器三视图（圆盘/滚筒/键盘），圆盘设为默认首视图。
- 折叠卡动画对 `Height=NaN` 元素做动画导致布局假死，改为显式 From/To + 先量后动画。

### v0.5 · 定时选择器改版（10-26）

- 键盘输入放大到 38px 数字、96×64 输入框、聚焦品牌描边。
- 圆盘重做：实心盘面 + 内外圈同角位（外圈 1–12、内圈 13–00），按落点半径选圈。
- 选中气泡、指针止于气泡边缘、圆心品牌色圆点。

### v0.6 · 守护透明化 + 推送 + 自动识别（10-26）

- 守护卡不再"黑箱"：状态胶囊 + 当前状态带 + 4 行真实明细（系统占用、BGI 内存、截图差异、事件数）+ 心跳 + 今日组 + 最近事件。
- 推送扩到 11 种渠道，统一解析/发送/测试，能识别 HTTP 200 里的业务错误码。
- 新增 `BgiDiscovery`：直接扫 `User\ScriptGroup`、`User\OneDragon`，不需要在 BGI 里填推送地址。

### v0.7 · 定时双页 + 严格同步（10-26）

- 定时卡改手机式双页（概览 ⇄ 编辑），高度变化控制在 60px 内。
- 心跳虚线动画周期改为 21，根治每轮跳 5px 的闪烁。
- 配置组与 BGI 目录严格同步：8 秒巡检、删组自动清勾选、全新 BGI 视为 0 组而非报错。

### v0.8 · 启动卡内联 + 拟真心电（10-27）

- 配置组勾选并入启动卡，减少一次展开。
- 心电图从"扫描线"改为拟真波形（P-QRS-T 高斯叠加），开始有形状。
- 时间页切换重构，修编辑页高度把概览拉伸的问题。

### v0.9 · 信息权重调整（10-27）

- 主按钮上移到启动台，守护明细降级为可折叠条，一屏之内先看到"能不能跑"。
- 守护条折叠时只留一行摘要，异常时变黄并自动展开。

### v0.10 · 尺寸错位 + 音效（10-27）

- 修"改完时间点返回，卡片高度延迟半秒"：改为同一帧交还 Auto 高度，只用透明度/缩放/位移做过渡。
- 按键音换成 iOS 风格短促音，改为全局 `PreviewMouseLeftButtonDown` 统一分发（按下即响），删掉 30 余处分散的 `Play` 调用。

### v0.11 · 监护仪心电 + 音效根因（10-27）

- 心电图重写为滚动模型：每个像素取"若干毫秒前"的采样，整条波形持续左移，配网格、辉光、写头、左旧右新亮度渐变；待机也有轻微起伏。
- 音效"点了没声"的根因：衰减时间常数只有 4ms，能量集中在 5ms 内，系统音量 20% 时人耳和峰值表都读不到。重做包络（click 88ms），播放后端换 `winmm.PlaySound` 并检查返回值。
- 报错弹窗从系统 MessageBox 换成应用内统一组件（含"打开日志目录"）。
- 主窗口启动居中改用 Win32 物理像素 + 鼠标所在显示器工作区，规避多屏混合 DPI 偏差。
- 顺带：静态画刷冻结（跨线程 Freezable 崩溃）、窗口置顶抢前台等。

### v1.0 · 首个对外发布（10-27）· **主版本进位**

> 进位理由：这是第一次"别人拿到也能用"的版本 —— 命名规范化、双产物、文档齐备、配置格式冻结。

- 命名规范：源码/目录英文，发布物中文（`一条龙管家-…zip`；自 v1.2 起追加 `-v{版本}`）。
- 双产物：需 .NET 8 运行时版（依赖 .NET 8，0.8 MB）+ 免安装便携版（自包含单文件，155 MB）。
- 便携版补 `IncludeNativeLibrariesForSelfExtract=true`，真正做到"一个 exe 拷走就能跑"。
- 按键音效默认关闭（静音），开关与配置项保留。
- 工程/产物重命名为 Dragon Butler，运行期标识统一（互斥体、计划任务、Toast 标识）。
- README、BUILD_REPORT、LICENSE 补齐；版本号改为读取程序集，不再硬编码。

### v1.1 · 自定义主题（10-27）

- 主题四档：深色 / 浅色 / 跟随系统 / 自定义。
- 自定义支持两种背景：调色板取色（18 预设 + 系统取色器）、图片背景（可调遮罩 0–90%）。
- 编辑器左预览右参数，改动实时作用于整个应用；底部实时显示正文对比度（AAA/AA 判定）。
- 可读性靠"三层 + 锚定"：背景层 / 遮罩层 / 卡片层，文字永远落在卡片上；卡片亮度按方案锚定，换背景不稀释对比度。
- 「恢复默认主题」回到跟随系统，「取消」不写配置。
- 新增参数：`CustomBgKind`、`CustomBgColor`、`CustomBgImage`、`CustomOverlay`、`CustomScheme`。
- 发布目录按"是否需要运行时"重命名（原 `Dragon Butler-win-x64*`），压缩包同名；`使用说明.txt` 同步改写版本说明，并明确"两个版本都不需要安装"。
- 发布产物命名带上版本号：压缩包为 `一条龙管家-v1.2-需.NET8运行时.zip` / `一条龙管家-v1.2-免安装便携版.zip`（版本取自 csproj `<Version>`，去掉末尾 `.0`）；包内顶层目录与源目录保持固定名，便于覆盖升级。
- README 按使用者视角重写（189 行、5 张实拍截图、含"已知边界"）。

### v1.3.1 · 完成判定补强（10-29）

- BGI 副信号追加 `ProcessExit0`（退出码 0 才算）：解决"BGI 跑完不关游戏、又没配 Webhook"时一直等到宽限 → 悬置。
- `RunCoordinator`：进程退出作为互证时只有 exit=0 才判 Done；非 0 继续等其它副信号/宽限（防崩溃误判完成）。
- 老配置自动升级：`ProfileRegistry` 检测 `secondarySchema < 2` 时补一条副信号并写回 `tools/bettergi.json` 与 settings，日志记"已升级 BGI 副信号"。
- 探针 `bgisecondary`：锚点 + 进程正常退出 → `Done` 4.1s（此前要等 60 秒宽限后 Halted）。
- 验证老配置（`SecondarySignals=[2,0]`）启动后变为 `[2,0,1]`、`secondarySchema=2`，两处文件均已写入。

### v1.3 · 多游戏排队（10-29）

- 新增 `QueueService`（队列、间隔、失败策略、汇总通知、跑完动作 DRYRUN）、`WebhookHub`（全库唯一 HttpListener + 扇出）、`ToolDiscovery`（四级自动寻找）、`FirstRunWindow`（三步向导）。
- 完成判定收敛：`SentinelService` 卸下 webhook/日志完成判定，仅保留存活/内存/截图/会话；组归档改由 `GroupArchiveWatcher` 吃 Webhook + 日志。
- `BgiService` 门面物理删除（`git grep -i BgiService` 零结果）。
- UI 三层布局：队列卡 / 三张工具卡（默认折叠）/ 高级设置（默认折叠）；旧哨兵四行卡删除，底部盾牌替代。
- 版本 1.3.0；自查见 `PHASE2_REPORT.md`（队列/重试/中止/超时/悬置/锚点/防打扰/残留/冲突/度假/DRYRUN/自动寻找全部用 StubTool 跑过）。
- 附录 B 人工验收通过；`AfterRunDryRun` 默认改为 `false`（跑完动作真实生效）。
- 已发 tag `v1.3.0` 与 `v1.3.1`，Release：
  <https://github.com/laopiaoke/Dragon-Butler/releases/tag/v1.3.1>
  （资产：`Dragon-Butler-v1.3.1-win-x64.zip` 解压即用、`Dragon-Butler-v1.3.1-win-x64-runtime-dependent.zip`）；
  v1.3.0 的 Release 正文已补"下载哪个"说明。

### v1.2 · 代码清理（10-27）

> 只删不加、无行为变更：所有删除项都没有调用方，保留的代码路径未改动。

- 删除 `MultiUserAutoStart.cs`（178 行，早期"为其他用户注册开机自启"的 WMI 实现，界面早已移除入口，全项目零调用）及随之唯一的依赖 `System.Management` 包引用。
  → 直装版单文件 **797,564 B → 472,696 B（-40.7%）**，便携版 162,350,993 B → 162,029,035 B。
- 删除被现役实现取代的旧入口：`BgiDiscovery.GetGroupNamesForPicker()`（现用 `Scan().Groups`）、`GroupArchive.GetNotRunToday()` 及因此失去唯一调用者的 `GetKnownGroupNames()`（现用 `GetUnfinished` / `GetTodayGroups`）。
- 删除只写不读的诊断代码：`SoundService.PlayCount`、`LastPlayed`、`LastPlayOk`、`ResetDiagnostics()`（保留用于日志限流的 `PlayFailures`）；`MainWindow.TbPath_TextChanged`（路径已改为文本展示，无控件再触发）。
- 删除主题兼容层：6 个旧键别名（TextMain / TextSub / AccentOn / AccentOff / LogBg / AccentOnPressed）与别名注册循环；XAML 与托盘取色改用规范键名（TextPrimary / TextSecondary / Brand / BrandPressed / Inset）。
- 净删除 **263 行**（8 个文件改动、1 个文件删除；代码 10,839 → 10,576 行）。
- 冒烟结果：启动正常（日志 `v1.2.0`）、Webhook 监听 18235、6 个音效生成、深色/浅色切换正常、自定义主题编辑器正常打开。

---

## 四、当前构建与产物

### 一条命令打包（推荐）

```powershell
# 构建并打包全部变体（默认 win-x64）
.\scripts\package.ps1

# 只打包便携版 / 只重新打 zip / 打其他平台
.\scripts\package.ps1 -Variant portable
.\scripts\package.ps1 -ZipOnly
.\scripts\package.ps1 -Runtime win-arm64
```

脚本是**命名的唯一来源**：版本号从 `Dragon Butler.csproj` 的 `<Version>` 读取，目录名与压缩包名由脚本拼出，
不需要手工敲 publish 命令或手工改文件名。发布前先退出正在运行的 Dragon Butler（否则 exe 被占用，清理旧目录会失败）。

若提示"在此系统上禁止运行脚本"，用（只影响当前进程，不改系统策略）：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\scripts\package.ps1
```

### 等价的原始命令

```bash
# 构建
dotnet build "Dragon Butler/Dragon Butler.csproj" -c Release
# → 0 错误，4 个既有警告（wf 别名 CS8981 ×2、DPI manifest WFAC010 ×2）

# runtime-dependent（框架依赖，体积最小）
dotnet publish "Dragon Butler/Dragon Butler.csproj" -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o "发布/Dragon-Butler-win-x64-runtime-dependent"

# portable（自包含单文件）
dotnet publish "Dragon Butler/Dragon Butler.csproj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -o "发布/Dragon-Butler-win-x64-portable"
```

> 工程与产物目录都含空格，命令行必须加引号；发布前先确保没有正在运行的实例（会锁住 exe）。

| 产物 | 路径 | 体积 |
| --- | --- | --- |
| runtime-dependent exe | `发布/Dragon-Butler-win-x64-runtime-dependent/Dragon Butler.exe` | 517,752 B |
| runtime-dependent 压缩包 | `发布/Dragon-Butler-v1.3.1-win-x64-runtime-dependent.zip` | ≈ 0.24 MB |
| portable exe | `发布/Dragon-Butler-win-x64-portable/Dragon Butler.exe` | 162,074,091 B |
| portable 压缩包 | `发布/Dragon-Butler-v1.3.1-win-x64-portable.zip` | ≈ 62.74 MB |
| Release 资产（解压即用） | `发布/Dragon-Butler-v1.3.1-win-x64.zip` | 62.74 MB |

压缩包内顶层目录是**不带版本**的固定名（与 `发布/` 下的源目录一致，如 `Dragon-Butler-win-x64-portable/`），内含一个 exe + `使用说明.txt`。
这样升级时把新包解压到原目录覆盖即可，`config\`（设置）不会丢；版本信息只体现在**下载到的文件名**上。

### 命名规则

```
<Product>-v<Version>-<Runtime>-<Variant>.zip
Dragon-Butler  -v1.2   -win-x64  -portable .zip
```

| 字段 | 取值 | 说明 |
| --- | --- | --- |
| `<Product>` | `Dragon-Butler` | 固定，与 GitHub 仓库名一致 |
| `v<Version>` | `v1.2` | 取自 csproj `<Version>`，去掉补位的 `.0`（`1.2.0` → `v1.2`） |
| `<Runtime>` | `win-x64` | .NET RID。当前只有 win-x64；加平台时只增不改名 |
| `<Variant>` | `runtime-dependent` / `portable` | 见下表 |

包内顶层目录 = `<Product>-<Runtime>-<Variant>`（**不带版本**），保证覆盖升级。

### 名称对照表

| 原名称（中文） | 新名称（英文） | 平台 / RID | 包类型 | 运行时依赖 | 体积 | 适用场景 |
| --- | --- | --- | --- | --- | --- | --- |
| `一条龙管家-v1.2-需.NET8运行时.zip` | `Dragon-Butler-v1.2-win-x64-runtime-dependent.zip` | Windows 10/11 x64 · `win-x64` | 绿色版（框架依赖） | 需已装 .NET 8 桌面运行时 | ≈ 0.22 MB | 机器上已有 .NET 8，只想下最小包；带宽/空间敏感 |
| `一条龙管家-v1.2-免安装便携版.zip` | `Dragon-Butler-v1.2-win-x64-portable.zip` | Windows 10/11 x64 · `win-x64` | 绿色版（自包含单文件） | 无，运行时已打包进 exe | ≈ 62.72 MB | 不确定有没有 .NET 8；换电脑、重装系统、放 U 盘随身携带 |
| （GitHub 自动生成，不可改名） | `Dragon-Butler-1.2.0.zip` | 全平台（源码） | 源码包 | 需 .NET 8 SDK 自行编译 | — | 想看代码、改代码或自己构建 |
| 暂无产物（规则预留） | `Dragon-Butler-v1.2-win-x64-setup.exe` | Windows 10/11 x64 · `win-x64` | 安装包 | 待定 | — | 将来若做安装向导，按此名预留，不与绿色版混淆 |
| `发布/一条龙管家-需.NET8运行时/` | `发布/Dragon-Butler-win-x64-runtime-dependent/` | Windows 10/11 x64 | 包内顶层目录 | — | — | 解压后的目录名，固定不带版本 |
| `发布/一条龙管家-免安装便携版/` | `发布/Dragon-Butler-win-x64-portable/` | Windows 10/11 x64 | 包内顶层目录 | — | — | 解压后的目录名，固定不带版本 |

> 两个压缩包**都是绿色版**（无安装向导、不写注册表、不注册服务），差别只在 .NET 运行时放在 exe 里还是系统里。
> 目前没有真正的安装包，`setup.exe` 只是为将来预留的命名位。

---

## 五、验证状态

**v1.1 / v1.2 未执行自动化验证**，仅完成编译、发布与一次启动级冒烟，功能由使用者自行验收（自定义主题的验收点见 README「已知边界」与下表）。

历史基线（v1.0 时的自动化验证台结果，供回归时对照，**不代表 v1.1 现状**）：

| 套件 | 结果 | 覆盖 |
| --- | --- | --- |
| `--shot` | 21/0 | 三主题离屏渲染、关键尺寸 |
| `--layout` | 39/0 | 卡片高度、折叠展开、滚动区 |
| `--transition` | 10/0 | 定时卡换页时序、返回后高度校正 |
| `--picker` | 34/0 | 圆盘点选/拖动、键盘输入 |
| `--center` | 6/0 | 多屏居中、DPI 缩放 |
| `--discover` | 17/0 | 配置组识别与目录同步 |
| `--push` | 24/0 | 11 种推送报文构造 |
| `--ui` | 59/0 | 大按钮四态、守护条、心电图 |
| `--sound` | 29/0 | 按键音时长/峰值/按下即响 |
| `--dialog` | 40/0 | 应用内弹窗四类型 |

v1.1 建议重点手测：

1. 标题栏「自定义」打开编辑器，左侧预览随参数实时变化；
2. 调色板选色 / 选图 / 拖遮罩，卡片与文字仍清晰，读数随动；
3. 「应用」后重启仍是自定义；「取消」回到原主题；
4. 「恢复默认主题」后 `settings.json` 的 `Theme` 为 `System`；
5. 切回深/浅/跟随系统时，图片层与遮罩层不残留。

---

## 六、发布检查清单

1. 先定版本：改 `Dragon Butler.csproj` 的 `<Version>`（显示号 = 去掉末尾 `.0`，如 `1.2.0` → `v1.2`）——**只改这一处**，压缩包与目录名都由脚本读出；
2. `dotnet build` 0 错误；
3. 退出正在运行的 Dragon Butler，然后跑 `.\scripts\package.ps1`（自动 publish 到 `发布/Dragon-Butler-win-x64-{variant}/`，目录名不带版本）；
4. 确认目录里只有 `Dragon Butler.exe` + `使用说明.txt`（脚本已自动删 `.pdb` 并拷入说明）；
5. 脚本已按 `<Product>-v<版本>-<Runtime>-<Variant>.zip` 打好包，核对 `发布/` 下的文件名与本报告「名称对照表」一致；
6. 更新 `docs/使用说明.txt` 里受影响的段落与页脚版本（脚本会把它拷进每个产物目录）；
7. 更新本文档的版本表与产物体积；
8. 需要时再更新 README 的版本徽章与下载表；
9. 上传 GitHub Release 附件时**用脚本生成的英文名**，不要用中文名（中文名在上传后会变成 `-v1.2-` 这类残缺名，见「已知约束」）。

---

## 七、已知约束

- 目标平台仅 Windows 10/11 x64，依赖 .NET 8（便携版自带）。
- 界面字体优先 `HarmonyOS Sans SC`，缺字回退 `Microsoft YaHei UI` / `Segoe UI`。
- 便携版首次启动会把原生库解压到 `%TEMP%\.net`，属 .NET 单文件机制。
- 图片背景亮度取整图平均色，极端构图可能判不准，需手动指定文字配色。
- 截图冻结检测要求游戏窗口可见，最小化到托盘时跳过。
- 无自动更新机制，升级靠手动替换 exe（`config\`、`全部日志\` 保留即可）。
- **GitHub Release 附件不能用中文名**：v1.2.0 上传中文名压缩包后，附件名被截断成 `-v1.2-`、`-v1.2-.NET8`、`-v1.2-.zip`，下载页完全无法辨认。改用英文名后不再有此问题；上传前务必确认附件名与「名称对照表」一致。
- GitHub 的源码包（`Source code (zip)`）由仓库名 + tag 自动生成（形如 `Dragon-Butler-1.2.0.zip`，前缀 `v` 会被去掉），**不受打包脚本控制**，不要手改。
