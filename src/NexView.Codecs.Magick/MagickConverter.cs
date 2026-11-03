using NexView.Core.Imaging;
using ImageMagick;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Localization;

namespace NexView.Codecs.Magick;

/// <summary>ImageMagick tabanlı dönüştürücü: yeniden boyutlandırma, metadata politikası, hedef boyut, güvenli yazma.</summary>
public sealed class MagickConverter : IImageConverter
{
    private const int A4Width = 1240, A4Height = 1754, A4Dpi = 150, PdfMaxEdge = 3508;

    public bool CanWrite(ImageFormat format) => format.Id == "PDF" || MagickHelpers.CanWrite(format);

    public ConversionResult Convert(string source, ConversionOptions o, OutputOptions output, CancellationToken ct)
    {
        var srcBytes = new FileInfo(source).Length;
        if (o.Target.Id == "PDF")
        {
            var pdfPath = OutputPaths.Build(source, FormatRegistry.Pdf, output);
            if (pdfPath == null) return new ConversionResult(source, null, ConversionStatus.Skipped, L.T("Aynı adlı dosya zaten var."), srcBytes);
            return CreatePdf([source], pdfPath, o, null, ct) with { Source = source, SourceBytes = srcBytes };
        }
        if (!CanWrite(o.Target))
            return new ConversionResult(source, null, ConversionStatus.Failed, L.T("{0} formatına yazma desteklenmiyor.", o.Target.DisplayName), srcBytes);

        var outPath = OutputPaths.Build(source, o.Target, output);
        if (outPath == null) return new ConversionResult(source, null, ConversionStatus.Skipped, L.T("Aynı adlı dosya zaten var."), srcBytes);

        var format = FormatDetector.Detect(source).Format;
        MagickImage image;
        try
        {
            image = MagickHelpers.ReadOriented(source, format);
        }
        catch (MagickException ex)
        {
            Core.Infrastructure.Log.Warn($"Dönüştürme için okunamadı: {source}", ex);
            return new ConversionResult(source, null, ConversionStatus.Failed,
                L.T("Dosya okunamadı; bozuk ya da desteklenmeyen bir varyasyon olabilir."), srcBytes);
        }
        using var _ = image;
        ct.ThrowIfCancellationRequested();
        Prepare(image, o);

        byte[] bytes;
        bool missed = false;
        if (o.TargetSizeBytes is long limit && limit > 0) (bytes, missed) = EncodeToSize(image, o, limit, ct);
        else bytes = Encode(image, o, o.Quality);

        Verify(bytes, o.Target);
        WriteSafely(bytes, outPath);
        return new ConversionResult(source, outPath, ConversionStatus.Success,
            missed ? L.T("Hedef boyuta tam inilemedi.") : null, srcBytes, bytes.Length, (int)image.Width, (int)image.Height, missed);
    }

    public ConversionResult CreatePdf(IReadOnlyList<string> sources, string outputPath, ConversionOptions o, IProgress<int>? progress, CancellationToken ct)
    {
        using var pages = new MagickImageCollection();
        long srcBytes = 0;
        int i = 0;
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            srcBytes += new FileInfo(src).Length;
            var img = MagickHelpers.ReadOriented(src, FormatDetector.Detect(src).Format);
            MagickHelpers.NormalizeColor(img);
            if (img.HasAlpha) { img.BackgroundColor = MagickColors.White; img.Alpha(AlphaOption.Remove); }
            img.Strip();
            img.Depth = 8;
            if (o.PdfPage == PdfPageMode.A4)
            {
                var page = PlaceOnA4(img);
                img.Dispose();
                pages.Add(page);
            }
            else
            {
                img.Resize(new MagickGeometry(PdfMaxEdge, PdfMaxEdge) { Greater = true });
                img.Density = new Density(300, 300, DensityUnit.PixelsPerInch);
                pages.Add(img);
            }
            progress?.Report(++i);
        }

        byte[] Build(int quality)
        {
            foreach (var p in pages) { p.Settings.Compression = CompressionMethod.JPEG; p.Quality = (uint)quality; }
            return pages.ToByteArray(MagickFormat.Pdf);
        }

        byte[] bytes = Build(o.Quality);
        bool missed = false;
        if (o.TargetSizeBytes is long limit && limit > 0 && bytes.Length > limit)
        {
            int lo = 25, hi = Math.Min(o.Quality, 95);
            byte[]? best = null;
            while (lo <= hi)
            {
                ct.ThrowIfCancellationRequested();
                int mid = (lo + hi) / 2;
                var b = Build(mid);
                if (b.Length <= limit) { best = b; lo = mid + 1; } else hi = mid - 1;
            }
            if (best != null) bytes = best;
            else
            {
                for (int r = 0; r < 4 && bytes.Length > limit; r++)
                {
                    foreach (var p in pages) p.Resize(new Percentage(80));
                    bytes = Build(40);
                }
                missed = bytes.Length > limit;
            }
        }

        var final = OutputPaths.Unique(outputPath);
        WriteSafely(bytes, final);
        return new ConversionResult(sources.Count == 1 ? sources[0] : "", final, ConversionStatus.Success,
            missed ? L.T("Hedef boyuta tam inilemedi.") : null, srcBytes, bytes.Length, 0, 0, missed);
    }

    public byte[] RenderPng(string source, int maxDimension, CancellationToken ct)
    {
        using var image = MagickHelpers.ReadOriented(source, FormatDetector.Detect(source).Format);
        ct.ThrowIfCancellationRequested();
        MagickHelpers.NormalizeColor(image);
        image.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension) { Greater = true });
        image.Depth = 8;
        return image.ToByteArray(MagickFormat.Png);
    }

    public byte[] RenderBmp(string source, int maxDimension, CancellationToken ct)
    {
        using var image = MagickHelpers.ReadOriented(source, FormatDetector.Detect(source).Format);
        ct.ThrowIfCancellationRequested();
        MagickHelpers.NormalizeColor(image);
        image.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension) { Greater = true });
        if (image.HasAlpha) { image.BackgroundColor = MagickColors.White; image.Alpha(AlphaOption.Remove); }
        image.Depth = 8;
        return image.ToByteArray(MagickFormat.Bmp3);
    }

    public ConversionResult SaveImage(DecodedImage image, string outputPath, ConversionOptions options, string? metadataSource, CancellationToken ct)
    {
        using var img = new MagickImage();
        img.ReadPixels(image.Stride == image.Width * 4 ? image.Pixels : Pack(image),
            new PixelReadSettings((uint)image.Width, (uint)image.Height, StorageType.Char, PixelMapping.BGRA));
        img.ColorSpace = ColorSpace.sRGB;
        bool opaque = true;
        for (int i = 3; i < image.Pixels.Length; i += 16) if (image.Pixels[i] < 255) { opaque = false; break; }
        if (opaque || !ConversionOptions.SupportsAlpha(options.Target))
        {
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaOption.Remove);
        }
        img.Depth = 8;
        if (metadataSource != null)
        {
            try
            {
                using var src = new MagickImage();
                src.Ping(metadataSource);
                if (src.GetExifProfile() is { } exif)
                {
                    exif.SetValue(ExifTag.Orientation, (ushort)1);
                    img.SetProfile(exif);
                }
            }
            catch (MagickException) { }
        }
        img.Orientation = OrientationType.TopLeft;
        ct.ThrowIfCancellationRequested();
        var bytes = Encode(img, options, options.Quality);
        var final = OutputPaths.Unique(outputPath);
        WriteSafely(bytes, final);
        return new ConversionResult(metadataSource ?? final, final, ConversionStatus.Success, null, 0, bytes.Length, image.Width, image.Height);
    }

    private static byte[] Pack(DecodedImage d)
    {
        var o = new byte[d.Width * d.Height * 4];
        for (int y = 0; y < d.Height; y++) Buffer.BlockCopy(d.Pixels, y * d.Stride, o, y * d.Width * 4, d.Width * 4);
        return o;
    }

    public void SaveAsPng(byte[] imageBytes, string outputPath)
    {
        using var image = new MagickImage(imageBytes);
        image.Depth = 8;
        WriteSafely(image.ToByteArray(MagickFormat.Png), outputPath);
    }

    // ---------- iç işlemler ----------

    private static MagickImage PlaceOnA4(MagickImage img)
    {
        bool landscape = img.Width > img.Height;
        uint pw = landscape ? (uint)A4Height : A4Width, ph = landscape ? (uint)A4Width : A4Height;
        const uint margin = 36;
        img.FilterType = FilterType.Lanczos;
        img.Resize(new MagickGeometry(pw - 2 * margin, ph - 2 * margin));
        var page = new MagickImage(MagickColors.White, pw, ph);
        page.Composite(img, Gravity.Center, CompositeOperator.Over);
        page.Density = new Density(A4Dpi, A4Dpi, DensityUnit.PixelsPerInch);
        return page;
    }

    private static void Prepare(MagickImage image, ConversionOptions o)
    {
        Resize(image, o);

        switch (o.Metadata)
        {
            case MetadataPolicy.RemoveAll:
                MagickHelpers.NormalizeColor(image);
                image.Strip();
                break;
            case MetadataPolicy.RemoveGps:
                RemoveGps(image);
                break;
        }

        if (!ConversionOptions.SupportsAlpha(o.Target) && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }

        if (o.BitDepth is int depth) image.Depth = (uint)depth;
        else if (o.Target.Id is "JPEG" or "WEBP" or "GIF" or "BMP" or "ICO") image.Depth = 8;

        if (o.Target.Id == "ICO")
        {
            image.Resize(new MagickGeometry(256, 256) { Greater = true });
            var side = Math.Max(image.Width, image.Height);
            image.BackgroundColor = MagickColors.None;
            image.Extent(side, side, Gravity.Center);
        }

        // Döndürme piksellere uygulandı; EXIF yönlendirmesi sıfırlanır
        image.Orientation = OrientationType.TopLeft;
    }

    private static void Resize(MagickImage image, ConversionOptions o)
    {
        if (o.ResizeMode == ResizeMode.None || o.ResizeValue <= 0) return;
        double w = image.Width, h = image.Height;
        double scale = o.ResizeMode switch
        {
            ResizeMode.LongEdge => o.ResizeValue / Math.Max(w, h),
            ResizeMode.ShortEdge => o.ResizeValue / Math.Min(w, h),
            ResizeMode.Width => o.ResizeValue / w,
            ResizeMode.Height => o.ResizeValue / h,
            ResizeMode.Percent => o.ResizeValue / 100.0,
            _ => 1,
        };
        if (scale <= 0 || Math.Abs(scale - 1) < 1e-6 || (o.ShrinkOnly && scale > 1)) return;
        var nw = (uint)Math.Max(1, Math.Round(w * scale));
        var nh = (uint)Math.Max(1, Math.Round(h * scale));
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry(nw, nh) { IgnoreAspectRatio = true });
    }

    private static readonly ExifTag[] GpsTags =
    [
        ExifTag.GPSVersionID, ExifTag.GPSLatitudeRef, ExifTag.GPSLatitude, ExifTag.GPSLongitudeRef, ExifTag.GPSLongitude,
        ExifTag.GPSAltitudeRef, ExifTag.GPSAltitude, ExifTag.GPSTimestamp, ExifTag.GPSDateStamp, ExifTag.GPSSatellites,
        ExifTag.GPSStatus, ExifTag.GPSMeasureMode, ExifTag.GPSDOP, ExifTag.GPSSpeedRef, ExifTag.GPSSpeed, ExifTag.GPSTrackRef,
        ExifTag.GPSTrack, ExifTag.GPSImgDirectionRef, ExifTag.GPSImgDirection, ExifTag.GPSMapDatum, ExifTag.GPSDestLatitudeRef,
        ExifTag.GPSDestLatitude, ExifTag.GPSDestLongitudeRef, ExifTag.GPSDestLongitude, ExifTag.GPSDestBearingRef,
        ExifTag.GPSDestBearing, ExifTag.GPSDestDistanceRef, ExifTag.GPSDestDistance, ExifTag.GPSProcessingMethod,
        ExifTag.GPSAreaInformation, ExifTag.GPSDifferential, ExifTag.GPSIFDOffset,
    ];

    private static void RemoveGps(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif != null)
        {
            foreach (var tag in GpsTags) exif.RemoveValue(tag);
            image.SetProfile(exif);
        }
        // XMP içinde de konum bulunabilir
        if (image.GetXmpProfile() != null) image.RemoveProfile("xmp");
    }

    private static MagickFormat FormatOf(ImageFormat f) => MagickHelpers.FormatMap[f.Id];

    internal static byte[] Encode(MagickImage image, ConversionOptions o, int quality)
    {
        var fmt = FormatOf(o.Target);
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        switch (o.Target.Id)
        {
            case "JPEG":
                image.Settings.Interlace = o.Progressive ? Interlace.Jpeg : Interlace.NoInterlace;
                image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", o.HighChroma ? "1x1" : "2x2");
                break;
            case "PNG":
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", Math.Clamp(o.PngCompression, 0, 9).ToString());
                break;
            case "WEBP":
                image.Settings.SetDefine(MagickFormat.WebP, "lossless", o.Lossless ? "true" : "false");
                image.Settings.SetDefine(MagickFormat.WebP, "method", "5");
                break;
            case "AVIF":
            case "HEIC":
                image.Settings.SetDefine(MagickFormat.Heic, "speed", "6");
                if (o.Lossless) image.Quality = 100;
                break;
            case "JXL":
            case "JP2":
                if (o.Lossless) image.Quality = 100;
                break;
            case "TIFF":
                image.Settings.Compression = o.TiffCompression switch
                {
                    "ZIP" => CompressionMethod.Zip,
                    "JPEG" => CompressionMethod.JPEG,
                    "None" => CompressionMethod.NoCompression,
                    _ => CompressionMethod.LZW,
                };
                break;
            case "ICO":
                image.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,16");
                break;
        }
        return image.ToByteArray(fmt);
    }

    private static (byte[] Bytes, bool Missed) EncodeToSize(MagickImage image, ConversionOptions o, long limit, CancellationToken ct)
    {
        bool lossy = ConversionOptions.IsLossy(o.Target) && !o.Lossless;
        for (int round = 0; round < 8; round++)
        {
            ct.ThrowIfCancellationRequested();
            int hi = Math.Clamp(o.Quality, 1, 95);
            var top = Encode(image, o, hi);
            if (top.Length <= limit) return (top, false);
            if (lossy)
            {
                int lo = 30;
                byte[]? best = null;
                while (lo <= hi)
                {
                    ct.ThrowIfCancellationRequested();
                    int mid = (lo + hi) / 2;
                    var b = Encode(image, o, mid);
                    if (b.Length <= limit) { best = b; lo = mid + 1; } else hi = mid - 1;
                }
                if (best != null) return (best, false);
            }
            image.FilterType = FilterType.Lanczos;
            image.Resize(new Percentage(85));
        }
        return (Encode(image, o, lossy ? 30 : o.Quality), true);
    }

    private static void Verify(byte[] bytes, ImageFormat target)
    {
        if (target.Id == "PDF") return;
        var info = new MagickImageInfo(bytes, new MagickReadSettings { Format = FormatOf(target) });
        if (info.Width == 0 || info.Height == 0) throw new InvalidDataException(L.T("Oluşan dosya doğrulanamadı."));
    }

    internal static void WriteSafely(byte[] bytes, string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".nexview-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, outputPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
