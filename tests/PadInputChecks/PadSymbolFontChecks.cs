using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WuwatappoNative.Pet;

internal static partial class Program
{
    static void CheckPadSymbolFont()
    {
        foreach (string label in new[] { "△", "○", "×", "□" })
        {
            using var font = PadSymbolFont.Create(label, 19);
            Assert(font.FontFamily.Name == "Wuwa Pad Symbols" && font.Style == FontStyle.Regular,
                "Each PS face symbol must load the embedded regular font, without synthetic bold or system fallback.");
            using var path = new GraphicsPath();
            path.AddString(label, font.FontFamily, (int)font.Style, 19, PointF.Empty, StringFormat.GenericTypographic);
            Assert(path.PointCount > 0 && path.GetBounds().Width > 10,
                "Each embedded face symbol must have a usable GDI+ outline.");
        }
        foreach (string label in new[] { "A", "B", "X", "Y", "L1", "R2", "Options", "Space", "↑" })
        {
            using var font = PadSymbolFont.Create(label, 19);
            using var original = new Font("Microsoft YaHei UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            Assert(font.FontFamily.Name == original.FontFamily.Name && font.Style == original.Style,
                "Other keys must keep the original system font and weight.");
        }
        // Collection and backing bytes must survive GC and repeated bitmap cache lifetimes.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var bubbles = new KeyBubbleLayer();
        foreach (string label in new[] { "△", "○", "×", "□" })
            Assert(bubbles.AddLabel(label), "Embedded symbols must still render after GC.");
        Console.WriteLine("PASS: embedded four-symbol font, GDI+ outlines, other-key fallback and lifetime.");
    }
}
