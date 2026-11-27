using System;
using System.Collections.Generic;
using System.Linq;
using WuwatappoNative.Pet;

// 纯输入回放，同时由 Windows 检查程序和跨平台检查程序调用，不依赖 XInput DLL 或手柄。
internal static class TriggerInputChecks
{
    internal static void Run(Action<bool, string> assert)
    {
        const ushort kd = PadInputLogic.KeyStrokeKeyDown;
        const ushort ku = PadInputLogic.KeyStrokeKeyUp;
        const ushort repeat = PadInputLogic.KeyStrokeRepeat;
        foreach (var (label, vk) in new[] { ("LT", (ushort)0x5806), ("RT", (ushort)0x5807) })
        {
            var tracker = new PadInputTracker();
            var bubbles = new List<string>();
            int downs = 0, ups = 0;
            void Poll(byte value, params ushort[] events)
            {
                tracker.BeginPoll();
                tracker.ObserveTriggerState(label == "LT" ? value : (byte)0,
                    label == "RT" ? value : (byte)0);
                foreach (ushort flags in events) tracker.ObserveKeystroke(vk, flags);
                if (tracker.PressedAny) ++downs;
                bubbles.AddRange(tracker.PressedLabels);
                if (tracker.ReleasedAll) ++ups;
            }

            // 模拟量先到达；XInput 的数字阈值可更深，且事件可能在后续轮询才到达。
            Poll(5);
            Poll(64, kd);
            Poll(255, (ushort)(kd | repeat));
            assert(bubbles.SequenceEqual(new[] { label }) && downs == 1,
                $"{label}: shallow state + later queue KEYDOWN + full travel must emit one bubble/down.");
            // 退回浅按时收到数字 KEYUP，再深按收到 KEYDOWN，仍属于同一次模拟量按压。
            Poll(5, ku);
            Poll(64, kd);
            tracker.ReleaseUnconfirmed();
            assert(tracker.AnyActive && bubbles.Count == 1 && ups == 0,
                $"{label}: queue threshold changes and fallback timer must not release a held trigger.");
            Poll(5);
            Poll(4, ku);
            assert(!tracker.AnyActive && ups == 1 && bubbles.Count == 1,
                $"{label}: raw 5 -> 4 release must end the press even below the render delta threshold.");

            // 同一轮内状态和队列一起到达；快速再次按下不能被时间防抖吞掉。
            Poll(5, kd);
            assert(bubbles.SequenceEqual(new[] { label, label }) && downs == 2,
                $"{label}: same-poll state/queue must emit one new bubble after a real release.");
            Poll(0, ku);
            assert(ups == 2, $"{label}: the second physical press must release once.");

            // 队列先到达，后续状态确认不应重复；未确认的队列按压仍可由定时器清理。
            tracker.Reset();
            bubbles.Clear();
            Poll(0, kd);
            Poll(5);
            tracker.ReleaseUnconfirmed();
            assert(bubbles.SequenceEqual(new[] { label }) && tracker.AnyActive,
                $"{label}: state confirmation after queue KEYDOWN must dedupe and survive fallback.");
            Poll(0, ku);
            assert(!tracker.AnyActive, $"{label}: confirmed queue press must physically release.");

            // 完整短点按落在两帧之间，仍须由 keystroke 队列补出来。
            tracker.Reset();
            bubbles.Clear();
            Poll(0, kd, ku);
            Poll(0, kd, ku);
            assert(bubbles.SequenceEqual(new[] { label, label }) && !tracker.AnyActive,
                $"{label}: two queue-only quick taps must each produce one bubble.");
            Poll(0, kd);
            tracker.ReleaseUnconfirmed();
            assert(!tracker.AnyActive, $"{label}: a missing queue KEYUP must still allow fallback release.");

            // 已按住时连接/切换来源只同步，不凭空产生新的气泡。
            tracker.SyncDown(new[] { label });
            bubbles.Clear();
            Poll(64, kd);
            assert(bubbles.Count == 0 && tracker.AnyActive,
                $"{label}: synchronizing a held trigger must suppress the initial queue duplicate.");
            Poll(0, ku);
            Poll(5);
            assert(bubbles.SequenceEqual(new[] { label }),
                $"{label}: a new press after initial synchronization must work.");
            tracker.Reset();
            assert(!tracker.AnyActive && !tracker.PressedAny,
                $"{label}: disconnect/reset must clear all trigger state.");
        }

        var combined = new PadInputTracker();
        combined.BeginPoll();
        combined.ObserveStateButton("A", true);
        combined.ObserveTriggerState(64, 128);
        combined.ObserveKeystroke(0x5806, kd);
        combined.ObserveKeystroke(0x5807, kd);
        assert(combined.PressedLabels.SequenceEqual(new[] { "A", "LT", "RT" }),
            "Simultaneous A + LT + RT must emit one label per physical button.");
        combined.BeginPoll();
        combined.ObserveStateButton("A", false);
        combined.ObserveTriggerState(0, 128);
        assert(combined.AnyActive && !combined.ReleasedAll,
            "Releasing A and LT must not release the still-held RT.");
        combined.BeginPoll();
        combined.ObserveTriggerState(0, 0);
        assert(!combined.AnyActive && combined.ReleasedAll,
            "Releasing the final trigger must emit ReleasedAll.");
        assert(!PadInputLogic.IsTriggerDown(0) && !PadInputLogic.IsTriggerDown(4) &&
            PadInputLogic.IsTriggerDown(5) && PadInputLogic.IsTriggerDown(255),
            "The existing > 4 analog trigger threshold must be preserved.");
    }
}
