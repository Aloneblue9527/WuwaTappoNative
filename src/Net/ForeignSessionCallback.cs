using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WuwatappoNative.Net;

/// <summary>
/// 官方国际服登录页的进程内回调解析：wuwa-buddy://auth/callback?state=...&amp;access_token=...
/// 不注册系统协议，由 WebView2 的 NavigationStarting/NewWindowRequested 拦截。
/// </summary>
public static class ForeignSessionCallback
{
    public static bool TryParse(string uri, string expectedState, out ForeignSessionInfo session, out string error)
    {
        session = null;
        error = "";
        if (!Uri.TryCreate(uri ?? "", UriKind.Absolute, out var parsed))
        {
            error = "登录回调地址无效";
            return false;
        }
        if (!string.Equals(parsed.Scheme, "wuwa-buddy", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parsed.Host, "auth", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parsed.AbsolutePath.TrimEnd('/'), "/callback", StringComparison.OrdinalIgnoreCase))
        {
            error = "登录回调地址格式不正确";
            return false;
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = part.IndexOf('=');
            if (split <= 0) continue;
            string key = WebUtility.UrlDecode(part[..split]);
            string value = WebUtility.UrlDecode(part[(split + 1)..]);
            if (string.IsNullOrEmpty(key) || query.ContainsKey(key))
            {
                error = "登录回调参数重复或为空";
                return false;
            }
            query[key] = value;
        }

        if (!query.TryGetValue("state", out string state) || !FixedEquals(state, expectedState))
        {
            error = "登录回调 state 校验失败";
            return false;
        }
        if (!query.TryGetValue("access_token", out string accessToken) ||
            string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 4096)
        {
            error = "登录回调缺少 access_token";
            return false;
        }
        query.TryGetValue("cuid", out string cuid);
        query.TryGetValue("puid", out string puid);
        query.TryGetValue("userId", out string userId);
        query.TryGetValue("userName", out string userName);
        cuid = (cuid ?? "").Trim();
        puid = (puid ?? "").Trim();
        userId = (userId ?? "").Trim();
        userName = (userName ?? "").Trim();
        if (cuid.Length == 0 || cuid.Length > 128)
        {
            error = "登录回调缺少 cuid";
            return false;
        }
        if (puid.Length > 128 || userId.Length > 128 || userName.Length > 256)
        {
            error = "登录回调字段过长";
            return false;
        }
        if (puid.Length == 0) puid = cuid;
        if (userId.Length == 0) userId = userName.Length > 0 ? userName : cuid;
        if (userName.Length == 0) userName = userId;

        session = new ForeignSessionInfo
        {
            AccessToken = accessToken,
            Cuid = cuid,
            Puid = puid,
            UserId = userId,
            UserName = userName,
        };
        return true;
    }

    static bool FixedEquals(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }
}
