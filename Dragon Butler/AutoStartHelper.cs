using System;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 当前用户开机自启：HKCU\...\Run，值名 "Dragon Butler"，值为带引号的自身 exe 路径。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dragon Butler";

    /// <summary>当前用户是否已设置开机自启</summary>
    public static bool IsSet()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            var v = k?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }

    /// <summary>设置 / 取消当前用户开机自启，异常静默吞掉</summary>
    public static void Set(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                k.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
            }
            else
            {
                k.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("设置开机自启（当前用户）失败：" + ex.Message);
        }
    }
}
