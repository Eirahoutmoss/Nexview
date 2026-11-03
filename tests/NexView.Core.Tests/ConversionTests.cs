using ImageMagick;
using NexView.Codecs.Magick;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Metadata;

namespace NexView.Core.Tests;

public class ConversionTests
{
    private static readonly MagickConverter Converter = new();

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "nexview-conv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string MakeNoisy(string dir, string name, MagickFormat fmt, uint w = 1200, uint h = 800, bool gps = false, bool alpha = false)
    {
        var p = Path.Combine(dir, name);
        using var img = new MagickImage("plasma:", w, h);
        if (alpha) img.Alpha(AlphaOption.Set);
        if (gps)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(37, 1), new Rational(45, 1), new Rational(0, 1)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "E");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(38, 1), new Rational(16, 1), new Rational(0, 1)]);
            img.SetProfile(exif);
        }
        img.Write(p, fmt);
        return p;
    }

    [Theory]
    [InlineData("PNG")]
    [InlineData("WEBP")]
    [InlineData("TIFF")]
    [InlineData("AVIF")]
    [InlineData("BMP")]
    [InlineData("GIF")]
    [InlineData("ICO")]
    public void ConvertsJpegTo(string target)
    {
        var dir = TempDir();
        var src = MakeNoisy(dir, "a.jpg", MagickFormat.Jpeg, 600, 400);
        var r = Converter.Convert(src, new ConversionOptions { Target = FormatRegistry.FromId(target)! }, new OutputOptions(), CancellationToken.None);
        Assert.Equal(ConversionStatus.Success, r.Status);
        Assert.True(File.Exists(r.Output));
        Assert.True(File.Exists(src));
        Assert.Equal(FormatRegistry.FromId(target)!.Id, FormatDetector.Detect(r.Output!).Format?.Id);
    }

    [Fact]
    public void NeverOverwritesAndRenames()
    {
        var dir = TempDir();
        var src = MakeNoisy(dir, "b.png", MagickFormat.Png, 200, 100);
        var o = new ConversionOptions { Target = FormatRegistry.Png };
        var r = Converter.Convert(src, o, new OutputOptions(), CancellationToken.None);
        Assert.Equal(Path.Combine(dir, "b (1).png"), r.Output);
        var skip = Converter.Convert(src, o, new OutputOptions { Conflict = ConflictPolicy.Skip }, CancellationToken.None);
        Assert.Equal(ConversionStatus.Skipped, skip.Status);
    }

    [Fact]
    public void ResizesLongEdgeShrinkOnly()
    {
        var dir = TempDir();
        var src = MakeNoisy(dir, "c.jpg", MagickFormat.Jpeg, 1200, 800);
        var r = Converter.Convert(src, new ConversionOptions { Target = FormatRegistry.Jpeg, ResizeMode = ResizeMode.LongEdge, ResizeValue = 600 }, new OutputOptions(), CancellationToken.None);
        Assert.Equal(600, r.Width);
        Assert.Equal(400, r.Height);
        var r2 = Converter.Convert(src, new ConversionOptions { Target = FormatRegistry.Png, ResizeMode = ResizeMode.LongEdge, ResizeValue = 5000 }, new OutputOptions(), CancellationToken.None);
        Assert.Equal(1200, r2.Width);
    }

    [Fact]
    public void RemovesGpsButKeepsCamera()
    {
        var dir = TempDir();
        var src = MakeNoisy(dir, "gps.jpg", MagickFormat.Jpeg, 300, 200, gps: true);
        Assert.True(MetadataService.Read(src).HasGps);
        var r = Converter.Convert(src, new ConversionOptions { Target = FormatRegistry.Jpeg, Metadata = MetadataPolicy.RemoveGps }, new OutputOptions(), CancellationToken.None);
        var m = MetadataService.Read(r.Output!);
        Assert.False(m.HasGps);
        Assert.Equal("TestCam", m.CameraMake);
    }

    [Fact]
    public void RemovesAllMetadata()
    {
        var dir = TempDir();
        var src = MakeNoisy(dir, "all.jpg", MagickFormat.Jpeg, 300, 200, gps: true);
        var r = Converter.Convert(src, new ConversionOptions { Target = FormatRegistry.Jpeg, Metadata = MetadataPolicy.RemoveAll }, new OutputOptions(), CancellationToken.None);
        var m = MetadataService.Read(r.Output!);
        Assert.False(m.HasGps);
        Assert.Null(m.CameraMake);
    }

    [Fact]
    public void HitsTargetSize()
    {
        var dir = TempDir();
        var src = MakeNoisy(dir, "big.png", MagickFormat.Png, 2400, 1600);
        var limit = 150 * 1024;
        var r = Converter.Convert(src, new ConversionOptions { Target = FormatRegistry.Jpeg, Quality = 95, TargetSizeBytes = limit }, new OutputOptions(), CancellationToken.None);
        Assert.Equal(ConversionStatus.Success, r.Status);
        Assert.True(new FileInfo(r.Output!).Length <= limit);
        Assert.False(r.TargetSizeMissed);
    }

    [Fact]
    public void AlphaIsFlattenedForJpeg()
    {
        var dir = TempDir();
        var src = MakeNoisy(dir, "alpha.png", MagickFormat.Png, 100, 100, alpha: true);
        var r = Converter.Convert(src, new ConversionOptions { Target = FormatRegistry.Jpeg }, new OutputOptions(), CancellationToken.None);
        Assert.Equal(ConversionStatus.Success, r.Status);
    }

    [Fact]
    public void CreatesMultiPagePdf()
    {
        var dir = TempDir();
        var a = MakeNoisy(dir, "p1.jpg", MagickFormat.Jpeg, 800, 1100);
        var b = MakeNoisy(dir, "p2.png", MagickFormat.Png, 1100, 800);
        var r = Converter.CreatePdf([a, b], Path.Combine(dir, "belge.pdf"), new ConversionOptions { Target = FormatRegistry.Pdf, Quality = 80 }, null, CancellationToken.None);
        Assert.Equal(ConversionStatus.Success, r.Status);
        var head = File.ReadAllBytes(r.Output!).Take(5).ToArray();
        Assert.Equal("%PDF-"u8.ToArray(), head);
    }

    [Fact]
    public async Task BatchContinuesAfterFailure()
    {
        var dir = TempDir();
        var good1 = MakeNoisy(dir, "g1.jpg", MagickFormat.Jpeg, 200, 100);
        var bad = Path.Combine(dir, "bad.jpg");
        File.WriteAllBytes(bad, [0xFF, 0xD8, 0xFF, 0xE0, 0, 1, 2]);
        var good2 = MakeNoisy(dir, "g2.jpg", MagickFormat.Jpeg, 200, 100);
        var outDir = Path.Combine(dir, "out");
        var runner = new BatchRunner(Converter);
        var report = await runner.RunAsync([good1, bad, good2], new ConversionOptions { Target = FormatRegistry.Png }, new OutputOptions { Folder = outDir }, null, new PauseController(), CancellationToken.None);
        Assert.Equal(2, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(2, Directory.GetFiles(outDir).Length);
    }

    [Fact]
    public void PresetsApply()
    {
        foreach (var p in Presets.All)
        {
            var o = new ConversionOptions();
            p.Apply(o);
            Assert.True(Converter.CanWrite(o.Target), p.Id);
        }
    }
}
