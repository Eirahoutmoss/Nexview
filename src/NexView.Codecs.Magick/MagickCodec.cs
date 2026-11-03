using ImageMagick;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Localization;

namespace NexView.Codecs.Magick;

/// <summary>
/// ImageMagick (Magick.NET) tabanlı codec. libheif/libde265 (HEIC), libavif, libraw (RAW), libjxl, FITS vb. içerir.
/// Alternatif modda format ipucu verilmez; RAW için gömülü önizleme kullanılır.
/// </summary>
public sealed class MagickCodec(bool alternative = false) : IImageCodec, IAnimationDecoder
{
    public static string LibraryVersion => MagickNET.Version;

    public string Name => alternative ? L.T("ImageMagick (alternatif mod)") : "ImageMagick";
    public int Priority => alternative ? 100 : 10;

    public bool CanRead(ImageFormat format) => MagickHelpers.CanRead(format);
    public bool CanWrite(ImageFormat format) => MagickHelpers.CanWrite(format);

    public DecodeResult Decode(string path, ImageFormat? format, DecodeOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (alternative && format?.IsRaw == true)
        {
            return TryDecodePreview(path, format, options.MaxDimension, ct)
                   ?? throw new InvalidDataException(L.T("RAW dosyasında kullanılabilir gömülü önizleme bulunamadı."));
        }

        var settings = MagickHelpers.CreateSettings(format, alternative);
        int frames = MagickHelpers.CountFrames(path, format);
        using var image = MagickHelpers.ReadPrimaryFrame(path, format, settings);
        ct.ThrowIfCancellationRequested();
        image.AutoOrient();
        var props = MagickHelpers.Describe(image, frames);
        var decoded = MagickHelpers.ToDecoded(image, options.MaxDimension, options.AutoStretch, ct);
        return new DecodeResult(decoded, props, Name);
    }

    public DecodeResult? TryDecodePreview(string path, ImageFormat? format, int maxDimension, CancellationToken ct)
    {
        if (format?.IsRaw != true) return null;
        var jpeg = EmbeddedJpegExtractor.ExtractLargest(path);
        if (jpeg == null) return null;
        ct.ThrowIfCancellationRequested();
        using var image = new MagickImage(jpeg, new MagickReadSettings { Format = MagickFormat.Jpeg });
        MagickHelpers.ApplyRawOrientation(image, path);
        var props = MagickHelpers.Describe(image, 1);
        var decoded = MagickHelpers.ToDecoded(image, maxDimension, false, ct);
        return new DecodeResult(decoded, props, L.T("Gömülü önizleme (JPEG)")) { IsPreview = true };
    }

    public IReadOnlyList<AnimationFrame>? DecodeFrames(string path, ImageFormat? format, int maxDimension, long maxBytes, CancellationToken ct)
    {
        if (alternative || format == null || !format.MayAnimate) return null;
        using var col = new MagickImageCollection(path, MagickHelpers.CreateSettings(format, false));
        if (col.Count < 2) return null;
        col.Coalesce();
        var list = new List<AnimationFrame>(col.Count);
        long bytes = 0;
        foreach (var f in col)
        {
            ct.ThrowIfCancellationRequested();
            var frame = MagickHelpers.ToDecoded((MagickImage)f, maxDimension, false, ct);
            bytes += frame.Pixels.Length;
            if (bytes > maxBytes) return null;
            int delay = (int)f.AnimationDelay * 1000 / Math.Max(1, (int)f.AnimationTicksPerSecond);
            list.Add(new AnimationFrame(frame, delay < 20 ? 100 : delay));
        }
        return list;
    }

    public byte[] CreateThumbnail(string path, ImageFormat? format, int maxSize, CancellationToken ct)
    {
        MagickImage image;
        bool raw = false;
        if (format?.IsRaw == true && EmbeddedJpegExtractor.ExtractLargest(path) is { } jpeg)
        {
            image = new MagickImage(jpeg, new MagickReadSettings { Format = MagickFormat.Jpeg });
            raw = true;
        }
        else
        {
            var settings = MagickHelpers.CreateSettings(format, alternative);
            if (format?.Id == "JPEG") settings.SetDefine(MagickFormat.Jpeg, "size", $"{maxSize * 2}x{maxSize * 2}");
            image = MagickHelpers.ReadPrimaryFrame(path, format, settings);
        }

        using (image)
        {
            ct.ThrowIfCancellationRequested();
            if (raw) MagickHelpers.ApplyRawOrientation(image, path);
            else image.AutoOrient();
            MagickHelpers.NormalizeColor(image);
            if (format?.Id == "FITS") image.AutoLevel();
            image.Thumbnail(new MagickGeometry((uint)maxSize, (uint)maxSize) { Greater = true });
            image.Strip();
            bool alpha = image.HasAlpha;
            image.Quality = 82;
            return image.ToByteArray(alpha ? MagickFormat.Png : MagickFormat.Jpeg);
        }
    }
}
