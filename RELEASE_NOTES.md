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
