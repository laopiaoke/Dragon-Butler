using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace DragonButler;

/// <summary>
/// 队列中的一项：勾选的工具 + 顺序（列表顺序即执行顺序）。
/// </summary>
public sealed class QueueEntry
{
    /// <summary>工具标识（bettergi / march7th / ok-ww）。</summary>
    public string ToolId { get; set; } = "";

    /// <summary>是否启用（界面勾选）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// v1.5 按日计划：bit0=周一 … bit6=周日，默认 0x7F（每天）。
    /// 只对定时触发生效；手动「一键开跑」永远跑当前勾选，不受它限制。
    /// </summary>
    public int WeekdayMask { get; set; } = 0x7F;
}

/// <summary>
/// 应用设置：与 settings.json 字段一一对应；旧配置缺新字段时反序列化自动保持默认值。
/// </summary>
public class AppSettings
{
    public string BgiPath { get; set; } = "";
    public string DragonConfigName { get; set; } = "";
    public bool StayInTray { get; set; } = true;
    public string Theme { get; set; } = "System";

    // ===== 自定义主题（Theme=Custom 时生效）=====
    /// <summary>背景方式：Color=调色板取色 / Image=图片背景</summary>
    public string CustomBgKind { get; set; } = "Color";
    /// <summary>调色板颜色（ARGB 十六进制，如 #FF1B2430）</summary>
    public string CustomBgColor { get; set; } = "#FF1B2430";
    /// <summary>背景图片绝对路径</summary>
    public string CustomBgImage { get; set; } = "";
    /// <summary>遮罩强度 0-90（%），越大背景越压暗/提亮、前景越稳</summary>
    public int CustomOverlay { get; set; } = 55;
    /// <summary>文字配色：Auto=按背景亮度自动 / Light=浅底深字 / Dark=深底浅字</summary>
    public string CustomScheme { get; set; } = "Auto";
    public bool ScheduleEnabled { get; set; } = false;
    public int ScheduleHour { get; set; } = 8;
    public int ScheduleMinute { get; set; } = 0;
    /// <summary>星期掩码：bit0=周一 … bit6=周日；0x7F=每天（v0.3 定时多选）</summary>
    public int ScheduleWeekdays { get; set; } = 0x7F;
    /// <summary>上次执行开始时间（yyyy-MM-dd HH:mm，空=无记录）</summary>
    public string LastRunAt { get; set; } = "";
    /// <summary>上次执行用时（秒）</summary>
    public int LastRunSeconds { get; set; }
    /// <summary>上次执行结果：true=完成，false=中止</summary>
    public bool LastRunOk { get; set; }
    public bool AutoRunOnLaunch { get; set; } = false;
    public bool SentinelEnabled { get; set; } = true;
    public bool AutoRestartBgi { get; set; } = true;
    public bool ScreenshotCheckEnabled { get; set; } = true;
    public string WebhookOutUrl { get; set; } = "";
    public int BgiWebhookPort { get; set; } = 18235;
    /// <summary>
    /// 按键音效开关。默认关闭（静音）：新装用户不会被突然的点击音打扰，
    /// 想听声音在「系统 → 按键音效」里打开即可，开关与 <see cref="SoundService"/> 的播放闸门保持不变。
    /// 已有的 settings.json 若显式写了 true/false，仍以保存值为准。
    /// </summary>
    public bool SoundEnabled { get; set; } = false;

    // ===== 推送渠道参数（与 BetterGI 支持的渠道对齐）=====
    /// <summary>企业微信群机器人 Webhook</summary>
    public string NotifyWecomUrl { get; set; } = "";
    /// <summary>飞书自定义机器人 Webhook</summary>
    public string NotifyFeishuUrl { get; set; } = "";
    /// <summary>飞书签名校验密钥（可选）</summary>
    public string NotifyFeishuSecret { get; set; } = "";
    /// <summary>钉钉自定义机器人 Webhook</summary>
    public string NotifyDingUrl { get; set; } = "";
    /// <summary>钉钉加签密钥（可选）</summary>
    public string NotifyDingSecret { get; set; } = "";
    public string NotifyTelegramToken { get; set; } = "";
    public string NotifyTelegramChat { get; set; } = "";
    /// <summary>Bark 推送地址（https://api.day.app/你的Key，可自建服务器）</summary>
    public string NotifyBarkUrl { get; set; } = "";
    public string NotifyEmailHost { get; set; } = "";
    public int NotifyEmailPort { get; set; } = 465;
    public string NotifyEmailUser { get; set; } = "";
    public string NotifyEmailPass { get; set; } = "";
    public string NotifyEmailFrom { get; set; } = "";
    public string NotifyEmailTo { get; set; } = "";
    /// <summary>xxtui 推送地址（含 API Key）或只填 Key</summary>
    public string NotifyXxtuiUrl { get; set; } = "";
    /// <summary>自定义 Webhook（POST JSON）</summary>
    public string NotifyWebhookUrl { get; set; } = "";
    /// <summary>WebSocket 推送地址（ws:// 或 wss://，连上发一条 JSON 即关闭）</summary>
    public string NotifyWebSocketUrl { get; set; } = "";
    /// <summary>只推送异常（Warning/Critical）；false=完成/启动也推送</summary>
    public bool NotifyOnlyImportant { get; set; } = false;

    /// <summary>勾选的配置组（BGI 专用；非空时按 --startGroups 执行）</summary>
    public string[] SelectedGroups { get; set; } = Array.Empty<string>();

    /// <summary>推送方式：Toast（Windows 通知，默认）/ Wecom（企业微信）/ Balloon（仅托盘气泡）</summary>
    public string NotifyMode { get; set; } = "Toast";

    // ===== v1.3：多工具档案 =====
    /// <summary>配置结构版本：0/缺省=旧版（v1.2 及以前），13=v1.3。</summary>
    public int Version { get; set; }

    /// <summary>v1.3 当前配置结构版本号。</summary>
    public const int CurrentVersion = 13;

    /// <summary>各工具档案（键为 ToolId）。旧配置无此节时为空字典。</summary>
    public Dictionary<string, ToolProfile> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ===== v1.3：排队引擎 =====
    /// <summary>队列（列表顺序即执行顺序）。空=首次运行，由 <see cref="AppState.EnsureQueueDefaults"/> 补齐。</summary>
    public List<QueueEntry> Queue { get; set; } = new();

    /// <summary>工具之间的等待（分钟），默认 2。</summary>
    public int QueueIntervalMinutes { get; set; } = 2;

    /// <summary>失败策略：Skip=跳过继续（默认）/ Abort=停止剩余队列。</summary>
    public string FailStrategy { get; set; } = "Skip";

    /// <summary>跑完动作：Notify=仅通知（默认）/ Sleep=睡眠 / Shutdown=关机。</summary>
    public string AfterRunAction { get; set; } = "Notify";

    /// <summary>队列有失败项时是否仍执行睡眠/关机（默认关）。</summary>
    public bool AfterRunOnFailure { get; set; } = false;

    /// <summary>
    /// DRYRUN：只写日志不真正睡眠/关机。
    /// 附录 B 已通过（真实睡眠/关机各验证一次），默认关闭 DRYRUN，跑完动作真实生效。
    /// 需要演练时把它改回 true。
    /// </summary>
    public bool AfterRunDryRun { get; set; } = false;

    /// <summary>防打扰：定时触发时若检测到用户正在用电脑则推迟（默认开）。</summary>
    public bool IdleGuardEnabled { get; set; } = true;

    /// <summary>定时触发时自动清理上次残留的工具进程（默认开）。</summary>
    public bool AutoKillResidual { get; set; } = true;

    /// <summary>度假模式：所有定时触发暂停（手动不受限）。</summary>
    public bool VacationMode { get; set; } = false;

    /// <summary>首启向导是否已完成（或跳过）。</summary>
    public bool FirstRunDone { get; set; } = false;

    // ===== v1.4：挂机护航 =====
    /// <summary>挂机护航总开关（默认开）：队列运行期间抑制睡眠、重启与屏保。</summary>
    public bool KeepAwakeEnabled { get; set; } = true;

    /// <summary>夜间息屏（默认关）：队列运行 N 分钟后关闭显示器。</summary>
    public bool NightScreenEnabled { get; set; } = false;

    /// <summary>夜间息屏延时（分钟，5~120，默认 30）。</summary>
    public int NightScreenMinutes { get; set; } = 30;

    /// <summary>STALLED 自动终止（默认关）：疑似挂起时结束进程并按失败策略处理。</summary>
    public bool StallAutoKill { get; set; } = false;

    /// <summary>护航前屏保原值（-1=未知/无需恢复；0=关闭；1=开启）。异常退出后下次启动补偿。</summary>
    public int KeepAwakeScreenSaverOriginal { get; set; } = -1;

    // ===== v1.5：晨报 =====
    /// <summary>晨报（默认关）：每天首次启动时用一句话汇报昨日的队列情况。</summary>
    public bool MorningReportEnabled { get; set; } = false;

    /// <summary>上次播报晨报的日期（yyyy-MM-dd），防止同一天重复播报。</summary>
    public string MorningReportDate { get; set; } = "";

    /// <summary>最近一次守护告警时间（底部盾牌转黄用），空=从未告警。</summary>
    public DateTime? ShieldAlertAt { get; set; }
}

/// <summary>
/// 应用级状态：设置读写、日志广播、运行状态轮询。
/// 只保留"数据与总线"职责，启停工具交给 <see cref="QueueService"/> / <see cref="ToolRunner"/>。
/// </summary>
public static class AppState
{
    /// <summary>当前设置（启动时从 settings.json 读取）。</summary>
    public static AppSettings Settings { get; private set; } = new AppSettings();

    /// <summary>运行状态变化（有队列在跑）。</summary>
    public static event Action<bool>? OnStatusChanged;

    /// <summary>最近事件广播（界面日志条/日志面板）。</summary>
    public static event Action<string>? OnLog;

    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions ReadOpt = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static bool _lastRunning;
    private static DispatcherTimer? _timer;
    private static bool _initialized;

    /// <summary>settings.json 路径：exe 旁 config\settings.json</summary>
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "config", "settings.json");

    /// <summary>初始化：读设置 → 载入工具档案 → 补齐队列默认值 → 2 秒状态轮询（幂等）。</summary>
    public static void Init()
    {
        LoadSettings();
        ProfileRegistry.Initialize();
        EnsureQueueDefaults();

        if (!string.IsNullOrWhiteSpace(Settings.BgiPath))
        {
            var bgi = ProfileRegistry.BetterGi;
            if (string.IsNullOrWhiteSpace(bgi.ExecutablePath)) bgi.ExecutablePath = Settings.BgiPath;
        }

        if (_initialized) return;
        _initialized = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    /// <summary>队列为空时给出默认三项（仅 BetterGI 勾选），避免首屏空白。</summary>
    public static void EnsureQueueDefaults()
    {
        Settings.Queue ??= new List<QueueEntry>();
        if (Settings.Queue.Count > 0) return;

        foreach (var p in ProfileRegistry.All)
        {
            Settings.Queue.Add(new QueueEntry
            {
                ToolId = p.ToolId,
                Enabled = string.Equals(p.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    /// <summary>队列中启用且顺序有效的条目（按列表顺序）。</summary>
    public static List<QueueEntry> EnabledQueue()
        => (Settings.Queue ?? new List<QueueEntry>()).Where(e => e.Enabled).ToList();

    /// <summary>是否有队列在跑（托盘/界面状态用）。</summary>
    public static bool IsRunning => QueueService.IsRunning;

    /// <summary>仅状态变化时广播。</summary>
    public static void RefreshStatus()
    {
        bool running;
        try { running = IsRunning; }
        catch { running = _lastRunning; }
        if (running == _lastRunning) return;
        _lastRunning = running;
        RaiseStatus(running);
    }

    /// <summary>读取 settings.json，损坏回退默认值。</summary>
    public static void LoadSettings()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ReadOpt);
                if (s != null) Settings = s;
            }
            // 旧版本只有一个企业微信 Webhook 字段：升级时迁移，避免用户重填
            if (string.IsNullOrWhiteSpace(Settings.NotifyWecomUrl) && !string.IsNullOrWhiteSpace(Settings.WebhookOutUrl))
                Settings.NotifyWecomUrl = Settings.WebhookOutUrl;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("settings.json 损坏，已回退默认值：" + ex.Message);
            Settings = new AppSettings();
        }
    }

    /// <summary>保存 settings.json（中文不转义、缩进、写前建目录）。</summary>
    public static void SaveSettings()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 把界面上的 BGI 路径/配置名/勾选组反写进 profile，保持单一事实来源
            try
            {
                if (ProfileRegistry.All.Count > 0)
                {
                    var bgi = ProfileRegistry.BetterGi.Clone();
                    if (!string.IsNullOrWhiteSpace(Settings.BgiPath)) bgi.ExecutablePath = Settings.BgiPath;
                    bgi.CustomData["dragonConfigName"] = Settings.DragonConfigName ?? "";
                    bgi.CustomData["selectedGroups"] = string.Join("|", Settings.SelectedGroups ?? Array.Empty<string>());
                    Settings.Tools ??= new Dictionary<string, ToolProfile>(StringComparer.OrdinalIgnoreCase);
                    Settings.Tools["bettergi"] = bgi;
                }
            }
            catch { }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, WriteOpt));
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存设置失败：" + ex.Message);
        }
    }

    /// <summary>对外广播一条日志（界面事件区/托盘提示）。</summary>
    public static void PublishLog(string msg) => RaiseLog(msg);

    private static void RaiseStatus(bool running)
    {
        var h = OnStatusChanged;
        if (h == null) return;
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.BeginInvoke(() => h(running));
        else h(running);
    }

    private static void RaiseLog(string msg)
    {
        var h = OnLog;
        if (h == null) return;
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.BeginInvoke(() => h(msg));
        else h(msg);
    }
}
