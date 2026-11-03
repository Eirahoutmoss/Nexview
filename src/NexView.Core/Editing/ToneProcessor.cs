namespace NexView.Core.Editing;

/// <summary>
/// BGRA (8 bit, ön çarpımsız) tampon üzerinde ton ve piksel işlemleri. Önizleme ve kayıt aynı kodu kullanır;
/// böylece ekranda görülen ile kaydedilen aynıdır. Parametreler çözünürlükten bağımsızdır.
/// </summary>
public static class ToneProcessor
{
    public static void Apply(byte[] px, int w, int h, EditRecipe r)
    {
        if (r.DocumentFilter != DocumentFilter.None) ApplyDocumentFilter(px, w, h, r.DocumentFilter);

        bool lutNeeded = r.LevelsBlack > 0 || r.LevelsWhite < 255 || r.Exposure != 0 || r.Brightness != 0 || r.Contrast != 0;
        bool wb = r.Temperature != 0 || r.Tint != 0;
        double sat = r.Grayscale ? -100 : r.Saturation;
        if (lutNeeded || wb || sat != 0)
        {
            var lut = BuildLut(r);
            double rMul = 1 + r.Temperature / 100 * 0.18, bMul = 1 - r.Temperature / 100 * 0.18, gMul = 1 - r.Tint / 100 * 0.12;
            double s = 1 + sat / 100;
            Parallel.For(0, h, y =>
            {
                int i = y * w * 4, end = i + w * 4;
                for (; i < end; i += 4)
                {
                    double b = px[i], g = px[i + 1], rr = px[i + 2];
                    if (wb) { rr = Math.Min(255, rr * rMul); g = Math.Min(255, g * gMul); b = Math.Min(255, b * bMul); }
                    rr = lut[(int)rr]; g = lut[(int)g]; b = lut[(int)b];
                    if (s != 1)
                    {
                        double l = 0.2126 * rr + 0.7152 * g + 0.0722 * b;
                        rr = l + (rr - l) * s; g = l + (g - l) * s; b = l + (b - l) * s;
                    }
                    px[i] = Clamp(b); px[i + 1] = Clamp(g); px[i + 2] = Clamp(rr);
                }
            });
        }
        if (r.Sharpness > 0) Sharpen(px, w, h, r.Sharpness / 100.0);
    }

    public static byte[] BuildLut(EditRecipe r)
    {
        var lut = new byte[256];
        double black = Math.Clamp(r.LevelsBlack, 0, 254), white = Math.Clamp(r.LevelsWhite, black + 1, 255);
        double exp = Math.Pow(2, Math.Clamp(r.Exposure, -4, 4));
        double c = Math.Clamp(r.Contrast, -100, 100) * 1.28;
        double cf = 259 * (c + 255) / (255 * (259 - c));
        double br = Math.Clamp(r.Brightness, -100, 100) / 100;
        for (int i = 0; i < 256; i++)
        {
            double v = (i - black) / (white - black);
            v = Math.Clamp(v, 0, 1);
            v = Math.Pow(v, 2.2) * exp;                    // pozlama doğrusal ışıkta
            v = Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2);
            v = br >= 0 ? v + (1 - v) * br * 0.6 : v * (1 + br * 0.6);
            v = (cf * (v * 255 - 128) + 128) / 255;
            lut[i] = Clamp(v * 255);
        }
        return lut;
    }

    /// <summary>Otomatik seviye: %0,5 ve %99,5 parlaklık yüzdelikleri.</summary>
    public static (double Black, double White) AutoLevels(byte[] px, int w, int h)
    {
        var hist = new long[256];
        long n = 0;
        int step = Math.Max(1, w * h / 500_000);
        for (int p = 0; p < w * h; p += step)
        {
            int i = p * 4;
            int l = (int)(0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]);
            hist[l]++; n++;
        }
        long lo = (long)(n * 0.005), hi = (long)(n * 0.995), acc = 0;
        int black = 0, white = 255;
        for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= lo) { black = i; break; } }
        acc = 0;
        for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= hi) { white = i; break; } }
        if (white - black < 16) return (0, 255);
        return (black, white);
    }

    private static void Sharpen(byte[] px, int w, int h, double amount)
    {
        var src = (byte[])px.Clone();
        int stride = w * 4;
        Parallel.For(1, h - 1, y =>
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * stride + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int ci = i + c;
                    double blur = (src[ci - 4] + src[ci + 4] + src[ci - stride] + src[ci + stride] + 4 * src[ci]) / 8.0;
                    px[ci] = Clamp(src[ci] + (src[ci] - blur) * amount * 2.5);
                }
            }
        });
    }

    // ---------- Gizleme ----------

    public static void Redact(byte[] px, int w, int h, Annotation a)
    {
        var b = a.Bounds;
        int x0 = (int)(b.X * w), y0 = (int)(b.Y * h);
        int x1 = (int)Math.Ceiling((b.X + b.W) * w), y1 = (int)Math.Ceiling((b.Y + b.H) * h);
        x0 = Math.Clamp(x0, 0, w); x1 = Math.Clamp(x1, 0, w); y0 = Math.Clamp(y0, 0, h); y1 = Math.Clamp(y1, 0, h);
        if (x1 - x0 < 1 || y1 - y0 < 1) return;
        switch (a.Kind)
        {
            case AnnotationKind.BlackBox:
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = 0; px[i + 3] = 255; }
                break;
            case AnnotationKind.Pixelate:
                Pixelate(px, w, x0, y0, x1, y1, Math.Max(6, Math.Max(w, h) / 70));
                break;
            case AnnotationKind.Blur:
                // Güvenli bulanıklaştırma: önce piksel blokla (geri döndürülemez), sonra yumuşat
                Pixelate(px, w, x0, y0, x1, y1, Math.Max(4, Math.Max(w, h) / 120));
                int r = Math.Max(3, Math.Max(w, h) / 150);
                for (int pass = 0; pass < 3; pass++) BoxBlur(px, w, x0, y0, x1, y1, r);
                break;
        }
    }

    private static void Pixelate(byte[] px, int w, int x0, int y0, int x1, int y1, int block)
    {
        for (int by = y0; by < y1; by += block)
        for (int bx = x0; bx < x1; bx += block)
        {
            int ex = Math.Min(bx + block, x1), ey = Math.Min(by + block, y1);
            long sb = 0, sg = 0, sr = 0, n = 0;
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++) { int i = (y * w + x) * 4; sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; n++; }
            byte mb = (byte)(sb / n), mg = (byte)(sg / n), mr = (byte)(sr / n);
            for (int y = by; y < ey; y++)
            for (int x = bx; x < ex; x++) { int i = (y * w + x) * 4; px[i] = mb; px[i + 1] = mg; px[i + 2] = mr; }
        }
    }

    private static void BoxBlur(byte[] px, int w, int x0, int y0, int x1, int y1, int r)
    {
        int rw = x1 - x0, rh = y1 - y0;
        var tmp = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
        for (int x = 0; x < rw; x++)
        {
            int s0 = 0, s1 = 0, s2 = 0, n = 0;
            for (int k = Math.Max(0, x - r); k <= Math.Min(rw - 1, x + r); k++)
            {
                int i = ((y + y0) * w + (k + x0)) * 4; s0 += px[i]; s1 += px[i + 1]; s2 += px[i + 2]; n++;
            }
            int t = (y * rw + x) * 4; tmp[t] = (byte)(s0 / n); tmp[t + 1] = (byte)(s1 / n); tmp[t + 2] = (byte)(s2 / n);
        }
        for (int y = 0; y < rh; y++)
        for (int x = 0; x < rw; x++)
        {
            int s0 = 0, s1 = 0, s2 = 0, n = 0;
            for (int k = Math.Max(0, y - r); k <= Math.Min(rh - 1, y + r); k++)
            {
                int t = (k * rw + x) * 4; s0 += tmp[t]; s1 += tmp[t + 1]; s2 += tmp[t + 2]; n++;
            }
            int i = ((y + y0) * w + (x + x0)) * 4; px[i] = (byte)(s0 / n); px[i + 1] = (byte)(s1 / n); px[i + 2] = (byte)(s2 / n);
        }
    }

    // ---------- Belge modu ----------

    /// <summary>Kâğıt zeminini beyazlatır (yerel ortalamaya bölme), isteğe bağlı gri ya da siyah-beyaz.</summary>
    public static void ApplyDocumentFilter(byte[] px, int w, int h, DocumentFilter f)
    {
        int n = w * h;
        var lum = new float[n];
        for (int p = 0; p < n; p++)
        {
            int i = p * 4;
            lum[p] = (float)(0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]);
        }
        int radius = Math.Max(8, Math.Max(w, h) / 40);
        var bg = BoxMean(lum, w, h, radius);

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, i = p * 4;
                double back = Math.Max(bg[p], 1);
                double scale = 245.0 / back;
                if (f == DocumentFilter.Enhance)
                {
                    for (int c = 0; c < 3; c++) px[i + c] = Clamp(px[i + c] * Math.Min(scale, 3));
                }
                else
                {
                    double v = Math.Min(255, lum[p] * Math.Min(scale, 3));
                    if (f == DocumentFilter.BlackWhite)
                    {
                        double t = (v - 150) / 50;   // yumuşak eşik
                        v = Math.Clamp(t, 0, 1) * 255;
                    }
                    else
                    {
                        v = Math.Clamp((v - 20) * 255 / 225, 0, 255);
                    }
                    px[i] = px[i + 1] = px[i + 2] = Clamp(v);
                }
            }
        });
    }

    private static float[] BoxMean(float[] src, int w, int h, int r)
    {
        // İntegral görüntü ile O(1) kutu ortalaması
        var integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double row = 0;
            for (int x = 0; x < w; x++)
            {
                row += src[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
            }
        }
        var dst = new float[w * h];
        Parallel.For(0, h, y =>
        {
            int ya = Math.Max(0, y - r), yb = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Max(0, x - r), xb = Math.Min(w, x + r + 1);
                double s = integral[yb * (w + 1) + xb] - integral[ya * (w + 1) + xb] - integral[yb * (w + 1) + xa] + integral[ya * (w + 1) + xa];
                dst[y * w + x] = (float)(s / ((xb - xa) * (yb - ya)));
            }
        });
        // Yazıyı zeminden ayırmak için ortalama yerine biraz daha parlak taraf: maksimuma yaklaştır
        return dst;
    }

    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
}
