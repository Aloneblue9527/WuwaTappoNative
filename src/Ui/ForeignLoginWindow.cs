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

    readonly string _expectedState;
    readonly string _profileDir;
    readonly List<Form> _popups = new();
    WebView2 _web;
    CoreWebView2Environment _environment;
    System.Windows.Forms.Timer _timeoutTimer;
    bool _completed;

    public ForeignSessionInfo Session { get; private set; }
    public string ErrorMessage { get; private set; } = "";

    public ForeignLoginWindow()
    {
        _expectedState = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _profileDir = Path.Combine(Paths.CacheDir, "webview-login");
        Directory.CreateDirectory(_profileDir);
        Text = "国际服网页登录";
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
            Log.Info("网页登录：WebView2 已初始化 runtime=" + (_environment.BrowserVersionString ?? "unknown"), "webview2");
            ConfigureCore(_web.CoreWebView2);
            _timeoutTimer = new System.Windows.Forms.Timer { Interval = (int)LoginTimeout.TotalMilliseconds };
            _timeoutTimer.Tick += (s, args) =>
            {
                _timeoutTimer.Stop();
                if (_completed) return;
                ErrorMessage = "登录超时，请重新打开登录窗口";
                _completed = true;
                Close();
            };
            _timeoutTimer.Start();
            string url = LoginUrlBase + "?state=" + Uri.EscapeDataString(_expectedState) +
                "&scheme=wuwa-buddy&lang=zh-cn";
            Log.Info("网页登录：打开登录页", "webview2");
            _web.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Log.Exception("网页登录初始化失败", ex);
            ErrorMessage = "无法启动网页登录：" + ex.Message;
            _completed = true;
            MessageBox.Show(this, ErrorMessage, "网页登录", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.LaunchingExternalUriScheme += Core_LaunchingExternalUriScheme;
        core.ProcessFailed += (s, e) =>
        {
            Log.Warn($"网页登录 WebView2 进程失败 kind={e.ProcessFailedKind} reason={e.Reason}", "webview2");
            ShowErrorPage("登录页面进程已退出：" + e.ProcessFailedKind);
        };
    }

    void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (TryConsumeCallback(e.Uri))
        {
            e.Cancel = true;
            return;
        }
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "about")
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
        ShowErrorPage("登录页加载失败：" + e.WebErrorStatus + "\n请检查网络或代理后关闭窗口重试。");
    }

    async void Core_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (TryConsumeCallback(e.Uri))
        {
            e.Handled = true;
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
                Text = "登录",
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
            ConfigureCore(child.CoreWebView2);
            popup.Show(this);
            e.NewWindow = child.CoreWebView2;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Exception("网页登录弹窗初始化失败", ex);
            ErrorMessage = "无法打开登录弹窗：" + ex.Message;
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
                "<body><h2>无法打开登录页</h2><p>" + safe + "</p></body></html>");
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
        base.OnFormClosed(e);
    }
}
