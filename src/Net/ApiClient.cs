using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

public sealed class ApiException : Exception
{
    public int Code { get; }
    public string RequestId { get; }
    /// <summary>非 2xx 响应时的原始 HTTP 状态码；业务错误通常为 0。</summary>
    public int HttpStatus { get; }
    public ApiException(int code, string message, string requestId = null, int httpStatus = 0) : base(message ?? "请求失败")
    {
        Code = code;
        RequestId = requestId;
        HttpStatus = httpStatus;
    }
}

/// <summary>
/// 官方 REST 封装。信封 {code,message,request_id,data}；code 0/200 为成功。
/// 请求头固定带 X-BLM-CID（本机持久 UUID）与每请求随机的 X-BLM-FRID。
/// </summary>
public sealed class ApiClient : IDisposable
{
    public const string DefaultBaseUrl = "https://api-mc-wuwatappo.kurogames-global.com";
    public const string GlobalBaseUrl = "https://api-wutheringwaves-wuwatappo.kurogames-global.com";

    public static string ResolveBaseUrl(string region)
    {
        if (string.Equals(region, "global", StringComparison.OrdinalIgnoreCase))
            return GlobalBaseUrl;
        return DefaultBaseUrl;
    }

    public string BaseUrl { get; }
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    readonly HttpClient _http;
    readonly CookieContainer _cookies = new();
    string _token = "";
    string _acceptLanguage = "zh-CN,zh;q=0.9,en;q=0.8";
    long _serverOffsetMs;
    public string Token => _token;
    public string LastCid { get; private set; } = "";
    /// <summary>服务器时间减去本机时间（毫秒）；用于本机时钟不准时校准宝箱倒计时。</summary>
    public long ServerTimeOffsetMs => Interlocked.Read(ref _serverOffsetMs);
    /// <summary>当前服务器时间（毫秒）；未收到服务器 Date 头时退回本机时间。</summary>
    public long ServerNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerTimeOffsetMs;
    public event Action<string> TokenRefreshed;
    /// <summary>
    /// 账号请求门禁：返回非空字符串时直接拒绝该请求，不落到网络层。
    /// 探针、显式登录/退出/恢复流程通过 bypassGate 绕过。
    /// </summary>
    public Func<string, string> Gate { get; set; }
    /// <summary>账号相关请求返回 401/403/10002/10003/90002 时触发，用于统一登录态判定。</summary>
    public event Action<string, ApiException> RequestFailed;

    public ApiClient(string cid, string baseUrl = null, string acceptLanguage = null)
    {
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
        LastCid = string.IsNullOrWhiteSpace(cid) ? Guid.NewGuid().ToString("N") : cid;
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _acceptLanguage = string.IsNullOrWhiteSpace(acceptLanguage) ? "zh-CN,zh;q=0.9,en;q=0.8" : acceptLanguage;
    }

    public void SetToken(string token) => _token = token ?? "";
    public void SetAcceptLanguage(string value)
    {
        _acceptLanguage = string.IsNullOrWhiteSpace(value) ? "zh-CN,zh;q=0.9,en;q=0.8" : value;
    }
    public string Cid => LastCid;

    public Task<JsonNode> GetAsync(string path, bool bypassGate = false) => SendAsync(HttpMethod.Get, path, null, bypassGate);
    public Task<JsonNode> PostAsync(string path, object body = null, bool bypassGate = false) => SendAsync(HttpMethod.Post, path, body, bypassGate);
    public Task<JsonNode> PutAsync(string path, object body = null, bool bypassGate = false) => SendAsync(HttpMethod.Put, path, body, bypassGate);
    public Task<JsonNode> DeleteAsync(string path, object body = null, bool bypassGate = false) => SendAsync(HttpMethod.Delete, path, body, bypassGate);

    async Task<JsonNode> SendAsync(HttpMethod method, string path, object body, bool bypassGate = false)
    {
        if (!bypassGate && Gate != null)
        {
            string blocked = Gate(path);
            if (!string.IsNullOrEmpty(blocked))
                throw new ApiException(-1, blocked);
        }

        using var req = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : path.TrimStart('/'));
        req.Headers.TryAddWithoutValidation("X-BLM-CID", LastCid);
        req.Headers.TryAddWithoutValidation("X-BLM-FRID", Guid.NewGuid().ToString("N"));
        req.Headers.TryAddWithoutValidation("Accept-Language", _acceptLanguage);
        req.Headers.TryAddWithoutValidation("Origin", "https://tauri.localhost");
        req.Headers.TryAddWithoutValidation("Referer", "https://tauri.localhost/");
        if (!string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");

        long requestStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requestWatch = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        int status = (int)resp.StatusCode;
        long requestEndedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 用服务器 Date 头校准本机时钟：宝箱解锁时间戳是服务器时间，
        // 用户 BIOS/Windows 时间慢几十分钟时不能直接与本机时间比较。
        if (resp.Headers.Date is DateTimeOffset serverDate)
        {
            long midpoint = requestStartedMs + Math.Max(0, requestEndedMs - requestStartedMs) / 2;
            Interlocked.Exchange(ref _serverOffsetMs, serverDate.ToUnixTimeMilliseconds() - midpoint);
        }
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (status == 601)
        {
            LogSlowRequest(requestWatch, method, path, status, null);
            throw new ApiException(601, Localization.T("api.maintenance", "当前服务正在维护中，请稍后再试。"), null);
        }
        if (!resp.IsSuccessStatusCode)
        {
            // 非 2xx 响应也可能带业务 code（例如 403 + 10002）。先解析信封，
            // 避免把“临时 403/风控”与真正的 10002/10003 登录失效混为一谈。
            int bizCode = 0;
            string bizMessage = null;
            string bizRequestId = null;
            try
            {
                if (JsonNode.Parse(text) is JsonObject error)
                {
                    bizCode = ReadInt(error["code"]);
                    bizMessage = error["message"]?.GetValue<string>();
                    bizRequestId = error["request_id"]?.GetValue<string>();
                }
            }
            catch { }

            bool hasBusinessCode = bizCode != 0 && bizCode != 200;
            int httpCode = hasBusinessCode ? bizCode : status;
            string httpMessage = hasBusinessCode
                ? DescribeCode(bizCode, bizMessage)
                : DescribeHttpStatus(status);
            string httpRequestId = bizRequestId ?? ResponseRequestId(resp);
            Log.Write($"API HTTP 错误 status={status} code={httpCode} message={bizMessage ?? ""} request_id={httpRequestId ?? ""} path={path}");
            LogSlowRequest(requestWatch, method, path, status, httpRequestId);
            var failure = new ApiException(httpCode, httpMessage, httpRequestId, status);
            if (!bypassGate && ShouldRaiseRequestFailed(status, bizCode, hasBusinessCode))
                RequestFailed?.Invoke(path, failure);
            throw failure;
        }

        JsonNode root;
        try { root = JsonNode.Parse(text); }
        catch
        {
            LogSlowRequest(requestWatch, method, path, status, null);
            throw new ApiException(-1, Localization.T("api.badResponse", "响应格式异常"), null);
        }
        if (root is not JsonObject obj)
        {
            LogSlowRequest(requestWatch, method, path, status, null);
            throw new ApiException(-1, Localization.T("api.badResponse", "响应格式异常"), null);
        }

        int code = ReadInt(obj["code"]);
        string message = obj["message"]?.GetValue<string>();
        string requestId = obj["request_id"]?.GetValue<string>();
        if (code != 0 && code != 200)
        {
            Log.Write($"API 业务错误 code={code} message={message} request_id={requestId} path={path}");
            LogSlowRequest(requestWatch, method, path, status, requestId);
            var failure = new ApiException(code, DescribeCode(code, message), requestId);
            if (!bypassGate && IsAuthFailureCode(code))
                RequestFailed?.Invoke(path, failure);
            throw failure;
        }

        LogSlowRequest(requestWatch, method, path, status, requestId);
        ReadTokenCookie();
        return obj["data"];
    }

    /// <summary>只记录 method/path/status/elapsed/request_id；不记录请求体或凭据。</summary>
    static void LogSlowRequest(Stopwatch watch, HttpMethod method, string path, int status, string requestId)
    {
        try
        {
            long elapsed = watch.ElapsedMilliseconds;
            if (elapsed <= 2000) return;
            Log.Warn($"API 慢请求 method={method?.Method} path={path} status={status} elapsed={elapsed}ms request_id={requestId ?? ""}", "api");
        }
        catch { }
    }

    static string ResponseRequestId(HttpResponseMessage response)
    {
        try
        {
            return response.Headers.TryGetValues("X-Request-Id", out var values)
                ? values.FirstOrDefault()
                : null;
        }
        catch { return null; }
    }

    static bool IsAuthFailureCode(int code)
        => code == 401 || code == 403 || code == 10002 || code == 10003 || code == 90002;

    /// <summary>
    /// 非 2xx 响应是否要通知登录态监听者。普通 HTTP 403 仍按临时拒绝处理（由 AuthService 复用探针确认）；
    /// 但带业务码的 403 必须看业务码：10007 只是“请求被服务器拒绝”，不能当成登录失效。
    /// </summary>
    internal static bool ShouldRaiseRequestFailed(int httpStatus, int businessCode, bool hasBusinessCode)
    {
        if (httpStatus == 401) return true;
        if (hasBusinessCode) return IsAuthFailureCode(businessCode);
        return httpStatus == 403;
    }

    /// <summary>
    /// 把服务器业务码统一映射成面向用户的中文。原始 message 仍会写入日志便于排查，
    /// 但不再直接出现在弹窗/房间错误里，避免 room is full / room not exist 之类的英文。
    /// </summary>
    internal static string DescribeCode(int code, string message)
    {
        string zh = code switch
        {
            10001 => Localization.T("api.code.10001", "参数无效，请确认 UID 与验证码都是数字"),
            10002 or 10003 => Localization.T("api.code.10002", "登录已失效，请重新登录"),
            10007 => Localization.T("api.code.10007", "请求被服务器拒绝，请稍后重试"),
            10023 => Localization.T("api.code.10023", "验证通道暂不可用，请改用游戏 UID 或继承官方客户端登录"),
            10024 => Localization.T("api.code.10024", "游戏登录失败，请检查账号或区服后重试"),
            10025 => Localization.T("api.code.10025", "查询不到该 UID 对应的游戏角色，请确认当前区服与 UID 正确"),
            10031 => Localization.T("api.code.10031", "验证码发送过于频繁，请稍后再试"),
            10032 => Localization.T("api.code.10032", "验证码发送失败，请稍后再试，或改用继承官方登录"),
            10034 => Localization.T("api.code.10034", "验证码已失效，请重新获取"),
            10035 => Localization.T("api.code.10035", "验证码不正确或已过期"),
            20017 => Localization.T("api.code.20017", "当前角色不能使用该表情"),
            41000 => Localization.T("api.code.41000", "需要完成人机验证后重试"),
            50002 => Localization.T("api.code.50002", "收集物合成失败，请稍后再试"),
            50003 => Localization.T("api.code.50003", "收藏失败，请稍后再试"),
            50004 => Localization.T("api.code.50004", "取消收藏失败，请稍后再试"),
            50005 => Localization.T("api.code.50005", "收集物不存在"),
            50006 => Localization.T("api.code.50006", "尚未拥有该收集物"),
            60002 => Localization.T("api.code.60002", "兑换失败，请检查兑换码后重试"),
            60003 => Localization.T("api.code.60003", "兑换码已使用"),
            60004 => Localization.T("api.code.60004", "兑换码无效"),
            70003 => Localization.T("api.code.70003", "客户端点击数异常，请刷新后重试"),
            70004 => Localization.T("api.code.70004", "暂无可开启的宝箱"),
            70005 => Localization.T("api.code.70005", "可用点击数不足"),
            70006 => Localization.T("api.code.70006", "宝箱冷却中，请稍后再试"),
            70008 => Localization.T("api.code.70008", "活动未开始"),
            80003 => Localization.T("api.code.80003", "仍在其他房间中，请先退出当前房间"),
            80004 => Localization.T("api.code.80004", "房间号不能为空"),
            80007 => Localization.T("api.code.80007", "房间不存在或已解散"),
            80008 => Localization.T("api.code.80008", "房间已满"),
            80010 => Localization.T("api.code.80010", "加入房间失败，请稍后再试"),
            80013 => Localization.T("api.code.80013", "操作过于频繁，请稍后再试"),
            80018 => Localization.T("api.code.80018", "离开房间失败，请稍后再试"),
            80021 or 80023 => Localization.T("api.code.80021", "房间操作失败，请稍后再试"),
            80022 => Localization.T("api.code.80022", "点击数据异常，请刷新后重试"),
            80025 => Localization.T("api.code.80025", "暂无可加入的公开房间，请稍后再试"),
            80026 => Localization.T("api.code.80026", "加入房间过于频繁，请稍后再试"),
            90002 => Localization.T("api.code.90002", "账号已在其他设备登录"),
            601 => Localization.T("api.code.601", "当前服务正在维护中，请稍后再试"),
            _ => null
        };
        if (zh != null) return zh;

        string raw = (message ?? "").Trim();
        // 未知码如果是服务端返回的中文文案，保留它；英文一律不再直接展示。
        return HasChinese(raw) ? raw : Localization.F("api.requestFailed", "请求失败（{0}）", code);
    }

    internal static string DescribeHttpStatus(int status)
    {
        return status switch
        {
            400 => Localization.T("api.http.400", "请求参数有误，请稍后再试"),
            401 => Localization.T("api.http.401", "登录已失效，请重新登录"),
            403 => Localization.T("api.http.403", "服务器拒绝了请求，请稍后重试"),
            404 => Localization.T("api.http.404", "请求的服务不存在"),
            408 => Localization.T("api.http.408", "请求超时，请检查网络后重试"),
            429 => Localization.T("api.http.429", "请求过于频繁，请稍后再试"),
            500 or 502 or 503 or 504 => Localization.T("api.http.500", "服务器暂时不可用，请稍后再试"),
            _ => Localization.F("api.requestFailed", "请求失败（HTTP {0}）", status)
        };
    }

    internal static bool HasChinese(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
        {
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        }
        return false;
    }

    void ReadTokenCookie()
    {
        try
        {
            foreach (Cookie c in _cookies.GetCookies(new Uri(BaseUrl)))
            {
                if (string.Equals(c.Name, "infoToken", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Name, "kuro_infoToken", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(c.Value) && c.Value != _token)
                    {
                        _token = c.Value;
                        TokenRefreshed?.Invoke(_token);
                    }
                }
            }
        }
        catch { }
    }

    public static int ReadInt(JsonNode node)
    {
        if (node == null) return 0;
        try
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<int>(out var i): return i;
                case JsonValue v when v.TryGetValue<long>(out var l): return (int)l;
                case JsonValue v when v.TryGetValue<string>(out var s): return int.TryParse(s, out var p) ? p : 0;
            }
        }
        catch { }
        return 0;
    }

    public static long ReadLong(JsonNode node)
    {
        if (node == null) return 0;
        try
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<long>(out var l): return l;
                case JsonValue v when v.TryGetValue<double>(out var d): return (long)d;
                case JsonValue v when v.TryGetValue<string>(out var s): return long.TryParse(s, out var p) ? p : 0;
            }
        }
        catch { }
        return 0;
    }

    public static string ReadString(JsonNode node)
    {
        if (node == null) return "";
        try { return node.GetValue<string>() ?? ""; } catch { return node.ToJsonString().Trim('"'); }
    }

    public static bool ReadBool(JsonNode node)
    {
        if (node == null) return false;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<long>(out var l)) return l != 0;
                if (v.TryGetValue<string>(out var s)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
