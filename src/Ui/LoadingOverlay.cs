using System;
using System.Drawing;
using System.Windows.Forms;
using WuwatappoNative.Core;

namespace WuwatappoNative.Ui;

/// <summary>
/// 区服切换等耗时操作的轻量原生加载框。WebView2 切服后会 ReloadPage，
/// HTML 遮罩会随页面销毁，所以这里用独立小窗口保证加载提示始终可见。
/// </summary>
public sealed class LoadingOverlay : Form
{
    readonly Label _label;

    public LoadingOverlay(string text)
    {
        Text = "Wuwatappo";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(360, 124);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(251, 237, 213);
        Padding = new Padding(18, 16, 18, 14);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular),
            ForeColor = Color.FromArgb(92, 77, 66),
            BackColor = Color.Transparent,
        };
        var bar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 5,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
        };
        Controls.Add(_label);
        Controls.Add(bar);
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowCentered(IWin32Window owner)
    {
        Rectangle bounds;
        if (owner is Form form && !form.IsDisposed)
            bounds = form.Bounds;
        else
            bounds = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        Location = new Point(
            bounds.Left + Math.Max(0, (bounds.Width - Width) / 2),
            bounds.Top + Math.Max(0, (bounds.Height - Height) / 2));
        Show(owner);
        BringToFront();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public void SetText(string text)
    {
        if (_label != null && !_label.IsDisposed) _label.Text = text ?? "";
    }
}
