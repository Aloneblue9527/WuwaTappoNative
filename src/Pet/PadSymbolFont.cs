using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WuwatappoNative.Pet;

/// <summary>Only the four PS face symbols use the embedded, uniform-stroke icon font.</summary>
internal static class PadSymbolFont
{
    // GDI+ requires the font bytes to remain alive while its family/fonts are in use.
    // Keep this tiny collection and its pinned backing buffer for the process lifetime.
    static readonly Lazy<PrivateFontCollection> Symbols = new(Load);
    static GCHandle _fontBytes;

    internal static Font Create(string label, float size) => label is "△" or "○" or "×" or "□"
        ? new Font(Symbols.Value.Families[0], size, FontStyle.Regular, GraphicsUnit.Pixel)
        : new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Pixel);

    static PrivateFontCollection Load()
    {
        using var stream = typeof(PadSymbolFont).Assembly.GetManifestResourceStream("WuwatappoNative.PadSymbols.ttf")
            ?? throw new InvalidOperationException("Embedded pad symbol font is missing.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var fonts = new PrivateFontCollection();
        try
        {
            fonts.AddMemoryFont(pinned.AddrOfPinnedObject(), bytes.Length);
            _fontBytes = pinned;
            return fonts;
        }
        catch
        {
            fonts.Dispose();
            pinned.Free();
            throw;
        }
    }
}
