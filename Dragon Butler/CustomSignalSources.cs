using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DragonButler;

/// <summary>
/// 窗口标题信号源（v1.5）：目标进程的主窗口标题包含指定文本时抛信号。
/// 文本来自 <c>profile.CustomData["customWindowTitle"]</c>。
/// 只认本会话内、由 <see cref="ToolProfile.ManagedProcessName"/> 指定的进程，避免同名窗口误判。
/// </summary>
public sealed class WindowTitleSignalSource : ICompletionSignalSource
{
    /// <inheritdoc />
    public string SourceId => "window-title";

    /// <inheritdoc />
    public event Action<CompletionSignal>? SignalRaised;

    private Timer? _timer;
    private string _needle = "";
    private string _processName = "";
    private int _raised;

    /// <summary>测试用：覆盖轮询间隔（默认 2 秒）。</summary>
    public static TimeSpan? PollIntervalOverride { get; set; }

    /// <inheritdoc />
    public void Start(ToolRunContext ctx)
    {
        _needle = NeedleOf(ctx.Profile);
        _processName = ctx.Profile.ManagedProcessName ?? "";
        if (_needle.Length == 0 || _processName.Length == 0) return;
        var interval = PollIntervalOverride ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, interval);
    }

    /// <summary>从档案里取窗口标题关键词（空=未配置）。</summary>
    public static string NeedleOf(ToolProfile profile)
        => profile.CustomData.TryGetValue(CustomSignalKeys.WindowTitle, out string? v)
            ? (v ?? "").Trim()
            : "";

    private void Poll()
    {
        if (Volatile.Read(ref _raised) == 1) return;
        try
        {
            foreach (var p in ProcessTools.GetSessionProcesses(_processName))
            {
                try
                {
                    foreach (string title in TitlesOf(p))
                    {
                        if (title.Contains(_needle, StringComparison.OrdinalIgnoreCase))
                        {
                            Hit(title);
                            return;
                        }
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
    }

    /// <summary>该进程的全部可见顶层窗口标题（含主窗口之外的弹窗）。</summary>
    private static List<string> TitlesOf(Process p)
    {
        var titles = new List<string>();
        int pid = 0;
        try { pid = p.Id; } catch { return titles; }

        try
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner != (uint)pid) return true;
                if (!IsWindowVisible(hwnd)) return true;
                if (GetWindowTextLength(hwnd) == 0) return true;
                titles.Add(TitleOf(hwnd));
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        if (titles.Count == 0)
        {
            try
            {
                IntPtr hwnd = p.MainWindowHandle;
                if (hwnd != IntPtr.Zero) titles.Add(TitleOf(hwnd));
            }
            catch { }
        }
        return titles;
    }

    private void Hit(string title)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 1) return;
        LogService.LogEvent($"窗口标题信号：命中「{_needle}」（实际标题：{Trim(title)}）");
        Raise(new CompletionSignal
        {
            SourceId = SourceId,
            Kind = CompletionSignalKind.WindowTitle,
            Confidence = SignalConfidence.Medium,
            Detail = "窗口标题包含「" + _needle + "」",
        });
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private void Raise(CompletionSignal s)
    {
        try { SignalRaised?.Invoke(s); } catch { }
    }

    /// <inheritdoc />
    public void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
    }

    // ===== Win32 =====

    private static string TitleOf(IntPtr hwnd)
    {
        try
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);
}

/// <summary>
/// 自定义信号源（v1.5）：把「日志包含文本 / 等待文件出现 / 主窗口标题包含文本」三种条件
/// 合成一个主信号，任一命中即抛 <see cref="CompletionSignalKind.CustomSignal"/>（只抛一次）。
/// 三种条件都可空，但全空时本信号源不启动（表单层也会拦住保存）。
/// </summary>
public sealed class CustomSignalSource : ICompletionSignalSource
{
    /// <inheritdoc />
    public string SourceId => "custom-signal";

    /// <inheritdoc />
    public event Action<CompletionSignal>? SignalRaised;

    private readonly List<string> _texts = new();
    private readonly List<FileCursor> _cursors = new();
    private string _filePath = "";
    private string _exeDir = "";
    private WindowTitleSignalSource? _titleSource;
    private Timer? _timer;
    private int _raised;

    /// <summary>测试用：覆盖轮询间隔（默认 2 秒）。</summary>
    public static TimeSpan? PollIntervalOverride { get; set; }

    private sealed class FileCursor
    {
        public string Path = "";
        public long Position;
    }

    /// <summary>解析档案里的三项配置（去空、去重）。</summary>
    public static (string[] Texts, string FilePath, string Title) ReadConfig(ToolProfile profile)
    {
        var texts = Array.Empty<string>();
        if (profile.CustomData.TryGetValue(CustomSignalKeys.LogTexts, out string? raw) && !string.IsNullOrWhiteSpace(raw))
        {
            texts = raw.Split(new[] { '\r', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(s => s.Trim())
                       .Where(s => s.Length > 0)
                       .Distinct(StringComparer.Ordinal)
                       .ToArray();
        }
        string path = profile.CustomData.TryGetValue(CustomSignalKeys.FilePath, out string? p) ? (p ?? "").Trim() : "";
        string title = WindowTitleSignalSource.NeedleOf(profile);
        return (texts, path, title);
    }

    /// <inheritdoc />
    public void Start(ToolRunContext ctx)
    {
        var (texts, path, title) = ReadConfig(ctx.Profile);
        _texts.AddRange(texts);
        _filePath = path;
        if (_texts.Count == 0 && _filePath.Length == 0 && title.Length == 0) return;

        try { _exeDir = Path.GetDirectoryName(ctx.Profile.ExecutablePath) ?? ""; }
        catch { _exeDir = ""; }

        if (title.Length > 0)
        {
            _titleSource = new WindowTitleSignalSource();
            _titleSource.SignalRaised += s => Hit("窗口标题命中：" + s.Detail);
            _titleSource.Start(ctx);
        }

        if (_filePath.Length > 0) LogService.LogEvent($"自定义信号：等待文件出现 {_filePath}");
        if (_texts.Count > 0)
        {
            SnapshotExistingLogs();
            LogService.LogEvent($"自定义信号：监视日志文本 {string.Join(" / ", _texts)}");
        }

        var interval = PollIntervalOverride ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(300), interval);
    }

    private void Poll()
    {
        if (Volatile.Read(ref _raised) == 1) return;
        try
        {
            if (_filePath.Length > 0)
            {
                try
                {
                    if (File.Exists(_filePath)) { Hit("文件已出现：" + Path.GetFileName(_filePath)); return; }
                }
                catch { }
            }

            if (_texts.Count > 0) ScanLogs();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("自定义信号轮询失败：" + ex.Message);
        }
    }

    /// <summary>启动时先把已有日志的当前位置记下来：只有之后新增的内容才算命中。</summary>
    private void SnapshotExistingLogs()
    {
        foreach (string dir in LogDirs())
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.log").Concat(Directory.EnumerateFiles(dir, "*.txt")))
                {
                    try { _cursors.Add(new FileCursor { Path = file, Position = new FileInfo(file).Length }); }
                    catch { }
                }
            }
            catch { }
        }
    }

    private IEnumerable<string> LogDirs()
    {
        if (_exeDir.Length == 0) yield break;
        yield return _exeDir;
        string log = Path.Combine(_exeDir, "log");
        if (Directory.Exists(log)) yield return log;
        string logs = Path.Combine(_exeDir, "logs");
        if (Directory.Exists(logs)) yield return logs;
    }

    /// <summary>扫描工具目录下的 *.log / *.txt，只看启动后新增的内容。</summary>
    private void ScanLogs()
    {
        foreach (string dir in LogDirs())
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.log").Concat(Directory.EnumerateFiles(dir, "*.txt"));
            }
            catch { continue; }

            foreach (string file in files)
            {
                var cursor = _cursors.FirstOrDefault(c => string.Equals(c.Path, file, StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (cursor == null)
                    {
                        // 本次运行期间新出现的文件：从头读
                        cursor = new FileCursor { Path = file, Position = 0 };
                        _cursors.Add(cursor);
                    }

                    long len = new FileInfo(file).Length;
                    if (len <= cursor.Position) continue;

                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(cursor.Position, SeekOrigin.Begin);
                    int toRead = (int)Math.Min(len - cursor.Position, 2 * 1024 * 1024);
                    var buf = new byte[toRead];
                    int read = fs.Read(buf, 0, toRead);
                    cursor.Position += read;
                    if (read <= 0) continue;

                    string text = Encoding.UTF8.GetString(buf, 0, read);
                    foreach (string line in text.Split('\n'))
                    {
                        foreach (string needle in _texts)
                        {
                            if (line.Contains(needle, StringComparison.Ordinal))
                            {
                                Hit($"日志命中「{needle}」（{Path.GetFileName(file)}）");
                                return;
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }

    private void Hit(string detail)
    {
        if (Interlocked.Exchange(ref _raised, 1) == 1) return;
        LogService.LogEvent("自定义信号：" + detail);
        Raise(new CompletionSignal
        {
            SourceId = SourceId,
            Kind = CompletionSignalKind.CustomSignal,
            Confidence = SignalConfidence.Medium,
            Detail = detail,
        });
    }

    private void Raise(CompletionSignal s)
    {
        try { SignalRaised?.Invoke(s); } catch { }
    }

    /// <inheritdoc />
    public void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
        try { _titleSource?.Stop(); } catch { }
        _titleSource = null;
    }
}
