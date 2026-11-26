using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using WuwatappoNative.Pet;

internal static partial class Program
{
    static void CheckDualSenseBubblePolling(KeyboardWindow window, ControllerBridgeClient bridge)
    {
        var emitted = new List<string>();
        window.PadButtonPressed += emitted.Add;
        SetPadField(window, "_controllerProtocolExtension", true);
        var neutral = new byte[64];
        neutral[0] = 1;
        neutral[1] = neutral[2] = neutral[3] = neutral[4] = 128;
        neutral[8] = 8;
        DualSenseRawInput.TryDecodeUsbReport(neutral, out var idle);
        SetPadField(bridge, "_state", idle);
        SetPadField(bridge, "_connected", true);
        Poll(window);
        foreach (var (offset, value, label) in new[] {
            (9, (byte)0x01, "L1"), (9, (byte)0x02, "R1"),
            (5, (byte)64, "L2"), (6, (byte)64, "R2"),
            (8, (byte)0x88, "△"), (8, (byte)0x48, "○"),
            (8, (byte)0x28, "×"), (8, (byte)0x18, "□")
        })
        {
            var report = (byte[])neutral.Clone();
            report[offset] = value;
            Assert(DualSenseRawInput.TryDecodeUsbReport(report, out var pressed), "Bubble test report must decode.");
            emitted.Clear();
            SetPadField(bridge, "_state", pressed);
            Poll(window);
            Assert(emitted.SequenceEqual(new[] { label }),
                $"USB report through PollPad must emit exactly {label}, got {string.Join(",", emitted)}.");
            Poll(window);
            Assert(emitted.Count == 1, "A held USB report must not repeat a renamed bubble.");
            SetPadField(bridge, "_state", idle);
            Poll(window);
        }
        window.SetControllerProtocolExtension(false);
        Console.WriteLine("PASS: USB reports through PollPad emit L1/R1/L2/R2 and triangle/circle/cross/square bubbles.");
    }

    static void CheckDualSenseBubbleLabels(string previewPath)
    {
        var labels = new (ushort Bit, string Xbox, string PS)[] {
            (0x0100, "LB", "L1"), (0x0200, "RB", "R1"),
            (0x1000, "A", "×"), (0x2000, "B", "○"),
            (0x4000, "X", "□"), (0x8000, "Y", "△"),
            (0x0040, "LS", "L3"), (0x0080, "RS", "R3"),
            (0x0020, "Back", "Create"), (0x0010, "Start", "Options"),
            (0x0001, "↑", "↑"), (0x0002, "↓", "↓"),
            (0x0004, "←", "←"), (0x0008, "→", "→")
        };
        using var window = new KeyboardWindow();
        var emitted = new List<string>();
        int counts = 0;
        window.PadButtonPressed += emitted.Add;
        window.PadButtonDown += () => counts++;
        var tracker = PadField<PadInputTracker>(window, "_padInput");
        var edges = typeof(KeyboardWindow).GetMethod("ApplyPadButtonStateEdges", BindingFlags.Instance | BindingFlags.NonPublic);
        var flush = typeof(KeyboardWindow).GetMethod("FlushPadInput", BindingFlags.Instance | BindingFlags.NonPublic);
        var raise = typeof(KeyboardWindow).GetMethod("RaisePadButtonPressed", BindingFlags.Instance | BindingFlags.NonPublic);

        foreach (var source in new[] {
            (Enabled: true, Extension: true, Kind: ControllerKind.DualSense, PS: true),
            (Enabled: true, Extension: true, Kind: ControllerKind.Generic, PS: false),
            (Enabled: true, Extension: false, Kind: ControllerKind.DualSense, PS: false),
            (Enabled: false, Extension: true, Kind: ControllerKind.DualSense, PS: false)
        })
        {
            SetPadField(window, "_controllerProtocolExtension", source.Enabled);
            SetPadField(window, "_padFromExtension", source.Extension);
            SetPadField(window, "_padKind", source.Kind);
            foreach (var (bit, xbox, ps) in labels)
            {
                tracker.Reset();
                emitted.Clear();
                int before = counts;
                tracker.BeginPoll();
                edges.Invoke(window, new object[] { (ushort)0, bit });
                flush.Invoke(window, null);
                Assert(emitted.SequenceEqual(new[] { source.PS ? ps : xbox }),
                    $"Bubble for {xbox} must match the current source, got {string.Join(",", emitted)}.");
                Assert(counts == before + 1, "Changing the display name must preserve the original one-press count.");
                tracker.BeginPoll();
                edges.Invoke(window, new object[] { bit, bit });
                flush.Invoke(window, null);
                Assert(emitted.Count == 1 && counts == before + 1, "Holding a button must not duplicate the renamed bubble.");
                tracker.BeginPoll();
                edges.Invoke(window, new object[] { bit, (ushort)0 });
                flush.Invoke(window, null);
                Assert(!tracker.AnyActive, "Releasing a renamed button must clear the internal tracker.");
            }
            foreach (var (xbox, ps) in new[] { ("LT", "L2"), ("RT", "R2") })
            {
                emitted.Clear();
                PadField<List<string>>(window, "_padTriggerLabels").Add(xbox);
                tracker.BeginPoll();
                flush.Invoke(window, null);
                Assert(emitted.SequenceEqual(new[] { source.PS ? ps : xbox }), "Analog trigger bubbles must use L2/R2 for DS5.");
            }
            emitted.Clear();
            raise.Invoke(window, new object[] { "" });
            Assert(emitted.Count == 0, "An empty label must not generate a bubble.");
        }

        using var bubbles = new KeyBubbleLayer(30);
        var kindFor = typeof(KeyBubbleLayer).GetMethod("PadKindFor", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var (xbox, ps) in new[] { ("A", "×"), ("B", "○"), ("X", "□"), ("Y", "△") })
            Assert(kindFor.Invoke(null, new object[] { xbox }).Equals(kindFor.Invoke(null, new object[] { ps })),
                "PS face symbols must retain the corresponding button's bubble color.");
        foreach (string label in new[] { "L1", "R1", "L2", "R2", "△", "○", "×", "□", "L3", "R3", "Create", "Options" })
            Assert(bubbles.AddLabel(label, label is "L1" or "R1" or "L2" or "R2"), "Every renamed bubble must render.");
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            using var image = new Bitmap(1000, 300);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.FromArgb(245, 244, 242));
            int index = 0;
            foreach (var bubble in PadField<Dictionary<string, Image>>(bubbles, "_cache").Values)
            {
                graphics.DrawImageUnscaled(bubble, 12 + index % 4 * 250, 10 + index / 4 * 96);
                index++;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(previewPath)));
            image.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(previewPath)), "dualsense-bubbles.png"), ImageFormat.Png);
        }
        Console.WriteLine("PASS: DS5 bubble names, face colors, held-button deduplication and Xbox fallback.");
    }
}
