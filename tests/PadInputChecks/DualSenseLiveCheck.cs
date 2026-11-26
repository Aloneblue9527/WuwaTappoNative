using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using WuwatappoNative.Core;
using WuwatappoNative.Pet;

internal static partial class Program
{
    static int ExerciseDualSense(string reportPath)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var pad = new KeyboardWindow();
        SetPadField(pad, "_mode", KeyboardWindow.Mode.Controller);
        using var form = new Form {
            Text = "DualSense 本地实机验收", ClientSize = new Size(850, 650),
            StartPosition = FormStartPosition.CenterScreen, Font = new Font("Microsoft YaHei UI", 10)
        };
        var enabled = new CheckBox { Text = "手柄协议扩展", Checked = true, AutoSize = true, Location = new Point(18, 15) };
        var themes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(195, 12), Width = 150 };
        themes.Items.AddRange(new object[] { "默认主题", "紫色主题", "独立深色底栏" });
        themes.SelectedIndex = 0;
        var status = new Label { Location = new Point(18, 55), Size = new Size(810, 55) };
        var canvas = new Panel { Location = new Point(10, 115), Size = new Size(500, 450) };
        var checklist = new Label { Location = new Point(530, 115), Size = new Size(310, 500), Font = new Font("Microsoft YaHei UI", 9) };
        form.Controls.AddRange(new Control[] { enabled, themes, status, canvas, checklist });
        canvas.Paint += (_, e) => pad.DrawTo(e.Graphics, new Rectangle(0, 0, 500, 450));
        themes.SelectedIndexChanged += (_, _) => {
            pad.ApplyTheme(themes.SelectedIndex switch {
                1 => new Settings { ThemeAccentColor = "#7066B4" },
                2 => new Settings { ThemeBarFollowStandard = false, ThemeBarColor = "#252039", ThemeBarTextFollowTheme = false, ThemeBarTextColor = "#FFFFFF" },
                _ => new Settings()
            });
            canvas.Invalidate();
        };
        enabled.CheckedChanged += (_, _) => pad.SetControllerProtocolExtension(enabled.Checked);
        pad.SetControllerProtocolExtension(true);
        var checks = new Dictionary<string, bool>();
        var buttons = new (ushort Bit, string Name)[] {
            (0x0001,"↑"),(0x0002,"↓"),(0x0004,"←"),(0x0008,"→"),
            (0x0010,"菜单"),(0x0020,"分享"),(0x0040,"L3"),(0x0080,"R3"),
            (0x0100,"L1"),(0x0200,"R1"),(0x1000,"×"),(0x2000,"○"),(0x4000,"□"),(0x8000,"△")
        };
        foreach (var (_, name) in buttons) checks[name] = false;
        foreach (string name in new[] { "L2", "R2", "PS", "触摸板按压", "麦克风键", "左杆四向", "右杆四向", "USB断开", "USB重连", "协议关闭", "协议重开", "切换主题" }) checks[name] = false;
        bool seenConnected = false, seenDisconnected = false;
        int leftDirections = 0, rightDirections = 0;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        int result = 1;
        timer.Tick += (_, _) => {
            bool connected = PadField<bool>(pad, "_padConnected");
            bool ds5 = connected && PadField<ControllerKind>(pad, "_padKind") == ControllerKind.DualSense;
            if (ds5)
            {
                seenConnected = true;
                if (seenDisconnected) checks["USB重连"] = true;
                if (checks["协议关闭"]) checks["协议重开"] = true;
                ushort down = PadField<ushort>(pad, "_padButtons");
                foreach (var (bit, name) in buttons) checks[name] |= (down & bit) != 0;
                checks["L2"] |= PadField<byte>(pad, "_lt") > 220;
                checks["R2"] |= PadField<byte>(pad, "_rt") > 220;
                var extra = PadField<DualSenseButtons>(pad, "_dualSenseButtons");
                checks["PS"] |= (extra & DualSenseButtons.PS) != 0;
                checks["触摸板按压"] |= (extra & DualSenseButtons.Touchpad) != 0;
                checks["麦克风键"] |= (extra & DualSenseButtons.Microphone) != 0;
                leftDirections |= StickDirections(PadField<short>(pad, "_lx"), PadField<short>(pad, "_ly"));
                rightDirections |= StickDirections(PadField<short>(pad, "_rx"), PadField<short>(pad, "_ry"));
                checks["左杆四向"] = leftDirections == 15;
                checks["右杆四向"] = rightDirections == 15;
            }
            if (enabled.Checked && seenConnected && !connected)
            {
                seenDisconnected = true;
                checks["USB断开"] = true;
            }
            if (!enabled.Checked && !connected) checks["协议关闭"] = true;
            checks["切换主题"] |= themes.SelectedIndex != 0;
            status.Text = $"当前来源 {(ds5 ? "DualSense" : connected ? "XInput / 其他手柄" : "未连接")}　已通过 {checks.Count(c => c.Value)}/{checks.Count}　剩余 {Math.Max(0, 240 - (int)elapsed.Elapsed.TotalSeconds)} 秒\n逐个按键、双杆转圈、拔插 USB，切换顶部主题，并关闭再打开协议扩展。";
            checklist.Text = string.Join("\n", checks.Select(c => (c.Value ? "✓  " : "·  ") + c.Key));
            canvas.Invalidate();
            if (checks.All(c => c.Value) || elapsed.Elapsed.TotalSeconds >= 240)
            {
                result = checks.All(c => c.Value) ? 0 : 1;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
                File.WriteAllLines(reportPath, checks.Select(c => $"{(c.Value ? "PASS" : "PENDING")}: {c.Key}"));
                using var image = RenderPad(pad, 3);
                image.Save(Path.ChangeExtension(reportPath, ".png"));
                form.Close();
            }
        };
        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
        timer.Stop();
        pad.SetControllerProtocolExtension(false);
        pad.Close();
        Console.WriteLine($"Live exercise exit={result}; passed={checks.Count(c => c.Value)}/{checks.Count}; report={reportPath}");
        return result;
    }

    static int StickDirections(short x, short y) =>
        (x < -20000 ? 1 : 0) | (x > 20000 ? 2 : 0) | (y < -20000 ? 4 : 0) | (y > 20000 ? 8 : 0);
}
