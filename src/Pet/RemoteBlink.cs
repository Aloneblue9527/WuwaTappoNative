using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace WuwatappoNative.Pet;

/// <summary>主角与联机角色共用的眨眼节奏：5 秒眨一次、闭眼 250ms。</summary>
internal static class BlinkTiming
{
    internal const int IntervalMs = 5000;
    internal const int ClosedMs = 250;
}

/// <summary>
/// 联机角色/本地伙伴的眨眼时间参数。节奏参考主角，但给每个窗口分配不同相位
/// 并叠加逐轮抖动，避免所有人同时眨眼。
/// </summary>
internal static class RemoteBlinkLogic
{
    /// <summary>每轮间隔在 5 秒上下浮动，避免相位在长时间运行后重新对齐。</summary>
    internal const int JitterMs = 400;
    internal const int MaxIntervalMs = BlinkTiming.IntervalMs + JitterMs;
    internal const int MinWaitMs = 15;

    /// <summary>
    /// 首次眨眼相位（0–4999ms）。<paramref name="seed"/> 使用递增序号而不是成员 id：
    /// 2654435761 与 5000 互质，连续序号会均匀铺满一个周期。
    /// </summary>
    internal static long InitialPhaseMs(long seed)
    {
        unchecked
        {
            ulong mixed = (ulong)seed * 2654435761UL + 7919UL;
            return (long)(mixed % (ulong)BlinkTiming.IntervalMs);
        }
    }

    /// <summary>
    /// 晚加入/重新显示时，在当前已有窗口的到期相位之间找最大空隙插入，
    /// 避免序号不连续时撞进别人的眨眼时刻。<paramref name="existingOffsets"/> 为相对现在的到期毫秒。
    /// </summary>
    internal static long InitialDelayMs(IReadOnlyList<long> existingOffsets, long fallbackSeed)
    {
        if (existingOffsets == null || existingOffsets.Count == 0)
            return InitialPhaseMs(fallbackSeed);

        var sorted = new long[existingOffsets.Count];
        for (int i = 0; i < sorted.Length; i++)
        {
            long offset = existingOffsets[i] % BlinkTiming.IntervalMs;
            if (offset < 0) offset += BlinkTiming.IntervalMs;
            sorted[i] = offset;
        }
        Array.Sort(sorted);
        long bestStart = sorted[^1];
        long bestGap = sorted[0] + BlinkTiming.IntervalMs - sorted[^1];
        for (int i = 1; i < sorted.Length; i++)
        {
            long gap = sorted[i] - sorted[i - 1];
            if (gap <= bestGap) continue;
            bestGap = gap;
            bestStart = sorted[i - 1];
        }
        return (bestStart + bestGap / 2) % BlinkTiming.IntervalMs;
    }

    /// <summary>下一次眨眼间隔：以主角的 5 秒为基准，±400ms 抖动打散长期同步。</summary>
    internal static long NextIntervalMs(long seed, long cycle)
    {
        unchecked
        {
            ulong mixed = (ulong)seed ^ ((ulong)cycle * 0x9E3779B97F4A7C15UL);
            return BlinkTiming.IntervalMs - JitterMs + (long)(Hash(mixed) % (uint)(JitterMs * 2 + 1));
        }
    }

    /// <summary>
    /// 共享计时器下一次唤醒的等待时间。上限必须覆盖带抖动的最大间隔，
    /// 否则每个大于 5 秒的周期都会多醒一次。
    /// </summary>
    internal static int ArmDelay(long next, long now) =>
        (int)Math.Clamp(next - now, MinWaitMs, MaxIntervalMs);

    static uint Hash(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCDUL;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53UL;
        value ^= value >> 33;
        return (uint)value;
    }
}

/// <summary>
/// 所有可见联机桌宠/伙伴共用一只计时器：只在最近一次眨眼到期时唤醒，
/// 而不是每个窗口各持一只 5 秒计时器。隐藏或关闭的窗口立即退出调度，
/// 人多时也不会产生持续的后台重绘。
/// </summary>
internal static class RemoteBlinkAnimator
{
    sealed class State
    {
        public long NextMs;
        public long CloseMs;
        public long LastStartMs;
        public long Cycle;
        public bool Closed;
        public int Failures;
    }

    const int MaxFailures = 3;
    static readonly Dictionary<RemoteCatWindow, State> Active = new();
    static readonly List<RemoteCatWindow> Snapshot = new();
    static readonly System.Windows.Forms.Timer Timer = new();
    static long _nextOrdinal;

    static RemoteBlinkAnimator()
    {
        Timer.Tick += (s, e) => Pump();
    }

    internal static void Add(RemoteCatWindow window)
    {
        if (window == null || window.IsDisposed) return;
        // 防御未来从网络/后台回调误调：静态 Timer 和容器只能在 UI 线程使用。
        if (window.InvokeRequired)
        {
            try { window.BeginInvoke(new Action<RemoteCatWindow>(Add), window); } catch { }
            return;
        }
        if (Active.ContainsKey(window) || !window.Visible || !window.CanBlink) return;

        long now = AnimationTimer.NowMs();
        var offsets = new List<long>(Active.Count);
        foreach (var state in Active.Values)
            offsets.Add((state.Closed ? state.CloseMs : state.NextMs) - now);
        long delay = RemoteBlinkLogic.InitialDelayMs(offsets, _nextOrdinal++);
        Active[window] = new State { NextMs = now + delay };
        Arm(now);
    }

    internal static void Remove(RemoteCatWindow window)
    {
        if (window == null) return;
        try
        {
            if (window.InvokeRequired)
            {
                window.BeginInvoke(new Action<RemoteCatWindow>(Remove), window);
                return;
            }
        }
        catch { }
        if (!Active.Remove(window)) return;
        Arm(AnimationTimer.NowMs());
    }

    /// <summary>测试用：当前参与共享眨眼时钟的窗口数量。</summary>
    internal static int ActiveCount => Active.Count;

    /// <summary>测试用：窗口是否已经登记到共享眨眼时钟。</summary>
    internal static bool IsScheduled(RemoteCatWindow window) => window != null && Active.ContainsKey(window);

    static void Pump()
    {
        long now = AnimationTimer.NowMs();
        Snapshot.Clear();
        Snapshot.AddRange(Active.Keys);
        foreach (var window in Snapshot)
        {
            if (!Active.TryGetValue(window, out var state)) continue;
            if (window.IsDisposed || !window.Visible || !window.CanBlink)
            {
                // 换装/帧未就绪导致不能眨眼时，必须把可能停在闭眼状态的窗口归位。
                if (!window.IsDisposed && window.Visible) window.SetBlinkHead(1);
                Active.Remove(window);
                continue;
            }
            try
            {
                if (state.Closed)
                {
                    if (now < state.CloseMs) continue;
                    // 先推进状态再重绘：即使绘制抛异常也不会停留在已到期的旧状态反复重试。
                    state.Closed = false;
                    // 主角的 5 秒是“闭眼起点到下一次闭眼起点”，所以下一拍从本次闭眼起点算。
                    state.NextMs = state.LastStartMs + RemoteBlinkLogic.NextIntervalMs(window.MemberId, state.Cycle++);
                    window.SetBlinkHead(1);
                    state.Failures = 0;
                }
                else if (now >= state.NextMs)
                {
                    state.Closed = true;
                    state.LastStartMs = now;
                    state.CloseMs = now + BlinkTiming.ClosedMs;
                    window.SetBlinkHead(2);
                    state.Failures = 0;
                }
            }
            catch
            {
                // 单个窗口的 GDI+/资源异常不能中断同一轮其他窗口的眨眼。
                if (++state.Failures >= MaxFailures) Active.Remove(window);
            }
        }
        Arm(AnimationTimer.NowMs());
    }

    static void Arm(long now)
    {
        if (Active.Count == 0)
        {
            if (Timer.Enabled) Timer.Stop();
            return;
        }
        long next = long.MaxValue;
        foreach (var state in Active.Values)
        {
            long due = state.Closed ? state.CloseMs : state.NextMs;
            if (due < next) next = due;
        }
        int wait = RemoteBlinkLogic.ArmDelay(next, now);
        if (Timer.Interval != wait) Timer.Interval = wait;
        if (!Timer.Enabled) Timer.Start();
    }
}
