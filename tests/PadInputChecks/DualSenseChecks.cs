using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using WuwatappoNative.Core;
using WuwatappoNative.Pet;

internal static partial class Program
{
    static void CheckDualSenseIdentity()
    {
        var generic = new ControllerPadState(0x1000, 63, 192);
        var sony = new ControllerPadState(0, 0, 0, Kind: ControllerKind.DualSense,
            ExtraButtons: DualSenseButtons.PS | DualSenseButtons.Touchpad | DualSenseButtons.Microphone);
        Assert(generic.Kind == ControllerKind.Generic, "Existing callers must retain the generic Xbox layout.");
        Assert(generic.Merge(sony).Kind == ControllerKind.DualSense &&
               sony.Merge(generic).Kind == ControllerKind.DualSense &&
               generic.Merge(sony).ExtraButtons == sony.ExtraButtons,
            "Merging extension devices must retain the verified DualSense source and extra buttons.");
        var joy = new WinMmPadBridge.JoyInfoEx { dwPOV = WinMmPadBridge.PovCentered };
        Assert(WinMmPadBridge.Convert(joy).Kind == ControllerKind.Generic &&
               WinMmPadBridge.Convert(joy, 0x09CC).Kind == ControllerKind.Generic &&
               WinMmPadBridge.Convert(joy, 0x0CE6).Kind == ControllerKind.DualSense &&
               WinMmPadBridge.Convert(joy, 0x0DF2).Kind == ControllerKind.DualSense,
            "WinMM must identify DualSense/Edge while other controllers keep the Xbox layout.");
        var report = new byte[64];
        report[0] = 1;
        report[1] = report[2] = report[3] = report[4] = 128;
        report[8] = 8;
        for (int bits = 0; bits < 8; bits++)
        {
            report[10] = (byte)(bits | 0xF8);
            Assert(DualSenseRawInput.TryDecodeUsbReport(report, out var decoded) &&
                   decoded.Kind == ControllerKind.DualSense && decoded.ExtraButtons == (DualSenseButtons)bits &&
                   decoded.Buttons == 0,
                "PS, touchpad and microphone bits must remain independent from XInput button counts.");
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var state in new[] { generic, sony, generic.Merge(sony), default }) state.WriteTo(writer);
        writer.Flush();
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        foreach (var state in new[] { generic, sony, generic.Merge(sony), default })
            Assert(ControllerPadState.ReadFrom(reader) == state, "Pipe frames must preserve source, extra buttons and alignment.");
        Assert(stream.Position == stream.Length, "Every pipe byte must belong to the expected frame.");
    }

    static void SetPadField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    static T PadField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    static void Poll(KeyboardWindow window) =>
        typeof(KeyboardWindow).GetMethod("PollPad", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { false });

    static Bitmap RenderPad(KeyboardWindow window, int scale = 1)
    {
        var bitmap = new Bitmap(200 * scale, 180 * scale, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        window.DrawTo(graphics, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return bitmap;
    }

    static bool ContainsPadColor(Bitmap image, Rectangle region, Color color)
    {
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
                if (image.GetPixel(x, y).ToArgb() == color.ToArgb()) return true;
        return false;
    }

    static void CheckDualSenseAppearance(string previewPath)
    {
        using var window = new KeyboardWindow();
        SetPadField(window, "_mode", KeyboardWindow.Mode.Controller);
        SetPadField(window, "_padConnected", true);
        SetPadField(window, "_padFromExtension", true);
        SetPadField(window, "_padKind", ControllerKind.DualSense);
        SetPadField(window, "_controllerProtocolExtension", true);
        var configurations = new[] {
            new Settings(),
            new Settings { ThemeAccentColor = "#7066B4" },
            new Settings { ThemeBarFollowStandard = false, ThemeBarColor = "#286EA8", ThemeBarTextFollowTheme = false, ThemeBarTextColor = "#102F49" },
            new Settings { ThemeBarFollowStandard = false, ThemeBarColor = "#252039", ThemeBarTextFollowTheme = false, ThemeBarTextColor = "#FFFFFF" },
            new Settings { ThemeAccentColor = "#FFF8F0" }
        };
        using var sheet = new Bitmap(1000, 360);
        using var sheetGraphics = Graphics.FromImage(sheet);
        sheetGraphics.Clear(Color.FromArgb(245, 244, 242));
        Color previousBody = Color.Empty;
        for (int i = 0; i < configurations.Length; i++)
        {
            var theme = KeyboardTheme.Resolve(configurations[i]);
            window.ApplyTheme(configurations[i]);
            SetPadField(window, "_padButtons", (ushort)0);
            SetPadField(window, "_dualSenseButtons", DualSenseButtons.None);
            SetPadField(window, "_lt", (byte)0);
            SetPadField(window, "_rt", (byte)0);
            using var idle = RenderPad(window);
            Assert(idle.GetPixel(100, 100).ToArgb() == theme.PadBody.ToArgb(), "DualSense body must use the resolved palette.");
            Assert(idle.GetPixel(100, 100) != previousBody, "Distinct theme settings must change the rendered body.");
            previousBody = idle.GetPixel(100, 100);
            Assert(ContainsPadColor(idle, new Rectangle(68, 53, 64, 44), theme.PadOutline), "Touchpad outline must use the palette.");
            Assert(ContainsPadColor(idle, new Rectangle(60, 106, 30, 32), theme.PadStickCap), "Stick caps must use the palette.");
            Assert(ContainsPadColor(idle, new Rectangle(42, 31, 30, 11), theme.PadTriggerTrack), "Trigger track must use the palette.");
            sheetGraphics.DrawImageUnscaled(idle, i * 200, 0);
            SetPadField(window, "_padButtons", (ushort)0xF3FF);
            SetPadField(window, "_dualSenseButtons", DualSenseButtons.PS | DualSenseButtons.Touchpad | DualSenseButtons.Microphone);
            SetPadField(window, "_lt", (byte)127);
            SetPadField(window, "_rt", (byte)255);
            using var pressed = RenderPad(window);
            var regions = new[] {
                (new Rectangle(45, 63, 11, 14), theme.PadPressedFor(theme.PadBody)),
                (new Rectangle(44, 42, 26, 10), theme.PadPressedFor(theme.PadBody)),
                (new Rectangle(130, 42, 26, 10), theme.PadPressedFor(theme.PadBody)),
                (new Rectangle(59, 56, 8, 13), theme.PadPressedFor(theme.Cream2)),
                (new Rectangle(133, 56, 8, 13), theme.PadPressedFor(theme.Cream2)),
                (new Rectangle(78, 57, 44, 33), theme.PadPressedFor(theme.PadStickWell)),
                (new Rectangle(93, 106, 14, 14), theme.PadPressedFor(theme.Cream2)),
                (new Rectangle(94, 126, 12, 9), theme.PadPressedFor(theme.Cream2)),
                (new Rectangle(60, 107, 30, 30), theme.PadPressedFor(theme.PadStickCap)),
                (new Rectangle(110, 107, 30, 30), theme.PadPressedFor(theme.PadStickCap)),
                (new Rectangle(42, 32, 30, 9), theme.PadTriggerFill)
            };
            foreach (var (region, color) in regions)
                Assert(ContainsPadColor(pressed, region, color), $"DualSense region {region} must show the resolved pressed color {color}.");
            Assert(idle.GetPixel(151, 90) != pressed.GetPixel(151, 90), "Cross button must visibly change on press.");
            sheetGraphics.DrawImageUnscaled(pressed, i * 200, 180);
        }
        window.ApplyTheme(configurations[0]);
        window.PreviewTheme(configurations[1]);
        using (var preview = RenderPad(window))
            Assert(preview.GetPixel(100, 100).ToArgb() == KeyboardTheme.Resolve(configurations[1]).PadBody.ToArgb(), "Theme preview must recolor DualSense immediately.");
        window.ClearThemePreview();
        using (var restored = RenderPad(window))
            Assert(restored.GetPixel(100, 100).ToArgb() == KeyboardTheme.Resolve(configurations[0]).PadBody.ToArgb(), "Cancelling preview must restore DualSense colors.");
        // 同一来源信息只有在扩展开启并且当前输入来自扩展时才能选择 DS5。
        SetPadField(window, "_padButtons", (ushort)0);
        SetPadField(window, "_lt", (byte)0);
        SetPadField(window, "_rt", (byte)0);
        SetPadField(window, "_padFromExtension", false);
        using var xinput = RenderPad(window);
        SetPadField(window, "_padFromExtension", true);
        SetPadField(window, "_padKind", ControllerKind.Generic);
        using var thirdParty = RenderPad(window);
        SetPadField(window, "_padKind", ControllerKind.DualSense);
        SetPadField(window, "_controllerProtocolExtension", false);
        using var disabled = RenderPad(window);
        Assert(xinput.GetPixel(100, 139) == thirdParty.GetPixel(100, 139) &&
               xinput.GetPixel(100, 139) == disabled.GetPixel(100, 139) &&
               xinput.GetPixel(100, 139).A == 0,
            "XInput, generic extension and disabled extension must retain the Xbox silhouette.");
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(previewPath)));
            sheet.Save(previewPath, ImageFormat.Png);
        }
        Console.WriteLine("PASS: DualSense theme pixels, highlights, preview and Xbox fallback.");
    }

    static void CheckLiveDualSense()
    {
        using var window = new KeyboardWindow();
        int counts = 0;
        window.PadButtonDown += () => counts++;
        window.SetControllerProtocolExtension(true);
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000 && PadField<ControllerKind>(window, "_padKind") != ControllerKind.DualSense)
        {
            Application.DoEvents();
            Poll(window);
            Thread.Sleep(16);
        }
        Assert(PadField<bool>(window, "_padConnected") && PadField<bool>(window, "_padFromExtension") &&
               PadField<ControllerKind>(window, "_padKind") == ControllerKind.DualSense,
            "The real wired DualSense must be recognized through the child process and normal PollPad path.");
        Console.WriteLine($"PASS: live DualSense detected in {timer.ElapsedMilliseconds}ms; buttons=0x{PadField<ushort>(window, "_padButtons"):X4}.");
        // 用正常 PollPad/TryRead 链路回放第三方手柄和断开快照，验证来源变化本身就能换肤。
        var bridge = PadField<ControllerBridgeClient>(window, "_controllerBridge");
        bridge.Stop();
        SetPadField(bridge, "_state", new ControllerPadState(0x1000, 0, 0));
        SetPadField(bridge, "_connected", true);
        Poll(window);
        Assert(PadField<ControllerKind>(window, "_padKind") == ControllerKind.Generic,
            "A third-party extension snapshot must switch the normal polling path back to Xbox.");
        SetPadField(bridge, "_state", new ControllerPadState(0x1000, 0, 0, Kind: ControllerKind.DualSense));
        Poll(window);
        Assert(PadField<ControllerKind>(window, "_padKind") == ControllerKind.DualSense && counts == 0,
            "A source change with identical held buttons must switch appearance without a false press.");
        SetPadField(bridge, "_connected", false);
        Poll(window);
        Assert(!PadField<bool>(window, "_padConnected") && PadField<ControllerKind>(window, "_padKind") == ControllerKind.Generic,
            "A disconnect snapshot must clear the DualSense appearance.");
        SetPadField(bridge, "_connected", true);
        Poll(window);
        Assert(PadField<ControllerKind>(window, "_padKind") == ControllerKind.DualSense && counts == 0,
            "Reconnecting with a held button must synchronize without a false press.");
        window.SetControllerProtocolExtension(false);
        Assert(!PadField<bool>(window, "_padConnected") && PadField<ControllerKind>(window, "_padKind") == ControllerKind.Generic &&
               PadField<DualSenseButtons>(window, "_dualSenseButtons") == DualSenseButtons.None,
            "Disabling the extension must clear the DualSense source and button state.");
        Assert(counts == 0, "Initial connection and protocol toggling must not generate button counts.");
        SetPadField(bridge, "_state", new ControllerPadState(0, 0, 0, Kind: ControllerKind.DualSense));
        SetPadField(bridge, "_connected", true);
        Poll(window);
        Assert(!PadField<bool>(window, "_padConnected"), "Disabled protocol must ignore even a stale DualSense snapshot.");
        Console.WriteLine("PASS: live protocol off resets DualSense state without counting a press.");
        CheckDualSenseBubblePolling(window, bridge);
    }
}
