# 热修 0 交付报告：剪贴板复制失败（v1.3 前置）

> 范围：仅修"复制地址偶发失败"。不含 Phase 2 任何内容（未创建任何 Phase 2 文件）。
> 版本：随 v1.3.0 发布，本次不单独发版。程序集版本仍为 1.2.0（Phase 2 收尾统一升 1.3.0）。

## 一、结论

- 现象根因：Windows 剪贴板独占，剪贴板管理器 / 输入法 / 截图工具瞬时占用 → 写入返回 `CLIPBRD_E_CANT_OPEN (0x800401D0)`，属环境竞争。
- 修复：新增 `ClipboardHelper.TrySetText`（8 次 × 125ms 同步重试）+ 失败后弹出只读可全选文本框的"手动复制"兜底；UI 不再出现任何错误码。
- 验证：占用期间写入失败并在 **962ms** 返回 false；释放后写入成功且内容正确；兜底弹窗外观与文案已截图确认。

## 二、文件级变更清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `Dragon Butler/ClipboardHelper.cs` | 新增 | 剪贴板唯一入口：`TrySetText`（重试层）+ `ShowManualCopyFallback`（兜底层）；Win32 `OpenClipboard/EmptyClipboard/SetClipboardData` 写 `CF_UNICODETEXT` |
| `Dragon Butler/AppDialog.xaml` | 修改（增量） | 新增 `DlgCopyBox` 样式与 `CopyBox` 文本框（只读、可全选、默认隐藏）；不影响既有调用 |
| `Dragon Butler/AppDialog.xaml.cs` | 修改（增量） | `Show/Create/Setup` 末尾新增可选参数 `copyText`；传入时显示文本框并在加载后聚焦 + 全选 |
| `Dragon Butler/MainWindow.xaml.cs` | 修改 | 两处 `Clipboard.SetText` 改为 `ClipboardHelper.TrySetText`，失败走 `ShowManualCopyFallback`；删除 `✗ 复制失败：{ex.Message}`（HRESULT 泄漏点） |
| `tests/StubClipboardHolder/` | 新增 | 剪贴板占用替身：隐藏窗口 + `OpenClipboard(hwnd)` 保持 N 秒 |
| `tests/CoordinatorProbe/Program.cs` | 修改 | 新增 `clipboard`（占用/空闲对照）、`cliptry`（单次尝试）、`clipdialog`（兜底弹窗外观）三种模式 |
| `docs/clipboard_fallback.png` | 新增 | 兜底弹窗实拍截图（验收证据） |

调用点收敛核对：`grep "Clipboard\."` 在产品代码中仅剩 `ClipboardHelper.cs` 的注释，无任何直接调用。

## 三、实现要点

**重试层**（`TrySetText`）
- 8 次尝试 × 125ms 间隔，7 次等待合计 875ms，实测总阻塞 962ms（含系统调用开销），满足"≤1 秒"。
- 同步阻塞，未异步化（避免 UI 线程之外的 COM/剪贴板单元问题）。
- 第 2 次起成功会写日志"复制成功：第 N 次尝试写入"；8 次全失败写"复制地址失败：重试 8 次仍被占用（0x800401D0）"。

**实现选择（需要你知悉的一处技术偏差）**
原计划直接对 `Clipboard.SetText` 做外层重试。实测 .NET 8 的 WPF `Clipboard` 把 **10 次 × 100ms 的内部重试写死在实现里**（公开 API 无 `retryTimes` 重载，4 参数版本为 internal），剪贴板被占用时单次调用就阻塞约 1.2 秒，外层再重试 8 次要 8 秒以上，与"≤1 秒"直接冲突。
因此改为直接调用 Win32 剪贴板 API（`OpenClipboard` → `EmptyClipboard` → `SetClipboardData(CF_UNICODETEXT)`），这样才能精确控制节奏；写入格式与 `Clipboard.SetText` 等价（记事本 / 浏览器 / 终端均可粘贴，`Clipboard.GetText()` 可读回）。行为验收项全部满足，仅"捕获 ExternalException"这一实现细节换成了"OpenClipboard 返回 false"。

**兜底层**（`ShowManualCopyFallback`）
- `AppDialog` 弹出，正文"剪贴板被其他程序暂时占用，请手动复制（Ctrl+C）。"
- 下方只读文本框显示完整 URL/路径，弹窗打开即聚焦并全选，按一次 Ctrl+C 即可。
- 技术细节只进 `全部日志`，UI 任何位置不出现 `OpenClipboard` / `0x800401D0`。

## 四、自查清单

| 验收项 | 结果 | 如何验证 |
|---|---|---|
| Build 0 error 0 warning | ✅ | `dotnet build "Dragon Butler/Dragon Butler.csproj" -c Release` → 0/0；三个测试工程同为 0/0 |
| 占用期间走兜底且 UI 无 HRESULT | ✅ | `CoordinatorProbe clipboard <holder>` → `CLIP busy ok=False elapsed=962ms`；兜底弹窗截图见 `docs/clipboard_fallback.png`（无任何错误码） |
| 空闲回归复制成功 | ✅ | 同一命令后半段 `CLIP free ok=True text=http://127.0.0.1:18235/webhook` |
| 提交并 push | ✅ | 提交 `f3b846a`，已推送 `origin/main`（本地 HEAD = 远端 `f3b846a`） |

复跑命令（在仓库根）：

```bash
dotnet build "tests/StubClipboardHolder/StubClipboardHolder.csproj" -c Release
dotnet build "tests/CoordinatorProbe/CoordinatorProbe.csproj" -c Release
tests/CoordinatorProbe/bin/Release/net8.0-windows/CoordinatorProbe.exe clipboard \
  "%CD%\tests\StubClipboardHolder\bin\Release\net8.0-windows\StubClipboardHolder.exe"
# 预期：CLIP busy ok=False elapsed≈900ms / CLIP free ok=True text=http://127.0.0.1:18235/webhook
```

## 五、推送记录

- 提交：`f3b846a fix(clipboard): 复制地址增加重试与手动兜底，移除 UI 技术细节泄露`
- 推送：`origin/main 5006946..f3b846a`（含 Phase 1 的 `83db2f1`），远端与本地 HEAD 一致
- 工作区提交后为空（无未跟踪残留）

## 六、待人工确认项

1. **真机点击回归**：自动截图来自探针宿主（同一个 `AppDialog` + 同一个 `ClipboardHelper.ShowManualCopyFallback`），未在主窗口里真实点击"复制地址"。建议你手动跑一次：占用期间点复制 → 应弹兜底窗；关闭占用后点复制 → 应显示"✓ 已复制"。
2. **Win32 直写剪贴板**：如你的常用软件（微信 / QQ / 某些编辑器）粘贴异常，请反馈，可改回 OLE 路径并接受约 1 秒阻塞。
3. **`--seconds` 替身时长**：默认 5 秒，测试脚本里用 8 秒；如需更长的占用窗口可自行调整。
