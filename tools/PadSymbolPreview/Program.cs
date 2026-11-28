using System.Drawing.Imaging;
using System.Reflection;
using WuwatappoNative.Pet;

// Render the real application's cached keycaps, including on the pre-change revision.
// Usage (Windows): dotnet run --project tools/PadSymbolPreview -- OUTPUT.png CAPTION
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var bubbles = new KeyBubbleLayer();
        foreach (string label in new[] { "△", "○", "×", "□" }) bubbles.AddLabel(label);
        var cache = (Dictionary<string, Image>)typeof(KeyBubbleLayer)
            .GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bubbles);
        using var image = new Bitmap(720, 380);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.FromArgb(245, 244, 242));
        using var title = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        using var caption = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        using var legacy = new Font("Microsoft YaHei UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString(args.Length > 1 ? args[1] : "DS5 face symbols", title, Brushes.Black, 20, 14);
        g.DrawString("Actual cached bubbles: 100% logical size / 2x detail", caption, Brushes.DimGray, 20, 50);
        int i = 0;
        foreach (var bubble in cache.Values)
        {
            g.DrawImage(bubble, new Rectangle(30 + i * 170, 90, bubble.Width / 2, bubble.Height / 2));
            g.DrawImageUnscaled(bubble, 30 + i * 170, 164);
            i++;
        }
        g.DrawString("Labels: triangle / circle / cross / square", caption, Brushes.DimGray, 20, 280);
        g.DrawString($"Windows GDI+; requested legacy font: Microsoft YaHei UI Bold; resolved: {legacy.Name}",
            caption, Brushes.DimGray, new RectangleF(20, 310, 680, 60));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        image.Save(args[0], ImageFormat.Png);
        Console.WriteLine($"Saved {args[0]}; legacy font resolves to {legacy.Name}");
    }
}
