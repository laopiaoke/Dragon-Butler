# v1.5.0 按日计划 · 自由接入

> 已发布：<https://github.com/laopiaoke/Dragon-Butler/releases/tag/v1.5.0>

## 新增
- **按日计划**：一周七天各跑各的工具组合，周本和日常可以自动分开；空着的那天到点会跳过并提醒你
- **自定义信号**：自定义工具的完成方式多了第三选——「日志包含文本 / 等待文件出现 / 主窗口标题包含」，三种条件可以任意组合，命中一个就算完成
- **更换图标**：任何工具（含内置三个）都能换成你喜欢的图标，支持 .ico / .png / .jpg，也能借别的 exe 的图标；随时一键还原
- **晨报**（默认关）：每天第一次打开时用一句话汇报昨天跑了什么、成功与否、总耗时

## 改进
- 队列卡每个工具后面直接显示「每天 / 6天」，一眼看出按日计划的安排；今天跑哪几项写在队列下方
- 手动「一键开跑」不受按日计划限制，永远跑当前勾选——计划只约束定时触发

## 下载哪个
- **免安装便携版（推荐）**：`Dragon-Butler-v1.5-win-x64-portable.zip` —— 约 62.8 MB，解压即用，自带 .NET 运行时
- **需 .NET 8 运行时**：`Dragon-Butler-v1.5-win-x64-runtime-dependent.zip` —— 约 0.27 MB，机器上已装 .NET 8 桌面运行时可用

## 升级注意
- 按日计划默认每天全选，不配置的话行为和上一版完全一样
- 旧配置直接可用，`config\`、`tools\` 都不用改；新字段缺省即默认值
- 回退：在 Releases 页下载 v1.4.0

## 已知限制
- 「日志包含文本」只看程序目录及其 `log` / `logs` 子目录下的 `.log`、`.txt`，日志写到别处的工具请用「等待文件出现」或「窗口标题」
- 自定义信号命中后进入互证，仍需工具自己退出（退出码 0）或宽限内收尾，避免误判「跑完其实还在跑」
- 晨报按配置组归档统计，「N 次运行」= 昨天有记录的配置组条数

# v1.4.0 挂机护航 · 自定义工具

> 已发布：<https://github.com/laopiaoke/Dragon-Butler/releases/tag/v1.4.0>

## 新增
- **挂机护航**：队列跑起来后自动按住系统——不空闲睡眠、不因 Windows 更新弹重启、不弹屏保；队列结束（跑完 / 失败 / 手动停止）只还原真正改过的项，不需要配置，也不改你的电源计划
- **夜间息屏**：可设「运行 N 分钟后关闭显示器」，系统保持唤醒、队列继续跑（默认关闭）
- **疑似挂起仲裁**：进程还活着但 CPU 和日志连续 3 分钟都没动静 → 队列标「疑似挂起」、暂停并通知；可在高级设置里选「自动终止」，结束进程后自动继续下一项
- **自定义工具**：任意 exe 都能接入——工具卡最下方「＋ 添加」，填程序路径、进程名、完成方式即可
- **到达时长完成方式**：除了「进程退出（退出码 0）」，新增「跑满 N 分钟算完成」，给不会自己退出的工具用
- **图标自动提取**：工具卡显示 exe 自带图标，取不到时回退首字母

## 修复
- ok-ww 的工具卡操作落到 BetterGI 档案的问题（Tag 写错）
- 未知工具 ID 会静默回退到 BetterGI 档案的问题，现在返回空档案

## 下载哪个
- **免安装便携版（推荐）**：`Dragon-Butler-v1.4-win-x64-portable.zip` —— 约 62.8 MB，解压即用，自带 .NET 运行时
- **需 .NET 8 运行时**：`Dragon-Butler-v1.4-win-x64-runtime-dependent.zip` —— 约 0.26 MB，机器上已装 .NET 8 桌面运行时可用

## 升级注意
- 覆盖安装即可，`config\` 设置不动；`tools\` 下缺的档案会自动补齐，已有档案只增不减
- 新增的 `tools\`、`cache\icons\` 都在 exe 旁边，删掉会自动重建
- 回退：在 Releases 页下载 v1.3.1

## 已知限制
- 挂机护航只能拦「应用请求的重启」与屏保；组策略强制的重启、笔记本合盖睡眠仍按系统设置走
- 队列按顺序执行；「疑似挂起」默认只暂停不终止，需要人工确认

# v1.3.1 完成判定补强

> 已发布：<https://github.com/laopiaoke/Dragon-Butler/releases/tag/v1.3.1>

## 修复
- BetterGI 跑完不关游戏、又没配 Webhook 时，不再一直等到宽限后停下队列：新增"BGI 进程正常退出（退出码 0）"作为互证副信号
- 老配置自动升级：首次启动把这条副信号补进 `tools/bettergi.json`，日志记一行"已升级 BGI 副信号"，无需手动改

## 下载哪个
- **免安装便携版（推荐）**：`Dragon-Butler-v1.3.1-win-x64.zip` —— 解压即用，自带 .NET 运行时
- **需 .NET 8 运行时**：`Dragon-Butler-v1.3.1-win-x64-runtime-dependent.zip` —— 约 0.24 MB，机器上已装 .NET 8 桌面运行时可用

## 升级注意
- 覆盖安装即可，`config\` 设置不动；`tools/bettergi.json` 会被自动补一条副信号（只增不减）

# v1.3.0 睡前一键，多游戏排队

> 已发布：<https://github.com/laopiaoke/Dragon-Butler/releases/tag/v1.3.0>（tag `v1.3.0`）

## 新增
- 多游戏排队：勾选 BetterGI / 三月七助手 / ok-ww，点"一键开跑"，依次自动执行
- 跑完动作：仅通知 / 自动睡眠 / 自动关机（默认仅通知；关机有 60 秒倒计时，可取消）
- 防打扰：定时任务发现你正在用电脑，会等你空闲后再自动开始
- 自动寻找工具路径：点一下自动定位，多数情况无需手动浏览
- 自动清理上次残留进程、度假模式（一键暂停所有定时）、首次使用三步引导

## 改进
- BetterGI 零配置接入：自动读取 BGI 日志识别完成，无需填写 Webhook（可选填写加速）
- 守护功能收入高级设置，日常不再打扰

## 修复
- 复制地址在剪贴板被其他程序占用时不再报错，自动重试并提供手动复制

## 升级注意
- 首次启动自动迁移旧配置（原文件备份为 .bak.v12），无需手动操作
- 回退：在 Releases 页下载 v1.2.x

## 已知限制
- 队列按顺序执行；任一工具"报告完成但没退出"时，队列会停下来等你处理（通知会说明原因）

---

# 历史版本

# 一条龙管家 Dragon Butler · 发行声明

> 以下内容与 GitHub Release（tag `v1.2.0`）正文保持一致，发版时直接复制粘贴。

---

## v1.2 · 代码清理（10-27）

只删不加、无行为变更：所有删除项都没有调用方，保留的代码路径未改动。

- 删除 `MultiUserAutoStart.cs`（178 行，早期"为其他用户注册开机自启"的 WMI 实现，界面早已移除入口，全项目零调用）及随之唯一的依赖 `System.Management` 包引用。
  → 直装版单文件 **797,564 B → 472,696 B（-40.7%）**，便携版 162,350,993 B → 162,029,035 B。
- 删除被现役实现取代的旧入口：`BgiDiscovery.GetGroupNamesForPicker()`（现用 `Scan().Groups`）、`GroupArchive.GetNotRunToday()` 及因此失去唯一调用者的 `GetKnownGroupNames()`（现用 `GetUnfinished` / `GetTodayGroups`）。
- 删除只写不读的诊断代码：`SoundService.PlayCount`、`LastPlayed`、`LastPlayOk`、`ResetDiagnostics()`（保留用于日志限流的 `PlayFailures`）；`MainWindow.TbPath_TextChanged`（路径已改为文本展示，无控件再触发）。
- 删除主题兼容层：6 个旧键别名与别名注册循环；XAML 与托盘取色改用规范键名。
- 净删除 **263 行**（8 个文件改动、1 个文件删除；代码 10,839 → 10,576 行）。
- 冒烟结果：启动正常（日志 `v1.2.0`）、Webhook 监听 18235、6 个音效生成、深色/浅色切换正常、自定义主题编辑器正常打开。

---

## 下载哪个包装哪个

**一句话**：机器上已经装过 .NET 8，下 `runtime-dependent`；不确定或要换电脑用，下 `portable`。两个包功能完全一样，都是绿色版（无安装向导、不写注册表、不注册服务）。

| 压缩包文件名 | 是什么包 | 适用平台 | 包类型 | 运行时依赖 | 体积 | 什么时候下它 |
| --- | --- | --- | --- | --- | --- | --- |
| `Dragon-Butler-v1.2-win-x64-runtime-dependent.zip` | 需 .NET 8 运行时版 | Windows 10/11 x64（`win-x64`） | 绿色版（框架依赖） | **需要**电脑上已装 .NET 8 桌面运行时 | ≈ 0.22 MB | 已装 .NET 8，只想下最小包；带宽或空间敏感 |
| `Dragon-Butler-v1.2-win-x64-portable.zip` | 免安装便携版 | Windows 10/11 x64（`win-x64`） | 绿色版（自包含单文件） | **不需要**，运行时已打包进 exe | ≈ 62.72 MB | 不确定有没有 .NET 8；要换电脑、重装系统、放 U 盘随身携带 |
| `Dragon-Butler-1.2.0.zip` | 源码包（GitHub 自动生成） | 全平台 | 源码包 | 需 .NET 8 SDK 自行编译 | ≈ 1 MB | 想看代码、改代码或自己构建 |
| `Dragon-Butler-v1.2-win-x64-setup.exe` | 暂无产物，命名位预留 | Windows 10/11 x64 | 安装包 | 待定 | — | 将来若做安装向导，用这个名字，不与绿色版混淆 |

### 文件名怎么看

```
Dragon-Butler  -v1.2      -win-x64  -portable            .zip
└─ 产品名        └─ 版本     └─ 平台    └─ 变体             └─ 格式
```

- **产品名**固定 `Dragon-Butler`；
- **版本**取自 `Dragon Butler.csproj` 的 `<Version>`，去掉补位的 `.0`（`1.2.0` → `v1.2`）；
- **平台**是 .NET RID，目前只有 `win-x64`，将来加平台只增不改名；
- **变体** `runtime-dependent` = 运行时在系统里，`portable` = 运行时在 exe 里。

### 解压后

顶层目录是**不带版本**的固定名，升级时把新包解压到原目录直接覆盖即可，`config\` 里的设置不会丢：

```
Dragon-Butler-win-x64-runtime-dependent\Dragon Butler.exe + 使用说明.txt
Dragon-Butler-win-x64-portable\        Dragon Butler.exe + 使用说明.txt
```

---

## 这一版起改了什么命名

| 原名称 | 新名称 |
| --- | --- |
| `一条龙管家-v1.2-需.NET8运行时.zip` | `Dragon-Butler-v1.2-win-x64-runtime-dependent.zip` |
| `一条龙管家-v1.2-免安装便携版.zip` | `Dragon-Butler-v1.2-win-x64-portable.zip` |
| `发布/一条龙管家-需.NET8运行时/` | `发布/Dragon-Butler-win-x64-runtime-dependent/` |
| `发布/一条龙管家-免安装便携版/` | `发布/Dragon-Butler-win-x64-portable/` |

原因：中文名上传 GitHub Release 后附件名会被截断成 `-v1.2-` 这类不可辨认的名字，改用英文名后不再有此问题。
