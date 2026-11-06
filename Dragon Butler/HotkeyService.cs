using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DragonButler;

/// <summary>
/// 全局热键（v1.8 / F9）。
///
/// 为什么需要一个隐藏窗口：RegisterHotKey 需要一个真实的窗口句柄来收 WM_HOTKEY。
/// 这里照抄 <see cref="KeepAwakeService"/> 的做法——建一个 0×0 的 WS_POPUP 隐藏 HwndSource，
/// 不新增可见窗口、不影响任何界面。
///
/// 默认关闭：游戏的反作弊对全局键盘钩子的容忍度不可控，
/// 主动注册热键属于「可能引起他人麻烦」的行为，必须用户自己打开。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // RegisterHotKey 修饰键
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private readonly Dictionary<int, string> _actions = new();
    private int _nextId = 9100;

    /// <summary>热键被按下。参数是注册时给的动作名（"start" / "stop"）。</summary>
    public event Action<string>? Pressed;

    /// <summary>已成功注册的热键数量。</summary>
    public int RegisteredCount => _actions.Count;

    /// <summary>
    /// 解析 "Ctrl+Alt+R" 这种写法。返回 null 表示写法不合法。
    /// 纯函数，静态、无副作用——Probe 直接断言这一层，不去碰真实的系统注册
    /// （CI 没有交互式桌面会话，RegisterHotKey 必然失败）。
    /// </summary>
    public static (uint Modifiers, uint VirtualKey)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        uint mods = 0;
        uint vk = 0;
        bool sawKey = false;

        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return null;

            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= ModControl;
                    continue;
                case "alt":
                    mods |= ModAlt;
                    continue;
                case "shift":
                    mods |= ModShift;
                    continue;
                case "win":
                case "windows":
                    mods |= ModWin;
                    continue;
            }

            // 已经有一个主键了，不能有第二个
            if (sawKey) return null;
            if (!TryParseKey(part, out vk)) return null;
            sawKey = true;
        }

        if (!sawKey) return null;
        // 光有主键没有修饰键的热键会霸占普通打字，拒绝
        if (mods == 0) return null;
        return (mods, vk);
    }

    /// <summary>把单个键名转成虚拟键码：A-Z、0-9、F1-F12、Space、Escape、Tab、Enter。</summary>
    private static bool TryParseKey(string part, out uint vk)
    {
        vk = 0;
        if (part.Length == 1)
        {
            char c = char.ToUpperInvariant(part[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
            return false;
        }

        if ((part[0] == 'F' || part[0] == 'f') && int.TryParse(part.Substring(1), out int n)
            && n >= 1 && n <= 12)
        {
            vk = (uint)(0x70 + n - 1); // VK_F1 = 0x70
            return true;
        }

        switch (part.ToLowerInvariant())
        {
            case "space": vk = 0x20; return true;
            case "escape":
            case "esc": vk = 0x1B; return true;
            case "tab": vk = 0x09; return true;
            case "enter":
            case "return": vk = 0x0D; return true;
        }
        return false;
    }

    /// <summary>给用户看的写法（保存前归一化，避免 "ctrl+alt+r" 和 "Ctrl+Alt+R" 两种都进配置）。</summary>
    public static string Normalize(string? text)
    {
        var parsed = Parse(text);
        if (parsed == null) return (text ?? "").Trim();

        var parts = new List<string>();
        if ((parsed.Value.Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((parsed.Value.Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((parsed.Value.Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((parsed.Value.Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(parsed.Value.VirtualKey));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x20 => "Space",
            0x1B => "Escape",
            0x09 => "Tab",
            0x0D => "Enter",
            _ => "?",
        };
    }

    /// <summary>
    /// 注册一组热键。任何一个注册失败都只记日志、不影响其它热键。
    /// 返回是否至少注册成功一个。
    /// </summary>
    public bool Register(params (string Action, string Text)[] hotkeys)
    {
        UnregisterAll();

        try
        {
            _source ??= new HwndSource(new HwndSourceParameters("DragonButlerHotkey")
            {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP，隐藏窗口
                Width = 0,
                Height = 0,
            });
            _source.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("热键：无法创建消息窗口，功能不可用（" + ex.Message + "）");
            return false;
        }

        foreach (var (action, text) in hotkeys)
        {
            var parsed = Parse(text);
            if (parsed == null)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    LogService.LogEvent($"热键：{action} 的写法「{text}」不合法，已跳过");
                continue;
            }

            int id = _nextId++;
            bool ok;
            try
            {
                ok = RegisterHotKey(_source.Handle, id, parsed.Value.Modifiers, parsed.Value.VirtualKey);
            }
            catch
            {
                ok = false;
            }

            if (ok)
            {
                _actions[id] = action;
                LogService.LogEvent($"热键：{Normalize(text)} → {action}");
            }
            else
            {
                // 最常见的原因：别的程序已经占了这个组合
                LogService.LogEvent($"热键：{Normalize(text)} 注册失败（可能已被其它程序占用）");
            }
        }
        return _actions.Count > 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (_actions.TryGetValue(id, out var action))
        {
            handled = true;
            try { Pressed?.Invoke(action); }
            catch (Exception ex) { LogService.LogEvent("热键：处理失败 " + ex.Message); }
        }
        return IntPtr.Zero;
    }

    public void UnregisterAll()
    {
        try
        {
            if (_source == null) return;
            foreach (int id in _actions.Keys)
            {
                try { UnregisterHotKey(_source.Handle, id); } catch { }
            }
            _actions.Clear();
        }
        catch { }
    }

    public void Dispose()
    {
        UnregisterAll();
        try
        {
            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch { }
    }
}
