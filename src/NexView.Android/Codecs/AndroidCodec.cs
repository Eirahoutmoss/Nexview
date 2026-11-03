using Android.Graphics;
using Android.OS;
using NexView.Core.Formats;
using ImageFormat = NexView.Core.Formats.ImageFormat;
using NexView.Core.Imaging;

namespace NexView.Droid.Codecs;

/// <summary>Android'in yerleşik çözücüsü (ImageDecoder): JPEG, PNG, GIF, WebP, BMP, HEIC/HEIF, AVIF (Android 12+), DNG; diğer RAW'lar gömülü önizlemeyle.</summary>
public sealed class AndroidCodec : IImageCodec
{
    private static readonly HashSet<string> Native = new(StringComparer.Ordinal)
        { "JPEG", "PNG", "GIF", "BMP", "WEBP", "HEIC", "HEIF", "ICO", "DNG" };

    public string Name => "Android ImageDecoder";
    public int Priority => 0;

    public bool CanRead(ImageFormat format) =>
        Native.Contains(format.Id) || format.IsRaw || (format.Id == "AVIF" && Build.VERSION.SdkInt >= BuildVersionCodes.S);

    public bool CanWrite(ImageFormat format) => format.Id is "JPEG" or "PNG" or "WEBP";

    public DecodeResult Decode(string path, ImageFormat? format, DecodeOptions options, CancellationToken ct)
    {
        format ??= FormatRegistry.FromPath(path);
        if (format != null && !CanRead(format))
            throw new DecodeException(format, "Bu format Android'de henüz açılamıyor.", [Name + ": desteklenmiyor"]);
        AndroidImaging.Decoded d;
        try { d = AndroidImaging.Decode(path, format, (w, h) => AndroidImaging.Fit(w, h, options.MaxDimension)); }
        catch (DecodeException) { throw; }
        catch (Exception ex) { throw new DecodeException(format, "Dosya çözümlenemedi; bozuk ya da desteklenmeyen bir alt tür olabilir.", [Name + ": " + ex.Message]); }
        ct.ThrowIfCancellationRequested();
        using var bmp = d.Bitmap;
        var image = AndroidImaging.ToDecodedImage(bmp, d.OriginalWidth, d.OriginalHeight);
        bmp.Recycle();
        var props = new ImageProperties
        {
            CodecFormat = format?.Id ?? "",
            Width = d.OriginalWidth,
            Height = d.OriginalHeight,
            BitDepth = 8,
            Channels = 4,
            ColorSpace = "sRGB",
            HasAlpha = format?.MayHaveAlpha == true,
        };
        return new DecodeResult(image, props, Name) { IsPreview = format?.IsRaw == true && format.Id != "DNG" };
    }

    public DecodeResult? TryDecodePreview(string path, ImageFormat? format, int maxDimension, CancellationToken ct) =>
        format?.IsRaw == true ? Decode(path, format, new DecodeOptions(maxDimension), ct) : null;

    public byte[] CreateThumbnail(string path, ImageFormat? format, int maxSize, CancellationToken ct)
    {
        format ??= FormatRegistry.FromPath(path);
        var d = AndroidImaging.Decode(path, format, (w, h) => AndroidImaging.Fit(w, h, maxSize));
        using var bmp = d.Bitmap;
        var bytes = bmp.HasAlpha
            ? AndroidImaging.Compress(bmp, Bitmap.CompressFormat.Png!, 100)
            : AndroidImaging.Compress(bmp, Bitmap.CompressFormat.Jpeg!, 82);
        bmp.Recycle();
        return bytes;
    }
}
