using NexView.Core.Imaging;

namespace NexView.Core.Editing;

/// <summary>
/// Fotoğraftaki belgenin (kâğıt, kart, makbuz) köşelerini bulur. Belge genellikle zeminden açık renklidir:
/// küçültülmüş gri görüntü bulanıklaştırılır, Otsu eşiğiyle ikilenir, en büyük açık bölge seçilir
/// ve köşeleri (x+y, x−y uç noktaları) döndürülür. Bulunamazsa null.
/// </summary>
public static class DocumentDetector
{
    public static NPoint[]? Detect(DecodedImage img)
    {
        const int N = 320;
        var small = EditGeometry.Downscale(img, N);
        int w = small.Width, h = small.Height;
        if (w < 32 || h < 32) return null;

        var gray = new byte[w * h];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
            gray[i] = (byte)((small.Pixels[p] * 29 + small.Pixels[p + 1] * 150 + small.Pixels[p + 2] * 77) >> 8);
        gray = BoxBlur(gray, w, h, 3);

        var t = Otsu(gray);
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = gray[i] > t;

        // En büyük bağlı açık bölge (4-komşuluk), kenara dokunması serbest.
        var label = new int[w * h];
        int best = 0, bestSize = 0, next = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || label[i] != 0) continue;
            next++;
            int size = 0;
            stack.Push(i); label[i] = next;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); size++;
                int x = c % w, y = c / w;
                if (x > 0 && mask[c - 1] && label[c - 1] == 0) { label[c - 1] = next; stack.Push(c - 1); }
                if (x < w - 1 && mask[c + 1] && label[c + 1] == 0) { label[c + 1] = next; stack.Push(c + 1); }
                if (y > 0 && mask[c - w] && label[c - w] == 0) { label[c - w] = next; stack.Push(c - w); }
                if (y < h - 1 && mask[c + w] && label[c + w] == 0) { label[c + w] = next; stack.Push(c + w); }
            }
            if (size > bestSize) { bestSize = size; best = next; }
        }
        var frac = bestSize / (double)(w * h);
        if (frac < 0.12 || frac > 0.97) return null;

        // Köşe adayları
        double tl = double.MaxValue, br = double.MinValue, tr = double.MinValue, bl = double.MaxValue;
        (int X, int Y) pTL = default, pBR = default, pTR = default, pBL = default;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (label[y * w + x] != best) continue;
            double s = x + y, d = x - y;
            if (s < tl) { tl = s; pTL = (x, y); }
            if (s > br) { br = s; pBR = (x, y); }
            if (d > tr) { tr = d; pTR = (x, y); }
            if (d < bl) { bl = d; pBL = (x, y); }
        }
        NPoint P((int X, int Y) p) => new((p.X + 0.5) / w, (p.Y + 0.5) / h);
        var quad = new[] { P(pTL), P(pTR), P(pBR), P(pBL) };

        // Dörtgen alanı bölgenin makul bir kısmını kapsamalı (yanlış köşelere karşı)
        var area = Math.Abs(Shoelace(quad));
        if (area < 0.1 || area < frac * 0.6) return null;
        return quad;
    }

    private static double Shoelace(NPoint[] q)
    {
        double s = 0;
        for (int i = 0; i < q.Length; i++)
        {
            var a = q[i]; var b = q[(i + 1) % q.Length];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    private static int Otsu(byte[] g)
    {
        var hist = new int[256];
        foreach (var v in g) hist[v]++;
        long total = g.Length, sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (long)hist[i];
        long sumB = 0, wB = 0; double max = 0; int t = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += i * (long)hist[i];
            double mB = sumB / (double)wB, mF = (sum - sumB) / (double)wF;
            double between = wB * (double)wF * (mB - mF) * (mB - mF);
            if (between > max) { max = between; t = i; }
        }
        return t;
    }

    private static byte[] BoxBlur(byte[] src, int w, int h, int r)
    {
        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int s = 0, n = 0;
            for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) { s += src[y * w + k]; n++; }
            tmp[y * w + x] = (byte)(s / n);
        }
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int s = 0, n = 0;
            for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) { s += tmp[k * w + x]; n++; }
            dst[y * w + x] = (byte)(s / n);
        }
        return dst;
    }
}
