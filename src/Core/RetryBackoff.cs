using System;

namespace WuwatappoNative.Core;

/// <summary>
/// 连续失败后的指数退避状态（纯内存、线程安全、不持久化）。
/// 调用方在失败时调用 <see cref="RecordFailure"/>，成功时调用 <see cref="Reset"/>；
/// 冷却判断统一用 <see cref="IsCoolingDown"/>，避免定时器/手动触发各写一套。
/// </summary>
internal sealed class RetryBackoff
{
    /// <summary>点击上报被拒：30s → 60s → 120s → 300s 封顶。</summary>
    public static readonly int[] ClickRejectSchedule = { 30, 60, 120, 300 };

    /// <summary>自动升阶失败：1 → 2 → 5 → 10 → 30 分钟封顶。</summary>
    public static readonly int[] AutoSynthSchedule = { 60, 120, 300, 600, 1800 };

    readonly object _gate = new();
    readonly int[] _schedule;
    int _failures;
    long _nextAllowedMs;

    public RetryBackoff(int[] scheduleSeconds)
    {
        if (scheduleSeconds == null || scheduleSeconds.Length == 0)
            throw new ArgumentException("scheduleSeconds 不能为空", nameof(scheduleSeconds));
        _schedule = (int[])scheduleSeconds.Clone();
    }

    /// <summary>连续失败次数（最近一次成功后清零）。</summary>
    public int Failures
    {
        get { lock (_gate) return _failures; }
    }

    public bool IsCoolingDown(long nowMs)
    {
        lock (_gate) return nowMs < _nextAllowedMs;
    }

    /// <summary>记录一次失败并开始下一档冷却，返回本次退避秒数。</summary>
    public int RecordFailure(long nowMs)
    {
        lock (_gate)
        {
            _failures++;
            int seconds = DelaySeconds(_failures, _schedule);
            _nextAllowedMs = nowMs + seconds * 1000L;
            return seconds;
        }
    }

    /// <summary>成功后清零连续失败计数与冷却。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _failures = 0;
            _nextAllowedMs = 0;
        }
    }

    /// <summary>按连续失败次数取退避秒数；超出档位后保持最后一档（封顶）。</summary>
    public static int DelaySeconds(int consecutiveFailures, int[] scheduleSeconds)
    {
        if (consecutiveFailures <= 0 || scheduleSeconds == null || scheduleSeconds.Length == 0) return 0;
        int index = Math.Min(consecutiveFailures - 1, scheduleSeconds.Length - 1);
        return Math.Max(0, scheduleSeconds[index]);
    }
}
