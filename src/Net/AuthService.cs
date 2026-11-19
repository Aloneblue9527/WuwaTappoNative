using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WuwatappoNative.Core;

namespace WuwatappoNative.Net;

public enum AuthState
{
    LoggedOut,
    Checking,
    Valid,
    Conflict,
    Invalid,
    NetworkUnknown,
}

/// <summary>登录编排：UID 验证码、国服手机号通行证、token 存档、48 小时内自动续期、user/me 同步。</summary>
public sealed class AuthService
{
    readonly Settings _cfg;
    readonly object _sessionGate = new();
    readonly object _stateGate = new();
    readonly SemaphoreSlim _probeGate = new(1, 1);
    int _recoveryDepth;
    AuthState _state;
    string _stateMessage = "";
    long _lastCheckedAtMs;
    bool? _lastProbeCredentialValid;
    long _lastRenewAttemptMs;
    public ApiClient Api { get; private set; }
    public event Action Changed;
    /// <summary>登录态（凭据是否有效、是否被其他客户端占用）变化。高频的用户资料刷新不会触发。</summary>
    public event Action StateChanged;

    public string Token => _cfg.Token;
    public bool HasToken => !string.IsNullOrEmpty(_cfg.Token);
    /// <summary>面板意义上的“已登录”：凭据失效后为 false；被其他客户端占用时仍为 true。</summary>
    public bool IsLoggedIn => HasToken && _state != AuthState.Invalid && _state != AuthState.LoggedOut;
    /// <summary>账号相关 HTTP 与联机 socket 是否可用。只有确认有效时才允许。</summary>
    public bool CanUseOnline => HasToken && _state == AuthState.Valid;
    /// <summary>桌宠计数条是否切换为“未登录”。冲突与凭据失效都显示统一短文案。</summary>
    public bool ShowLoggedOutOnPet => !HasToken || _state == AuthState.Invalid || _state == AuthState.Conflict;
    public AuthState State { get { lock (_stateGate) return _state; } }
    public string StateMessage { get { lock (_stateGate) return _stateMessage; } }
    public long LastCheckedAtMs { get { lock (_stateGate) return _lastCheckedAtMs; } }
    public bool? LastProbeCredentialValid { get { lock (_stateGate) return _lastProbeCredentialValid; } }
    public string StateName => State switch
    {
        AuthState.LoggedOut => "loggedOut",
        AuthState.Checking => "checking",
        AuthState.Valid => "valid",
        AuthState.Conflict => "conflict",
        AuthState.Invalid => "invalid",
        _ => "network",
    };
    public string PauseMessage => State switch
    {
        AuthState.LoggedOut => "未登录：请先登录账号。",
        AuthState.Checking => "正在确认登录状态，请稍候。",
        AuthState.Conflict => "账号功能已暂停：登录态正在其他客户端使用。",
        AuthState.Invalid => "账号功能已暂停：当前登录凭据已失效。",
        AuthState.NetworkUnknown => "网络不可用：暂时无法确认登录状态。",
        _ => "账号功能已暂停。",
    };
    public long UserId => _cfg.UserId;
    public long PlayerId => _cfg.PlayerId > 0 ? _cfg.PlayerId : _cfg.UserId;
    public string Cuid => _cfg.Cuid;
    public string Nickname => _cfg.Nickname;
    public long RoleId => _cfg.RoleId;
    public string ServerRoomNo => _cfg.RoomNo;
    public long TotalClickCount { get; private set; }
    /// <summary>服务器记录的累计消耗（开宝箱等），用于算可用余额。</summary>
    public long TotalClickConsume { get; private set; }
    /// <summary>服务器时间减去本机时间（毫秒）；由最近一次 API 响应的 Date 头校准。</summary>
    public long ServerTimeOffsetMs => Api?.ServerTimeOffsetMs ?? 0;
    /// <summary>当前服务器时间（毫秒），用于宝箱倒计时。</summary>
    public long ServerNowMs => Api?.ServerNowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public JsonObject UserInfo { get; private set; }

    public AuthService(Settings cfg)
    {
        _cfg = cfg;
        _state = string.IsNullOrEmpty(cfg.Token) ? AuthState.LoggedOut : AuthState.Checking;
        _stateMessage = string.IsNullOrEmpty(cfg.Token) ? "未登录：请先登录账号。" : "正在确认登录状态…";
        Api = CreateApi(Guid.NewGuid().ToString("N"), ApiClient.ResolveBaseUrl(cfg.ServerRegion), cfg.Token);
    }

    ApiClient CreateApi(string cid, string baseUrl, string token)
    {
        var api = new ApiClient(cid, baseUrl);
        api.SetToken(token);
        api.Gate = AccountRequestBlockedReason;
        api.TokenRefreshed += refreshed => TryUseSession(api, () =>
        {
            bool changed = !string.Equals(_cfg.Token, refreshed, StringComparison.Ordinal);
            _cfg.Token = refreshed;
            ConfigStore.Save(_cfg);
            if (changed) Log.Info("token 已通过响应 cookie 更新 len=" + (refreshed?.Length ?? 0), "auth");
        });
        api.RequestFailed += (path, failure) => OnApiRequestFailed(api, path, failure);
        return api;
    }

    string AccountRequestBlockedReason(string path)
    {
        if (Volatile.Read(ref _recoveryDepth) > 0) return null;
        string p = (path ?? "").TrimStart('/');
        // 公共数据、登录接口、探针、显式退出与续期始终放行；它们不建立新的实时会话。
        if (p.StartsWith("api/common/", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.StartsWith("api/auth/", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(p, "api/frontend/user/me", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(p, "api/frontend/user/logout", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(p, "api/frontend/user/renew-token", StringComparison.OrdinalIgnoreCase)) return null;
        if (State == AuthState.Valid) return null;
        return PauseMessage;
    }

    void OnApiRequestFailed(ApiClient api, string path, ApiException failure)
    {
        if (!ReferenceEquals(api, Api)) return;
        string p = (path ?? "").TrimStart('/');
        if (!p.StartsWith("api/frontend/", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(p, "api/frontend/user/me", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, "api/frontend/user/logout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, "api/frontend/user/renew-token", StringComparison.OrdinalIgnoreCase)) return;
        if (failure.Code == 90002)
            _ = ReportSessionTakenOverAsync(p + " 90002");
        else if (failure.Code == 401 || failure.Code == 403 || failure.Code == 10002 || failure.Code == 10003)
            _ = ReportUnauthorizedAsync(p + " " + failure.Code);
    }

    /// <summary>用户主动重新连接时临时放行账号请求；只由显式恢复流程使用。</summary>
    public IDisposable BeginRecovery()
    {
        Interlocked.Increment(ref _recoveryDepth);
        return new RecoveryScope(this);
    }

    sealed class RecoveryScope : IDisposable
    {
        readonly AuthService _owner;
        int _disposed;
        public RecoveryScope(AuthService owner) { _owner = owner; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _owner._recoveryDepth);
        }
    }

    internal ApiClient CaptureSession()
    {
        lock (_sessionGate) return HasToken ? Api : null;
    }

    internal bool TryUseSession(ApiClient session, Action action)
    {
        lock (_sessionGate)
        {
            if (session == null || !ReferenceEquals(Api, session) || !HasToken) return false;
            action();
            return true;
        }
    }

    // Each login gets its own client and cookie jar. Late requests cannot use the next login's cookies.
    void ReplaceSession(string token, long expires, string baseUrl = null)
    {
        var previous = Api;
        Api = CreateApi(baseUrl == null ? previous.Cid : Guid.NewGuid().ToString("N"), baseUrl ?? previous.BaseUrl, token);
        _cfg.Token = token;
        _cfg.TokenExpireMs = expires;
        _cfg.UserId = 0;
        UserInfo = null;
        TotalClickCount = TotalClickConsume = 0;
        previous.Dispose();
    }

    public void UpdateBaseUrl(string newBaseUrl)
    {
        lock (_sessionGate) ReplaceSession(_cfg.Token, _cfg.TokenExpireMs, newBaseUrl);
        SetState(string.IsNullOrEmpty(_cfg.Token) ? AuthState.LoggedOut : AuthState.Checking,
            string.IsNullOrEmpty(_cfg.Token) ? "未登录：请先登录账号。" : "正在确认登录状态…");
    }

    void SetState(AuthState state, string message, bool credentialValid = false)
    {
        bool changed;
        AuthState oldState;
        lock (_stateGate)
        {
            message ??= "";
            oldState = _state;
            changed = _state != state || _stateMessage != message;
            _state = state;
            _stateMessage = message;
            _lastCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (state == AuthState.Valid) _lastProbeCredentialValid = true;
            else if (state == AuthState.Invalid) _lastProbeCredentialValid = false;
            else if (credentialValid) _lastProbeCredentialValid = true;
        }
        if (!changed) return;
        // 只记录状态与原因，绝不记录 token/Cookie。
        Log.Info($"登录态变化 old={oldState} new={state} reason={message}", "auth");
        try { StateChanged?.Invoke(); } catch { }
        try { Changed?.Invoke(); } catch { }
    }

    enum ProbeResult { Valid, Invalid, Conflict, Forbidden, Network }

    /// <summary>安全探针：user/me 不会踢掉其他设备，只用于判断当前 token 是否仍有效。</summary>
    async Task<ProbeResult> ProbeCoreAsync(ApiClient session, bool applyUserInfo)
    {
        try
        {
            var data = await session.GetAsync("api/frontend/user/me", bypassGate: true).ConfigureAwait(false);
            if (data is not JsonObject me) return ProbeResult.Network;
            if (applyUserInfo)
            {
                if (!TryUseSession(session, () => ApplyUserInfo(me))) return ProbeResult.Network;
            }
            return ProbeResult.Valid;
        }
        catch (ApiException ex) when (ex.Code == 90002)
        {
            return ProbeResult.Conflict;
        }
        catch (ApiException ex) when (IsDefinitiveInvalid(ex))
        {
            return ProbeResult.Invalid;
        }
        catch (ApiException ex) when (IsForbiddenFailure(ex))
        {
            // HTTP 403 不一定是 token 失效：可能是风控、限流、WAF 或服务端短暂拒绝。
            // 由调用方做短重试，只有明确的 401/10002/10003 才进入 Invalid。
            return ProbeResult.Forbidden;
        }
        catch (OperationCanceledException) when (CaptureSession() != session)
        {
            return ProbeResult.Network;
        }
        catch (Exception ex)
        {
            Log.Write("登录态探针失败：" + ex.Message);
            return ProbeResult.Network;
        }
    }

    /// <summary>明确的凭据失效信号；HTTP 403 不算，必须再看响应业务码。</summary>
    internal static bool IsDefinitiveInvalid(ApiException ex)
        => ex != null && (ex.HttpStatus == 401 || ex.Code == 401 || ex.Code == 10002 || ex.Code == 10003);

    /// <summary>HTTP 403/业务码 403：可能是临时拒绝，先用 user/me 短重试确认。</summary>
    internal static bool IsForbiddenFailure(ApiException ex)
        => ex != null && ex.Code == 403;

    /// <summary>
    /// 403 短重试：1 秒、2 秒两次，避免一次瞬时 WAF/限流把登录态打成 Invalid。
    /// 401/10002/10003、90002 和网络结果立即返回，不重复打探针。
    /// </summary>
    async Task<ProbeResult> ProbeWithForbiddenRetryAsync(ApiClient session, bool applyUserInfo)
    {
        ProbeResult result = ProbeResult.Network;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            result = await ProbeCoreAsync(session, applyUserInfo).ConfigureAwait(false);
            if (result != ProbeResult.Forbidden) return result;
            if (attempt < 3)
            {
                Log.Warn("user/me 返回 403，短暂重试 " + attempt + "/2", "auth");
                await Task.Delay(1000 * attempt).ConfigureAwait(false);
            }
        }
        return result;
    }

    /// <summary>
    /// 判定一次运行中的登录异常。sessionTakenOver 表示已经收到明确的顶号信号（90002/服务端踢号），
    /// 此时即使探针网络失败也保持“被占用”，不能误判成普通断网。
    /// </summary>
    async Task ReportAuthFailureAsync(bool sessionTakenOver, string detail)
    {
        var session = CaptureSession();
        if (session == null) { SetState(AuthState.LoggedOut, "未登录：请先登录账号。"); return; }
        if (State == AuthState.Invalid || State == AuthState.Conflict) return;
        if (!await _probeGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            // 顶号信号已经足够明确：立即暂停并提示，不先让用户等 25 秒探针超时。
            SetState(sessionTakenOver ? AuthState.Conflict : AuthState.Checking,
                sessionTakenOver ? "登录态正在其他客户端使用；正在确认登录凭据…" : "正在确认登录状态…");
            Log.Write("检测到登录异常：" + (detail ?? "") + "，开始 user/me 探针");
            var result = await ProbeWithForbiddenRetryAsync(session, applyUserInfo: false).ConfigureAwait(false);
            if (CaptureSession() != session) return;
            if (result == ProbeResult.Invalid)
            {
                SetState(AuthState.Invalid, "当前登录凭据已失效；请重新登录或重新继承官方登录。");
                return;
            }
            if (result == ProbeResult.Conflict || sessionTakenOver)
            {
                SetState(AuthState.Conflict, result == ProbeResult.Network
                    ? "登录态正在其他客户端使用；网络暂时不可用，无法确认登录凭据。"
                    : "登录态正在其他客户端使用；账号功能已暂停，不会自动重新登录或重连。");
                return;
            }
            if (result == ProbeResult.Valid)
                SetState(AuthState.Valid, "");
            else if (result == ProbeResult.Forbidden)
                SetState(AuthState.NetworkUnknown, "服务器暂时拒绝请求，正在自动重试…");
            else
                SetState(AuthState.NetworkUnknown, "网络不可用，暂时无法确认登录状态。");
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public Task ReportSessionTakenOverAsync(string detail = null)
        => ReportAuthFailureAsync(true, detail);

    public Task ReportUnauthorizedAsync(string detail = null)
        => ReportAuthFailureAsync(false, detail);

    /// <summary>用户主动“重新检测”：只更新凭据是否有效，不自动恢复 HTTP 或联机。</summary>
    public async Task<bool> RecheckAsync()
    {
        var session = CaptureSession();
        if (session == null)
        {
            SetState(AuthState.LoggedOut, "未登录：请先登录账号。");
            return false;
        }
        if (!await _probeGate.WaitAsync(0).ConfigureAwait(false)) return LastProbeCredentialValid == true;
        try
        {
            var previous = State;
            var result = await ProbeWithForbiddenRetryAsync(session, applyUserInfo: false).ConfigureAwait(false);
            if (CaptureSession() != session) return false;
            if (result == ProbeResult.Invalid)
            {
                SetState(AuthState.Invalid, "当前登录凭据已失效；请重新登录或重新继承官方登录。");
                return false;
            }
            if (result == ProbeResult.Conflict)
            {
                SetState(AuthState.Conflict, "登录态正在其他客户端使用；账号功能已暂停，不会自动重新登录或重连。");
                return false;
            }
            if (result == ProbeResult.Forbidden)
            {
                if (previous == AuthState.Conflict)
                    SetState(AuthState.Conflict, "登录态正在其他客户端使用；服务器暂时拒绝请求，无法确认凭据。", credentialValid: LastProbeCredentialValid == true);
                else
                    SetState(AuthState.NetworkUnknown, "服务器暂时拒绝请求，正在自动重试…");
                return false;
            }
            if (result == ProbeResult.Network)
            {
                if (previous == AuthState.Conflict)
                    SetState(AuthState.Conflict, "登录态正在其他客户端使用；网络暂时不可用，无法确认登录凭据。", credentialValid: LastProbeCredentialValid == true);
                else
                    SetState(AuthState.NetworkUnknown, "网络不可用，暂时无法确认登录状态。");
                return false;
            }
            if (previous == AuthState.Conflict)
            {
                SetState(AuthState.Conflict, "登录凭据有效；账号功能仍暂停，点击“重新连接”恢复。", credentialValid: true);
                return true;
            }
            if (previous == AuthState.Invalid)
            {
                SetState(AuthState.Invalid, "登录凭据已恢复有效；账号功能仍暂停，点击“重新连接”恢复。", credentialValid: true);
                return true;
            }
            SetState(AuthState.Valid, "", credentialValid: true);
            return true;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>用户显式点击“重新连接”：确认凭据有效后放行账号请求，由 App 再恢复房间。</summary>
    public async Task<bool> ProbeAndResumeAsync()
    {
        var session = CaptureSession();
        if (session == null)
        {
            SetState(AuthState.LoggedOut, "未登录：请先登录账号。");
            return false;
        }
        await _probeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetState(AuthState.Checking, "正在确认登录状态…");
            var result = await ProbeWithForbiddenRetryAsync(session, applyUserInfo: true).ConfigureAwait(false);
            if (CaptureSession() != session) return false;
            if (result == ProbeResult.Valid)
            {
                SetState(AuthState.Valid, "");
                return true;
            }
            if (result == ProbeResult.Invalid)
            {
                SetState(AuthState.Invalid, "当前登录凭据已失效；请重新登录或重新继承官方登录。");
                return false;
            }
            if (result == ProbeResult.Conflict)
            {
                SetState(AuthState.Conflict, "登录态正在其他客户端使用；账号功能已暂停，不会自动重新登录或重连。");
                return false;
            }
            if (result == ProbeResult.Forbidden)
            {
                SetState(AuthState.NetworkUnknown, "服务器暂时拒绝请求，正在自动重试…");
                return false;
            }
            SetState(AuthState.NetworkUnknown, "网络不可用，暂时无法确认登录状态。");
            return false;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>网络恢复后的自动重试；只在 NetworkUnknown 状态使用，不用于顶号后的自动抢登。</summary>
    public async Task<bool> RetryNetworkAsync()
    {
        if (State != AuthState.NetworkUnknown) return CanUseOnline;
        var session = CaptureSession();
        if (session == null) return false;
        if (!await _probeGate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            var result = await ProbeWithForbiddenRetryAsync(session, applyUserInfo: true).ConfigureAwait(false);
            if (CaptureSession() != session) return false;
            if (result == ProbeResult.Valid)
            {
                SetState(AuthState.Valid, "");
                return true;
            }
            if (result == ProbeResult.Invalid)
                SetState(AuthState.Invalid, "当前登录凭据已失效；请重新登录或重新继承官方登录。");
            else if (result == ProbeResult.Conflict)
                SetState(AuthState.Conflict, "登录态正在其他客户端使用；账号功能已暂停，不会自动重新登录或重连。");
            else if (result == ProbeResult.Forbidden)
                SetState(AuthState.NetworkUnknown, "服务器暂时拒绝请求，正在自动重试…");
            else
                SetState(AuthState.NetworkUnknown, "网络不可用，暂时无法确认登录状态。");
            return false;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public async Task<bool> TryRestoreAsync()
    {
        var session = CaptureSession();
        if (session == null)
        {
            SetState(AuthState.LoggedOut, "未登录：请先登录账号。");
            return false;
        }
        SetState(AuthState.Checking, "正在确认登录状态…");
        try
        {
            if (_cfg.TokenExpireMs > 0 && _cfg.TokenExpireMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < 48L * 3600 * 1000)
                await RenewAsync(session);
        }
        catch (ApiException ex)
        {
            // 续期失败不等于凭据失效：最终以不会踢其他设备的 user/me 探针为准。
            Log.Write("token 续期失败，继续用 user/me 确认：" + ex.Code + " " + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("token 续期异常，继续用 user/me 确认：" + ex.Message);
        }

        var result = await ProbeWithForbiddenRetryAsync(session, applyUserInfo: true);
        if (CaptureSession() != session) return false;
        if (result == ProbeResult.Valid)
        {
            SetState(AuthState.Valid, "");
            return true;
        }
        if (result == ProbeResult.Invalid)
        {
            Log.Write("token 失效：user/me 返回未授权");
            SetState(AuthState.Invalid, "当前登录凭据已失效；请重新登录或重新继承官方登录。");
            return false;
        }
        if (result == ProbeResult.Conflict)
        {
            Log.Write("恢复登录失败：user/me 返回顶号信号");
            SetState(AuthState.Conflict, "登录态正在其他客户端使用；账号功能已暂停，不会自动重新登录或重连。");
            return false;
        }
        if (result == ProbeResult.Forbidden)
        {
            Log.Write("恢复登录暂时被服务器拒绝：user/me 返回 403，保持可重试状态");
            SetState(AuthState.NetworkUnknown, "服务器暂时拒绝请求，正在自动重试…");
            return false;
        }
        Log.Write("恢复登录失败：无法连接 user/me");
        SetState(AuthState.NetworkUnknown, "网络不可用，暂时无法确认登录状态。");
        return false;
    }

    /// <summary>
    /// 发送 UID 验证码。服务端按数字校验 playerId，传字符串会返回 10001 Invalid parameters
    /// （原版前端也是 Number(uid)）。
    /// </summary>
    public async Task SendUidCodeAsync(string playerId)
    {
        long uid = ParsePlayerId(playerId);
        await Api.PostAsync("api/auth/uid-send-code", new { playerId = uid });
    }

    /// <summary>
    /// UID 登录。playerId 与 code 都必须是数字，字符串会被判定为无效参数。
    /// </summary>
    public async Task LoginWithUidAsync(string playerId, string code)
    {
        long uid = ParsePlayerId(playerId);
        string trimmed = (code ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ApiException(-1, "请输入验证码");
        if (!trimmed.All(char.IsDigit)) throw new ApiException(-1, "验证码只能是数字");
        if (trimmed.Length != 6) throw new ApiException(-1, "UID 验证码为 6 位数字");

        long codeNum = long.Parse(trimmed);
        _cfg.PlayerId = uid;
        var data = await Api.PostAsync("api/auth/uid-login", new { playerId = uid, code = codeNum });
        ApplyAuthToken(data);
        await WhoAmIAsync();
        SetState(AuthState.Valid, "");
    }

    /// <summary>发送库洛通行证手机号验证码（国服 H5 SDK 通道）。</summary>
    public async Task SendPhoneCodeAsync(string phone)
    {
        if (!string.Equals(_cfg.ServerRegion, "cn", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(-1, "手机号登录仅支持国服");
        string rawPhone = NormalizePhone(phone);
        await KuroSdkClient.SendPhoneCodeAsync(rawPhone, _cfg.SdkDeviceNum);
    }

    /// <summary>手机号验证码登录：先用 H5 SDK 换通行证 access_token，再换 WuwaTappo token。</summary>
    public async Task LoginWithPhoneAsync(string phone, string code)
    {
        if (!string.Equals(_cfg.ServerRegion, "cn", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(-1, "手机号登录仅支持国服");
        string rawPhone = NormalizePhone(phone);
        string trimmed = (code ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new ApiException(-1, "请输入短信验证码");
        if (!trimmed.All(char.IsDigit)) throw new ApiException(-1, "短信验证码只能是数字");
        if (trimmed.Length < 4 || trimmed.Length > 6) throw new ApiException(-1, "请输入 4-6 位短信验证码");

        var user = await KuroSdkClient.PhoneLoginAsync(rawPhone, trimmed, _cfg.SdkDeviceNum);
        await LoginWithSdkSessionAsync(user.Cuid, user.SdkOpenId, user.UserName, user.AccessToken, domestic: true);
    }

    /// <summary>
    /// 用通行证会话换取 WuwaTappo token。国服走 is_domestic=1，查询角色后按官方 SDK 参数登录。
    /// </summary>
    public async Task LoginWithSdkSessionAsync(string cuid, string sdkOpenId, string userName, string accessToken, bool domestic)
    {
        if (string.IsNullOrWhiteSpace(cuid)) throw new ApiException(-1, "缺少通行证 cuid");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ApiException(-1, "缺少通行证 access_token");
        if (string.IsNullOrWhiteSpace(sdkOpenId)) sdkOpenId = cuid;

        var queryResp = await Api.PostAsync("api/auth/player/query", new
        {
            cuid,
            is_domestic = domestic ? 1 : 0,
            // 官方 1.0.4 的 player/query 也要求携带通行证 accessToken；
            // 只传 cuid 会直接返回 10024 Game login failed。
            accessToken,
        });
        if (queryResp is not JsonObject queryObj)
            throw new ApiException(-1, "查询游戏角色失败");

        long channelId = ApiClient.ReadLong(queryObj["channelId"]);
        if (channelId <= 0) channelId = domestic ? 201 : 18;
        if (queryObj["items"] is not JsonArray items || items.Count == 0)
            throw new ApiException(-1, "未找到游戏角色，请先进入游戏创建角色");

        // 官方客户端取玩家列表第一项；这里保持一致。
        var selectedRole = items.OfType<JsonObject>().FirstOrDefault();
        if (selectedRole == null) throw new ApiException(-1, "未找到可用游戏角色");
        long playerId = ApiClient.ReadLong(selectedRole["playerId"]);
        string serverId = ApiClient.ReadString(selectedRole["serverId"]);
        if (playerId <= 0) throw new ApiException(-1, "游戏角色信息不完整");
        if (string.IsNullOrEmpty(serverId)) serverId = "0";

        _cfg.PlayerId = playerId;
        string cname = string.IsNullOrWhiteSpace(userName) ? sdkOpenId : userName;
        var loginData = await Api.PostAsync("api/auth/login", new
        {
            cuid,
            puid = sdkOpenId,
            channelId,
            userid = sdkOpenId,
            cname,
            accessToken,
            source = "sdk",
            playerId,
            serverId,
            is_domestic = domestic ? 1 : 0,
        });
        ApplyAuthToken(loginData);
        await WhoAmIAsync();
        SetState(AuthState.Valid, "");
    }

    static string NormalizePhone(string phone)
    {
        string raw = (phone ?? "").Trim().Replace(" ", "").Replace("-", "");
        if (raw.Length != 11 || !raw.All(char.IsDigit) || raw[0] != '1' || raw[1] < '3' || raw[1] > '9')
            throw new ApiException(-1, "请输入 11 位手机号");
        return raw;
    }

    /// <summary>
    /// 国际服凭海外通行证会话（Kuro Passport OAuth）登录。
    /// 服务端不支持国际服 UID 邮件验证码，采用官方通行证的 cuid + accessToken 登录。
    /// </summary>
    public async Task LoginWithForeignSessionAsync(
        string cuid,
        string puid,
        string userId,
        string userName,
        string accessToken,
        long? targetPlayerId = null,
        string targetServerId = null)
    {
        if (string.IsNullOrWhiteSpace(cuid)) throw new ApiException(-1, "缺少 cuid");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ApiException(-1, "缺少 accessToken");

        // 1. 查询该 cuid 下的角色列表。官方国际服客户端允许“通行证已登录但账号下没有游戏角色”，
        // 这种情况下仍然可以用 playerId=0 / serverId=0 换取账号级 token，不能直接判定登录失败。
        var queryResp = await Api.PostAsync("api/auth/player/query", new
        {
            cuid = cuid,
            is_domestic = 0,
            accessToken = accessToken,
        });
        if (queryResp is not JsonObject queryObj)
            throw new ApiException(-1, "查询国际服角色失败");

        long channelId = ApiClient.ReadLong(queryObj["channelId"]);
        if (channelId <= 0) channelId = 18;

        var items = queryObj["items"] as JsonArray;
        JsonObject selectedRole = null;
        if (targetPlayerId.HasValue && targetPlayerId.Value > 0)
        {
            selectedRole = items?.OfType<JsonObject>().FirstOrDefault(r => ApiClient.ReadLong(r["playerId"]) == targetPlayerId.Value);
        }
        if (selectedRole == null && !string.IsNullOrWhiteSpace(targetServerId))
        {
            selectedRole = items?.OfType<JsonObject>().FirstOrDefault(r => string.Equals(ApiClient.ReadString(r["serverId"]), targetServerId, StringComparison.OrdinalIgnoreCase));
        }
        if (selectedRole == null && items != null && items.Count > 0)
            selectedRole = items[0] as JsonObject;

        long playerId = 0;
        string serverId = "0";
        if (selectedRole != null)
        {
            playerId = ApiClient.ReadLong(selectedRole["playerId"]);
            serverId = ApiClient.ReadString(selectedRole["serverId"]);
            if (playerId <= 0) throw new ApiException(-1, "游戏角色信息不完整");
        }
        _cfg.PlayerId = playerId;

        // 2. 调用 SDK 登录；无角色账号使用 0/0，官方客户端也是同样的降级登录方式。
        var loginData = await Api.PostAsync("api/auth/login", new
        {
            cuid = cuid,
            puid = string.IsNullOrEmpty(puid) ? cuid : puid,
            channelId = channelId,
            userid = string.IsNullOrEmpty(userId) ? cuid : userId,
            cname = string.IsNullOrEmpty(userName) ? (userId ?? cuid) : userName,
            accessToken = accessToken,
            source = "sdk",
            playerId = playerId,
            serverId = string.IsNullOrEmpty(serverId) ? "0" : serverId,
            is_domestic = 0
        });

        ApplyAuthToken(loginData);

        // 无角色账号的 user/me 可能没有 player_id 或超时；token 已经换到，不能让登录状态被这次
        // 同步失败整体回滚。后续刷新用户信息时会自然补齐。
        try
        {
            await WhoAmIAsync();
        }
        catch (Exception ex) when (playerId == 0)
        {
            Log.Write("无角色国际服账号登录成功，但 user/me 同步失败：" + ex.Message);
        }
        SetState(AuthState.Valid, "");
    }

    public Task RenewAsync() => RenewAsync(CaptureSession());

    /// <summary>
    /// 运行期间的轻量续期：每 6 小时最多尝试一次。官方 token 的有效期字段可能为未知值，
    /// 不能只依赖启动时续期；失败只记录，不改变当前有效状态。
    /// </summary>
    public async Task MaybeRenewAsync()
    {
        if (!CanUseOnline) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long last = Interlocked.Read(ref _lastRenewAttemptMs);
        if (last != 0 && now - last < 6L * 3600 * 1000) return;
        if (Interlocked.CompareExchange(ref _lastRenewAttemptMs, now, last) != last) return;
        var session = CaptureSession();
        if (session == null) return;
        try
        {
            await RenewAsync(session);
        }
        catch (Exception ex)
        {
            Log.Write("token 定期续期失败：" + ex.Message);
        }
    }

    async Task RenewAsync(ApiClient session)
    {
        Interlocked.Exchange(ref _lastRenewAttemptMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Task<JsonNode> request = null;
        if (!TryUseSession(session, () => request = session.GetAsync("api/frontend/user/renew-token"))) return;
        var data = await request;
        if (data is JsonValue v && v.TryGetValue<bool>(out var ok) && ok)
        {
            TryUseSession(session, () =>
            {
                if (_cfg.TokenExpireMs > 0) _cfg.TokenExpireMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 7L * 24 * 3600 * 1000;
                ConfigStore.Save(_cfg);
                Log.Info("token 续期成功", "auth");
            });
        }
        else
        {
            Log.Warn("token 续期未返回成功标记", "auth");
        }
    }

    /// <summary>开箱响应成功后先本地把该侧宝箱时间戳清零，让 UI 立即消失；随后 user/me 会用真实冷却时间修正。</summary>
    public void MarkChestOpened(string place)
    {
        if (UserInfo is not JsonObject me) return;
        string key = string.Equals(place, "left", StringComparison.OrdinalIgnoreCase)
            ? "left_treasure_chest"
            : "right_treasure_chest";
        me[key] = 0;
    }

    /// <summary>轻量刷新：只同步点击数、宝箱时间戳等运行时字段，不回写本地外观。</summary>
    public Task RefreshClicksAsync() => RefreshClicksAsync(CaptureSession());

    internal Task<bool> RefreshClicksAsync(ApiClient session) => ReadUserAsync(session, me =>
    {
        UserInfo = me;
        TotalClickCount = ApiClient.ReadLong(me["total_click_count"]);
        TotalClickConsume = ApiClient.ReadLong(me["total_click_consume"]);
        Changed?.Invoke();
    });

    async Task<bool> ReadUserAsync(ApiClient session, Action<JsonObject> apply, bool probe = false)
    {
        Task<JsonNode> request = null;
        if (!TryUseSession(session, () => request = session.GetAsync("api/frontend/user/me", bypassGate: probe))) return false;
        try
        {
            var data = await request;
            return data is JsonObject me && TryUseSession(session, () => apply(me));
        }
        catch (OperationCanceledException) when (CaptureSession() != session) { return false; }
        catch (ApiException ex) when (!probe && (ex.Code == 401 || ex.Code == 403 || ex.Code == 10002 || ex.Code == 10003))
        {
            _ = ReportUnauthorizedAsync("user/me " + ex.Code);
            throw;
        }
    }

    public Task WhoAmIAsync() => WhoAmIAsync(CaptureSession());

    internal Task<bool> WhoAmIAsync(ApiClient session) => ReadUserAsync(session, ApplyUserInfo);

    void ApplyUserInfo(JsonObject me)
    {
        UserInfo = me;
        string serverCuid = ApiClient.ReadString(me["cuid"]);
        bool accountChanged = !string.IsNullOrEmpty(_cfg.Cuid) && !string.IsNullOrEmpty(serverCuid) && _cfg.Cuid != serverCuid;
        if (accountChanged)
        {
            _cfg.LocalAppearance = false;
            _cfg.LocalEmoteSlots = false;
            // 房主房间属于账号；换账号后不能沿用上一个账号的房主保护数据。
            _cfg.OwnerRoomNo = "";
            _cfg.OwnerRoomCuid = "";
        }
        _cfg.UserId = ApiClient.ReadLong(me["id"]);
        long pid = ApiClient.ReadLong(me["player_id"]);
        if (pid <= 0) pid = ApiClient.ReadLong(me["playerId"]);
        if (pid > 0) _cfg.PlayerId = pid;
        _cfg.Cuid = serverCuid;
        _cfg.Nickname = ApiClient.ReadString(me["nickname"]);
        _cfg.RoleId = ApiClient.ReadLong(me["role_id"]);
        _cfg.RoomNo = ApiClient.ReadString(me["room_no"]);
        TotalClickCount = ApiClient.ReadLong(me["total_click_count"]);
        TotalClickConsume = ApiClient.ReadLong(me["total_click_consume"]);

        long skin = ApiClient.ReadLong(me["character"]?["id"]);
        long acc = ApiClient.ReadLong(me["accessory"]?["id"]);
        // 云端安全值始终记录服务器最近一次确认的内容；本地是否覆盖由 LocalAppearance 决定。
        _cfg.CloudCharacterAssetId = skin;
        _cfg.CloudAccessoryAssetId = acc;
        if (!_cfg.LocalAppearance)
        {
            if (skin > 0) _cfg.CharacterAssetId = skin;
            _cfg.AccessoryAssetId = acc;
        }

        var emotes = new long[5];
        if (me["emote"] is JsonArray arr)
        {
            int i = 0;
            foreach (var e in arr)
            {
                if (i >= 5) break;
                emotes[i++] = ApiClient.ReadLong(e?["id"]);
            }
        }
        _cfg.CloudEmoteAssetIds = emotes;
        if (!_cfg.LocalEmoteSlots && emotes.Any(x => x > 0)) _cfg.EmoteAssetIds = emotes;
        ConfigStore.Save(_cfg);
        Changed?.Invoke();
    }

    public async Task LogoutAsync()
    {
        var session = CaptureSession();
        try
        {
            Task<JsonNode> request = null;
            if (!TryUseSession(session, () => request = session.PostAsync("api/frontend/user/logout", new { click_num = 0 }))) return;
            await request;
        }
        catch (Exception ex) { Log.Write("登出请求失败：" + ex.Message); }
        TryUseSession(session, ClearLocal);
    }

    public void ClearLocal()
    {
        lock (_sessionGate)
        {
            ReplaceSession("", 0);
            _cfg.RoomNo = "";
            _cfg.PlayerId = 0;
            _cfg.Nickname = "";
            _cfg.CloudCharacterAssetId = 0;
            _cfg.CloudAccessoryAssetId = 0;
            _cfg.CloudEmoteAssetIds = new long[5];
            ConfigStore.Save(_cfg);
        }
        SetState(AuthState.LoggedOut, "未登录：请先登录账号。");
    }

    public void SetTokenManually(string token)
    {
        StoreSession((token ?? "").Trim(), 0);
    }

    void ApplyAuthToken(JsonNode data)
    {
        if (data is not JsonObject obj) throw new ApiException(-1, "登录响应缺少 data");
        string token = ApiClient.ReadString(obj["token"]);
        if (string.IsNullOrEmpty(token)) throw new ApiException(-1, "登录响应缺少 token");
        StoreSession(token, ParseExpire(obj["expire_time"]));
    }

    void StoreSession(string token, long expires)
    {
        lock (_sessionGate)
        {
            ReplaceSession(token, expires);
            ConfigStore.Save(_cfg);
        }
        SetState(string.IsNullOrEmpty(token) ? AuthState.LoggedOut : AuthState.Checking,
            string.IsNullOrEmpty(token) ? "未登录：请先登录账号。" : "正在确认登录状态…");
    }

    static long ParseExpire(JsonNode node)
    {
        if (node == null) return 0;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l < 100000000000L ? l * 1000 : l;
                if (v.TryGetValue<string>(out var s))
                {
                    if (long.TryParse(s, out var n)) return n < 100000000000L ? n * 1000 : n;
                    if (DateTimeOffset.TryParse(s, out var dto)) return dto.ToUnixTimeMilliseconds();
                }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>UID 登录校验：游戏 UID 为 9~10 位纯数字；手机号请走手机号登录通道。</summary>
    static long ParsePlayerId(string playerId)
    {
        string raw = (playerId ?? "").Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(raw)) throw new ApiException(-1, "请输入游戏 UID");
        if (!raw.All(char.IsDigit)) throw new ApiException(-1, "游戏 UID 只能是数字");
        if (raw.Length == 11)
            throw new ApiException(-1, "UID 登录不支持手机号，请切换到手机号登录");
        if (raw.Length < 9 || raw.Length > 10)
            throw new ApiException(-1, "游戏 UID 为 9-10 位数字");
        return long.Parse(raw);
    }
}
