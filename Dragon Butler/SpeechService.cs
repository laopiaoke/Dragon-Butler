using System;
using System.Runtime.InteropServices;

namespace DragonButler;

/// <summary>
/// 跑完说一句话（v1.8 / F14）。
///
/// 治的病：挂着一条龙跑一整晚，人早就不看屏幕了。完成通知是弹窗，
/// 但你不在电脑前就永远看不到；而声音会穿过整个房间。
///
/// 零依赖做法：直接走 COM 的 SAPI.SpVoice（Windows 自带，与
/// <see cref="ToolDiscovery"/> 里用 WScript.Shell 的 ProgID 是同一套路），
/// 不引 System.Speech、不加任何 NuGet 包。
///
/// 默认关闭：突然出声吓人一跳，比没有这个功能更糟。
/// </summary>
public static class SpeechService
{
    /// <summary>上次朗读的时间，用于防抖（同一条消息连续来时不重复念）。</summary>
    private static DateTime _lastSpoken = DateTime.MinValue;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private const uint CoinitApartmentThreaded = 0x2;
    private const uint CoinitDisableOle1Dde = 0x4;
    private const int RpcEChangedMode = unchecked((int)0x80010106); // 已经是别的套间模型了，不算错误

    /// <summary>
    /// 本机有没有可用的中文/英文语音引擎。没有就不做任何事——
    /// 静默返回 false，绝不抛异常（有些精简版 Windows 把 SAPI 引擎删了）。
    /// </summary>
    public static bool Available()
    {
        object? voice = null;
        bool inited = false;
        try
        {
            inited = InitCom();
            voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!);
            return voice != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(voice);
            if (inited) try { CoUninitialize(); } catch { }
        }
    }

    /// <summary>
    /// 念一句话。任何失败都只是返回 false，不打断调用方流程。
    /// </summary>
    /// <param name="text">要念的内容（尽量短，一句完整的话）。</param>
    /// <param name="minGapSeconds">与上次朗读的最小间隔，防抖用；0=不限制。</param>
    public static bool Speak(string? text, int minGapSeconds = 0)
    {
        if (!AppState.Settings.SpeechEnabled) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (minGapSeconds > 0 && (DateTime.Now - _lastSpoken).TotalSeconds < minGapSeconds)
            return false;

        object? voice = null;
        bool inited = false;
        try
        {
            inited = InitCom();
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type == null)
            {
                LogService.LogEvent("语音：本机没有 SAPI 语音引擎，已跳过");
                return false;
            }

            voice = Activator.CreateInstance(type);
            if (voice == null) return false;

            // SAPI 的 Speak 签名：Speak(string text, int flags = 1 /*Async*/)
            // 同步朗读（flags=0）会阻塞 UI 线程直到念完，这里必须异步。
            type.InvokeMember("Speak",
                System.Reflection.BindingFlags.InvokeMethod,
                null, voice, new object[] { text.Trim(), 1 });

            _lastSpoken = DateTime.Now;
            LogService.LogEvent("语音：" + text.Trim());
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("语音：朗读失败 " + ex.Message);
            return false;
        }
        finally
        {
            Release(voice);
            if (inited) try { CoUninitialize(); } catch { }
        }
    }

    private static bool InitCom()
    {
        try
        {
            int hr = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded | CoinitDisableOle1Dde);
            // S_OK / S_FALSE = 我们初始化了，需要配平；RPC_E_CHANGED_MODE = 别处已初始化，不能配平
            if (hr == RpcEChangedMode) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Release(object? obj)
    {
        if (obj == null) return;
        try
        {
            if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
        catch { }
    }
}
