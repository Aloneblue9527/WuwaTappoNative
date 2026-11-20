using System;

namespace WuwatappoNative.Core;

/// <summary>
/// 开箱冷却策略。官方客户端在开箱成功后按左/右箱分别记 30 分钟本地冷却；
/// 70006 时还要以服务端返回的宝箱时间戳为准，避免本机时钟偏快导致提前重试。
/// </summary>
internal static class ChestCooldownPolicy
{
    public const long MinimumMs = 30 * 60 * 1000;
    public const long MaximumMs = 6 * 60 * 60 * 1000;
    public const long SafetyMarginMs = 5000;

    /// <summary>服务器时间不可用时退回 30 分钟；正常时取 max(30 分钟, 服务端时间戳 + 余量)。</summary>
    public static TimeSpan Compute(long serverNowMs, long unlockAtMs)
    {
        if (serverNowMs <= 0)
            return TimeSpan.FromMilliseconds(MinimumMs);

        long waitMs = unlockAtMs > 0 ? unlockAtMs - serverNowMs + SafetyMarginMs : 0;
        return TimeSpan.FromMilliseconds(Math.Clamp(waitMs, MinimumMs, MaximumMs));
    }
}
