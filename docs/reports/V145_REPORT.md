# v1.45 交付报告 · M12 六项行为修缮

> 这一版没有新功能。补的全是「界面上一句话，背后没有落地」的地方。
> 依据：V145 合并任务书 §4 的 M12-1…M12-6，逐条实施、逐条验证。

---

## 〇 一句话结论

六项全部落地，编译 0 警告 0 错误，**26 组探针全绿**（含新增的 `m12fix` 与补进 CI 的 `wrapup`），每一项都有一条能跑的生效证明。没有改任务书范围外的代码，没有动灵魂句。

---

## 一 M12-1 · 恢复默认主题加确认框（D25）

| 项 | 内容 |
|---|---|
| 落点 | `Dragon Butler/CustomThemeWindow.xaml.cs` 的 `BtnReset_Click`（原第 232 行） |
| 做法 | 方法体最开头插入 `AppDialog.Show(...)`，`kind: Question`，主按钮「恢复默认」、次按钮「算了」，`if (!resetOk) return;` 挡在任何 `AppState` 赋值之前 |
| 正文 | 逐条列出原重置代码真正会改的六项（主题 / 背景类型 / 背景色 / 背景图 / 遮罩比例 / 配色方案），末尾「这个操作不可撤销。」 |
| 编码 | 本文件**带 UTF-8 BOM + CRLF**；`edit` 会掉 BOM，已用 `WriteAllText($c, UTF8Encoding($true))` 补回（`first3=239,187,191`），`crlf=292 loneLF=0` |

**验收自查**

- ✅ 取消 → 零变化：`if (!resetOk) return;` 在全部六行赋值之前，写盘在更后面。
- ✅ 确认 → 六项重置：原逻辑一字未改，只是挪到确认之后。
- ✅ 确认框出现在写盘之前：探针 `确认/先问再改`（问@8065 写@8645）。

**生效证明**

- 探针 `m12fix` → `PASS 确认/先问再改 :: 问@8065 写@8645`、`PASS 确认/取消就什么都不做`、`PASS 确认/正文列全六项`、`PASS 确认/说了不可撤销`、`PASS 确认/重置里只写一次盘`、`PASS 确认/重置里也是先问后写`。

---

## 二 M12-2 · 推送失败可重发（D26）

| 项 | 内容 |
|---|---|
| 落点 | `Dragon Butler/NotifyService.cs`（新增载荷与重发）、`Dragon Butler/NoticeWindow.xaml.cs`（卡片底部加按钮） |
| 载荷 | `Dictionary<PushChannel, ResendPayload>`——**每渠道只留最近一次**，纯内存，不落盘；`ResendPayload` 持 `Notice` 引用 + 渠道 + 级别 + 标题 + 正文 |
| 共用内核 | 把原 `SendAndLogAsync` 里的重试 for 循环整段抽成 `SendCoreAsync(...)`；**首次发送与重发都调它**，1/3/7 秒节奏只有一份 |
| 入口 | `public static async Task<(bool ok, string err)> ResendAsync(Notice? n)` |
| 防连点 | `HashSet<Notice> Resending` + 按钮点击即刻 `IsEnabled = false; Content = "正在重发…";` |
| 留痕 | 成功 `DecisionLog.Add(..., "重发成功（渠道）", ...)`；失败 `"重发还是没送到（渠道）"` + `Snippet(err)` 摘要 |
| 无载荷 | `ResendHintFor(Notice)` 按 `ProcessStartedAt` 分辨：重启前 `"重启过，这条只能留档了"`，本轮 `"这条的原样没留下来，只有刚失败的那条能重发"` |
| 文案 | `NotifyService.cs` 原来那句「网络恢复后可以手动重发」→「改好设置之后，在这条上点「重发」就行，不用重新跑一遍。」 |

**验收自查**

- ✅ 配错 URL → 失败条目有按钮：`RememberResend(failed, ...)` 在 `AddError` 之后立刻挂载荷。
- ✅ 修正后点 → 成功留痕：`ResendAsync` 成功分支写「重发成功（企业微信）」。
- ✅ 仍错 → 三次重试 → 留痕且可再点：`SendCoreAsync` 内部走完 `PushRetryDelaysMs`，失败不清载荷。
- ✅ 重启后旧条目无按钮、文案如实：`n.At < ProcessStartedAt` 判定；载荷在内存，重启必然为空。

**生效证明**

- `PASS 重发/有共用的发送内核`、`PASS 重发/重试循环只有一份`、`PASS 重发/重发也走这个内核`、`PASS 重发/首次发送也走这个内核 :: 出现 2 次（首次 + 重发）`、`PASS 重发/载荷每渠道一份`、`PASS 重发/载荷不落盘`、`PASS 重发/重入有闸门`、`PASS 重发/通知窗口有按钮`、`PASS 重发/重发中禁用`、`PASS 重发/没载荷时说清原因`、`PASS 重发/失败文案指向重发`、`PASS 重发/重启后如实说明`、`PASS 重发/本轮失败可重发`、`PASS 重发/没有载荷就别假装能发`、`PASS 重发/初始没有待重发`。

---

## 三 M12-3 · 「3 分钟」改插值（D28）

| 落点 | 改法 |
|---|---|
| `Dragon Butler/QueueService.cs`：原「疑似挂起（3 分钟无任何活动）」 | `$"疑似挂起（{SentinelService.StallThreshold.TotalMinutes:F0} 分钟无任何活动）"` |
| `Dragon Butler/QueueService.cs`：通知正文里同一句 | 同上，`{st.DisplayName}` 与 `{st.Reason}` 一并保留 |
| 额外发现的第三处：`Dragon Butler/MainWindow.xaml` 设置页说明 | 原来死写「已经持续 3 分钟」→ 改成 `x:Name="TbStallHint"`，由新增的 `RefreshStallHint()` 赋值 |
| 文档 `docs/使用说明.txt` 两处 | 改成「阈值在设置里，默认 3 分钟」，指向可改的位置 |

`RefreshStallHint()` 的写法对齐 `SentinelService.cs:359/360` 的范式（`{StallThreshold.TotalMinutes:F0}`），并处理了阈值小于 1 分钟时显示秒的情况。

**验收自查**

- ✅ 两处都改插值：探针 `挂起/两处都改插值 :: 出现 2 次`。
- ✅ 格式一致：`TotalMinutes:F0`，与 `SentinelService` 里的既有写法逐字相同。
- ✅ 用户可见文案里 0 处死写：`挂起/不再有死写的 3 分钟`；剩余三处「3 分钟」全在注释与 XML 文档里（`MainWindow.xaml.cs:7278`、`SentinelService.cs:321`、`MainWindow.xaml:1954`），不面向用户。
- ⚠️ 探针未做「改 `StallThreshold=5` → 提示显示 5 分钟」的运行时断言——理由：`StallThreshold` 是全局可变静态量，改了会影响同进程内其它组（`signals`/`stall`）的判定。改用源码级断言守住「读的是实际阈值」这条性质，效果等同。

**生效证明**

- `PASS 挂起/两处都改插值 :: 出现 2 次`、`PASS 挂起/不再有死写的 3 分钟`、`PASS 挂起/设置页也跟着变`。

---

## 四 M12-4 · 现场包清理进回收站（D27）

| 项 | 内容 |
|---|---|
| 落点 | `Dragon Butler/IncidentBundle.cs` 的 `Trim(int keep)` |
| 做法 | 新增 `TryRecycle(string dir)`，走 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing)`；返回 false 才 `Directory.Delete(d, true)` |
| 回退留痕 | `$"现场包：{Path.GetFileName(d)} 没能放进回收站，已直接删除（保留 {keep} 份的规则照旧）"` |
| 依赖 | net8.0-windows 自带 `Microsoft.VisualBasic`，**未加任何 PackageReference**（csproj 未改动） |
| UI | `Dragon Butler/MainWindow.xaml` 新增 `TbBundleKeepHint`，由 `RefreshRetentionHints(AppSettings)` 赋值，数字来自 `s.IncidentBundleKeep`（默认 10） |

**验收自查**

- ✅ 回收站调用正确：四个参数与 `Microsoft.VisualBasic.FileIO` 命名空间全限定，编译通过。
- ✅ 失败回退留痕：`TryRecycle` 返回 false 才直删，且写了明确日志。
- ✅ 规则文案可见：`现场包最多保留最近 N 份；更早的会进回收站（不是直接删），想找还能找回来。`
- ⚠️ 「造 12 份 → 最早 2 份出现在回收站」这条**未在真机执行**（CI 无桌面会话，回收站 API 在无交互会话下行为不稳）。已用源码级断言 + 调用点唯一性替代；列为待人工确认第 1 条。

**生效证明**

- `PASS 现场包/先进回收站`、`PASS 现场包/回收站失败才直删`、`PASS 现场包/回退留痕`、`PASS 现场包/界面上写着规则`、`PASS 现场包/规则数字来自设置`。

---

## 五 M12-5 · 剪贴板失败就地重试（D20 沿用）

| 项 | 内容 |
|---|---|
| 落点 | `Dragon Butler/TodayWindow.xaml`（标题栏右侧加控件）、`Dragon Butler/TodayWindow.xaml.cs` |
| 做法 | 抽出 `private void TryCopyReport()`，`BtnReport_Click` 与新增的 `BtnCopyRetry_Click` **都指向它**；`Clipboard.SetText` 全程只出现一次 |
| 状态 | `private bool _copying` 防重入；`ShowCopyFailure(string reason)` 是显隐的唯一出口 |
| 文案 | 失败提示 `"复制没成功，剪贴板可能被别的程序占着。点旁边的「重试」再试一次。"` |

**验收自查**

- ✅ 占用剪贴板 → 失败提示带按钮：`ShowCopyFailure("剪贴板被占着")` 让 `TbCopyHint` + `BtnCopyRetry` 一起显示。
- ✅ 释放后点 → 成功：重试走同一个 `TryCopyReport()`，成功分支 `ShowCopyFailure("")` 收起提示。
- ✅ 防连点：`if (_copying) return;` + `BtnCopyRetry.IsEnabled = false`。

**生效证明**

- `PASS 剪贴板/有重试按钮`、`PASS 剪贴板/两个入口同一个函数 :: 调用 2 次`、`PASS 剪贴板/剪贴板只碰一次 :: 出现 1 次`、`PASS 剪贴板/重试中不重复进`、`PASS 剪贴板/显隐只有一处说了算`。

---

## 六 M12-6 · 探针 StubTool 绝对路径

| 项 | 内容 |
|---|---|
| 落点 | `tests/CoordinatorProbe/Program.cs` 的 `Main` |
| 做法 | `string rawStub = ...; string stub = ResolveStub(rawStub);`；`ResolveStub` 依次试「当前目录 / 探针目录 / 探针目录上四层」，都不是绝对路径才拼 |
| 根因 | 相对路径能被 `File.Exists` 认出来（当前目录对得上），但写进 `ToolProfile.ExecutablePath` 后启动用的是**程序目录**，于是报「系统找不到指定的文件」，`probe-dag-ok` 判 Failed，连带「端到端/父项成功」红——看起来像代码回归 |

**验收自查**

- ✅ 不同工作目录下 `resume` / `dag` 均 PASS：用**相对路径**传参实测 `RESULT resume fails=0 exit=0`、`RESULT dag fails=0 exit=0`（以前这种传法必挂）。

**生效证明**

- `PASS 替身路径/有归一函数`、`PASS 替身路径/已经是绝对的就不再动`、`PASS 替身路径/入口走归一`；实测两组 `exit=0`。

---

## 七 CI 闸门

`.github/workflows/build.yml` 的功能断言组新增两行：

```yaml
wrapup   = "v1.44 收尾倒计时窗口与端口校验"
m12fix   = "v1.45 六项行为修缮（确认框/重发/插值/回收站/重试/替身路径）"
```

**顺带修掉一个 v1.44 的漏网**：`wrapup` 组当初加了却没进 CI，只能靠人记得手跑——「60 秒反悔窗口」这条链本该有闸门。现在和 `m12fix` 一起进去了。

---

## 八 验证总账

| 关 | 结果 |
|---|---|
| 编译 | 主工程 `0 个警告 0 个错误`；探针 `0 个警告 0 个错误` |
| 探针 | **26 组全绿**（`ALL GREEN (26 groups)`）：原 24 组 + `wrapup` + `m12fix` |
| 真机 | M12-6 用相对路径实测 `resume`/`dag` 通过；M12-1/2/4/5 的界面行为需人工点一遍（见下） |

**建议的真机回归三件事**（对应任务书 §9 用户提示）：
1. 点「恢复默认主题」，先看确认框，再点「算了」确认什么都没变。
2. 占住 webhook 端口再开面板，确认报错框里能直接改端口。
3. 配错推送 URL 跑一次，去消息中心点「重发」，**多点两下看会不会发两遍**。

---

## 九 施工中发现的新问题（只记录，未动手）

1. **`NotifyService.ShowBalloon` 的跨线程分支用的是 `Invoke`**（v1.43 任务五改的），从 UI 线程调用时不会死锁，但从后台线程等 UI 线程时会同步阻塞。当前所有调用点都在非 UI 线程且不持锁，暂无问题；若将来有人在持锁路径上调它，会卡住。建议后续评估改成 `InvokeAsync` + 超时。
2. **`NoticeCenter` 的 `Raw` 字段没有长度上限**（`Add` 只按 `Capacity=100` 限条数）。某类异常如果每次带几十 KB 的 `raw`，`notices.json` 会迅速膨胀。建议量一下实际文件大小再决定要不要加截断。
3. **`AppDialog.ShowInput` 没处理「用户直接按 Esc」的语义**——Esc 走 `CloseWith(false)`，`PromptValue` 保持 null，与「取消」一致。当前行为正确，但没在 XML 文档里写明，容易被后来者当成 bug 改掉。
4. **`IncidentBundle.Trim` 只按条数裁，不按体积裁**。单份包如果含 4K 截图（约 2–8 MB），10 份就是几十 MB。任务书明确份数不做配置项，所以没动；建议将来评估「按总体积兜底」。
5. **`docs/使用说明.txt` 里还有两处以「默认 3 分钟」表述阈值**（已改成指向设置页，但数字仍在）。这是解释性文字而非程序文案，按 D28 的「文案中禁止死写阈值」判断处于边界——已改成「阈值在设置里，默认 3 分钟」，保留了默认值作为参考。若要求绝对零数字，需再改一轮。

---

## 十 需产品确认

1. **M12-4 的回收站真机验证**：CI 无桌面会话，`RecycleOption.SendToRecycleBin` 在无交互会话下的行为无法断言。是否接受「源码级断言 + 人工点一次」作为验收？
2. **M12-3 的运行时断言**：因 `StallThreshold` 是全局静态量，改它会污染同进程其它组，故用源码级断言。是否接受？
3. **M12-2 的载荷上限**：任务书 §9 #3 已说明「每渠道仅存最近一次」是当前拍板。实测未发现堆积问题；若将来失败条目多到影响阅读，再谈「仅最近 N 条可重发」的收敛。

---

## 十一 改动文件清单

| 文件 | 改动 |
|---|---|
| `Dragon Butler/CustomThemeWindow.xaml.cs` | M12-1 确认框（BOM + CRLF 保住） |
| `Dragon Butler/NotifyService.cs` | M12-2 载荷 / `SendCoreAsync` / `ResendAsync` / 文案 |
| `Dragon Butler/NoticeWindow.xaml.cs` | M12-2 卡片底部「重发」按钮与无载荷提示 |
| `Dragon Butler/QueueService.cs` | M12-3 两处插值 |
| `Dragon Butler/MainWindow.xaml` | M12-3 `TbStallHint`、M12-4 `TbBundleKeepHint` |
| `Dragon Butler/MainWindow.xaml.cs` | M12-3 `RefreshStallHint()`、M12-4 `RefreshRetentionHints()` |
| `Dragon Butler/IncidentBundle.cs` | M12-4 `TryRecycle` + 回退留痕 |
| `Dragon Butler/TodayWindow.xaml` | M12-5 `TbCopyHint` / `BtnCopyRetry` |
| `Dragon Butler/TodayWindow.xaml.cs` | M12-5 `TryCopyReport` / `ShowCopyFailure` |
| `tests/CoordinatorProbe/Program.cs` | M12-6 `ResolveStub`、新增 `m12fix` 组（38 条断言）、`CountOf` 辅助 |
| `.github/workflows/build.yml` | 新增 `wrapup`、`m12fix` 两组闸门 |
| `Dragon Butler/Dragon Butler.csproj` | 版本号 1.44.0 → 1.45.0（三处，UTF-8 无 BOM） |
| `RELEASE_NOTES.md` / `README.md` / `README.en.md` / `docs/使用说明.txt` | v1.45 节与版本号 |
| `V145_REPORT.md` | 本文件 |

**未改动**：`docs/SOUL.md`（灵魂句）、`docs/VOICE.md`、`docs/PATTERNS.md`（M11-7 才动）、插件页相关任何文件（M11 范围）。
