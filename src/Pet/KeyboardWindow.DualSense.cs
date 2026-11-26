using System.Drawing;
using System.Drawing.Drawing2D;

namespace WuwatappoNative.Pet;

public sealed partial class KeyboardWindow
{
    // 与 Xbox 面板共用逻辑尺寸、调色板和按键绘制方法。
    void DrawDualSense(Graphics g, float s, KeyboardTheme theme)
    {
        var saved = g.Save();
        try
        {
            g.TranslateTransform(0, (ToolbarH + 2) * s);
            g.ScaleTransform(s, s);
            using var body = new GraphicsPath();
            body.AddBezier(100, 20, 118, 20, 139, 17, 151, 18);
            body.AddBezier(151, 18, 165, 19, 174, 29, 179, 43);
            body.AddBezier(179, 43, 185, 58, 188, 78, 193, 99);
            body.AddBezier(193, 99, 198, 119, 198, 133, 191, 142);
            body.AddBezier(191, 142, 182, 152, 168, 152, 159, 142);
            body.AddBezier(159, 142, 150, 132, 143, 121, 134, 116);
            body.AddBezier(134, 116, 124, 110, 111, 115, 100, 115);
            body.AddBezier(100, 115, 89, 115, 76, 110, 66, 116);
            body.AddBezier(66, 116, 57, 121, 50, 132, 41, 142);
            body.AddBezier(41, 142, 32, 152, 18, 152, 9, 142);
            body.AddBezier(9, 142, 2, 133, 2, 119, 7, 99);
            body.AddBezier(7, 99, 12, 78, 15, 58, 21, 43);
            body.AddBezier(21, 43, 26, 29, 35, 19, 49, 18);
            body.AddBezier(49, 18, 61, 17, 82, 20, 100, 20);
            body.CloseFigure();
            using (var brush = new SolidBrush(theme.PadBody)) g.FillPath(brush, body);
            using (var pen = new Pen(theme.PadOutline, 2f)) g.DrawPath(pen, body);

            DrawPadTrigger(g, 1, 42, 6, 30, "L2", _lt, theme);
            DrawPadTrigger(g, 1, 128, 6, 30, "R2", _rt, theme);
            DrawPadBumper(g, 1, 44, 16, 26, "L1", PadPressed(0x0100), theme);
            DrawPadBumper(g, 1, 130, 16, 26, "R1", PadPressed(0x0200), theme);

            bool touchDown = (_dualSenseButtons & DualSenseButtons.Touchpad) != 0;
            using (var touch = new GraphicsPath())
            {
                touch.AddBezier(77, 29, 89, 28.5f, 111, 28.5f, 123, 29);
                touch.AddBezier(123, 29, 128, 29, 131, 32, 130, 37);
                touch.AddBezier(130, 37, 129, 44, 127, 54, 125.5f, 61);
                touch.AddBezier(125.5f, 61, 124.5f, 66, 122, 68, 117, 68);
                touch.AddBezier(117, 68, 107, 68.5f, 93, 68.5f, 83, 68);
                touch.AddBezier(83, 68, 78, 68, 75.5f, 66, 74.5f, 61);
                touch.AddBezier(74.5f, 61, 73, 54, 71, 44, 70, 37);
                touch.AddBezier(70, 37, 69, 32, 72, 29, 77, 29);
                touch.CloseFigure();
                using var brush = new SolidBrush(touchDown ? theme.PadPressedFor(theme.PadStickWell) : theme.PadStickWell);
                using var pen = new Pen(theme.PadOutline, 1.7f);
                g.FillPath(brush, touch);
                g.DrawPath(pen, touch);
            }
            using (var pen = new Pen(theme.PadOutline, 1.2f)) g.DrawLine(pen, 95.5f, 61.7f, 104.5f, 61.7f);

            DrawPadDash(g, 1, 58.9f, 30.2f, 8.2f, 12.6f, PadPressed(0x0020), theme);
            DrawPadDash(g, 1, 132.9f, 30.2f, 8.2f, 12.6f, PadPressed(0x0010), theme);
            using (var pen = new Pen(theme.PadTextFor(PadPressed(0x0020) ? theme.PadPressedFor(theme.Cream2) : theme.Cream2), 0.7f))
            {
                g.DrawLine(pen, 60.9f, 36.5f, 62, 35.5f);
                g.DrawLine(pen, 63, 36.15f, 63, 34.5f);
                g.DrawLine(pen, 65.1f, 36.5f, 64, 35.5f);
            }
            using (var pen = new Pen(theme.PadTextFor(PadPressed(0x0010) ? theme.PadPressedFor(theme.Cream2) : theme.Cream2), 0.7f))
                for (int row = 0; row < 3; row++) g.DrawLine(pen, 135.4f, 35.2f + row * 1.45f, 138.6f, 35.2f + row * 1.45f);

            DrawDualSenseDirection(g, 0, PadPressed(0x0001), theme);
            DrawDualSenseDirection(g, 90, PadPressed(0x0008), theme);
            DrawDualSenseDirection(g, 180, PadPressed(0x0002), theme);
            DrawDualSenseDirection(g, 270, PadPressed(0x0004), theme);
            DrawFace(g, 1, 151, 40.4f, "△", PadPressed(0x8000), Color.FromArgb(252, 226, 156), theme);
            DrawFace(g, 1, 137.4f, 54, "□", PadPressed(0x4000), Color.FromArgb(214, 233, 248), theme);
            DrawFace(g, 1, 164.6f, 54, "○", PadPressed(0x2000), Color.FromArgb(250, 217, 212), theme);
            DrawFace(g, 1, 151, 67.6f, "×", PadPressed(0x1000), Color.FromArgb(215, 237, 208), theme);
            DrawStick(g, 1, 75, 96, _lx, _ly, PadPressed(0x0040), "L3", theme);
            DrawStick(g, 1, 125, 96, _rx, _ry, PadPressed(0x0080), "R3", theme);
            DrawPadKey(g, 1, 92.55f, 79.65f, 14.9f, 14.9f, "PS",
                (_dualSenseButtons & DualSenseButtons.PS) != 0, 7.45f, theme);

            bool micDown = (_dualSenseButtons & DualSenseButtons.Microphone) != 0;
            DrawPadDash(g, 1, 93.7f, 99.8f, 12.6f, 9.1f, micDown, theme);
            using (var pen = new Pen(theme.PadTextFor(micDown ? theme.PadPressedFor(theme.Cream2) : theme.Cream2), 0.75f))
            {
                g.DrawEllipse(pen, 99.2f, 101.45f, 1.6f, 3.1f);
                g.DrawArc(pen, 98, 102, 4, 3.5f, 0, 180);
                g.DrawLine(pen, 100, 105.5f, 100, 106.9f);
            }
        }
        finally { g.Restore(saved); }
    }

    void DrawDualSenseDirection(Graphics g, float angle, bool down, KeyboardTheme theme)
    {
        var saved = g.Save();
        try
        {
            g.TranslateTransform(50, 55);
            g.RotateTransform(angle);
            using var path = new GraphicsPath();
            path.AddPolygon(new[] {
                new PointF(-5.3f, -16.9f), new PointF(5.3f, -16.9f),
                new PointF(5.3f, -8.8f), new PointF(0, -4.7f), new PointF(-5.3f, -8.8f)
            });
            using var brush = new SolidBrush(down ? theme.PadPressedFor(theme.PadBody) : theme.PadBody);
            using var pen = new Pen(theme.PadOutline, 1.65f) { LineJoin = LineJoin.Round };
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        finally { g.Restore(saved); }
    }
}
