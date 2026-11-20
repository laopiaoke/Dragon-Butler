using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DragonButler;

/// <summary>
/// 米游社登录（v1.31）。见 MiyousheLoginWindow.xaml 顶部的说明。
///
/// 两条路都通向同一件事：拿到那四件套（cookie_token / stuid / stoken / mid）。
/// 走浏览器那条是主路——它把这套东西里唯一需要用户动脑的步骤（抄 cookie）去掉了。
/// </summary>
public partial class MiyousheLoginWindow : Window
{
    private const string LoginUrl = "https://user.mihoyo.com/";

    private EdgeCdp? _cdp;
    private CancellationTokenSource? _poll;
    private MiyousheQr.Session? _qr;
    private CancellationTokenSource? _qrPoll;

    /// <summary>保存成功后置 true，调用方据此决定要不要刷新界面。</summary>
    public bool Saved { get; private set; }

    public MiyousheLoginWindow()
    {
        InitializeComponent();
        LoadSettingsIntoUi();
    }

    private void LoadSettingsIntoUi()
    {
        var s = AppState.Settings;
        bool logged = !string.IsNullOrWhiteSpace(s.MiyousheCookie);
        TbState.Text = logged
            ? (string.IsNullOrWhiteSpace(s.MiyousheNickname) ? "已登录" : "已登录 · " + s.MiyousheNickname)
            : "还没登录";

        // 已有的 cookie 不明文回显——它不是密码，但铺满一屏也帮不上忙。
        // 只显示「有」和长度，用户想换就整段覆盖。
        if (logged)
        {
            TbCookie.Text = "";
            TbCookie.ToolTip = "已经有一份凭证了（" + s.MiyousheCookie.Length + " 字符）。要换就整段粘新的，留空表示不改。";
            TbRecognize.Text = "现在存着的这份还能用。要换账号就整段覆盖；不动就直接关掉。";
            TbRecognize.Foreground = Brush("TextTertiaryBrush");
        }
        if (!string.IsNullOrWhiteSpace(s.MiyousheStoken))
            TbStoken.ToolTip = "已经有一份 stoken。留空表示不改。";
    }

    private static Brush? Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    // ===== 主路径：开浏览器、等登录、收 cookie =====

    /// <summary>
    /// 扫码登录（v1.31 的主路）。点一下 → 要 ticket → 自己画二维码 → 每 1.5 秒问一次扫了没。
    ///
    /// 为什么要自己画二维码、而不是开网页：那张扫码页在桌面浏览器里不渲染真二维码
    /// （它期待用户用手机 App 扫手机那一屏），我们只能拿回包里的 url 自己编。
    /// </summary>
    private async void BtnQr_Click(object sender, RoutedEventArgs e)
    {
        StopQr();
        BtnQr.IsEnabled = false;
        TbQrHint.Text = "正在向米游社要二维码…";
        TbQrHint.Foreground = Brush("TextTertiaryBrush");
        QrPanel.Visibility = Visibility.Visible;
        QrImage.Source = null;

        _qrPoll = new CancellationTokenSource();
        var token = _qrPoll.Token;

        var (session, reason) = await MiyousheQr.CreateAsync(token);
        if (session == null)
        {
            TbQrHint.Text = reason;
            TbQrHint.Foreground = Brush("WarningBrush");
            BtnQr.IsEnabled = true;
            return;
        }

        _qr = session;

        // 二维码内容用官方给的 url：里面带 expire 和 tk，扫完米游社自己认。
        var bmp = QrBitmap.Render(session.Url, scale: 6, quiet: 4);
        if (bmp == null)
        {
            TbQrHint.Text = "二维码画不出来（内容太长）。先用「用浏览器登录」。";
            TbQrHint.Foreground = Brush("WarningBrush");
            BtnQr.IsEnabled = true;
            return;
        }

        QrImage.Source = bmp;
        TbQrHint.Text = "手机上的米游社 → 右上角扫一扫。";

        await WatchQrAsync(session, token);
    }

    /// <summary>盯着二维码的状态。扫到 / 确认 / 过期 / 取消，各自有话说。</summary>
    private async Task WatchQrAsync(MiyousheQr.Session session, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1500, token);
                var (state, cred, reason) = await MiyousheQr.PollAsync(session, token);

                switch (state)
                {
                    case MiyousheQr.QrState.Created:
                    case MiyousheQr.QrState.Unknown:
                        continue;

                    case MiyousheQr.QrState.Scanned:
                        TbQrHint.Text = reason.Length > 0 ? reason : "扫到了，在手机上点一下确认。";
                        TbQrHint.Foreground = Brush("BrandBrush");
                        continue;

                    case MiyousheQr.QrState.Confirmed:
                        FinishQr(cred);
                        return;

                    case MiyousheQr.QrState.Expired:
                    case MiyousheQr.QrState.Canceled:
                    case MiyousheQr.QrState.Failed:
                        TbQrHint.Text = reason.Length > 0 ? reason : "这次没成，再点一次「扫码登录」。";
                        TbQrHint.Foreground = Brush("WarningBrush");
                        BtnQr.IsEnabled = true;
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                TbQrHint.Text = "等扫码结果时出了点岔子：" + ex.Message;
                TbQrHint.Foreground = Brush("WarningBrush");
                BtnQr.IsEnabled = true;
            }
        }
    }

    /// <summary>扫完了：把那几样写进设置，然后关窗。</summary>
    private void FinishQr(MiyousheQr.Credential? cred)
    {
        StopQr();

        if (cred == null || string.IsNullOrWhiteSpace(cred.Stoken))
        {
            TbQrHint.Text = "扫是扫上了，但没拿到续期凭证。重新扫一次；还是这样就先用「用浏览器登录」。";
            TbQrHint.Foreground = Brush("WarningBrush");
            BtnQr.IsEnabled = true;
            return;
        }

        // cookie 里必须有 cookie_token 才能签到。确认回包有时只给 stoken，
        // 那就先用 stoken 换一次 cookie_token——这正是 MiyousheService 每次签到前做的事，
        // 这里提前做一遍，用户点完就能立刻看到「已登录」。
        string cookie = cred.Cookie;
        if (cookie.Length == 0 || !cookie.Contains("cookie_token=", StringComparison.Ordinal))
        {
            try
            {
                var (ok, fresh, _) = MiyousheSign
                    .RefreshCookieTokenAsync(cred.Stuid, cred.Stoken, cred.Mid, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (ok && fresh.Length > 0)
                    cookie = $"cookie_token={fresh}" +
                             (cred.Stuid.Length > 0 ? $"; ltuid={cred.Stuid}" : "");
            }
            catch { }
        }

        if (cookie.Length == 0 || !cookie.Contains("cookie_token=", StringComparison.Ordinal))
        {
            TbQrHint.Text = "拿到了续期凭证，但换不出签到要用的 cookie_token。"
                          + "先用「用浏览器登录」那个入口，它能一次收全。";
            TbQrHint.Foreground = Brush("WarningBrush");
            BtnQr.IsEnabled = true;
            return;
        }

        Persist(cookie, cred.Stoken, cred.Stuid, cred.Mid);
        if (cred.Nickname.Length > 0) AppState.Settings.MiyousheNickname = cred.Nickname;
        AppState.SaveSettings();

        Saved = true;
        TbState.Text = "已登录" + (cred.Nickname.Length > 0 ? " · " + cred.Nickname : "");
        TbQrHint.Text = "好了。凭证已经收下，关掉这个窗口就能用。";
        TbQrHint.Foreground = Brush("BrandBrush");

        Dispatcher.BeginInvoke(new Action(() => { DialogResult = true; Close(); }),
            DispatcherPriority.Background);
    }

    private void StopQr()
    {
        try { _qrPoll?.Cancel(); } catch { }
        _qrPoll = null;
        _qr = null;
        BtnQr.IsEnabled = true;
    }

    private async void BtnBrowser_Click(object sender, RoutedEventArgs e)
    {
        BtnBrowser.IsEnabled = false;
        BtnCancel.Visibility = Visibility.Visible;
        TbBrowserHint.Text = "正在打开浏览器…";

        var (cdp, reason) = await EdgeCdp.LaunchAsync(LoginUrl);
        if (cdp == null)
        {
            TbBrowserHint.Text = reason;
            TbBrowserHint.Foreground = Brush("WarningBrush");
            BtnBrowser.IsEnabled = true;
            BtnCancel.Visibility = Visibility.Collapsed;
            return;
        }

        _cdp = cdp;
        TbBrowserHint.Text = "浏览器已打开。在里面登录，成功之后这边自己会继续——不用回来点任何东西。";
        TbBrowserHint.Foreground = Brush("TextTertiaryBrush");

        _poll = new CancellationTokenSource();
        await WatchLoginAsync(_poll.Token);
    }

    /// <summary>
    /// 盯着那个浏览器，直到看见登录态。每 1.2 秒看一眼：地址离开登录页 + cookie 里有身份字段。
    /// 用轮询而不是 CDP 事件，是因为登录页是单页应用、跳转时机不确定，轮询反而更省事也更稳。
    /// </summary>
    private async Task WatchLoginAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _cdp != null)
            {
                await Task.Delay(1200, token);
                bool loggedIn = await _cdp.LooksLoggedInAsync(token);
                if (!loggedIn) continue;

                var cookies = await _cdp.CookiesAsync(token);
                ApplyCookies(cookies);
                StopBrowser();
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                TbBrowserHint.Text = "读取登录结果时出了点岔子：" + ex.Message + "。也可以改成手填。";
                TbBrowserHint.Foreground = Brush("WarningBrush");
                StopBrowser();
            }
        }
    }

    /// <summary>把浏览器里读到的 cookie 拆成四件套，能存就存，然后关窗。</summary>
    private void ApplyCookies(System.Collections.Generic.Dictionary<string, string> cookies)
    {
        string raw = string.Join("; ", cookies.Select(kv => kv.Key + "=" + kv.Value));
        var (cookie, stoken, stuid, mid, reason) = MiyousheSession.FromCookieString(raw);
        if (reason.Length > 0)
        {
            TbBrowserHint.Text = "登录看起来成功了，但 " + reason;
            TbBrowserHint.Foreground = Brush("WarningBrush");
            return;
        }

        Persist(cookie, stoken, stuid, mid);
        TbState.Text = "已登录 · 刚刚";
        TbBrowserHint.Text = "好了。凭证已经收下，关掉这个窗口就能用。";
        TbBrowserHint.Foreground = Brush("BrandBrush");
        Saved = true;

        // 收完就关：用户此刻的目的达成了，留一个空窗口在那儿只会让人以为还没完。
        Dispatcher.BeginInvoke(new Action(() => { DialogResult = true; Close(); }),
            DispatcherPriority.Background);
    }

    private void StopBrowser()
    {
        try { _poll?.Cancel(); } catch { }
        _poll = null;
        try { _cdp?.Dispose(); } catch { }
        _cdp = null;
        BtnBrowser.IsEnabled = true;
        BtnCancel.Visibility = Visibility.Collapsed;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        StopBrowser();
        TbBrowserHint.Text = "已停下。想再试一次就再点上面那个按钮。";
        TbBrowserHint.Foreground = Brush("TextTertiaryBrush");
    }

    // ===== 后路：手填 =====

    private void BtnRecognize_Click(object sender, RoutedEventArgs e)
    {
        string raw = TbCookie.Text ?? "";
        if (raw.Trim().Length == 0)
        {
            // 没粘新东西 = 想让我认已经存着的那份。
            raw = AppState.Settings.MiyousheCookie;
            if (raw.Length == 0)
            {
                TbRecognize.Text = "上面还是空的。要么粘一份，要么用浏览器登录。";
                TbRecognize.Foreground = Brush("WarningBrush");
                return;
            }
        }

        var (cookie, stoken, stuid, mid, reason) = MiyousheSession.FromCookieString(
            raw, AppState.Settings.MiyousheStuid, AppState.Settings.MiyousheMid);

        if (reason.Length > 0)
        {
            TbRecognize.Text = reason;
            TbRecognize.Foreground = Brush("WarningBrush");
            return;
        }

        var bits = new System.Collections.Generic.List<string>();
        bits.Add("cookie_token：有");
        bits.Add(string.IsNullOrWhiteSpace(stuid) ? "账号 uid：没认出来" : "账号 uid：" + stuid);
        bits.Add(string.IsNullOrWhiteSpace(stoken) ? "stoken：没有（能签到，过期后要再登一次）" : "stoken：有，能自动续期");
        if (MiyousheSession.NeedsMid(stoken))
            bits.Add(string.IsNullOrWhiteSpace(mid) ? "mid：缺（v2_ 的 stoken 必须有）" : "mid：有");

        TbRecognize.Text = string.Join("　·　", bits);
        TbRecognize.Foreground = Brush(string.IsNullOrWhiteSpace(stoken) ? "WarningBrush" : "BrandBrush");
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string raw = TbCookie.Text ?? "";
        string stokenTyped = TbStoken.Text ?? "";

        if (raw.Trim().Length == 0 && stokenTyped.Trim().Length == 0)
        {
            // 什么都没输：如果本来就有凭证，等于「不改」，直接关。
            if (!string.IsNullOrWhiteSpace(AppState.Settings.MiyousheCookie))
            {
                DialogResult = Saved;
                Close();
                return;
            }
            TbRecognize.Text = "还没有凭证。用上面那个按钮登录，或者粘一份 cookie 进来。";
            TbRecognize.Foreground = Brush("WarningBrush");
            return;
        }

        var s = AppState.Settings;
        string baseCookie = raw.Trim().Length > 0 ? raw : s.MiyousheCookie;
        var (cookie, stoken, stuid, mid, reason) = MiyousheSession.FromCookieString(
            baseCookie, s.MiyousheStuid, s.MiyousheMid);

        if (reason.Length > 0)
        {
            TbRecognize.Text = reason;
            TbRecognize.Foreground = Brush("WarningBrush");
            return;
        }

        // 手填 stoken：用户只补了 stoken 也算数，别把 cookie 一起丢了。
        if (stokenTyped.Trim().Length > 0) stoken = stokenTyped.Trim();

        Persist(cookie, stoken, stuid, mid);
        Saved = true;
        DialogResult = true;
        Close();
    }

    /// <summary>写设置。Nickname 顺手清空——换了账号还挂着上一个号的名字最糟。</summary>
    private void Persist(string cookie, string stoken, string stuid, string mid)
    {
        var s = AppState.Settings;
        s.MiyousheCookie = cookie;
        s.MiyousheStoken = stoken;
        s.MiyousheStuid = stuid;
        s.MiyousheMid = mid;
        s.MiyousheNickname = "";
        AppState.SaveSettings();
    }

    // ===== 窗口 =====

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = Saved;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopQr();
        StopBrowser();
        base.OnClosed(e);
    }
}
