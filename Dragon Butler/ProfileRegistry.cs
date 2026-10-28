using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DragonButler;

/// <summary>
/// 工具档案注册表：负责 3 个内置 profile 的加载、持久化（exe 旁 <c>tools\*.json</c>）
/// 与 v1.2 → v1.3 的配置迁移。
/// 迁移失败时**不写入任何新配置**，原 settings.json 保持原样，并通过 <see cref="MigrationFailed"/> 通知界面。
/// </summary>
public static class ProfileRegistry
{
    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions ReadOpt = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly List<ToolProfile> _all = new();
    private static bool _initialized;

    /// <summary>迁移失败（参数为给用户看的提示语）；UI 负责弹窗。</summary>
    public static event Action<string>? MigrationFailed;

    /// <summary><c>tools\</c> 目录：exe 旁。</summary>
    public static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>全部已加载的工具档案（只读视图）。</summary>
    public static IReadOnlyList<ToolProfile> All => _all;

    /// <summary>按 ToolId 取档案；找不到时回退到 bettergi（保持旧行为）。</summary>
    public static ToolProfile Get(string toolId)
    {
        if (_all.Count == 0) _all.AddRange(ToolProfileDefaults.CreateAll());
        return _all.FirstOrDefault(p => string.Equals(p.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
               ?? _all[0];
    }

    /// <summary>BGI 档案（旧代码的唯一入口，等价于 Get("bettergi")）。</summary>
    public static ToolProfile BetterGi => Get("bettergi");

    /// <summary>BGI Webhook 端口（profile.CustomData["webhookPort"]，缺省回退设置里的值）。</summary>
    public static int BetterGiWebhookPort
    {
        get
        {
            if (BetterGi.CustomData.TryGetValue("webhookPort", out string? raw)
                && int.TryParse(raw, out int port) && port > 0 && port < 65536)
                return port;
            return BgiService.Settings.BgiWebhookPort;
        }
    }

    /// <summary>
    /// 初始化：合并内置默认值、settings 中的用户配置与 tools/*.json 外部覆盖，并执行一次 v1.2→v1.3 迁移。
    /// 幂等；重复调用只刷新内存副本。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            LoadInternal();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("加载工具档案失败：" + ex.Message);
            MigrationFailed?.Invoke("配置迁移失败，请联系开发者（详见日志）");
        }
    }

    /// <summary>重新从磁盘加载（用户改了 tools/*.json 后可调用）。</summary>
    public static void Reload()
    {
        _all.Clear();
        LoadInternal();
    }

    /// <summary>保存单个档案：写 tools/{ToolId}.json，并同步到 settings.Tools。</summary>
    public static void Save(ToolProfile profile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profile.ToolId)) return;
            Directory.CreateDirectory(ToolsDir);
            File.WriteAllText(Path.Combine(ToolsDir, profile.ToolId + ".json"),
                JsonSerializer.Serialize(profile, WriteOpt));

            BgiService.Settings.Tools[profile.ToolId] = profile.Clone();
            BgiService.SaveSettings();
        }
        catch (Exception ex)
        {
            LogService.LogEvent($"保存工具档案 {profile.ToolId} 失败：{ex.Message}");
        }
    }

    // ===== 内部 =====

    private static void LoadInternal()
    {
        var settings = BgiService.Settings;
        settings.Tools ??= new Dictionary<string, ToolProfile>(StringComparer.OrdinalIgnoreCase);

        bool migrated = MigrateFromV12IfNeeded(settings);

        // 1) 内置默认值打底
        foreach (var p in ToolProfileDefaults.CreateAll()) _all.Add(p);

        // 2) settings.Tools 覆盖（用户上次保存的值）
        foreach (var kv in settings.Tools)
        {
            var target = _all.FirstOrDefault(p => string.Equals(p.ToolId, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (target == null) { _all.Add(kv.Value); continue; }
            CopyInto(kv.Value, target);
        }

        // 3) tools/*.json 外部文件覆盖（便于用户直接改文件）
        try
        {
            if (Directory.Exists(ToolsDir))
            {
                foreach (string file in Directory.GetFiles(ToolsDir, "*.json"))
                {
                    var p = JsonSerializer.Deserialize<ToolProfile>(File.ReadAllText(file), ReadOpt);
                    if (p == null || string.IsNullOrWhiteSpace(p.ToolId)) continue;
                    var target = _all.FirstOrDefault(x => string.Equals(x.ToolId, p.ToolId, StringComparison.OrdinalIgnoreCase));
                    if (target == null) _all.Add(p);
                    else CopyInto(p, target);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("读取 tools/*.json 失败：" + ex.Message);
        }

        if (migrated)
        {
            // 迁移成功后补一份 tools/bettergi.json，方便用户直接查看/修改
            try
            {
                Directory.CreateDirectory(ToolsDir);
                File.WriteAllText(Path.Combine(ToolsDir, "bettergi.json"),
                    JsonSerializer.Serialize(BetterGi, WriteOpt));
            }
            catch (Exception ex)
            {
                LogService.LogEvent("写入 tools/bettergi.json 失败：" + ex.Message);
            }
        }

        FileLog.Snapshot("profiles", string.Join(",", _all.Select(p => p.ToolId)));
    }

    /// <summary>
    /// v1.2 → v1.3 迁移：旧 settings 只有 BgiPath / DragonConfigName / SelectedGroups，
    /// 迁入 Tools["bettergi"]；原文件先备份为 settings.json.bak.v12。
    /// 返回是否实际发生了迁移。
    /// </summary>
    private static bool MigrateFromV12IfNeeded(AppSettings settings)
    {
        bool looksMigrated = settings.Version >= AppSettings.CurrentVersion
                             && settings.Tools.ContainsKey("bettergi");
        bool hasLegacy = !string.IsNullOrWhiteSpace(settings.BgiPath)
                         || !string.IsNullOrWhiteSpace(settings.DragonConfigName)
                         || (settings.SelectedGroups?.Length ?? 0) > 0;

        if (looksMigrated || !hasLegacy) return false;

        try
        {
            string path = BgiService.SettingsPath;
            string backup = path + ".bak.v12";
            if (File.Exists(path) && !File.Exists(backup))
                File.Copy(path, backup, overwrite: false);

            var bgi = settings.Tools.TryGetValue("bettergi", out var existing)
                ? existing
                : ToolProfileDefaults.CreateAll().First(p => p.ToolId == "bettergi");

            if (!string.IsNullOrWhiteSpace(settings.BgiPath))
                bgi.ExecutablePath = settings.BgiPath;
            bgi.CustomData["dragonConfigName"] = settings.DragonConfigName ?? "";
            bgi.CustomData["selectedGroups"] = string.Join("|", settings.SelectedGroups ?? Array.Empty<string>());
            if (!bgi.CustomData.ContainsKey("webhookPort"))
                bgi.CustomData["webhookPort"] = settings.BgiWebhookPort.ToString();

            settings.Tools["bettergi"] = bgi;
            settings.Version = AppSettings.CurrentVersion;
            BgiService.SaveSettings();

            LogService.LogEvent("已迁移配置 v1.2→v1.3");
            return true;
        }
        catch (Exception ex)
        {
            // 不写任何迁移结果，保持旧文件可用
            LogService.LogEvent("配置迁移失败：" + ex.Message);
            MigrationFailed?.Invoke("配置迁移失败，请联系开发者（详见日志）");
            return false;
        }
    }

    private static void CopyInto(ToolProfile from, ToolProfile to)
    {
        if (!string.IsNullOrWhiteSpace(from.DisplayName)) to.DisplayName = from.DisplayName;
        to.ExecutablePath = from.ExecutablePath ?? "";
        if (!string.IsNullOrWhiteSpace(from.LaunchArguments)) to.LaunchArguments = from.LaunchArguments;
        if (!string.IsNullOrWhiteSpace(from.ManagedProcessName)) to.ManagedProcessName = from.ManagedProcessName;
        if (from.GameProcessNames is { Length: > 0 }) to.GameProcessNames = from.GameProcessNames;
        to.PrimarySignal = from.PrimarySignal;
        if (from.SecondarySignals is { Length: > 0 }) to.SecondarySignals = from.SecondarySignals;
        if (from.LogCompletionPattern != null) to.LogCompletionPattern = from.LogCompletionPattern;
        if (from.MaxDurationMinutes > 0) to.MaxDurationMinutes = from.MaxDurationMinutes;
        to.PostExitGraceMinutes = from.PostExitGraceMinutes;
        if (from.RetryCount > 0) to.RetryCount = from.RetryCount;
        if (from.CustomData is { Count: > 0 })
        {
            foreach (var kv in from.CustomData) to.CustomData[kv.Key] = kv.Value;
        }
    }
}

/// <summary>轻量结构化事件行，便于测试脚本 grep（带时间戳与键值）。</summary>
internal static class FileLog
{
    public static void Snapshot(string kind, string detail)
        => LogService.LogEvent($"[{kind}] {detail}");
}
