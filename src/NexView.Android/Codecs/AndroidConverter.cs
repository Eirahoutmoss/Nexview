using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;
using AndroidX.ExifInterface.Media;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using ImageFormat = NexView.Core.Formats.ImageFormat;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using IOPath = System.IO.Path;

namespace NexView.Droid.Codecs;

/// <summary>Android kodlayıcısı: JPEG, PNG, WebP (Bitmap.Compress) ve PDF (PdfDocument). EXIF kopyalama AndroidX ExifInterface ile.</summary>
public sealed class AndroidConverter : IImageConverter
{
    private static readonly string[] GpsTags =
    [
        ExifInterface.TagGpsLatitude, ExifInterface.TagGpsLatitudeRef, ExifInterface.TagGpsLongitude, ExifInterface.TagGpsLongitudeRef,
        ExifInterface.TagGpsAltitude, ExifInterface.TagGpsAltitudeRef, ExifInterface.TagGpsTimestamp, ExifInterface.TagGpsDatestamp,
        ExifInterface.TagGpsImgDirection, ExifInterface.TagGpsImgDirectionRef, ExifInterface.TagGpsProcessingMethod,
        ExifInterface.TagGpsSpeed, ExifInterface.TagGpsSpeedRef, ExifInterface.TagGpsDestBearing, ExifInterface.TagGpsDestBearingRef,
    ];

    private static readonly string[] CameraTags =
    [
        ExifInterface.TagMake, ExifInterface.TagModel, ExifInterface.TagDatetime, ExifInterface.TagDatetimeOriginal, ExifInterface.TagDatetimeDigitized,
        ExifInterface.TagOffsetTime, ExifInterface.TagOffsetTimeOriginal, ExifInterface.TagOffsetTimeDigitized,
        ExifInterface.TagExposureTime, ExifInterface.TagFNumber, ExifInterface.TagPhotographicSensitivity, ExifInterface.TagFocalLength,
        ExifInterface.TagFocalLengthIn35mmFilm, ExifInterface.TagLensMake, ExifInterface.TagLensModel, ExifInterface.TagFlash,
        ExifInterface.TagWhiteBalance, ExifInterface.TagExposureBiasValue, ExifInterface.TagExposureProgram, ExifInterface.TagMeteringMode,
        ExifInterface.TagApertureValue, ExifInterface.TagShutterSpeedValue, ExifInterface.TagSoftware, ExifInterface.TagArtist,
        ExifInterface.TagCopyright, ExifInterface.TagImageDescription, ExifInterface.TagUserComment, ExifInterface.TagSubsecTimeOriginal,
    ];

    public bool CanWrite(ImageFormat format) => format.Id is "JPEG" or "PNG" or "WEBP" or "PDF";

    public ConversionResult Convert(string source, ConversionOptions options, OutputOptions output, CancellationToken ct)
    {
        long srcBytes = 0;
        try
        {
            srcBytes = new FileInfo(source).Length;
            var target = options.Target;
            if (target.Id == "PDF")
            {
                var pdfPath = OutputPaths.Build(source, target, output);
                if (pdfPath == null) return new ConversionResult(source, null, ConversionStatus.Skipped, L.T("Hedef dosya zaten var, atlandı."), srcBytes);
                return CreatePdf([source], pdfPath, options, null, ct);
            }
            if (!CanWrite(target))
                return new ConversionResult(source, null, ConversionStatus.Failed, L.T("Bu format Android'de yazılamıyor: {0}", target.DisplayName), srcBytes);

            var outPath = OutputPaths.Build(source, target, output);
            if (outPath == null) return new ConversionResult(source, null, ConversionStatus.Skipped, L.T("Hedef dosya zaten var, atlandı."), srcBytes);

            var fmt = FormatDetector.Detect(source).Format;
            var d = AndroidImaging.Decode(source, fmt, (w, h) => TargetSize(w, h, options));
            ct.ThrowIfCancellationRequested();
            var bmp = d.Bitmap;
            if (target.Id == "JPEG" && bmp.HasAlpha) bmp = Flatten(bmp);

            var (cf, quality) = CompressFormat(target, options);
            var bytes = AndroidImaging.Compress(bmp, cf, quality);
            var missed = false;
            if (options.TargetSizeBytes is { } limit && bytes.Length > limit)
                (bytes, bmp, missed) = FitToSize(bmp, cf, target, quality, limit, ct);

            var tmp = outPath + ".part";
            File.WriteAllBytes(tmp, bytes);
            if (options.Metadata != MetadataPolicy.RemoveAll && target.Id is "JPEG" or "PNG" or "WEBP")
                CopyExif(source, tmp, options.Metadata == MetadataPolicy.RemoveGps, bmp.Width, bmp.Height);
            File.Move(tmp, outPath, true);
            var result = new ConversionResult(source, outPath, ConversionStatus.Success,
                missed ? L.T("Hedef dosya boyutuna ulaşılamadı; en küçük sonuç kaydedildi.") : null,
                srcBytes, new FileInfo(outPath).Length, bmp.Width, bmp.Height, missed);
            bmp.Recycle();
            return result;
        }
        catch (System.OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Android dönüştürme başarısız: {source}", ex);
            var msg = ex is DecodeException de ? de.UserReason : ex.Message;
            return new ConversionResult(source, null, ConversionStatus.Failed, msg, srcBytes);
        }
    }

    private static (int W, int H)? TargetSize(int w, int h, ConversionOptions o)
    {
        double scale = 1;
        if (o.ResizeMode != ResizeMode.None && o.ResizeValue > 0)
        {
            scale = o.ResizeMode switch
            {
                ResizeMode.LongEdge => o.ResizeValue / (double)Math.Max(w, h),
                ResizeMode.ShortEdge => o.ResizeValue / (double)Math.Min(w, h),
                ResizeMode.Width => o.ResizeValue / (double)w,
                ResizeMode.Height => o.ResizeValue / (double)h,
                ResizeMode.Percent => o.ResizeValue / 100.0,
                _ => 1,
            };
            if (scale <= 0 || (o.ShrinkOnly && scale > 1)) scale = 1;
        }
        var pixels = w * (double)h * scale * scale;
        if (pixels > AndroidImaging.MaxPixels) scale *= Math.Sqrt(AndroidImaging.MaxPixels / pixels);
        if (Math.Abs(scale - 1) < 1e-6) return null;
        return ((int)Math.Round(w * scale), (int)Math.Round(h * scale));
    }

    private static (Bitmap.CompressFormat, int) CompressFormat(ImageFormat target, ConversionOptions o) => target.Id switch
    {
        "PNG" => (Bitmap.CompressFormat.Png!, 100),
        "WEBP" when Build.VERSION.SdkInt >= BuildVersionCodes.R => o.Lossless
            ? (Bitmap.CompressFormat.WebpLossless!, 100)
            : (Bitmap.CompressFormat.WebpLossy!, Math.Clamp(o.Quality, 1, 100)),
#pragma warning disable CA1422
        "WEBP" => (Bitmap.CompressFormat.Webp!, o.Lossless ? 100 : Math.Clamp(o.Quality, 1, 100)),
#pragma warning restore CA1422
        _ => (Bitmap.CompressFormat.Jpeg!, Math.Clamp(o.Quality, 1, 100)),
    };

    internal static Bitmap Flatten(Bitmap src)
    {
        var dst = Bitmap.CreateBitmap(src.Width, src.Height, Bitmap.Config.Argb8888!)!;
        using (var c = new Canvas(dst))
        {
            c.DrawColor(Color.White);
            c.DrawBitmap(src, 0, 0, null);
        }
        src.Recycle();
        return dst;
    }

    /// <summary>Önce kaliteyi düşürür (ikili arama), yetmezse boyutu %85 adımlarla küçültür.</summary>
    private static (byte[] Bytes, Bitmap Bmp, bool Missed) FitToSize(Bitmap bmp, Bitmap.CompressFormat cf, ImageFormat target, int quality, long limit, CancellationToken ct)
    {
        var lossy = target.Id is "JPEG" or "WEBP";
        byte[]? best = null;
        for (var round = 0; round < 12; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (lossy)
            {
                int lo = 35, hi = quality;
                byte[]? fit = null;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var b = AndroidImaging.Compress(bmp, cf, mid);
                    if (b.Length <= limit) { fit = b; lo = mid + 1; }
                    else { hi = mid - 1; best = best == null || b.Length < best.Length ? b : best; }
                }
                if (fit != null) return (fit, bmp, false);
            }
            else
            {
                var b = AndroidImaging.Compress(bmp, cf, 100);
                if (b.Length <= limit) return (b, bmp, false);
                best = b;
            }
            var w = (int)(bmp.Width * 0.85); var h = (int)(bmp.Height * 0.85);
            if (w < 64 || h < 64) break;
            var smaller = Bitmap.CreateScaledBitmap(bmp, w, h, true)!;
            bmp.Recycle();
            bmp = smaller;
        }
        return (best ?? AndroidImaging.Compress(bmp, cf, 35), bmp, true);
    }

    internal static void CopyExif(string source, string dest, bool removeGps, int width, int height)
    {
        try
        {
            var src = new ExifInterface(source);
            var dst = new ExifInterface(dest);
            foreach (var tag in CameraTags)
                if (src.GetAttribute(tag) is { } v) dst.SetAttribute(tag, v);
            if (!removeGps)
                foreach (var tag in GpsTags)
                    if (src.GetAttribute(tag) is { } v) dst.SetAttribute(tag, v);
            // Pikseller zaten döndürüldü.
            dst.SetAttribute(ExifInterface.TagOrientation, "1");
            dst.SetAttribute(ExifInterface.TagPixelXDimension, width.ToString());
            dst.SetAttribute(ExifInterface.TagPixelYDimension, height.ToString());
            dst.SaveAttributes();
        }
        catch (Exception ex) { Log.Warn($"EXIF kopyalanamadı: {source}", ex); }
    }

    public ConversionResult CreatePdf(IReadOnlyList<string> sources, string outputPath, ConversionOptions options, IProgress<int>? progress, CancellationToken ct)
    {
        const int A4W = 595, A4H = 842, Margin = 24;
        long srcBytes = sources.Sum(s => File.Exists(s) ? new FileInfo(s).Length : 0);
        using var doc = new PdfDocument();
        var pageNo = 0;
        var failed = new List<string>();
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fmt = FormatDetector.Detect(src).Format;
                var d = AndroidImaging.Decode(src, fmt, (w, h) => AndroidImaging.Fit(w, h, 2400));
                using var bmp = d.Bitmap;
                int pw, ph;
                if (options.PdfPage == PdfPageMode.A4)
                {
                    var landscape = bmp.Width > bmp.Height;
                    pw = landscape ? A4H : A4W; ph = landscape ? A4W : A4H;
                }
                else
                {
                    // Görüntü boyutu: 150 dpi kabulüyle punto.
                    var s = 72.0 / 150.0;
                    pw = Math.Max(1, (int)(d.OriginalWidth * s)); ph = Math.Max(1, (int)(d.OriginalHeight * s));
                }
                var page = doc.StartPage(new PdfDocument.PageInfo.Builder(pw, ph, ++pageNo).Create())!;
                var margin = options.PdfPage == PdfPageMode.A4 ? Margin : 0;
                var scale = Math.Min((pw - 2.0 * margin) / bmp.Width, (ph - 2.0 * margin) / bmp.Height);
                var dw = (float)(bmp.Width * scale); var dh = (float)(bmp.Height * scale);
                var left = (pw - dw) / 2; var top = (ph - dh) / 2;
                page.Canvas!.DrawColor(Color.White);
                using var paint = new Paint(PaintFlags.FilterBitmap | PaintFlags.AntiAlias);
                page.Canvas.DrawBitmap(bmp, null, new RectF(left, top, left + dw, top + dh), paint);
                doc.FinishPage(page);
                bmp.Recycle();
            }
            catch (System.OperationCanceledException) { throw; }
            catch (Exception ex) { failed.Add($"{IOPath.GetFileName(src)}: {ex.Message}"); Log.Warn($"PDF sayfası eklenemedi: {src}", ex); }
            progress?.Report(pageNo + failed.Count);
        }
        if (pageNo == 0)
            return new ConversionResult(sources.FirstOrDefault() ?? "", null, ConversionStatus.Failed, failed.FirstOrDefault() ?? L.T("Görüntü yok."), srcBytes);
        Directory.CreateDirectory(IOPath.GetDirectoryName(outputPath)!);
        using (var fs = File.Create(outputPath)) doc.WriteTo(fs);
        return new ConversionResult(sources[0], outputPath, ConversionStatus.Success,
            failed.Count > 0 ? L.T("{0} görüntü eklenemedi.", failed.Count) : null, srcBytes, new FileInfo(outputPath).Length);
    }

    public byte[] RenderPng(string source, int maxDimension, CancellationToken ct)
    {
        var d = AndroidImaging.Decode(source, FormatDetector.Detect(source).Format, (w, h) => AndroidImaging.Fit(w, h, maxDimension));
        using var bmp = d.Bitmap;
        var bytes = AndroidImaging.Compress(bmp, Bitmap.CompressFormat.Png!, 100);
        bmp.Recycle();
        return bytes;
    }

    public byte[] RenderBmp(string source, int maxDimension, CancellationToken ct) =>
        throw new NotSupportedException("BMP panosu Android'de kullanılmaz.");

    public ConversionResult SaveImage(DecodedImage image, string outputPath, ConversionOptions options, string? metadataSource, CancellationToken ct)
    {
        var bmp = AndroidImaging.FromDecoded(image);
        if (options.Target.Id is "JPEG" or "PDF" && bmp.HasAlpha) bmp = Flatten(bmp);
        var target = CanWrite(options.Target) && options.Target.Id != "PDF" ? options.Target : FormatRegistry.Jpeg;
        var (cf, q) = CompressFormat(target, options);
        var bytes = AndroidImaging.Compress(bmp, cf, q);
        var final = OutputPaths.Unique(IOPath.ChangeExtension(outputPath, OutputPaths.Extension(target)));
        var tmp = final + ".part";
        File.WriteAllBytes(tmp, bytes);
        if (metadataSource != null) CopyExif(metadataSource, tmp, false, bmp.Width, bmp.Height);
        File.Move(tmp, final, true);
        bmp.Recycle();
        return new ConversionResult(metadataSource ?? final, final, ConversionStatus.Success, null, 0, new FileInfo(final).Length, image.Width, image.Height);
    }

    public void SaveAsPng(byte[] imageBytes, string outputPath)
    {
        using var bmp = BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length)
                        ?? throw new InvalidOperationException("Görüntü baytları çözülemedi.");
        using var fs = File.Create(outputPath);
        bmp.Compress(Bitmap.CompressFormat.Png!, 100, fs);
    }
}
