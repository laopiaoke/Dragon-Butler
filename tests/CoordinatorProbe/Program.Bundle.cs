using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using DragonButler;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。
internal static partial class Program
{
    /// <summary>
    /// v1.6 / F3「失败现场包」验证：真的落一份包，然后检查它有什么、缺什么、泄没泄密。
    /// 用假凭据填进设置，再断言 env.json 里搜不到——这才是脱敏该验的东西。
    /// </summary>
    private static int RunBundle()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        const string FakeToken = "ZZTOKEN-should-never-appear-1234567890";
        const string FakePass = "ZZPASS-should-never-appear-0987654321";
        const string FakeMail = "someone@example.com";

        var s = AppState.Settings;
        string oldToken = s.NotifyTelegramToken, oldPass = s.NotifyEmailPass, oldTo = s.NotifyEmailTo;
        bool oldEnabled = s.IncidentBundleEnabled;
        try
        {
            s.IncidentBundleEnabled = true;
            s.NotifyTelegramToken = FakeToken;
            s.NotifyEmailPass = FakePass;
            s.NotifyEmailTo = FakeMail;

            var profile = new ToolProfile { ToolId = "probe-bundle", DisplayName = "探针假工具" };
            string? dir = IncidentBundle.Capture("探针：模拟一次失败", profile, includeScreenshot: false);

            Check("返回目录路径", !string.IsNullOrEmpty(dir), dir ?? "(null)");
            bool exists = dir != null && Directory.Exists(dir);
            Check("目录真的建出来了", exists);
            if (dir == null || !exists)
            {
                Console.WriteLine("RESULT bundle fails=" + (fails + 2));
                return 1;
            }

            Check("有 env.json", File.Exists(Path.Combine(dir, "env.json")));
            Check("有 log.txt", File.Exists(Path.Combine(dir, "log.txt")), "日志目录=" + LogService.Dir + " 今日文件=" + File.Exists(Path.Combine(LogService.Dir, DateTime.Now.ToString("yyyy-MM-dd") + "_运行.log")));

            string env = File.ReadAllText(Path.Combine(dir, "env.json"));
            Check("env 里有失败原因", env.Contains("模拟一次失败"));
            Check("env 里有档案信息", env.Contains("probe-bundle"));
            Check("env 里有进程列表", env.Contains("\"进程\""));
            // 脱敏三连——这三个串一个都不许出现
            Check("不泄露 token", !env.Contains(FakeToken));
            Check("不泄露密码", !env.Contains(FakePass));
            Check("不泄露邮箱", !env.Contains(FakeMail));
            Check("敏感字段确实被替换", env.Contains(IncidentBundle.Redacted));

            // ===== v1.43 / 任务六：结构化脱敏与采集完整性 =====
            //
            // 这一段的每一条都对应任务书六里点名的故障：
            // 以前 Tools 里每个工具都退化成 "DragonButler.ToolProfile"（看着有内容，其实没内容），
            // 路径与启动参数明文进包（用户名、令牌都在里面），
            // 而端口和热键反倒被误盖成 ***（排障最需要的两格反而看不见）。

            // 纯函数层：谁算敏感、谁不算
            Check("脱敏/端口不算敏感", !IncidentBundle.IsSensitiveKey("BgiWebhookPort") && !IncidentBundle.IsSensitiveKey("NotifyEmailPort"));
            Check("脱敏/热键不算敏感", !IncidentBundle.IsSensitiveKey("HotkeyStart") && !IncidentBundle.IsSensitiveKey("HotkeyEnabled"));
            Check("脱敏/账号算敏感", IncidentBundle.IsSensitiveKey("NotifyEmailUser") && IncidentBundle.IsSensitiveKey("NotifyTelegramChat"));
            Check("脱敏/路径算敏感", IncidentBundle.IsSensitiveKey("BgiPath") && IncidentBundle.IsSensitiveKey("CustomBgImage") && IncidentBundle.IsSensitiveKey("AfterRunAppPath"));
            Check("脱敏/参数算敏感", IncidentBundle.IsSensitiveKey("LaunchArguments") || IncidentBundle.IsSensitiveKey("Arguments"));

            string rp = IncidentBundle.RedactPath(@"C:\Users\someone-zz\Games\BetterGI\BetterGI.exe");
            Check("路径只留文件名", rp.EndsWith("BetterGI.exe", StringComparison.Ordinal) && !rp.Contains("someone-zz"), rp);

            string rt = IncidentBundle.RedactText("--startOneDragon --token=ZZARGS-abc123 --workdir C:\\Users\\bob-zz\\BGI");
            Check("自由文本盖令牌", !rt.Contains("ZZARGS-abc123"), rt);
            Check("自由文本盖用户名", !rt.Contains("bob-zz"), rt);
            Check("自由文本留结构", rt.Contains("--startOneDragon"), rt);

            // 结构层：Tools 要摊开成真字段，而不是类型名
            var oldTools = s.Tools;
            string oldHotkey = s.HotkeyStart;
            int oldPort = s.BgiWebhookPort;
            try
            {
                s.HotkeyStart = "Ctrl+Alt+Q";
                s.BgiWebhookPort = 39271;
                s.Tools = new Dictionary<string, ToolProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["probe-tool"] = new ToolProfile
                    {
                        ToolId = "probe-tool",
                        DisplayName = "探针工具",
                        ExecutablePath = @"C:\Users\someone-zz\Games\BetterGI\BetterGI.exe",
                        LaunchArguments = "--startOneDragon --token=ZZARGS-token-445566",
                        CustomData = new Dictionary<string, string>
                        {
                            ["customFilePath"] = @"C:\Users\someone-zz\secret.txt",
                            ["apiToken"] = "ZZCUSTOM-token-abcdef",
                        },
                    },
                };

                string? dir2 = IncidentBundle.Capture("探针：结构化脱敏", null, includeScreenshot: true);
                Check("结构化/包生成成功", dir2 != null && Directory.Exists(dir2));
                if (dir2 != null && Directory.Exists(dir2))
                {
                    string env2 = File.ReadAllText(Path.Combine(dir2, "env.json"));
                    Check("结构化/工具不再是类型名", !env2.Contains("DragonButler.ToolProfile", StringComparison.Ordinal));
                    Check("结构化/能看到工具字段", env2.Contains("\"LaunchArguments\"") && env2.Contains("\"ExecutablePath\""), "");
                    Check("结构化/用户名不漏", !env2.Contains("someone-zz", StringComparison.Ordinal));
                    Check("结构化/启动参数里的令牌不漏", !env2.Contains("ZZARGS-token-445566", StringComparison.Ordinal));
                    Check("结构化/CustomData 里的令牌不漏", !env2.Contains("ZZCUSTOM-token-abcdef", StringComparison.Ordinal));
                    Check("结构化/启动参数保留结构", env2.Contains("--startOneDragon", StringComparison.Ordinal));
                    Check("结构化/端口保留", env2.Contains("39271", StringComparison.Ordinal));
                    Check("结构化/热键保留", env2.Contains("Ctrl+Alt+Q", StringComparison.Ordinal));
                    Check("完整性/包里写着采集结果", env2.Contains("\"采集结果\"", StringComparison.Ordinal));
                    Check("完整性/截图这块有交代", env2.Contains("screen.png", StringComparison.Ordinal));
                    Check("完整性/日志这块有交代", env2.Contains("log.txt", StringComparison.Ordinal));
                    try { Directory.Delete(dir2, true); } catch { }
                }
                else
                {
                    fails += 9;
                }
            }
            finally
            {
                s.Tools = oldTools;
                s.HotkeyStart = oldHotkey;
                s.BgiWebhookPort = oldPort;
            }

            // 截图这一块要能自证：CI 里没有桌面会话，抓不到很正常，
            // 但包里必须写明为什么没有——「没采到」和「没发生」不能长得一样。
            try
            {
                var cap = ScreenshotMonitor.CaptureEx();
                bool explained = cap.Buf != null
                    ? (cap.Scope.Length > 0 && cap.W > 0 && cap.H > 0)
                    : !string.IsNullOrEmpty(cap.Error);
                Check("截图/结果与说明对得上", explained, cap.Buf != null ? cap.Scope : (cap.Error ?? "(空)"));
            }
            catch (Exception ex)
            {
                Check("截图/结果与说明对得上", false, ex.Message);
            }

            // 保留策略：多抓几份，检查只留最新 N 份
            var dirsBefore = Directory.GetDirectories(IncidentBundle.Root).Length;
            Check("Trim 后份数不超过配置值", dirsBefore <= Math.Max(1, s.IncidentBundleKeep) + 2, "before=" + dirsBefore);

            // 关掉开关就不该再产出
            s.IncidentBundleEnabled = false;
            Check("关掉开关返回 null", IncidentBundle.Capture("探针：开关关闭", profile, includeScreenshot: false) == null);

            try { Directory.Delete(dir, true); } catch { }
        }
        finally
        {
            s.NotifyTelegramToken = oldToken;
            s.NotifyEmailPass = oldPass;
            s.NotifyEmailTo = oldTo;
            s.IncidentBundleEnabled = oldEnabled;
        }

        Console.WriteLine("RESULT bundle fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>配置迁移：写一份 v1.2 形态的 settings.json，验证迁移结果与备份。</summary>
    private static int Migrate()
    {
        // v1.42.1：settings.json 的位置早就搬到「所有配置」下了（Paths.ConfigRoot），
        // 这里再往 config\ 写，LoadSettings 根本看不见，跑出来的永远是上一轮的残留。
        // 直接问 AppState.SettingsPath，位置只有一处定义。
        string path = AppState.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 先删旧的：不删的话夹具读不到时会静默沿用上一轮留下的 v17 状态，断言全是假的。
        // tools/bettergi.json 也会盖掉夹具：上一次渲染探针往它里面写过假 BGI 路径，
        // 不一起清掉，读出来的 bgiPath / 旧镜像全是上一轮的。
        string staleProfile = Path.Combine(Paths.ToolsDir, "bettergi.json");
        if (File.Exists(staleProfile)) File.Delete(staleProfile);
        if (File.Exists(path)) File.Delete(path);
        string backup = path + ".bak.v12";
        if (File.Exists(backup)) File.Delete(backup);

        File.WriteAllText(path, """
        {
          "BgiPath": "D:\\BetterGI\\BetterGI.exe",
          "DragonConfigName": "日常一条龙",
          "SelectedGroups": ["日常", "挖矿"],
          "Queue": [ { "ToolId": "bettergi", "Enabled": true } ],
          "ScheduleEnabled": true,
          "ScheduleHour": 8,
          "Theme": "Dark"
        }
        """, Encoding.UTF8);

        AppState.LoadSettings();
        ProfileRegistry.Initialize();

        var bgi = ProfileRegistry.BetterGi;
        Console.WriteLine("MIGRATE version=" + AppState.Settings.Version);
        Console.WriteLine("MIGRATE bgiPath=" + bgi.ExecutablePath);
        Console.WriteLine("MIGRATE configName=" + bgi.CustomData.GetValueOrDefault("dragonConfigName"));
        // v1.42 / M9-2（D16）：勾选单源化之后，旧镜像键应当不再被写入；
        // 迁移结果看的是 QueueEntry.Groups 有没有接住旧全局勾选。
        Console.WriteLine("MIGRATE groups=" + string.Join("|", (AppState.Settings.Queue ?? new List<QueueEntry>())
            .FirstOrDefault(e => string.Equals(e.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase))?.Groups ?? new List<string>()));
        Console.WriteLine("MIGRATE legacyMirror=" + (bgi.CustomData.TryGetValue("selectedGroups", out string? mirror) && !string.IsNullOrEmpty(mirror) ? mirror : "empty"));
        Console.WriteLine("MIGRATE schedule=" + AppState.Settings.ScheduleEnabled + "/" + AppState.Settings.ScheduleHour);
        Console.WriteLine("MIGRATE backup=" + File.Exists(backup));
        Console.WriteLine("MIGRATE toolsJson=" + File.Exists(Path.Combine(ProfileRegistry.ToolsDir, "bettergi.json")));
        // v1.42 / M9-2（D16）：单源化之后，迁移的正确结果是「勾选搬进 QueueEntry.Groups、旧镜像不再写」。
        // 这条以前只打日志不判定，跑绿了也不知道对不对——现在判死。
        var bgiEntry = (AppState.Settings.Queue ?? new List<QueueEntry>())
            .FirstOrDefault(e => string.Equals(e.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase));
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (string.IsNullOrEmpty(detail) ? "" : "  :: " + detail));
            if (!ok) fails++;
        }
        Check("迁移/旧全局勾选搬进 bettergi 的 Groups",
            string.Join("|", bgiEntry?.Groups ?? new List<string>()) == "日常|挖矿",
            "实际：" + string.Join("|", bgiEntry?.Groups ?? new List<string>()));
        Check("迁移/旧镜像不再写入",
            !bgi.CustomData.TryGetValue("selectedGroups", out string? m) || string.IsNullOrEmpty(m));
        Check("迁移/版本号到 17", AppState.Settings.Version == 17);
        Console.WriteLine("RESULT migrate " + (fails == 0 ? "fails=0" : "fails=" + fails));
        Console.WriteLine("MIGRATE profiles=" + string.Join(",", System.Linq.Enumerable.Select(ProfileRegistry.All, p => p.ToolId)));
        return fails == 0 ? 0 : 1;
    }

}
