using ImageMagick;
using NexView.Codecs.Magick;
using NexView.Core.Formats;
using NexView.Core.Imaging;

namespace NexView.Core.Tests;

public class CodecTests
{
    private static string MakeImage(string ext, MagickFormat fmt, uint w = 64, uint h = 48)
    {
        var p = Path.Combine(Path.GetTempPath(), "nexview-test-" + Guid.NewGuid().ToString("N") + ext);
        using var img = new MagickImage(MagickColors.Red, w, h);
        img.Write(p, fmt);
        return p;
    }

    private static CodecManager Manager()
    {
        var m = new CodecManager();
        m.Register(new MagickCodec());
        m.Register(new MagickCodec(alternative: true));
        return m;
    }

    [Theory]
    [InlineData(".png", MagickFormat.Png)]
    [InlineData(".jpg", MagickFormat.Jpeg)]
    [InlineData(".webp", MagickFormat.WebP)]
    [InlineData(".tif", MagickFormat.Tiff)]
    [InlineData(".bmp", MagickFormat.Bmp)]
    [InlineData(".gif", MagickFormat.Gif)]
    public void DecodesBasicFormats(string ext, MagickFormat fmt)
    {
        var path = MakeImage(ext, fmt);
        var r = Manager().Decode(path, FormatDetector.Detect(path), new DecodeOptions(), CancellationToken.None);
        Assert.Equal(64, r.Image.Width);
        Assert.Equal(48, r.Image.Height);
        Assert.Equal(64 * 48 * 4, r.Image.Pixels.Length);
        // BGRA: kırmızı ≈ (0,0,255,255)
        Assert.True(r.Image.Pixels[2] > 200 && r.Image.Pixels[0] < 60 && r.Image.Pixels[3] == 255);
    }

    [Fact]
    public void DownsamplesLargeImages()
    {
        var path = MakeImage(".png", MagickFormat.Png, 1000, 500);
        var r = Manager().Decode(path, FormatDetector.Detect(path), new DecodeOptions(MaxDimension: 200), CancellationToken.None);
        Assert.Equal(200, r.Image.Width);
        Assert.Equal(1000, r.Image.OriginalWidth);
    }

    [Fact]
    public void ThumbnailIsCreated()
    {
        var path = MakeImage(".jpg", MagickFormat.Jpeg, 800, 600);
        var bytes = Manager().CreateThumbnail(path, FormatRegistry.Jpeg, 256, CancellationToken.None);
        Assert.NotNull(bytes);
        using var t = new MagickImage(bytes!);
        Assert.Equal(256u, t.Width);
    }

    [Fact]
    public void CorruptFileGivesReadableError()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexview-test-" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7]);
        var ex = Assert.Throws<DecodeException>(() => Manager().Decode(path, FormatDetector.Detect(path), new DecodeOptions(), CancellationToken.None));
        Assert.Equal("JPEG", ex.Format?.Id);
        Assert.NotEmpty(ex.Attempts);
    }

    [Fact]
    public void CoreFormatsAreReadable()
    {
        var m = Manager();
        foreach (var id in new[] { "JPEG", "PNG", "WEBP", "TIFF", "BMP", "GIF", "AVIF", "HEIC", "DNG", "CR2", "NEF", "ARW" })
            Assert.True(m.GetSupport(FormatRegistry.FromId(id)!) != FormatSupport.Unsupported, id);
    }

    [Fact]
    public void EmbeddedJpegIsExtracted()
    {
        using var img = new MagickImage(MagickColors.Blue, 400, 300);
        var jpeg = img.ToByteArray(MagickFormat.Jpeg);
        var fake = new byte[1000].Concat(jpeg).Concat(new byte[500]).ToArray();
        var found = EmbeddedJpegExtractor.ExtractLargest(fake, minBytes: 100);
        Assert.NotNull(found);
        Assert.Equal(jpeg.Length, found!.Length);
    }

    [Fact]
    public void FallbackIsUsedWhenPrimaryFails()
    {
        var m = new CodecManager();
        m.Register(new FailingCodec());
        m.Register(new MagickCodec(alternative: true));
        var path = MakeImage(".png", MagickFormat.Png);
        var r = m.Decode(path, FormatDetector.Detect(path), new DecodeOptions(), CancellationToken.None);
        Assert.True(r.UsedFallback);
    }

    private sealed class FailingCodec : IImageCodec
    {
        public string Name => "fail";
        public int Priority => 0;
        public bool CanRead(ImageFormat format) => true;
        public bool CanWrite(ImageFormat format) => false;
        public DecodeResult Decode(string path, ImageFormat? format, DecodeOptions options, CancellationToken ct) => throw new InvalidOperationException("boom");
        public DecodeResult? TryDecodePreview(string path, ImageFormat? format, int maxDimension, CancellationToken ct) => null;
        public byte[] CreateThumbnail(string path, ImageFormat? format, int maxSize, CancellationToken ct) => throw new InvalidOperationException();
    }
}

public class AstroAndAnimationTests
{
    private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), "nexview-test-" + Guid.NewGuid().ToString("N") + ext);

    [Fact]
    public void FitsIsDetectedAndStretched()
    {
        var p = Temp(".fits");
        using (var img = new MagickImage("gradient:black-gray10", 200, 100)) img.Write(p, MagickFormat.Fits);
        var det = FormatDetector.Detect(p);
        Assert.Equal("FITS", det.Format?.Id);
        var codec = new MagickCodec();
        var plain = codec.Decode(p, det.Format, new DecodeOptions(), CancellationToken.None);
        var stretched = codec.Decode(p, det.Format, new DecodeOptions(AutoStretch: true), CancellationToken.None);
        double Mean(byte[] px) { long s = 0; for (int i = 2; i < px.Length; i += 4) s += px[i]; return s / (px.Length / 4.0); }
        Assert.True(Mean(stretched.Image.Pixels) > Mean(plain.Image.Pixels) + 20);
    }

    [Fact]
    public void AnimatedGifFramesAreDecoded()
    {
        var p = Temp(".gif");
        using (var col = new MagickImageCollection())
        {
            foreach (var c in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
            {
                var f = new MagickImage(c, 40, 30) { AnimationDelay = 20 };
                col.Add(f);
            }
            col.Write(p, MagickFormat.Gif);
        }
        var frames = new MagickCodec().DecodeFrames(p, FormatRegistry.Gif, 512, 100_000_000, CancellationToken.None);
        Assert.NotNull(frames);
        Assert.Equal(3, frames!.Count);
        Assert.Equal(200, frames[0].DelayMs);
    }
}
