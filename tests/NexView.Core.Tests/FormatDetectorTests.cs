using NexView.Core.Formats;

namespace NexView.Core.Tests;

public class FormatDetectorTests
{
    private static string Temp(string ext, byte[] content)
    {
        var p = Path.Combine(Path.GetTempPath(), "nexview-test-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(p, content);
        return p;
    }

    private static byte[] Pad(params byte[] head) { var b = new byte[64]; head.CopyTo(b, 0); return b; }
    private static byte[] Ftyp(string brand, params string[] compat)
    {
        var b = new byte[64];
        int size = 16 + compat.Length * 4;
        b[3] = (byte)size;
        "ftyp"u8.CopyTo(b.AsSpan(4));
        System.Text.Encoding.ASCII.GetBytes(brand).CopyTo(b, 8);
        for (int i = 0; i < compat.Length; i++) System.Text.Encoding.ASCII.GetBytes(compat[i]).CopyTo(b, 16 + i * 4);
        return b;
    }

    [Fact] public void Jpeg() => Assert.Equal("JPEG", FormatDetector.Detect(Temp(".jpg", Pad(0xFF, 0xD8, 0xFF, 0xE0))).Format?.Id);
    [Fact] public void Png() => Assert.Equal("PNG", FormatDetector.Detect(Temp(".png", Pad(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))).Format?.Id);

    [Fact]
    public void PngWithJpgExtensionIsMismatch()
    {
        var r = FormatDetector.Detect(Temp(".jpg", Pad(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)));
        Assert.Equal("PNG", r.Format?.Id);
        Assert.True(r.ExtensionMismatch);
    }

    [Fact] public void Heic() => Assert.Equal("HEIC", FormatDetector.Detect(Temp(".heic", Ftyp("heic", "mif1", "heic"))).Format?.Id);
    [Fact] public void Avif() => Assert.Equal("AVIF", FormatDetector.Detect(Temp(".avif", Ftyp("avif", "mif1"))).Format?.Id);
    [Fact] public void AvifCompatibleBrand() => Assert.Equal("AVIF", FormatDetector.Detect(Temp(".bin", Ftyp("mif1", "avif"))).Format?.Id);
    [Fact] public void Cr3() => Assert.Equal("CR3", FormatDetector.Detect(Temp(".cr3", Ftyp("crx "))).Format?.Id);

    [Fact]
    public void TiffBasedRawUsesExtension()
    {
        var r = FormatDetector.Detect(Temp(".nef", Pad((byte)'M', (byte)'M', 0, 0x2A)));
        Assert.Equal("NEF", r.Format?.Id);
        Assert.False(r.ExtensionMismatch);
    }

    [Fact] public void Cr2Signature() => Assert.Equal("CR2", FormatDetector.Detect(Temp(".tif", Pad((byte)'I', (byte)'I', 0x2A, 0, 0x10, 0, 0, 0, (byte)'C', (byte)'R'))).Format?.Id);
    [Fact] public void PlainTiff() => Assert.Equal("TIFF", FormatDetector.Detect(Temp(".tif", Pad((byte)'I', (byte)'I', 0x2A, 0))).Format?.Id);
    [Fact] public void TgaFallsBackToExtension() => Assert.Equal("TGA", FormatDetector.Detect(Temp(".tga", Pad(0, 0, 2, 0, 0, 0, 0, 0))).Format?.Id);
    [Fact] public void UnknownIsNull() => Assert.Null(FormatDetector.Detect(Temp(".xyz", Pad(1, 2, 3, 4))).Format);
}
