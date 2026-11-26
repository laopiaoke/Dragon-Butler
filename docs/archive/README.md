# 归档：2026-10 的一次性报告

这个目录放的是项目早期四份一次性验收报告，已经从仓库根目录搬到这里：

| 文件 | 是什么 |
|---|---|
| `BUILD_REPORT.md` | 打包脚本与发布流程的第一份说明（v1.3 前后） |
| `PHASE1_REPORT.md` | 第一阶段施工记录 |
| `PHASE2_REPORT.md` | 第二阶段施工记录（队列/重试/中止/超时/悬置/锚点全用 StubTool 跑过） |
| `HOTFIX0_REPORT.md` | 早期一次紧急修复的记录 |

## 为什么搬走而不是删掉

它们记录了当时如何验收，事后追查「某条规则为什么这样定」仍有参考价值；但内容已过时，继续留在根目录会增加每次排查的噪音。版本交付报告现统一放在 [`docs/reports/`](../reports/README.md)（`V141_REPORT.md` 起）。

## 引用与截图归档

- `V1421_REPORT.md` 与 `V1451_REPORT.md`（均在 `docs/reports/`）中提到的 `BUILD_REPORT.md` / `PHASE2_REPORT.md` 等名称，均指本目录下的同名文件。
- `screenshots/` 子目录归档了 v1.12–v1.15 期间的 19 张早期文档截图（含 `HOTFIX0_REPORT.md` 中提到的 `clipboard_fallback.png`）。历史报告正文保留原样，路径以本索引为准。
