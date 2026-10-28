using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DragonButler;

/// <summary>
/// 应用设置（SPBGI）：与 settings.json 字段一一对应；旧配置缺新字段时反序列化自动保持默认值。
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

    /// <summary>勾选的配置组（非空时按 --startGroups 执行；空则用配置名/当前选中）</summary>
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
}

/// <summary>
/// BetterGI 启停 / 进程管理 / 设置读写（SPBGI）。
/// 铁律：所有 BGI/原神进程的检测与终止必须按 SessionId 过滤，防止 A 会话误杀 B 会话的 BGI。
/// </summary>
public static class BgiService
{
    public static AppSettings Settings { get; private set; } = new AppSettings();

    /// <summary>运行状态变化（running）</summary>
    public static event Action<bool>? OnStatusChanged;

    /// <summary>最近事件广播</summary>
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

    /// <summary>初始化：LoadSettings + 2 秒轮询（幂等）</summary>
    public static void Init()
    {
        LoadSettings();
        // v1.3：加载/迁移工具档案（内部会读写 settings.json，失败时保持原文件不动）
        ProfileRegistry.Initialize();
        var bgi = ProfileRegistry.BetterGi;
        if (string.IsNullOrWhiteSpace(Settings.BgiPath) && !string.IsNullOrWhiteSpace(bgi.ExecutablePath))
            Settings.BgiPath = bgi.ExecutablePath;
        if (_initialized) return;
        _initialized = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    /// <summary>
    /// 本会话的 BetterGI 进程（SessionId 过滤，取不到 SessionId 的跳过）
    /// </summary>
    public static Process[] GetSessionBgi() => ToolRunner.GetSessionProcesses(ProfileRegistry.BetterGi);

    /// <summary>本会话的原神进程（YuanShen.exe / GenshinImpact.exe，SessionId 过滤）</summary>
    public static Process[] GetSessionGame()
    {
        int me = Process.GetCurrentProcess().SessionId;
        var list = new List<Process>();
        foreach (var name in new[] { "YuanShen", "GenshinImpact" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.SessionId == me) list.Add(p);
                    else p.Dispose();
                }
                catch
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        return list.ToArray();
    }

    /// <summary>BetterGI 是否正在运行（基于本会话）</summary>
    public static bool IsRunning => GetSessionBgi().Length > 0;

    /// <summary>仅在状态变化时广播</summary>
    public static void RefreshStatus()
    {
        bool running;
        try { running = IsRunning; }
        catch { running = _lastRunning; }
        if (running == _lastRunning) return;
        _lastRunning = running;
        RaiseStatus(running);
    }

    /// <summary>
    /// 启动核心：先杀旧进程（Task.Run 包裹防冻结）→ runas 启动 → 广播与记日志
    /// </summary>
    public static async Task<(bool ok, string err)> StartCore(string args, string source)
    {
        var profile = ProfileRegistry.BetterGi;
        string exe = !string.IsNullOrWhiteSpace(profile.ExecutablePath) ? profile.ExecutablePath : Settings.BgiPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return (false, "BetterGI.exe 路径无效，请先在主界面填写");
        profile.ExecutablePath = exe;

        // 对已在运行的 BGI 实例传参无效 → 先杀后启；杀进程必须 Task.Run（WaitForExit 最长 3 秒，同步会冻结界面）
        if (IsRunning)
        {
            await Task.Run(() => KillAll());
            await Task.Delay(800);
        }

        try
        {
            using var p = ToolRunner.StartProcess(profile, args);
            RaiseLog($"✓ 已启动一条龙（来源：{source}）参数：{args}");
            LogService.LogEvent($"BGI：启动一条龙（来源：{source}）参数：{args}");
            await Task.Delay(1200);
            RefreshStatus();
            return (true, "");
        }
        catch (Exception ex)
        {
            RaiseLog($"✗ 启动失败：{ex.Message}");
            LogService.LogEvent($"BGI：启动失败：{ex.Message}");
            RefreshStatus();
            return (false, ex.Message);
        }
    }

    /// <summary>启动一条龙：无配置名用 startOneDragon，有配置名用 startOneDragon "配置名"</summary>
    public static Task<(bool ok, string err)> StartDragonAsync(string source)
    {
        string args = ToolRunner.BuildArguments(ProfileRegistry.BetterGi, Settings.DragonConfigName);
        return StartCore(args, source);
    }

    /// <summary>调度器模式跑多个配置组："继续跑未完成"的实现：--startGroups "组A" "组B"</summary>
    public static Task<(bool ok, string err)> StartGroupsAsync(string[] groups, string source)
    {
        string joined = string.Join(" ", groups.Select(g => "\"" + g + "\""));
        return StartCore("--startGroups " + joined, source);
    }

    /// <summary>
    /// 统一启动入口：勾选了配置组 → 按组执行（--startGroups）；
    /// 否则 → startOneDragon [配置名]（留空用 BGI 当前选中）
    /// </summary>
    public static Task<(bool ok, string err)> StartDragonOrGroupsAsync(string source)
    {
        var selected = Settings.SelectedGroups ?? Array.Empty<string>();
        if (selected.Length > 0) return StartGroupsAsync(selected, source);
        return StartDragonAsync(source);
    }

    /// <summary>停止：Task.Run(KillAll) + Delay 300 + 刷新 + 广播/日志</summary>
    public static async Task StopAsync(string source)
    {
        await Task.Run(() => KillAll());
        await Task.Delay(300);
        RefreshStatus();
        RaiseLog($"■ 已停止 BetterGI（来源：{source}）");
        LogService.LogEvent($"BGI：停止（来源：{source}）");
    }

    /// <summary>只杀本会话 BGI（Kill + WaitForExit(3000) + Dispose）</summary>
    public static void KillAll() => ToolRunner.KillAll(ProfileRegistry.BetterGi);

    /// <summary>读取 settings.json，损坏回退默认值</summary>
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

    /// <summary>保存 settings.json（中文不转义、缩进、写前建目录）</summary>
    public static void SaveSettings()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            // v1.3：把界面上的 BGI 路径/配置名/勾选组反写进 profile，保持单一事实来源
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

    /// <summary>对外广播一条日志（供定时任务等非 BgiService 内部场景使用）</summary>
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
