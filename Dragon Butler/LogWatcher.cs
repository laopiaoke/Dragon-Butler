using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DragonButler;

/// <summary>
/// 哨兵统一事件（SPBGI）：LogWatcher / WebhookListener / ScreenshotMonitor / SentinelService
/// 全部产出统一事件，由 SentinelService 汇总决策，避免各监测源各自为战。
/// </summary>
public sealed class SentinelEvent
{
    public SentinelEventKind Kind { get; init; }
    public string Payload { get; init; } = "";

    /// <summary>来源：webhook / log / screen / proc</summary>
    public string Source { get; init; } = "unknown";
}

/// <summary>
/// BGI 日志 tail 追踪 + 正则解析（SPBGI）。
/// 只读增量（维护 lastPosition 文件偏移量），绝不重读全文件；
/// 跨天检测到新日期文件自动切换并把 lastPosition 归零
/// （防"检查昨天日志误判今天已完成"的关键）；当天隔离，绝不用昨天的行更新今天的状态。
/// </summary>
public sealed class LogWatcher
{
    public event Action<SentinelEvent>? OnEvent;

    // 解析正则（源自 auto-bgi 实测，直接照抄）
    private static readonly Regex RxGroupStart = new(@"配置组 ""(.*?)"" 加载完成", RegexOptions.Compiled);
    private static readonly Regex RxGroupEnd = new(@"配置组 ""(.*?)"" 执行结束", RegexOptions.Compiled);

    // 错误关键字表（可配置项暂用内置默认）
    private static readonly string[] ErrorKeywords = { "失败", "异常", "报错", "错误", "取消" };

    private string? _currentFile;
    private long _lastPosition;
    private string _currentDate = "";
    private string _leftover = "";

    /// <summary>启动：定位 BGI 安装目录 log\，找当天文件（文件名含今天 yyyyMMdd）</summary>
    public void Start()
    {
        try
        {
            string dir = LogDir();
            if (dir.Length == 0 || !Directory.Exists(dir)) return;
            string today = DateTime.Now.ToString("yyyyMMdd");
            var todayFile = Directory.GetFiles(dir, "better-genshin-impact*.log")
                .Where(f => Path.GetFileName(f).Contains(today))
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .FirstOrDefault();
            if (todayFile != null)
            {
                _currentFile = todayFile;
                _currentDate = today;
                // 只追新增行：从当前文件末尾开始
                try { _lastPosition = new FileInfo(todayFile).Length; } catch { _lastPosition = 0; }
                _leftover = "";
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("LogWatcher 启动失败：" + ex.Message);
        }
    }

    private static string LogDir()
    {
        try
        {
            string? baseDir = Path.GetDirectoryName(BgiService.Settings.BgiPath);
            return string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, "log");
        }
        catch { return ""; }
    }

    /// <summary>
    /// 由 SentinelService 5 秒驱动，同步快读：文件切换检查 → Seek 读新增行 → 逐行正则匹配
    /// </summary>
    public void Pump()
    {
        try
        {
            CheckSwitch();
            if (_currentFile == null || !File.Exists(_currentFile)) return;

            using var fs = new FileStream(_currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _lastPosition)
            {
                // 文件被 BGI 删除重建（日期翻转）→ position 归零
                _lastPosition = 0;
                _leftover = "";
            }
            if (fs.Length == _lastPosition) return;

            int toRead = (int)Math.Min(fs.Length - _lastPosition, 4 * 1024 * 1024);
            var buf = new byte[toRead];
            fs.Seek(_lastPosition, SeekOrigin.Begin);
            int read = fs.Read(buf, 0, toRead);
            if (read <= 0) return;
            _lastPosition += read;

            string text = Encoding.UTF8.GetString(buf, 0, read);
            string[] lines = (_leftover + text).Split('\n');
            _leftover = lines[^1]; // 最后一段可能不完整，留给下次
            foreach (var raw in lines.Take(lines.Length - 1))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0) ProcessLine(line);
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("LogWatcher Pump 失败：" + ex.Message);
        }
    }

    /// <summary>文件切换检查：出现更新的日期文件则切换并把 lastPosition 归零</summary>
    private void CheckSwitch()
    {
        string dir = LogDir();
        if (dir.Length == 0 || !Directory.Exists(dir)) return;
        string today = DateTime.Now.ToString("yyyyMMdd");
        if (today == _currentDate) return;

        var todayFile = Directory.GetFiles(dir, "better-genshin-impact*.log")
            .Where(f => Path.GetFileName(f).Contains(today))
            .OrderByDescending(f => new FileInfo(f).LastWriteTime)
            .FirstOrDefault();
        if (todayFile == null) return;

        _currentFile = todayFile;
        _currentDate = today;
        _lastPosition = 0;
        _leftover = "";
        LogService.LogEvent("LogWatcher：已切换到当天日志 " + Path.GetFileName(todayFile));
    }

    private void ProcessLine(string line)
    {
        var m = RxGroupStart.Match(line);
        if (m.Success)
        {
            Emit(SentinelEventKind.GroupStarted, m.Groups[1].Value);
            return;
        }
        m = RxGroupEnd.Match(line);
        if (m.Success)
        {
            Emit(SentinelEventKind.GroupEnded, m.Groups[1].Value);
            return;
        }
        if (ErrorKeywords.Any(k => line.Contains(k, StringComparison.Ordinal)))
        {
            Emit(SentinelEventKind.LogErrorLine, line);
        }
    }

    private void Emit(SentinelEventKind kind, string payload)
    {
        try
        {
            OnEvent?.Invoke(new SentinelEvent { Kind = kind, Payload = payload, Source = "log" });
        }
        catch { }
    }
}
