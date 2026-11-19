using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using WuwatappoNative.Net;

namespace WuwatappoNative.Core;

/// <summary>
/// 只读 bags/list 的重试策略。合成 POST 非幂等，绝不复用这里的重试规则。
/// 只对网络异常、连接重置、超时、10007 和 HTTP 403 做有限重试。
/// </summary>
internal static class BagFetchPolicy
{
    public const int MaxAttempts = 3;

    /// <summary>第 1/2 次失败后的等待：1s、2s；最后一次失败不再等待。</summary>
    public static int RetryDelayMs(int failedAttempt)
    {
        return failedAttempt switch
        {
            1 => 1000,
            2 => 2000,
            _ => 0,
        };
    }

    public static bool IsRetryable(Exception ex)
    {
        switch (ex)
        {
            case null:
                return false;
            case ApiException api:
                // 登录失效（401/10002/10003/90002）重试没有意义；10007 可能是瞬间风控/WAF，
                // 普通 403 同理，两者都只对只读 bags/list 做有限重试。
                if (api.Code == 401 || api.Code == 10002 || api.Code == 10003 || api.Code == 90002)
                    return false;
                return api.Code == 10007 || api.Code == 403 || api.HttpStatus == 403;
            case HttpRequestException:
            case IOException:
                return true;
            case OperationCanceledException:
                // 调用方的取消会在 FetchBagAsync 的 catch when 中先被过滤；走到这里的通常是 HttpClient 超时。
                return true;
            default:
                return false;
        }
    }

    public static bool IsRetryable(Exception ex, int failedAttempt)
        => failedAttempt < MaxAttempts && IsRetryable(ex);
}
