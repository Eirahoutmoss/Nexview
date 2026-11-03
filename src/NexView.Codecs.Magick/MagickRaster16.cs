using ImageMagick;
using NexView.Core.Conversion;
using NexView.Core.Formats;

namespace NexView.Codecs.Magick;

/// <summary>16 bit RGB okuma/yazma (Stüdyo motoru için): yönlendirilmiş, sRGB, isteğe bağlı küçültme.</summary>
public static class MagickRaster16
{
    /// <summary>RGB, satır sıralı, kanal başına 16 bit. maxDimension ≤ 0 ise tam çözünürlük.</summary>
    public static (ushort[] Rgb, int Width, int Height) Load(string path, int maxDimension)
    {
        using var img = MagickHelpers.ReadOriented(path, FormatDetector.Detect(path).Format);
        MagickHelpers.NormalizeColor(img);
        if (img.HasAlpha)
        {
            img.BackgroundColor = MagickColors.Black;
            img.Alpha(AlphaOption.Remove);
        }
        if (maxDimension > 0 && Math.Max(img.Width, img.Height) > maxDimension)
            img.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension) { Greater = true });
        using var pixels = img.GetPixelsUnsafe();
        var rgb = pixels.ToShortArray("RGB") ?? throw new InvalidDataException("Piksel verisi alınamadı.");
        return (rgb, (int)img.Width, (int)img.Height);
    }

    /// <summary>16 bit RGB'yi kaydeder (JPEG/WebP 8 bit, TIFF/PNG 16 bit). Benzersiz dosya adını döndürür.</summary>
    public static string Save(ushort[] rgb, int width, int height, string outputPath, ImageFormat format, int quality, string? exifSource, string? xmp = null)
    {
        using var img = new MagickImage();
        var bytes = new byte[rgb.Length * 2];
        Buffer.BlockCopy(rgb, 0, bytes, 0, bytes.Length);
        img.ReadPixels(bytes, new PixelReadSettings((uint)width, (uint)height, StorageType.Short, PixelMapping.RGB));
        img.ColorSpace = ColorSpace.sRGB;
        if (exifSource != null)
        {
            try
            {
                using var src = new MagickImage();
                src.Ping(exifSource);
                if (src.GetExifProfile() is { } exif)
                {
                    exif.SetValue(ExifTag.Orientation, (ushort)1);
                    img.SetProfile(exif);
                }
                if (src.GetColorProfile() is { } icc && icc.ColorSpace == ColorSpace.sRGB) img.SetProfile(icc);
            }
            catch (MagickException) { }
        }
        if (xmp != null) img.SetProfile(new XmpProfile(System.Text.Encoding.UTF8.GetBytes(xmp)));
        img.Orientation = OrientationType.TopLeft;
        img.Depth = format.Id is "JPEG" or "WEBP" ? 8u : 16u;
        var data = MagickConverter.Encode(img, new ConversionOptions { Target = format, Quality = quality, TiffCompression = "ZIP" }, quality);
        var final = OutputPaths.Unique(outputPath);
        MagickConverter.WriteSafely(data, final);
        return final;
    }
}
