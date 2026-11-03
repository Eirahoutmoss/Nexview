using Android.Graphics;
using NexView.Core.Formats;
using ImageFormat = NexView.Core.Formats.ImageFormat;
using NexView.Core.Imaging;
using NexView.Core.Metadata;
using Path = System.IO.Path;

namespace NexView.Droid.Codecs;

/// <summary>Android ImageDecoder yardımcıları: yönlendirilmiş, yazılım (CPU) belleğinde ARGB_8888 bitmap üretir.</summary>
internal static class AndroidImaging
{
    /// <summary>Bellek güvenliği için tek görüntüde izin verilen en fazla piksel (≈ 64 MP).</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    public sealed record Decoded(Bitmap Bitmap, int OriginalWidth, int OriginalHeight);

    private sealed class HeaderListener(Func<int, int, (int W, int H)?> target) : Java.Lang.Object, ImageDecoder.IOnHeaderDecodedListener
    {
        public int Width, Height;
        public void OnHeaderDecoded(ImageDecoder decoder, ImageDecoder.ImageInfo info, ImageDecoder.Source source)
        {
            Width = info.Size.Width;
            Height = info.Size.Height;
            decoder.Allocator = ImageDecoderAllocator.Software;
            decoder.MemorySizePolicy = ImageDecoderMemoryPolicy.Default;
            decoder.SetTargetColorSpace(ColorSpace.Get(ColorSpace.Named.Srgb!)!);
            if (target(Width, Height) is { } t && (t.W != Width || t.H != Height))
                decoder.SetTargetSize(Math.Max(1, t.W), Math.Max(1, t.H));
        }
    }

    /// <summary>RAW için gömülü JPEG'i, diğerleri için dosyanın kendisini çözer. target: (genişlik, yükseklik) → hedef boyut.</summary>
    public static Decoded Decode(string path, ImageFormat? format, Func<int, int, (int W, int H)?> target)
    {
        byte[]? embedded = null;
        if (format?.IsRaw == true && format.Id != "DNG")
        {
            embedded = EmbeddedJpegExtractor.ExtractLargest(path)
                       ?? throw new DecodeException(format, "Bu RAW dosyasında gömülü önizleme bulunamadı.", ["Android: gömülü JPEG yok"]);
        }

        var source = embedded != null
            ? ImageDecoder.CreateSource(Java.Nio.ByteBuffer.Wrap(embedded))
            : ImageDecoder.CreateSource(new Java.IO.File(path));
        var listener = new HeaderListener(target);
        Bitmap bmp;
        try { bmp = ImageDecoder.DecodeBitmap(source, listener); }
        catch (Java.Lang.Exception ex) when (embedded == null && format?.IsRaw == true)
        {
            // DNG: çözülemezse gömülü önizlemeye dön.
            var jpeg = EmbeddedJpegExtractor.ExtractLargest(path)
                       ?? throw new DecodeException(format, "RAW dosyası çözümlenemedi.", ["Android: " + ex.Message]);
            embedded = jpeg;
            listener = new HeaderListener(target);
            bmp = ImageDecoder.DecodeBitmap(ImageDecoder.CreateSource(Java.Nio.ByteBuffer.Wrap(jpeg)), listener);
        }

        if (bmp.GetConfig() != Bitmap.Config.Argb8888)
        {
            var copy = bmp.Copy(Bitmap.Config.Argb8888!, false)!;
            bmp.Recycle();
            bmp = copy;
        }

        int ow = listener.Width, oh = listener.Height;
        // Gömülü önizlemelerin yönü RAW dosyasının EXIF'inden gelir (ImageDecoder normal dosyalarda zaten uygular).
        if (embedded != null)
        {
            var o = MetadataService.ReadOrientation(path) ?? 1;
            if (o is 5 or 6 or 7 or 8) (ow, oh) = (oh, ow);
            bmp = ApplyOrientation(bmp, o);
        }
        return new Decoded(bmp, ow, oh);
    }

    public static Bitmap ApplyOrientation(Bitmap bmp, int orientation)
    {
        if (orientation is <= 1 or > 8) return bmp;
        var m = new Matrix();
        switch (orientation)
        {
            case 2: m.PostScale(-1, 1); break;
            case 3: m.PostRotate(180); break;
            case 4: m.PostScale(1, -1); break;
            case 5: m.PostRotate(90); m.PostScale(-1, 1); break;
            case 6: m.PostRotate(90); break;
            case 7: m.PostRotate(270); m.PostScale(-1, 1); break;
            case 8: m.PostRotate(270); break;
        }
        var r = Bitmap.CreateBitmap(bmp, 0, 0, bmp.Width, bmp.Height, m, true)!;
        if (!ReferenceEquals(r, bmp)) bmp.Recycle();
        return r;
    }

    /// <summary>Uzun kenarı en fazla max olacak boyut (küçültme yoksa null).</summary>
    public static (int W, int H)? Fit(int w, int h, int max)
    {
        var scale = Math.Min(1.0, max / (double)Math.Max(w, h));
        var pixels = (long)w * h * scale * scale;
        if (pixels > MaxPixels) scale *= Math.Sqrt(MaxPixels / (double)pixels);
        return scale >= 1 ? null : ((int)Math.Round(w * scale), (int)Math.Round(h * scale));
    }

    /// <summary>Android bitmap'i (önçarpımlı RGBA) NexView'in BGRA (önçarpımsız) formatına çevirir.</summary>
    public static DecodedImage ToDecodedImage(Bitmap bmp, int ow, int oh)
    {
        int w = bmp.Width, h = bmp.Height;
        var buf = Java.Nio.ByteBuffer.AllocateDirect(w * h * 4)!;
        bmp.CopyPixelsToBuffer(buf);
        buf.Rewind();
        var px = new byte[w * h * 4];
        buf.Get(px);
        buf.Dispose();
        for (var i = 0; i < px.Length; i += 4)
        {
            byte r = px[i], b = px[i + 2], a = px[i + 3];
            if (a is not 255 and not 0)
            {
                r = (byte)Math.Min(255, r * 255 / a);
                px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
                b = (byte)Math.Min(255, b * 255 / a);
            }
            px[i] = b;
            px[i + 2] = r;
        }
        return new DecodedImage(w, h, w * 4, px, ow, oh);
    }

    /// <summary>NexView BGRA (önçarpımsız) pikselleri Android ARGB_8888 bitmap'e çevirir.</summary>
    public static Bitmap FromDecoded(DecodedImage img)
    {
        int w = img.Width, h = img.Height;
        var px = new byte[w * h * 4];
        var src = img.Pixels;
        for (int y = 0; y < h; y++)
        {
            int si = y * img.Stride, di = y * w * 4;
            for (int x = 0; x < w; x++, si += 4, di += 4)
            {
                int b = src[si], g = src[si + 1], r = src[si + 2], a = src[si + 3];
                if (a != 255) { r = r * a / 255; g = g * a / 255; b = b * a / 255; }
                px[di] = (byte)r; px[di + 1] = (byte)g; px[di + 2] = (byte)b; px[di + 3] = (byte)a;
            }
        }
        var bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
        using var buf = Java.Nio.ByteBuffer.Wrap(px)!;
        bmp.CopyPixelsFromBuffer(buf);
        return bmp;
    }

    public static byte[] Compress(Bitmap bmp, Bitmap.CompressFormat format, int quality)
    {
        using var ms = new MemoryStream();
        bmp.Compress(format, quality, ms);
        return ms.ToArray();
    }

    public static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();
}
