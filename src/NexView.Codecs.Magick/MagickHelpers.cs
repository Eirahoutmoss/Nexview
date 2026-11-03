using ImageMagick;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Metadata;

namespace NexView.Codecs.Magick;

internal static class MagickHelpers
{
    public static readonly Dictionary<string, MagickFormat> FormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPEG"] = MagickFormat.Jpeg, ["PNG"] = MagickFormat.Png, ["GIF"] = MagickFormat.Gif, ["BMP"] = MagickFormat.Bmp,
        ["TIFF"] = MagickFormat.Tiff, ["WEBP"] = MagickFormat.WebP, ["AVIF"] = MagickFormat.Avif, ["HEIC"] = MagickFormat.Heic,
        ["HEIF"] = MagickFormat.Heif, ["ICO"] = MagickFormat.Ico, ["TGA"] = MagickFormat.Tga, ["DDS"] = MagickFormat.Dds,
        ["PSD"] = MagickFormat.Psd, ["PSB"] = MagickFormat.Psb, ["JXL"] = MagickFormat.Jxl, ["JP2"] = MagickFormat.Jp2,
        ["EXR"] = MagickFormat.Exr, ["FITS"] = MagickFormat.Fits, ["PDF"] = MagickFormat.Pdf,
        ["DNG"] = MagickFormat.Dng, ["CR2"] = MagickFormat.Cr2, ["CR3"] = MagickFormat.Cr3, ["NEF"] = MagickFormat.Nef,
        ["ARW"] = MagickFormat.Arw, ["RAF"] = MagickFormat.Raf, ["ORF"] = MagickFormat.Orf, ["RW2"] = MagickFormat.Rw2,
        ["SRW"] = MagickFormat.Srw, ["PEF"] = MagickFormat.Pef,
    };

    private static readonly Lazy<Dictionary<MagickFormat, IMagickFormatInfo>> FormatInfos = new(() =>
    {
        var d = new Dictionary<MagickFormat, IMagickFormatInfo>();
        foreach (var f in MagickNET.SupportedFormats) d.TryAdd(f.Format, f);
        return d;
    });

    public static bool CanRead(ImageFormat f) => f.Id != "PDF" && TryInfo(f, out var i) && i.SupportsReading;
    public static bool CanWrite(ImageFormat f) => TryInfo(f, out var i) && i.SupportsWriting && !f.IsRaw && f.Id is not ("PSD" or "PSB" or "DDS" or "EXR" or "FITS" or "HEIF");

    private static bool TryInfo(ImageFormat format, out IMagickFormatInfo info)
    {
        info = null!;
        return FormatMap.TryGetValue(format.Id, out var mf) && FormatInfos.Value.TryGetValue(mf, out info!);
    }

    public static MagickReadSettings CreateSettings(ImageFormat? format, bool alternative)
    {
        var s = new MagickReadSettings();
        if (!alternative && format != null && FormatMap.TryGetValue(format.Id, out var mf)) s.Format = mf;
        if (format?.IsRaw == true) s.SetDefine(MagickFormat.Dng, "use-camera-wb", "true");
        return s;
    }

    public static MagickImage ReadPrimaryFrame(string path, ImageFormat? format, MagickReadSettings settings)
    {
        if (format?.Id == "ICO")
        {
            using var col = new MagickImageCollection(path, settings);
            if (col.Count == 0) throw new InvalidDataException("Simge dosyasında görüntü yok.");
            var best = col[0];
            foreach (var f in col)
                if ((long)f.Width * f.Height > (long)best.Width * best.Height) best = f;
            return (MagickImage)best.Clone();
        }
        settings.FrameIndex = 0;
        settings.FrameCount = 1;
        return new MagickImage(path, settings);
    }

    /// <summary>Tam çözünürlükte, yönlendirilmiş okuma. RAW çözümlenemezse gömülü önizlemeye düşer.</summary>
    public static MagickImage ReadOriented(string path, ImageFormat? format)
    {
        try
        {
            var img = ReadPrimaryFrame(path, format, CreateSettings(format, false));
            img.AutoOrient();
            return img;
        }
        catch when (format?.IsRaw == true)
        {
            var jpeg = EmbeddedJpegExtractor.ExtractLargest(path) ?? throw new InvalidDataException("RAW dosyası çözümlenemedi.");
            var img = new MagickImage(jpeg, new MagickReadSettings { Format = MagickFormat.Jpeg });
            ApplyRawOrientation(img, path);
            return img;
        }
    }

    public static int CountFrames(string path, ImageFormat? format)
    {
        if (format == null || !(format.MayAnimate || format.Id is "TIFF" or "ICO")) return 1;
        try { return Math.Max(1, MagickImageInfo.ReadCollection(path).Count()); }
        catch { return 1; }
    }

    public static void ApplyRawOrientation(MagickImage image, string path)
    {
        if (image.Orientation is OrientationType.Undefined or OrientationType.TopLeft)
        {
            var o = MetadataService.ReadOrientation(path);
            if (o is >= 2 and <= 8) image.Orientation = (OrientationType)o.Value;
        }
        image.AutoOrient();
    }

    public static ImageProperties Describe(MagickImage image, int frameCount)
    {
        string? icc = null;
        try { icc = image.GetColorProfile()?.Description; } catch { }
        return new ImageProperties
        {
            CodecFormat = image.Format.ToString().ToUpperInvariant(),
            Width = (int)image.Width,
            Height = (int)image.Height,
            BitDepth = (int)image.Depth,
            Channels = (int)image.ChannelCount,
            ColorSpace = image.ColorSpace.ToString(),
            HasAlpha = image.HasAlpha,
            IccProfile = icc,
            Compression = image.Compression == CompressionMethod.Undefined ? null : image.Compression.ToString(),
            FrameCount = frameCount,
            DpiX = image.Density.X,
            DpiY = image.Density.Y,
            DensityUnit = image.Density.Units.ToString(),
        };
    }

    public static void NormalizeColor(MagickImage image)
    {
        try
        {
            var profile = image.GetColorProfile();
            if (profile != null && image.ColorSpace != ColorSpace.Gray &&
                !(profile.Description ?? "").Contains("sRGB", StringComparison.OrdinalIgnoreCase))
            {
                image.TransformColorSpace(ColorProfiles.SRGB);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Renk profili dönüştürülemedi: {ex.Message}");
        }
        if (image.ColorSpace != ColorSpace.sRGB) image.ColorSpace = ColorSpace.sRGB;
    }

    public static DecodedImage ToDecoded(MagickImage image, int maxDimension, bool autoStretch, CancellationToken ct)
    {
        int ow = (int)image.Width, oh = (int)image.Height;
        NormalizeColor(image);
        if (Math.Max(ow, oh) > maxDimension)
            image.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension));
        ct.ThrowIfCancellationRequested();
        if (autoStretch) return Stretch(image, ow, oh);
        if (!image.HasAlpha) image.Alpha(AlphaOption.Opaque);
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.ToByteArray("BGRA") ?? throw new InvalidDataException("Piksel verisi alınamadı.");
        int w = (int)image.Width, h = (int)image.Height;
        return new DecodedImage(w, h, w * 4, data, ow, oh);
    }

    /// <summary>
    /// Astro "Screen Transfer Function" benzeri otomatik germe: medyan ve MAD'e göre gölge kırpma
    /// ve orta ton aktarımı. Yalnızca görüntüleme içindir; dosyayı değiştirmez.
    /// </summary>
    private static DecodedImage Stretch(MagickImage image, int ow, int oh)
    {
        int w = (int)image.Width, h = (int)image.Height;
        int ch = (int)image.ChannelCount;
        bool alpha = image.HasAlpha;
        int colorCh = Math.Max(1, Math.Min(3, alpha ? ch - 1 : ch));
        using var pixels = image.GetPixelsUnsafe();
        var src = pixels.ToArray() ?? throw new InvalidDataException("Piksel verisi alınamadı.");
        double max = Quantum.Max;

        // İstatistik: ~250k örnek, kanalların ortalaması (bağlı germe)
        int count = w * h;
        int step = Math.Max(1, count / 250_000);
        var samples = new List<double>(count / step + 1);
        for (int i = 0; i < count; i += step)
        {
            double v = 0;
            for (int c = 0; c < colorCh; c++) v += src[i * ch + c];
            samples.Add(v / colorCh / max);
        }
        samples.Sort();
        double median = samples[samples.Count / 2];
        for (int i = 0; i < samples.Count; i++) samples[i] = Math.Abs(samples[i] - median);
        samples.Sort();
        double mad = samples[samples.Count / 2] * 1.4826;
        double shadows = Math.Clamp(median - 2.8 * mad, 0, 1);
        double mid = Mtf(0.25, Math.Clamp(median - shadows, 1e-6, 1));

        var lut = new byte[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            double x = i / 65535.0;
            x = shadows >= 1 ? 0 : Math.Clamp((x - shadows) / (1 - shadows), 0, 1);
            lut[i] = (byte)Math.Round(Mtf(mid, x) * 255);
        }

        var dst = new byte[w * h * 4];
        double scale = 65535.0 / max;
        for (int i = 0; i < count; i++)
        {
            int s = i * ch, d = i * 4;
            byte r = lut[(int)(src[s] * scale)];
            byte g = colorCh >= 3 ? lut[(int)(src[s + 1] * scale)] : r;
            byte b = colorCh >= 3 ? lut[(int)(src[s + 2] * scale)] : r;
            dst[d] = b; dst[d + 1] = g; dst[d + 2] = r;
            dst[d + 3] = alpha ? (byte)(src[s + ch - 1] / max * 255) : (byte)255;
        }
        return new DecodedImage(w, h, w * 4, dst, ow, oh);
    }

    private static double Mtf(double m, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (Math.Abs(x - m) < 1e-12) return 0.5;
        return (m - 1) * x / ((2 * m - 1) * x - m);
    }
}
