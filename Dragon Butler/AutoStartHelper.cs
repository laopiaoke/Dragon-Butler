using System;
using System.IO;
using Microsoft.Win32;

namespace DragonButler;

/// <summary>
/// 当前用户开机自启：HKCU\...\Run，值名 "Dragon Butler"，值为带引号的自身 exe 路径。
///
/// v1.38：以前这里只做「写进去就算成功」。用户报告「开了但没自启」，查下来有三种
/// 都不会报错、也不会写日志的失败方式：
///   1. 值写进去了，但路径是旧的——用户把便携版文件夹挪了位置，注册表还指着已经不存在的 exe；
///   2. 值被任务管理器的「启动」页禁用过，Windows 会在
///      HKCU\...\Explorer\StartupApproved\Run 里留一条 12 字节的禁用标记，
///      之后这个值就算还在也不会被运行；
///   3. 纯写失败（被安全软件拦、注册表项权限异常），旧代码 catch 住只记一行日志。
///
/// 现在写完之后一定回读比对，并把 StartupApproved 的拦阻清掉；<see cref="Set"/> 返回
/// 一句人话，界面照着这句话决定勾子该不该亮。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Dragon Butler";

    /// <summary>本程序此刻的 exe 路径（可能为空——单文件部署的极少数情况下取不到）。</summary>
    public static string ExePath
    {
        get
        {
            string? p = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(p)) return p;
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrWhiteSpace(asm)) return asm;
            }
            catch { }
            return "";
        }
    }

    /// <summary>
    /// 现在是不是「开且有效」：值存在、非空、且指向的正是当前这个 exe。
    /// 指向别处（多半是搬过文件夹）算没开——否则勾子是亮的、实际开机什么都不会发生。
    /// </summary>
    public static bool IsSet()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(ValueName) is not string raw) return false;
            string want = ExePath;
            if (want.Length == 0) return raw.Trim().Length > 0;
            string got = raw.Trim().Trim('"');
            return string.Equals(got, want, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>注册表里现在写的是什么（诊断用，界面上「为什么没生效」要靠它）。</summary>
    public static string StoredValue()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 设置 / 取消当前用户开机自启。
    /// </summary>
    /// <returns>(成功, 给用户看的一句话)。失败时那句里带上真正的原因，不要只说「失败」。</returns>
    public static (bool Ok, string Message) Set(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return (false, "打不开注册表的启动项（HKCU\\…\\Run）。可能被安全软件锁了。");

            if (!enable)
            {
                k.DeleteValue(ValueName, false);
                ClearApprovedBlock();
                return (true, "已取消开机自启");
            }

            string exe = ExePath;
            if (exe.Length == 0) return (false, "取不到自己的程序路径，没法设置自启。");
            if (!File.Exists(exe)) return (false, $"自己的程序路径不存在：{exe}");

            string quoted = "\"" + exe + "\"";
            k.SetValue(ValueName, quoted, RegistryValueKind.String);
            // 写完立刻回读：注册表写入成功但被别的东西立刻改回去的情况是存在的，
            // 不回读就会像上次一样——界面上亮着，开机没动静。
            string back = k.GetValue(ValueName) as string ?? "";
            if (!string.Equals(back, quoted, StringComparison.Ordinal)) return (false, "写进去了但没留住，可能被安全软件改回去了。");

            ClearApprovedBlock();
            LogService.LogEvent("开机自启已写入：" + quoted);
            return (true, "已设为开机自启：" + exe);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("设置开机自启（当前用户）失败：" + ex.Message);
            return (false, "设置开机自启失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 清掉任务管理器留下的「已禁用」标记。那条值是 12 字节，首字节 0x02/0x06 表示启用、
    /// 0x03 表示被用户禁用。直接删掉整条最省事——Windows 下次启动时按「启用」处理。
    /// </summary>
    private static void ClearApprovedBlock()
    {
        try
        {
            using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
            a?.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { LogService.LogEvent("清理启动项禁用标记失败：" + ex.Message); }
    }

    /// <summary>
    /// 启动时对一次账：用户上次是开着的，但注册表被系统清理 / 路径变了 / 被禁用，
    /// 就悄悄补写回去。这样「开了之后一直有效」不依赖用户再去点一次。
    /// </summary>
    /// <returns>补写过了就返回一句日志用的说明，没有则返回 null。</returns>
    public static string? Heal(bool wanted)
    {
        if (!wanted) return null;
        if (IsSet()) { ClearApprovedBlock(); return null; }
        var (ok, msg) = Set(true);
        return ok ? "开机自启对账：原来的值失效了（路径变了或被禁用），已重新写回。" : "开机自启对账失败：" + msg;
    }
}
