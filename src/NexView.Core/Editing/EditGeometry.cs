using NexView.Core.Imaging;

namespace NexView.Core.Editing;

public enum EditTool { Adjust, Crop, Markup, Document }
public enum CompareMode { Off, Original, Split }

/// <summary>
/// Tarifin geometri adımlarının saf C# uygulaması (BGRA, 8 bit). Magick bulunmayan platformlar (Android, iOS) için.
/// Sıra ve sonuçlar MagickEditor ile aynıdır: belge perspektifi → 90° döndürme → çevirme → ince düzeltme → kırpma.
/// </summary>
public static class EditGeometry
{
    public static DecodedImage Apply(DecodedImage src, EditRecipe r, PreviewStage stage)
    {
        if (stage == PreviewStage.BeforeDocument) return src;
        var img = src;
        if (r.DocumentQuad is { Length: 4 } q) img = Perspective(img, q);
        var rot = ((r.Rotation % 360) + 360) % 360;
        if (rot != 0) img = Rotate90(img, rot);
        if (r.FlipH || r.FlipV) img = Flip(img, r.FlipH, r.FlipV);
        if (Math.Abs(r.Straighten) > 0.01) img = Straighten(img, r.Straighten);
        if (stage == PreviewStage.Full && !r.Crop.IsFull) img = Crop(img, r.Crop.Normalize());
        return img;
    }

    private static DecodedImage New(int w, int h, byte[] px) => new(w, h, w * 4, px, w, h);

    public static DecodedImage Rotate90(DecodedImage s, int degrees)
    {
        int w = s.Width, h = s.Height;
        bool swap = degrees is 90 or 270;
        int ow = swap ? h : w, oh = swap ? w : h;
        var src = s.Pixels;
        var dst = new byte[ow * oh * 4];
        for (int y = 0; y < h; y++)
        {
            int si = y * s.Stride;
            for (int x = 0; x < w; x++, si += 4)
            {
                int dx, dy;
                switch (degrees)
                {
                    case 90: dx = h - 1 - y; dy = x; break;      // saat yönünde
                    case 180: dx = w - 1 - x; dy = h - 1 - y; break;
                    default: dx = y; dy = w - 1 - x; break;       // 270
                }
                Buffer.BlockCopy(src, si, dst, (dy * ow + dx) * 4, 4);
            }
        }
        return New(ow, oh, dst);
    }

    public static DecodedImage Flip(DecodedImage s, bool horizontal, bool vertical)
    {
        int w = s.Width, h = s.Height;
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = vertical ? h - 1 - y : y;
            for (int x = 0; x < w; x++)
            {
                int sx = horizontal ? w - 1 - x : x;
                Buffer.BlockCopy(s.Pixels, sy * s.Stride + sx * 4, dst, (y * w + x) * 4, 4);
            }
        }
        return New(w, h, dst);
    }

    public static DecodedImage Crop(DecodedImage s, NRect c)
    {
        int x0 = (int)Math.Round(c.X * s.Width), y0 = (int)Math.Round(c.Y * s.Height);
        int w = Math.Max(1, Math.Min(s.Width - x0, (int)Math.Round(c.W * s.Width)));
        int h = Math.Max(1, Math.Min(s.Height - y0, (int)Math.Round(c.H * s.Height)));
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(s.Pixels, (y0 + y) * s.Stride + x0 * 4, dst, y * w * 4, w * 4);
        return New(w, h, dst);
    }

    /// <summary>Saat yönünde (pozitif derece) döndürür ve siyah köşe kalmayacak en büyük iç dikdörtgeni kırpar.</summary>
    public static DecodedImage Straighten(DecodedImage s, double degrees)
    {
        double w = s.Width, h = s.Height, a = Math.Abs(degrees) * Math.PI / 180;
        double k = Math.Min(w / (w * Math.Cos(a) + h * Math.Sin(a)), h / (w * Math.Sin(a) + h * Math.Cos(a)));
        int ow = Math.Max(1, (int)Math.Floor(w * k)), oh = Math.Max(1, (int)Math.Floor(h * k));
        double t = degrees * Math.PI / 180, cos = Math.Cos(t), sin = Math.Sin(t);
        double scx = w / 2, scy = h / 2, ocx = ow / 2.0, ocy = oh / 2.0;
        var dst = new byte[ow * oh * 4];
        Parallel.For(0, oh, y =>
        {
            double dy = y + 0.5 - ocy;
            int di = y * ow * 4;
            for (int x = 0; x < ow; x++, di += 4)
            {
                double dx = x + 0.5 - ocx;
                // Ters dönüşüm: çıktı noktasını kaynağa geri döndür.
                double sx = dx * cos + dy * sin + scx - 0.5;
                double sy = -dx * sin + dy * cos + scy - 0.5;
                Sample(s, sx, sy, dst, di);
            }
        });
        return New(ow, oh, dst);
    }

    /// <summary>Dört köşeli bölgeyi (sol üst, sağ üst, sağ alt, sol alt; normalleştirilmiş) düz dikdörtgene açar.</summary>
    public static DecodedImage Perspective(DecodedImage s, NPoint[] q)
    {
        double W = s.Width, H = s.Height;
        var p = q.Select(pt => (X: Math.Clamp(pt.X, 0, 1) * W, Y: Math.Clamp(pt.Y, 0, 1) * H)).ToArray();
        static double D((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        int dw = (int)Math.Max(16, Math.Round(Math.Max(D(p[0], p[1]), D(p[3], p[2]))));
        int dh = (int)Math.Max(16, Math.Round(Math.Max(D(p[0], p[3]), D(p[1], p[2]))));
        // Bellek koruması: çıktı kaynaktan çok büyük olmasın.
        var limit = Math.Sqrt(W * H * 1.5 / ((double)dw * dh));
        if (limit < 1) { dw = (int)(dw * limit); dh = (int)(dh * limit); }

        var hm = Homography([(0, 0), (dw, 0), (dw, dh), (0, dh)], p);
        var dst = new byte[dw * dh * 4];
        Parallel.For(0, dh, y =>
        {
            int di = y * dw * 4;
            double v = y + 0.5;
            for (int x = 0; x < dw; x++, di += 4)
            {
                double u = x + 0.5;
                double z = hm[6] * u + hm[7] * v + 1;
                double sx = (hm[0] * u + hm[1] * v + hm[2]) / z - 0.5;
                double sy = (hm[3] * u + hm[4] * v + hm[5]) / z - 0.5;
                Sample(s, sx, sy, dst, di);
            }
        });
        return New(dw, dh, dst);
    }

    /// <summary>from → to eşlemesini veren 3x3 homografi (h33 = 1), 8 elemanlı dizi.</summary>
    public static double[] Homography((double X, double Y)[] from, (double X, double Y)[] to)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = from[i];
            var (u, v) = to[i];
            int r = i * 2;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -x * u; a[r, 7] = -y * u; a[r, 8] = u;
            a[r + 1, 3] = x; a[r + 1, 4] = y; a[r + 1, 5] = 1; a[r + 1, 6] = -x * v; a[r + 1, 7] = -y * v; a[r + 1, 8] = v;
        }
        // Kısmi pivotlu Gauss eliminasyonu
        for (int c = 0; c < 8; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 8; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-12) throw new InvalidOperationException("Belge köşeleri geçersiz (dejenere dörtgen).");
            if (piv != c) for (int k = 0; k < 9; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]);
            for (int r = 0; r < 8; r++)
            {
                if (r == c) continue;
                var f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int k = c; k < 9; k++) a[r, k] -= f * a[c, k];
            }
        }
        var h = new double[8];
        for (int i = 0; i < 8; i++) h[i] = a[i, 8] / a[i, i];
        return h;
    }

    /// <summary>Kenar piksellerini uzatan çift doğrusal örnekleme.</summary>
    private static void Sample(DecodedImage s, double sx, double sy, byte[] dst, int di)
    {
        int w = s.Width, h = s.Height;
        sx = Math.Clamp(sx, 0, w - 1);
        sy = Math.Clamp(sy, 0, h - 1);
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = sx - x0, fy = sy - y0;
        var px = s.Pixels;
        int i00 = y0 * s.Stride + x0 * 4, i10 = y0 * s.Stride + x1 * 4, i01 = y1 * s.Stride + x0 * 4, i11 = y1 * s.Stride + x1 * 4;
        for (int c = 0; c < 4; c++)
        {
            double top = px[i00 + c] + (px[i10 + c] - px[i00 + c]) * fx;
            double bot = px[i01 + c] + (px[i11 + c] - px[i01 + c]) * fx;
            dst[di + c] = (byte)Math.Clamp((int)(top + (bot - top) * fy + 0.5), 0, 255);
        }
    }

    /// <summary>Uzun kenarı en fazla max piksel olacak şekilde küçültür (kutu ortalaması).</summary>
    public static DecodedImage Downscale(DecodedImage s, int max)
    {
        var scale = max / (double)Math.Max(s.Width, s.Height);
        if (scale >= 1) return s;
        int ow = Math.Max(1, (int)(s.Width * scale)), oh = Math.Max(1, (int)(s.Height * scale));
        var dst = new byte[ow * oh * 4];
        double fx = s.Width / (double)ow, fy = s.Height / (double)oh;
        Parallel.For(0, oh, y =>
        {
            int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, (int)((y + 1) * fy));
            for (int x = 0; x < ow; x++)
            {
                int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, (int)((x + 1) * fx));
                long b = 0, g = 0, r = 0, a = 0; int n = 0;
                for (int yy = y0; yy < y1 && yy < s.Height; yy++)
                for (int xx = x0; xx < x1 && xx < s.Width; xx++)
                {
                    int i = yy * s.Stride + xx * 4;
                    b += s.Pixels[i]; g += s.Pixels[i + 1]; r += s.Pixels[i + 2]; a += s.Pixels[i + 3]; n++;
                }
                int di = (y * ow + x) * 4;
                dst[di] = (byte)(b / n); dst[di + 1] = (byte)(g / n); dst[di + 2] = (byte)(r / n); dst[di + 3] = (byte)(a / n);
            }
        });
        return New(ow, oh, dst);
    }
}
