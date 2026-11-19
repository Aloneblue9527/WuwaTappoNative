namespace WuwatappoNative.Core;

/// <summary>自动升阶结束后对失败冷却采取的动作。</summary>
internal enum AutoSynthBackoffAction
{
    /// <summary>不改变冷却状态（空扫描、忙碌/门禁拦截、取消）。</summary>
    None,
    /// <summary>真正完成过工作，清空连续失败计数与冷却。</summary>
    Reset,
    /// <summary>真实失败，推进一档退避冷却。</summary>
    RecordFailure,
}

/// <summary>
/// 自动升阶退避语义的纯逻辑：空成功（背包空/无满足材料）不能清零失败冷却，
/// 否则一次失败后紧接着的空检查会把下一次开箱重新变成高频请求。
/// </summary>
internal static class AutoSynthBackoffPolicy
{
    public static AutoSynthBackoffAction Decide(AutoSynthResult result)
    {
        if (result == null) return AutoSynthBackoffAction.None;
        if (result.Success)
            return result.NoWork ? AutoSynthBackoffAction.None : AutoSynthBackoffAction.Reset;
        if (result.Busy || result.GateBlocked || result.Cancelled)
            return AutoSynthBackoffAction.None;
        return AutoSynthBackoffAction.RecordFailure;
    }
}
