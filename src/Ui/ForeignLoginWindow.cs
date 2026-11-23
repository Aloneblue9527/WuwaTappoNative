using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WuwatappoNative.Core;
using WuwatappoNative.Net;

namespace WuwatappoNative.Ui;

/// <summary>
/// 国际服官方网页登录窗口。登录页会回调 wuwa-buddy://auth/callback?...；
/// 这里在 WebView2 进程内拦截，不注册系统 wuwa-buddy 协议，也不启动官方客户端。
/// </summary>
public sealed class ForeignLoginWindow : Form
{
    const string LoginHost = "login-wutheringwaves-wuwatappo.kurogames-global.com";
    const string LoginUrlBase = "https://" + LoginHost + "/login/";
    static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);
    // 第三方登录的 redirect 页会执行 window.parent.opener.postMessage(...)（X/Line/Naver
    // 的 code 是 123123，Facebook/Apple 的载荷不同）。WebView2 的弹出窗口有时把
    // opener 指到顶层登录页或中间弹窗而不是 H5 SDK iframe，这里统一转发到 SDK iframe：
    // 顶层有 sdk-frame 时转发给它；中间弹窗没有 sdk-frame 时转发给自己的 opener。
    const string PopupMessageBridge =
        "(()=>{if(window.top!==window)return;" +
        "window.addEventListener('message',function(e){try{var d=e.data;if(!d)return;" +
        "var f=document.getElementById('sdk-frame');" +
        "if(f&&f.contentWindow&&e.source!==f.contentWindow){f.contentWindow.postMessage(d,'*');return;}" +
        "if(window.opener&&e.source!==window.opener)window.opener.postMessage(d,'*');" +
        "}catch(x){}},false);})();";

    readonly string _expectedState;
    readonly string _profileDir;
    readonly List<Form> _popups = new();
    WebView2 _web;
    CoreWebView2Environment _environment;
    System.Windows.Forms.Timer _timeoutTimer;
    Form _warmPopup;
    WebView2 _warmPopupWeb;
    bool _warmPopupReady;
    bool _warmPopupBusy;
    bool _completed;

    public ForeignSessionInfo Session { get; private set; }
    public string ErrorMessage { get; private set; } = "";

    public ForeignLoginWindow()
    {
        _expectedState = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        CleanupStaleProfiles();
        _profileDir = Path.Combine(Paths.CacheDir, "webview-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileDir);
        Text = Localization.T("webLogin.title", "国际服网页登录");
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 760);
        MinimumSize = new Size(760, 560);
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(251, 237, 213);

        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(251, 237, 213),
        };
        Controls.Add(_web);
    }

    public ForeignSessionInfo ShowLogin(IWin32Window owner)
    {
        ShowDialog(owner);
        return Session;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            Log.Info("网页登录：初始化 WebView2，profile=" + _profileDir, "webview2");
            _environment = await CoreWebView2Environment.CreateAsync(null, _profileDir);
            await _web.EnsureCoreWebView2Async(_environment);
            try { _web.CoreWebView2.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None; } catch { }
            Log.Info("网页登录：WebView2 已初始化 runtime=" + (_environment.BrowserVersionString ?? "unknown"), "webview2");
            await ConfigureCoreAsync(_web.CoreWebView2, this);
            _timeoutTimer = new System.Windows.Forms.Timer { Interval = (int)LoginTimeout.TotalMilliseconds };
            _timeoutTimer.Tick += (s, args) =>
            {
                _timeoutTimer.Stop();
                if (_completed) return;
                ErrorMessage = Localization.T("webLogin.timeout", "登录超时，请重新打开登录窗口");
                _completed = true;
                Close();
            };
            _timeoutTimer.Start();
            await EnsureWarmPopupAsync();
            string lang = Localization.Current == "ja" ? "ja-jp" : (Localization.Current == "en" ? "en-us" : "zh-cn");
            string url = LoginUrlBase + "?state=" + Uri.EscapeDataString(_expectedState) +
                "&scheme=wuwa-buddy&lang=" + lang;
            Log.Info("网页登录：打开登录页", "webview2");
            _web.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Log.Exception("网页登录初始化失败", ex);
            ErrorMessage = Localization.F("webLogin.startFailed", "无法启动网页登录：{0}", ex.Message);
            _completed = true;
            MessageBox.Show(this, ErrorMessage, Localization.T("webLogin.failedTitle", "网页登录"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    async Task ConfigureCoreAsync(CoreWebView2 core, Form closeTarget)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PopupMessageBridge);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(XRedirectHintScript());
        }
        catch (Exception ex)
        {
            Log.Warn("网页登录：注入弹窗消息桥失败 " + ex.Message, "webview2");
        }
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.LaunchingExternalUriScheme += Core_LaunchingExternalUriScheme;
        if (closeTarget != null)
        {
            core.WindowCloseRequested += (s, e) =>
            {
                Log.Info("网页登录：弹窗请求关闭", "webview2");
                // redirect 页先 postMessage 再 window.close()，延后一点关窗，
                // 避免消息还没进入 SDK iframe 的事件队列就把渲染进程销毁。
                var timer = new System.Windows.Forms.Timer { Interval = 300 };
                timer.Tick += (s2, e2) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    try { closeTarget.Close(); } catch { }
                };
                timer.Start();
            };
        }
        core.ProcessFailed += (s, e) =>
        {
            Log.Warn($"网页登录 WebView2 进程失败 kind={e.ProcessFailedKind} reason={e.Reason}", "webview2");
            ShowErrorPage(Localization.F("webLogin.processExited", "登录页面进程已退出：{0}", e.ProcessFailedKind));
        };
    }

    async Task EnsureWarmPopupAsync()
    {
        if (_warmPopup != null || _environment == null || IsDisposed) return;
        Form popup = null;
        WebView2 web = null;
        try
        {
            popup = new Form
            {
                Text = Localization.T("webLogin.popupTitle", "登录"),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(720, 780),
                ShowInTaskbar = false,
                Opacity = 0,
                BackColor = Color.FromArgb(251, 237, 213),
            };
            web = new WebView2 { Dock = DockStyle.Fill };
            popup.Controls.Add(web);
            _warmPopup = popup;
            _warmPopupWeb = web;
            popup.FormClosed += (s, args) =>
            {
                _warmPopupReady = false;
                _warmPopupBusy = false;
                _warmPopup = null;
                _warmPopupWeb = null;
                try { web.Dispose(); } catch { }
                try { popup.Dispose(); } catch { }
            };
            popup.Show();
            await web.EnsureCoreWebView2Async(_environment);
            await ConfigureCoreAsync(web.CoreWebView2, popup);
            popup.Hide();
            _warmPopupReady = true;
            Log.Info("网页登录：弹窗宿主已预热", "webview2");
        }
        catch (Exception ex)
        {
            Log.Warn("网页登录：弹窗宿主预热失败，将使用按需创建 " + ex.Message, "webview2");
            _warmPopupReady = false;
            try { popup?.Close(); } catch { }
        }
    }

    void ShowWarmPopup()
    {
        if (_warmPopup == null || _warmPopup.IsDisposed) return;
        _warmPopup.StartPosition = FormStartPosition.Manual;
        _warmPopup.Location = new Point(
            Left + Math.Max(0, (Width - _warmPopup.Width) / 2),
            Top + Math.Max(0, (Height - _warmPopup.Height) / 2));
        _warmPopup.Opacity = 1;
        _warmPopup.Show(this);
        _warmPopup.Activate();
    }

    void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Log.Info("网页登录：导航 " + SafeUriForLog(e.Uri), "webview2");
        if (TryConsumeCallback(e.Uri))
        {
            e.Cancel = true;
            return;
        }
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "about" &&
            uri.Scheme != "data" && uri.Scheme != "blob")
        {
            e.Cancel = true;
        }
    }

    void Core_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            Log.Info("网页登录：登录页加载完成", "webview2");
            return;
        }
        Log.Warn("网页登录：登录页加载失败 status=" + e.WebErrorStatus, "webview2");
        ShowErrorPage(Localization.F("webLogin.loadFailed", "登录页加载失败：{0}\n请检查网络或代理后关闭窗口重试。", e.WebErrorStatus));
    }

    async void Core_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        Log.Info("网页登录：请求新窗口 " + SafeUriForLog(e.Uri), "webview2");
        if (TryConsumeCallback(e.Uri))
        {
            e.Handled = true;
            return;
        }

        // X 的 SDK 会先开一个空窗口再改 location；部分 WebView2 版本会把这个导航再报成
        // 新窗口请求，形成“子窗口的子窗口”。X/redirect 请求直接在预热窗口内导航。
        if (sender == _warmPopupWeb?.CoreWebView2 && _warmPopupBusy && IsTwitterFlowUrl(e.Uri))
        {
            Log.Info("网页登录：X 弹窗内联导航", "webview2");
            e.Handled = true;
            try { _warmPopupWeb.CoreWebView2.Navigate(e.Uri); } catch { }
            return;
        }
        if (_warmPopupReady && !_warmPopupBusy && _warmPopupWeb?.CoreWebView2 != null)
        {
            _warmPopupBusy = true;
            Log.Info("网页登录：使用预热弹窗同步接管 OAuth 窗口", "webview2");
            e.NewWindow = _warmPopupWeb.CoreWebView2;
            e.Handled = true;
            ShowWarmPopup();
            return;
        }

        // Google GSI / Apple / Facebook 等第三方登录会走 window.open，
        // 必须真正创建一个同环境子 WebView2，否则提供方会认为弹窗被拦截。
        var deferral = e.GetDeferral();
        Form popup = null;
        WebView2 child = null;
        try
        {
            popup = new Form
            {
                Text = Localization.T("webLogin.popupTitle", "登录"),
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(720, 780),
                MinimumSize = new Size(480, 560),
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(251, 237, 213),
            };
            child = new WebView2
            {
                Dock = DockStyle.Fill,
            };
            popup.Controls.Add(child);
            _popups.Add(popup);
            popup.FormClosed += (s, args) =>
            {
                _popups.Remove(popup);
                try { child.Dispose(); } catch { }
                try { popup.Dispose(); } catch { }
            };
            await child.EnsureCoreWebView2Async(_environment);
            await ConfigureCoreAsync(child.CoreWebView2, popup);
            e.NewWindow = child.CoreWebView2;
            e.Handled = true;
            popup.Show(this);
        }
        catch (Exception ex)
        {
            Log.Exception("网页登录弹窗初始化失败", ex);
            ErrorMessage = Localization.F("webLogin.popupFailed", "无法打开登录弹窗：{0}", ex.Message);
            e.Handled = true;
            try { popup?.Close(); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    void Core_LaunchingExternalUriScheme(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        // 绝不让系统协议处理器启动官方客户端；回调已在 NavigationStarting 中消费。
        e.Cancel = true;
    }

    static string XRedirectHintScript()
    {
        string title = JsString(Localization.T("webLogin.xRedirectTitle", "X 授权已完成"));
        string message = JsString(Localization.T(
            "webLogin.xRedirectHint",
            "如果当前页面白屏，请关闭这个窗口，然后再次点击 X 登录；第二次会直接成功。"));
        string close = JsString(Localization.T("webLogin.xRedirectClose", "关闭窗口"));
        return "(()=>{try{if(window.top!==window)return;" +
               "if(location.pathname.indexOf('/pro/h5sdk/redirect')<0)return;" +
               "if(location.search.indexOf('type=twitter')<0)return;" +
               "if(document.getElementById('wn-x-retry-hint'))return;" +
               "var b=document.createElement('div');b.id='wn-x-retry-hint';" +
               "b.style.cssText='position:fixed;inset:0;z-index:2147483647;background:#fbefd5;color:#5c4d42;" +
               "display:flex;align-items:center;justify-content:center;text-align:center;padding:40px;" +
               "font-family:\"Microsoft YaHei UI\",Segoe UI,sans-serif;line-height:1.8';" +
               "b.innerHTML='<div><h2 style=\"margin:0 0 12px;font-size:20px\">'+" + title + "+'</h2><p style=\"margin:0 0 20px;font-size:14px\">'+" + message + "+'</p><button id=\"wn-x-retry-close\" style=\"padding:8px 22px;border:1px solid #c9a77a;border-radius:6px;background:#f5e4cc;color:#5c4d42;font-size:14px;cursor:pointer\">'+" + close + "+'</button></div>';" +
               "var root=document.documentElement||document.body;" +
               "if(root)root.appendChild(b);else document.addEventListener('DOMContentLoaded',function(){document.body&&document.body.appendChild(b);});" +
               "var c=document.getElementById('wn-x-retry-close');if(c)c.onclick=function(){window.close();};" +
               "}catch(e){}})();";
    }

    static string JsString(string value)
    {
        return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
    }

    static bool IsTwitterFlowUrl(string uri)
    {
        if (!Uri.TryCreate(uri ?? "", UriKind.Absolute, out var parsed)) return false;
        string host = parsed.Host.ToLowerInvariant();
        if (host == "x.com" || host.EndsWith(".x.com", StringComparison.Ordinal) ||
            host == "twitter.com" || host.EndsWith(".twitter.com", StringComparison.Ordinal))
            return true;
        return parsed.AbsolutePath.Contains("/login/redirect", StringComparison.OrdinalIgnoreCase)
            && parsed.Query.Contains("type=twitter", StringComparison.OrdinalIgnoreCase);
    }

    static string SafeUriForLog(string uri)
    {
        if (!Uri.TryCreate(uri ?? "", UriKind.Absolute, out var parsed)) return "<invalid>";
        if (parsed.Scheme == "wuwa-buddy") return "wuwa-buddy://" + parsed.Host + parsed.AbsolutePath;
        if (parsed.Scheme == "data" || parsed.Scheme == "blob") return parsed.Scheme + ":";
        return parsed.Scheme + "://" + parsed.Host + parsed.AbsolutePath;
    }

    bool TryConsumeCallback(string uri)
    {
        if (_completed || !Uri.TryCreate(uri ?? "", UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, "wuwa-buddy", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!ForeignSessionCallback.TryParse(uri, _expectedState, out var session, out string error))
        {
            ErrorMessage = error;
            _completed = true;
            Close();
            return true;
        }
        Session = session;
        ErrorMessage = "";
        _completed = true;
        Close();
        return true;
    }

    static void CleanupStaleProfiles()
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(Paths.CacheDir, "webview-login*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                        Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

    void ShowErrorPage(string message)
    {
        ErrorMessage = message;
        try
        {
            var core = _web?.CoreWebView2;
            if (core == null) return;
            string safe = WebUtility.HtmlEncode(message ?? "").Replace("\n", "<br>");
            core.NavigateToString(
                "<!doctype html><html><head><meta charset=\"utf-8\">" +
                "<style>body{margin:0;padding:48px;background:#fbefd5;color:#5c4d42;" +
                "font-family:'Microsoft YaHei UI',Segoe UI,sans-serif;line-height:1.7}" +
                "h2{font-size:20px;margin:0 0 12px}p{font-size:14px;margin:0}</style></head>" +
                "<body><h2>" + WebUtility.HtmlEncode(Localization.T("webLogin.errorPageTitle", "无法打开登录页")) + "</h2><p>" + safe + "</p></body></html>");
        }
        catch (Exception ex)
        {
            Log.Exception("显示网页登录错误页失败", ex);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _timeoutTimer?.Stop(); _timeoutTimer?.Dispose(); } catch { }
        foreach (var popup in _popups.ToArray())
        {
            try { popup.Close(); popup.Dispose(); } catch { }
        }
        _popups.Clear();
        try { _warmPopup?.Close(); } catch { }
        try { _warmPopup?.Dispose(); } catch { }
        _warmPopup = null;
        _warmPopupWeb = null;
        _warmPopupReady = false;
        _warmPopupBusy = false;
        try
        {
            var core = _web?.CoreWebView2;
            if (core != null)
            {
                core.CookieManager.DeleteAllCookies();
                _ = core.ExecuteScriptAsync("localStorage.clear();sessionStorage.clear();");
            }
        }
        catch { }
        try { _web?.Dispose(); } catch { }
        _web = null;
        _environment = null;
        try { Directory.Delete(_profileDir, true); } catch { }
        base.OnFormClosed(e);
    }
}
