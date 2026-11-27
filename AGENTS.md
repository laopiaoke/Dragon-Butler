# AGENTS.md

给改这个仓库的 agent 看的一页纸。**只写「看目录猜不到」的事**，其余细节都在 `docs/`，按下面的路标去查。

## 这是什么

Dragon-Butler（一条龙管家）：Windows 桌面小工具，替人按下「开始挂机」、盯着跑完、跑完做事。WPF + .NET 8，单进程，无服务端。

- 主工程：`Dragon Butler/`（`Dragon Butler.csproj`，隐式编译，新增 `.cs` 自动纳入）
- 测试探针：`tests/CoordinatorProbe/`（84 入口 + `ui` 渲染截图）；替身程序：`tests/StubTool/`
- 打包：`scripts/package.ps1`；发布说明抽取：`scripts/release-notes.ps1`
- 分支 `main`，远端 `https://github.com/laopiaoke/Dragon-Butler.git`

## 仓库目录与文档放置规矩

- **根目录白名单**：仓库根目录只允许放 `README.md`、`README.en.md`、`RELEASE_NOTES.md`、`LICENSE`、`AGENTS.md`（及 `.gitignore` / `.gitattributes` 等点文件）和 `Dragon Butler/`、`docs/`、`scripts/`、`tests/` 四个目录。**严禁往仓库根目录放交付报告、方案草稿、调研笔记、截图或临时文档。**
- **不要散放在 `docs/` 根目录**：所有报告、方案、截图均按类别进对应子目录，并更新各自索引，不留孤立文件。

| 类型 | 唯一存放位置 | 说明 |
| --- | --- | --- |
| 对外介绍 | `README.md`、`README.en.md` | 面向用户的当前功能与上手说明，**禁止堆砌版本开发日记** |
| 版本变更日志 | `RELEASE_NOTES.md` | 逐版变更唯一来源，发版脚本从中抽取 |
| 随包用户手册 | `docs/使用说明.txt` | 固定路径（打包脚本读取），UTF-16LE + BOM + CRLF |
| 规范文件 | `docs/SOUL.md`、`VOICE.md`、`PATTERNS.md`、`SIGNATURES.md` | 灵魂句保护、文案语气、交互清单、失败模式签名 |
| 长期路线图 | `docs/路线图.md` | 历史决策与演进取舍 |
| 版本交付报告 | `docs/reports/` | 命名 `V<版本号>_REPORT.md`（如 `V146_REPORT.md`），附 `README.md` 索引 |
| 专题方案 / 调研 | `docs/plans/` | 设计方案、需求候选与性能调研，附 `README.md` 索引 |
| 现行文档截图 | `docs/proto/` | `README*` / 现行报告引用的界面截图（受限尺寸豁免目录） |
| 插件样例 | `docs/plugin-samples/` | 外部扩展参考样例 |
| 归档旧物 | `docs/archive/` 与 `docs/archive/screenshots/` | 早期一次性报告与已退役旧截图，**只归档不新增现行文档** |

## 先读这三份，别自己发明语气

| 文件 | 管什么 |
|---|---|
| `docs/SOUL.md` | 六句灵魂句**禁改**。迁移（一字不动地搬位置）可以，改写（含改标点、改语气、加「优化一下」）不行 |
| `docs/VOICE.md` | 文案语气与反例。新写一句面向用户的话，先在这里找同类的写法 |
| `docs/PATTERNS.md` | 交互模式 checklist。新加按钮/入口前对一遍 |

动代码前还要过一遍失败模式签名：`docs/SIGNATURES.md`（S1–S15，逐条自查；交付报告里引用「命中 S#」以那份为准）。

## 对外文档写作要求

- 写 `README.md`、`README.en.md`、`docs/使用说明.txt` 时直接说明当前功能、真实操作与配置路径，语言平实克制。
- **不写版本回顾流水账**（「这一版没加功能……」「以前是……现在改成……」），**不堆破折号 `——`**、排比反差句（「不是 A，而是 B」）、鸡汤说教或营销词（「一站式」「极致体验」等）。
- 构建命令、探针参数、目录路径与支持工具列表必须与源码（`Program.cs`、`ToolCatalog.cs`、`HarvestLedger.cs` 等）实测一致。

## 编码与换行：改错就是整片乱码或整份 diff

| 文件 | 编码 | 换行 |
|---|---|---|
| `Dragon Butler/Dragon Butler.csproj` | **UTF-8 无 BOM**（不是 GBK！按 GBK 读会毁掉中文注释并让编译报 `Invalid character in the given encoding`） | CRLF |
| `docs/使用说明.txt` | **UTF-16LE + BOM** | CRLF |
| `README.md` / `README.en.md` / `RELEASE_NOTES.md` / `docs/reports/*_REPORT.md` / `docs/plans/*.md` / `docs/archive/*.md` / `.github/workflows/build.yml` | UTF-8 无 BOM | LF |
| 其余 `.cs` / `.xaml` | UTF-8 无 BOM | 多为 CRLF；`NoticeCenter.cs` / `NoticeWindow.xaml(.cs)` / `TodayWindow.xaml(.cs)` / `IncidentBundle.cs` / `MorningReport.cs` / `SchedulePlanner.cs` 等是 **LF**，改前用 `git ls-files --eol` 确认 |

写 `使用说明.txt` 用 `[System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UnicodeEncoding($false,$true)))` —— 第二个参数 `$true` 才是写 BOM，写成 `$false` 会丢 BOM。
`edit` / `apply_patch` 会把带 BOM 的文件写成无 BOM（`CustomThemeWindow.xaml.cs` 就是），改完用 `[System.IO.File]::WriteAllText($c, (New-Object System.Text.UTF8Encoding($true)))` 补回。

## 大文件：别整份读，按分区跳

`Dragon Butler/MainWindow.xaml.cs` 有 9000+ 行 / 52 个 `// ===== 分区名 =====` 注释，`grep` 分区名就能定位，不用从头读到尾。主要分区（行号随改动漂移，按名字搜）：初始化 / 队列卡 / 消息中心 / 配置组合 / 横向工具行 / 桌面网格 / 设置页二级化路由 / 日志视图 / 汇报页 / 开跑前体检 / 备份与恢复 / 控制 API 与局域网面板 / 全局热键 / 首屏主卡 / 通知设置卡 / 插件卡 / 外观卡 / 命令面板 / 检查更新。

`MainWindow.xaml` 3000+ 行，同理靠 `x:Name` 定位。

## 探针：改完必须跑，跑法有讲究

```powershell
dotnet build "Dragon Butler\Dragon Butler.csproj" -c Release -v q   # 要 0 警告 0 错误
dotnet build "tests\CoordinatorProbe\CoordinatorProbe.csproj" -c Release
$exe = "tests\CoordinatorProbe\bin\Release\net8.0-windows\CoordinatorProbe.exe"
& $exe <组名> "tests\StubTool\bin\Release\net8.0\StubTool.exe"
```

- 组名：`schedule resume bundle explain retry api weekly hotkey progress command dag groups bgiupdate harvest mcp uirules pager morning recap toolgrid logpattern migrate afterrun signals wrapup m12fix`
- **StubTool 传绝对路径**（相对路径会让 `ExecutablePath` 落到程序目录，`resume` / `dag` 必挂）
- `ui` 组出截图到 `%TEMP%\dragon-ui`：`ui rows` 带硬闸门（首屏摘要/一键开跑/添加行三块必须在视口内，超了退出码非零）；`ui all` 出全部图
- 探针不可达的私有逻辑，抽成 `public static` 纯函数再断言（`WebhookHub.ValidatePortText` 是范式），别为过探针放宽封装

## 发版：五步，一步都别省

1. `Dragon Butler/Dragon Butler.csproj` 三处版本号（`<Version>` / `<FileVersion>` / `<AssemblyVersion>`）
2. `RELEASE_NOTES.md` 顶部加 `## vX.Y[.Z] 标题` 一节；`README.md` / `README.en.md` / `docs/使用说明.txt` 同步版本行与 zip 名
3. `git add -A` → commit → `git push origin main` → `git tag -a vX.Y -m "…"` → `git push origin vX.Y`
4. `./scripts/package.ps1 -Variant all` 在本地出两个 zip 到 `发布\`，**重算 `发布\SHA256SUMS.txt`**，删掉上一版的两个 zip
5. CI 跑绿后 `gh release upload vX.Y <两个zip> --clobber` 用本地产物覆盖 CI 产物（两边字节不一致，以本地为准）

`发布\` 被 `.gitignore` 忽略，本地打包不会产生提交。**每次都做**：不发版的时代已经过去了。

## 这台机器上反复踩的坑

- **不要用 PowerShell 字符串拼接改大 XAML**。已经截断过两次文件。要改就用 `IndexOf` 找锚点 + `Substring` 三段拼接，且**先把中间段赋给变量**再拼（`$a.Substring(0,$i) + @(...) -join $nl + ...` 里 `-join` 优先级高于 `+`，会把前半段丢掉）。改完立刻 `XmlDocument.Load()` 验证并核对字符数。
- `$t.Replace($old,$new,[StringComparison]::Ordinal)` 在 PS 5.1 报「参数计数为 3」，用 `IndexOf` + `Substring`。
- `[int]$x` 在 PowerShell 里是**四舍五入**，C# 是截断。核对 `(int)` 语义要写 `[int][Math]::Truncate($x)`。
- `git push` 的输出走 stderr，pwsh 会把它染成 `NativeCommandError` 并让 `[exit code: 1]` 看起来像失败——看输出里有没有 `-> main` 再判断。
- 写 commit message 用 `[System.IO.File]::WriteAllText($p, $msg, (New-Object System.Text.UTF8Encoding($false)))`，`Out-File -Encoding UTF8` 会带 BOM 把多行压成一行。
- `gh release view --json ...isLatest` 这个字段不被当前 gh 版本接受，会报「无效的 JSON 基元: Unknown」。
- 读 `.gitignore` 这类带中文的文件，`Get-Content` 在 pwsh 控制台显示乱码是**控制台按 GBK 显示**，文件本身通常是 UTF-8，别急着转码。
- 控制台中文乱码不等于文件坏了：先用 `[System.IO.File]::ReadAllText` 确认一次。
- **判断「没人引用」必须全库扫 `.md` / `.txt` / `.cs`，包括 `docs/reports/` 下各版本交付报告里的「产出物表」**。v1.45.2 清图时只扫了现行文档，漏掉 8 张有登记的产出物（差点删掉）。删东西之前先打包备份到仓库外——这次能补回全靠它。

## 两条硬规矩

- **提交被钩子拒了**：照报错把那个文件按功能拆开再提交，别加 `--no-verify`。写新函数、新规则之前，先搜一遍项目里有没有现成的。
- **改到一半异常就停**：立刻 `git checkout -- <文件>` 恢复，重来。不要在可疑状态上继续叠加改动。
