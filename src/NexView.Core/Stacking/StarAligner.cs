namespace NexView.Core.Stacking;

/// <summary>Yıldız bulma ve iki yıldız kümesi arasında benzerlik dönüşümü (dönme + öteleme) tahmini.</summary>
public static class StarAligner
{
    /// <summary>Parlaklık (0..1) dizisinde yerel maksimumları bulur; akıya göre sıralı en parlak <paramref name="max"/> yıldızı döndürür.</summary>
    public static List<Star> DetectStars(float[] lum, int w, int h, int max = 60)
    {
        int step = Math.Max(1, lum.Length / 200_000);
        var sample = new List<float>(lum.Length / step + 1);
        for (int i = 0; i < lum.Length; i += step) sample.Add(lum[i]);
        sample.Sort();
        float median = sample[sample.Count / 2];
        for (int i = 0; i < sample.Count; i++) sample[i] = Math.Abs(sample[i] - median);
        sample.Sort();
        float sigma = Math.Max(1e-4f, sample[sample.Count / 2] * 1.4826f);
        float thr = median + Math.Max(5 * sigma, 0.02f);

        const int r = 3;
        var stars = new List<Star>();
        for (int y = r; y < h - r; y++)
        {
            for (int x = r; x < w - r; x++)
            {
                float v = lum[y * w + x];
                if (v < thr) continue;
                bool isMax = true;
                for (int dy = -r; dy <= r && isMax; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    float n = lum[(y + dy) * w + x + dx];
                    if (n > v || (n == v && (dy < 0 || (dy == 0 && dx < 0)))) { isMax = false; break; }
                }
                if (!isMax) continue;
                double sx = 0, sy = 0, sf = 0;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    double f = Math.Max(0, lum[(y + dy) * w + x + dx] - median);
                    sx += f * (x + dx); sy += f * (y + dy); sf += f;
                }
                if (sf > 0) stars.Add(new Star(sx / sf, sy / sf, sf));
            }
        }
        return stars.OrderByDescending(s => s.Flux).Take(max).ToList();
    }

    /// <summary>Kare yıldızlarını referansa eşleyen dönüşümü bulur. Yetersiz eşleşmede null.</summary>
    public static Similarity? Estimate(IReadOnlyList<Star> reference, IReadOnlyList<Star> frame, double tolerance = 2.5)
    {
        if (reference.Count < 4 || frame.Count < 4) return null;
        var A = reference.Take(14).ToArray();
        var B = frame.Take(14).ToArray();
        var refAll = reference.ToArray();
        var frmAll = frame.Take(45).ToArray();
        double tol2 = tolerance * tolerance;

        Similarity? best = null;
        for (int i = 0; i < A.Length; i++)
        for (int j = i + 1; j < A.Length; j++)
        {
            double ax = A[j].X - A[i].X, ay = A[j].Y - A[i].Y;
            double dA = Math.Sqrt(ax * ax + ay * ay);
            if (dA < 10) continue;
            for (int k = 0; k < B.Length; k++)
            for (int l = 0; l < B.Length; l++)
            {
                if (k == l) continue;
                double bx = B[l].X - B[k].X, by = B[l].Y - B[k].Y;
                double dB = Math.Sqrt(bx * bx + by * by);
                if (Math.Abs(dA - dB) > 0.02 * dA + 1.5) continue;
                double s = dA / dB;
                if (Math.Abs(s - 1) > 0.03) continue;
                double ang = Math.Atan2(ay, ax) - Math.Atan2(by, bx);
                double a = s * Math.Cos(ang), b = s * Math.Sin(ang);
                double tx = A[i].X - (a * B[k].X - b * B[k].Y);
                double ty = A[i].Y - (b * B[k].X + a * B[k].Y);
                int inl = CountInliers(a, b, tx, ty, frmAll, refAll, tol2);
                if (best == null || inl > best.Value.Inliers) best = new Similarity(a, b, tx, ty, inl);
            }
        }
        if (best is not { } bst || bst.Inliers < Math.Min(6, Math.Min(reference.Count, frame.Count) / 2 + 1)) return null;
        return Refine(bst, frmAll, refAll, tol2);
    }

    private static int CountInliers(double a, double b, double tx, double ty, Star[] frame, Star[] reference, double tol2)
    {
        int n = 0;
        foreach (var f in frame)
        {
            double x = a * f.X - b * f.Y + tx, y = b * f.X + a * f.Y + ty;
            foreach (var r in reference)
            {
                double dx = r.X - x, dy = r.Y - y;
                if (dx * dx + dy * dy <= tol2) { n++; break; }
            }
        }
        return n;
    }

    /// <summary>Eşleşen çiftler üzerinden en küçük kareler benzerlik dönüşümü (Umeyama, ölçek serbest).</summary>
    private static Similarity Refine(Similarity s, Star[] frame, Star[] reference, double tol2)
    {
        var pairs = new List<(Star F, Star R)>();
        foreach (var f in frame)
        {
            var (x, y) = s.Apply(f.X, f.Y);
            Star? match = null;
            double bestD = tol2;
            foreach (var r in reference)
            {
                double d = (r.X - x) * (r.X - x) + (r.Y - y) * (r.Y - y);
                if (d <= bestD) { bestD = d; match = r; }
            }
            if (match is { } m) pairs.Add((f, m));
        }
        if (pairs.Count < 3) return s;
        double mfx = pairs.Average(p => p.F.X), mfy = pairs.Average(p => p.F.Y);
        double mrx = pairs.Average(p => p.R.X), mry = pairs.Average(p => p.R.Y);
        double sxx = 0, sxy = 0, var = 0;
        foreach (var (f, r) in pairs)
        {
            double fx = f.X - mfx, fy = f.Y - mfy, rx = r.X - mrx, ry = r.Y - mry;
            sxx += fx * rx + fy * ry;
            sxy += fx * ry - fy * rx;
            var += fx * fx + fy * fy;
        }
        if (var <= 0) return s;
        double a = sxx / var, b = sxy / var;
        double tx = mrx - (a * mfx - b * mfy), ty = mry - (b * mfx + a * mfy);
        return new Similarity(a, b, tx, ty, pairs.Count);
    }
}
