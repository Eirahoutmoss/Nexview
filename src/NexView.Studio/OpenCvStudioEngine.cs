using NexView.Core.Localization;
using NexView.Core.Stacking;
using OpenCvSharp;

namespace NexView.Studio;

/// <summary>
/// OpenCV tabanlı stüdyo motoru. Çalışma biçimi 32 bit kayan noktalı BGR (0..1); giriş/çıkış 16 bit.
/// Makro: ECC hizalama + piramit en-yüksek-ayrıntı (PMax) ya da yumuşak derinlik haritası.
/// HDR: öznitelik hizalama + Mertens pozlama kaynaştırma + stil (yerel kontrast).
/// Panorama: OpenCV Stitcher (küresel/düz) + pozlama telafisi + çok bantlı harmanlama + otomatik kırpma.
/// </summary>
public sealed class OpenCvStudioEngine(RasterLoader load) : IStudioEngine, IDisposable
{
    private const int AlignMax = 1400;
    private const int CacheMaxDimension = 1600;

    // Önizleme önbellekleri: canlı önizlemede yalnızca değişen adım yeniden hesaplanır.
    private readonly object _lock = new();
    private readonly Dictionary<(string, int), Mat> _frames = new();
    private (string Key, Size Size, Mat?[] Warps, List<string> Notes)? _macroAlign;
    private (string Key, int Ref, Mat?[] Homs, List<string> Notes)? _hdrAlign;
    private (string Key, Mat Pano, double Focal)? _pano;

    private static string KeyOf(string kind, IReadOnlyList<string> files, params object[] parts) =>
        kind + "|" + string.Join("|", parts) + "|" + string.Join("|", files);

    /// <summary>Önbellekleri boşaltır (ör. kareler değiştiğinde ya da pencere kapanırken).</summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            foreach (var m in _frames.Values) m.Dispose();
            _frames.Clear();
            if (_macroAlign is { } a) foreach (var w in a.Warps) w?.Dispose();
            if (_hdrAlign is { } h) foreach (var w in h.Homs) w?.Dispose();
            _pano?.Pano.Dispose();
            _macroAlign = null; _hdrAlign = null; _pano = null;
        }
    }

    public void Dispose() => ClearCache();

    private static Mat?[] CloneAll(Mat?[] a) => a.Select(m => m?.Clone()).ToArray();

    public StudioOutput Run(IReadOnlyList<string> files, StudioOptions o, IProgress<StackProgress>? progress, CancellationToken ct)
    {
        if (files.Count < 2) throw new InvalidOperationException(L.T("Birleştirme için en az iki kare gerekir."));
        return o.Kind switch
        {
            StudioKind.Macro => Macro(files, o, progress, ct),
            StudioKind.Hdr => Hdr(files, o, progress, ct),
            StudioKind.Panorama => Panorama(files, o, progress, ct),
            _ => throw new NotSupportedException(),
        };
    }

    // =====================================================================
    // Makro odak birleştirme
    // =====================================================================

    private StudioOutput Macro(IReadOnlyList<string> files, StudioOptions o, IProgress<StackProgress>? progress, CancellationToken ct)
    {
        int n = files.Count, refIdx = n / 2;
        var notes = new List<string>();

        // 1) Hizalama: komşu kareler arasında ECC (afin: odak kaymasının yarattığı büyümeyi de düzeltir), referansa zincirleme
        var warps = new Mat?[n]; // referans koordinatı → kare koordinatı (2×3, tam ölçek)
        Size size;
        var alignKey = KeyOf("M", files, o.MaxDimension, o.Align);
        bool cached = false;
        lock (_lock)
            if (_macroAlign is { } c && c.Key == alignKey)
            {
                warps = CloneAll(c.Warps); notes.AddRange(c.Notes); size = c.Size; cached = true;
            }
            else size = default;
        if (!cached)
        {
        using (var refImg = Load(files[refIdx], o.MaxDimension)) size = refImg.Size();
        warps[refIdx] = Identity2x3();
        if (o.Align)
        {
            var small = new Mat?[n];
            double scale = Math.Min(1.0, AlignMax / (double)Math.Max(size.Width, size.Height));
            try
            {
                for (int i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new StackProgress(L.T("Kareler hazırlanıyor"), i, n));
                    using var f = Load(files[i], o.MaxDimension);
                    if (f.Size() != size) { notes.Add(L.T("{0}: boyut farklı, atlandı", Path.GetFileName(files[i]))); continue; }
                    small[i] = GrayForAlign(f, scale);
                }
                int done = 0;
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    int prev = refIdx;
                    for (int i = refIdx + dir; i >= 0 && i < n; i += dir)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new StackProgress(L.T("Hizalanıyor"), ++done, n - 1));
                        if (small[i] == null) continue;
                        var step = Ecc(small[prev]!, small[i]!, scale);
                        if (step == null)
                        {
                            notes.Add(L.T("{0}: hizalanamadı, atlandı", Path.GetFileName(files[i])));
                            continue;
                        }
                        warps[i] = Compose(step, warps[prev]!);
                        step.Dispose();
                        prev = i;
                    }
                }
            }
            finally { foreach (var s in small) s?.Dispose(); }
        }
        else
            for (int i = 0; i < n; i++) warps[i] ??= Identity2x3();
        lock (_lock)
        {
            if (_macroAlign is { } old) foreach (var w in old.Warps) w?.Dispose();
            _macroAlign = (alignKey, size, CloneAll(warps), notes.ToList());
        }
        }

        // 2) Birleştirme (akış hâlinde: aynı anda yalnızca bir kare bellekte)
        int levels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Min(size.Width, size.Height) / 16.0)), 3, 8);
        using var common = new Mat(size, MatType.CV_8UC1, Scalar.All(255));
        IFocusFuser fuser = o.MacroMethod switch
        {
            MacroMethod.Pyramid => new PyramidFuser(levels),
            MacroMethod.DepthMap => new DepthMapFuser(size, o.Smoothness),
            _ => new HybridFuser(levels, size, o.Smoothness, o.HaloSuppression),
        };
        int used = 0;
        try
        {
            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new StackProgress(L.T("Netlik birleştiriliyor"), i, n));
                if (warps[i] == null) continue;
                using var raw = Load(files[i], o.MaxDimension);
                if (raw.Size() != size) continue;
                using var img = new Mat();
                using var valid = new Mat();
                if (i == refIdx || !o.Align) { raw.CopyTo(img); valid.Create(size, MatType.CV_8UC1); valid.SetTo(Scalar.All(255)); }
                else
                {
                    Cv2.WarpAffine(raw, img, warps[i]!, size, InterpolationFlags.Cubic | InterpolationFlags.WarpInverseMap, BorderTypes.Reflect101);
                    using var ones = new Mat(raw.Size(), MatType.CV_8UC1, Scalar.All(255));
                    Cv2.WarpAffine(ones, valid, warps[i]!, size, InterpolationFlags.Nearest | InterpolationFlags.WarpInverseMap, BorderTypes.Constant, Scalar.All(0));
                }
                Cv2.Min(common, valid, common);
                fuser.Add(img, valid);
                used++;
            }
            if (used < 2) throw new InvalidOperationException(L.T("Birleştirilebilecek en az iki kare bulunamadı."));
            progress?.Report(new StackProgress(L.T("Sonuç oluşturuluyor"), n, n));
            using var result = fuser.Result();
            using var cropped = o.CropToCommon ? CropToMask(result, common, 0.02) : result.Clone();
            var outp = ToOutput(cropped);
            outp.Used = used;
            outp.Notes.AddRange(notes);
            return outp;
        }
        finally
        {
            fuser.Dispose();
            foreach (var w in warps) w?.Dispose();
        }
    }

    private interface IFocusFuser : IDisposable
    {
        void Add(Mat img, Mat valid);
        Mat Result();
    }

    /// <summary>
    /// Laplace piramidinin her seviyesinde, yerel ayrıntı enerjisi en yüksek karenin katsayısı seçilir (Zerene "PMax" benzeri);
    /// en üst seviye ortalanır. Seçim kararları hafif bulanıklaştırılmış enerjiyle verildiği için tuz-biber artığı oluşmaz.
    /// </summary>
    private sealed class PyramidFuser(int levels) : IFocusFuser
    {
        private readonly Mat[] _best = new Mat[levels - 1];
        private readonly Mat[] _energy = new Mat[levels - 1];
        private Mat? _topSum;
        private int _count;

        public void Add(Mat img, Mat valid)
        {
            var g = new Mat[levels];
            var m = new Mat[levels];
            g[0] = img.Clone();
            m[0] = new Mat();
            valid.ConvertTo(m[0], MatType.CV_32F, 1 / 255.0);
            for (int k = 1; k < levels; k++)
            {
                g[k] = new Mat(); Cv2.PyrDown(g[k - 1], g[k]);
                m[k] = new Mat(); Cv2.PyrDown(m[k - 1], m[k]);
            }
            for (int k = 0; k < levels - 1; k++)
            {
                using var up = new Mat();
                Cv2.PyrUp(g[k + 1], up, g[k].Size());
                var lap = new Mat();
                Cv2.Subtract(g[k], up, lap);
                using var gray = new Mat();
                Cv2.CvtColor(lap, gray, ColorConversionCodes.BGR2GRAY);
                using Mat abs = Cv2.Abs(gray);
                var e = new Mat();
                Cv2.GaussianBlur(abs, e, new Size(0, 0), 1.5 + k * 0.5);
                // Kenar dışı / kısmen geçerli alanların seçilmesini önle
                using var mm = new Mat();
                Cv2.Threshold(m[k], mm, 0.999, 1, ThresholdTypes.Binary);
                Cv2.Multiply(e, mm, e);
                if (_best[k] == null) { _best[k] = lap; _energy[k] = e; }
                else
                {
                    using var mask = new Mat();
                    Cv2.Compare(e, _energy[k], mask, CmpType.GT);
                    lap.CopyTo(_best[k], mask);
                    e.CopyTo(_energy[k], mask);
                    lap.Dispose(); e.Dispose();
                }
            }
            var top = g[levels - 1];
            if (_topSum == null) _topSum = top.Clone();
            else Cv2.Add(_topSum, top, _topSum);
            _count++;
            foreach (var x in g) x.Dispose();
            foreach (var x in m) x.Dispose();
        }

        public Mat Result()
        {
            var r = new Mat();
            _topSum!.ConvertTo(r, MatType.CV_32FC3, 1.0 / _count);
            for (int k = levels - 2; k >= 0; k--)
            {
                var up = new Mat();
                Cv2.PyrUp(r, up, _best[k].Size());
                r.Dispose();
                Cv2.Add(up, _best[k], up);
                r = up;
            }
            Cv2.Min(r, 1.0, r);
            Cv2.Max(r, 0.0, r);
            return r;
        }

        public void Dispose()
        {
            foreach (var x in _best) x?.Dispose();
            foreach (var x in _energy) x?.Dispose();
            _topSum?.Dispose();
        }
    }

    /// <summary>
    /// Yumuşak derinlik haritası: kare başına yerel netlik ölçüsü (LoG enerjisi) geniş bölgede ortalanır; ağırlık = (ölçü/taban)^p.
    /// Daha pürüzsüz geçişler, daha az gürültü; çok ince ayrıntıda PMax kadar keskin değildir.
    /// </summary>
    private sealed class DepthMapFuser(Size size, double smoothness) : IFocusFuser
    {
        private readonly Mat _sum = new(size, MatType.CV_32FC3, Scalar.All(0));
        private readonly Mat _wsum = new(size, MatType.CV_32FC1, Scalar.All(0));
        private float _floor = -1;

        public void Add(Mat img, Mat valid)
        {
            using var gray = new Mat();
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            using var sm = new Mat();
            Cv2.GaussianBlur(gray, sm, new Size(0, 0), 1.0);
            using var lap = new Mat();
            Cv2.Laplacian(sm, lap, MatType.CV_32F, 3);
            using Mat abs = Cv2.Abs(lap);
            using var e = new Mat();
            double sigma = Math.Max(2, Math.Min(size.Width, size.Height) / 600.0 * Math.Clamp(smoothness, 1, 10));
            Cv2.GaussianBlur(abs, e, new Size(0, 0), sigma);
            if (_floor < 0)
            {
                Cv2.MinMaxLoc(e, out _, out double max);
                _floor = (float)Math.Max(1e-5, Median(e) * 1.5 + max * 1e-4);
            }
            using var w = new Mat();
            e.ConvertTo(w, MatType.CV_32F, 1.0 / _floor, 1.0);
            Cv2.Pow(w, 6, w);
            using var vf = new Mat();
            valid.ConvertTo(vf, MatType.CV_32F, 1 / 255.0);
            Cv2.Multiply(w, vf, w);
            using var w3 = new Mat();
            Cv2.Merge([w, w, w], w3);
            using var wi = new Mat();
            Cv2.Multiply(img, w3, wi);
            Cv2.Add(_sum, wi, _sum);
            Cv2.Add(_wsum, w, _wsum);
        }

        public Mat Result()
        {
            using var d = new Mat();
            Cv2.Max(_wsum, 1e-20, d);
            using var d3 = new Mat();
            Cv2.Merge([d, d, d], d3);
            var r = new Mat();
            Cv2.Divide(_sum, d3, r);
            return r;
        }

        public void Dispose() { _sum.Dispose(); _wsum.Dispose(); }

        private static double Median(Mat m)
        {
            using var small = new Mat();
            Cv2.Resize(m, small, new Size(Math.Max(1, m.Width / 8), Math.Max(1, m.Height / 8)), 0, 0, InterpolationFlags.Area);
            small.GetArray(out float[] v);
            Array.Sort(v);
            return v.Length > 0 ? v[v.Length / 2] : 0;
        }
    }

    /// <summary>
    /// Karma: düşük frekanslar (ton, büyük geçişler) derinlik haritasından, yüksek frekanslar (ince doku) piramitten alınır.
    /// Böylece PMax'ın keskinliği korunurken kenar haleleri ve renk lekeleri bastırılır.
    /// </summary>
    private sealed class HybridFuser(int levels, Size size, double smoothness, double halo) : IFocusFuser
    {
        private readonly PyramidFuser _detail = new(levels);
        private readonly DepthMapFuser _base = new(size, smoothness);

        public void Add(Mat img, Mat valid) { _detail.Add(img, valid); _base.Add(img, valid); }

        public Mat Result()
        {
            using var p = _detail.Result();
            using var d = _base.Result();
            double sigma = Math.Max(1.5, Math.Min(size.Width, size.Height) / (360.0 - 300.0 * Math.Clamp(halo, 0, 1)));
            using var pLow = new Mat(); Cv2.GaussianBlur(p, pLow, new Size(0, 0), sigma);
            using var dLow = new Mat(); Cv2.GaussianBlur(d, dLow, new Size(0, 0), sigma);
            var r = new Mat();
            Cv2.Subtract(p, pLow, r);
            Cv2.Add(r, dLow, r);
            Cv2.Min(r, 1.0, r);
            Cv2.Max(r, 0.0, r);
            return r;
        }

        public void Dispose() { _detail.Dispose(); _base.Dispose(); }
    }

    private static Mat GrayForAlign(Mat bgr, double scale)
    {
        using var g = new Mat();
        Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        var s = new Mat();
        if (scale < 1) Cv2.Resize(g, s, new Size(0, 0), scale, scale, InterpolationFlags.Area);
        else g.CopyTo(s);
        return s;
    }

    /// <summary>ECC afin: şablon (önceki kare) koordinatı → giriş karesi koordinatı, tam ölçekte. Başarısızsa null.</summary>
    private static Mat? Ecc(Mat template, Mat input, double scale)
    {
        var warp = Identity2x3();
        try
        {
            Cv2.FindTransformECC(template, input, warp, MotionTypes.Affine,
                new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 150, 1e-6), null, 5);
        }
        catch (OpenCVException)
        {
            warp.Dispose();
            return null;
        }
        // Akla yatkınlık: ölçek %15'ten, öteleme kare boyutunun %20'sinden fazla olamaz
        double a = warp.At<float>(0, 0), b = warp.At<float>(0, 1), c = warp.At<float>(1, 0), d = warp.At<float>(1, 1);
        double det = a * d - b * c;
        if (det < 0.7 || det > 1.3 || Math.Abs(warp.At<float>(0, 2)) > template.Width * 0.2 || Math.Abs(warp.At<float>(1, 2)) > template.Height * 0.2)
        {
            warp.Dispose();
            return null;
        }
        warp.Set(0, 2, (float)(warp.At<float>(0, 2) / scale));
        warp.Set(1, 2, (float)(warp.At<float>(1, 2) / scale));
        return warp;
    }

    private static Mat Identity2x3()
    {
        var m = new Mat(2, 3, MatType.CV_32F, Scalar.All(0));
        m.Set(0, 0, 1f); m.Set(1, 1, 1f);
        return m;
    }

    /// <summary>x → step(prev(x)): önce prev, sonra step (her ikisi de 2×3 afin).</summary>
    private static Mat Compose(Mat step, Mat prev)
    {
        double[,] A = To3x3(step), B = To3x3(prev);
        var r = new Mat(2, 3, MatType.CV_32F);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 3; j++)
                r.Set(i, j, (float)(A[i, 0] * B[0, j] + A[i, 1] * B[1, j] + A[i, 2] * B[2, j]));
        return r;
    }

    private static double[,] To3x3(Mat m) => new double[,]
    {
        { m.At<float>(0, 0), m.At<float>(0, 1), m.At<float>(0, 2) },
        { m.At<float>(1, 0), m.At<float>(1, 1), m.At<float>(1, 2) },
        { 0, 0, 1 },
    };

    // =====================================================================
    // HDR (pozlama kaynaştırma)
    // =====================================================================

    private StudioOutput Hdr(IReadOnlyList<string> files, StudioOptions o, IProgress<StackProgress>? progress, CancellationToken ct)
    {
        var notes = new List<string>();
        var imgs = new List<Mat>();
        try
        {
            Size size = default;
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new StackProgress(L.T("Kareler okunuyor"), i, files.Count));
                var m = Load(files[i], o.MaxDimension);
                if (imgs.Count > 0 && m.Size() != size) { notes.Add(L.T("{0}: boyut farklı, atlandı", Path.GetFileName(files[i]))); m.Dispose(); continue; }
                size = m.Size();
                imgs.Add(m);
            }
            if (imgs.Count < 2) throw new InvalidOperationException(L.T("Birleştirilebilecek en az iki kare bulunamadı."));

            using var common = new Mat(size, MatType.CV_8UC1, Scalar.All(255));
            // Referans: ortalama parlaklığı ortanca olan kare
            var means = imgs.Select(m => { var v = Cv2.Mean(m); return v.Val0 + v.Val1 + v.Val2; }).ToArray();
            var sorted = means.OrderBy(x => x).ToArray();
            int refIdx = Array.IndexOf(means, sorted[sorted.Length / 2]);
            if (o.Align)
            {
                var alignKey = KeyOf("H", files, o.MaxDimension, imgs.Count);
                Mat?[] homs;
                lock (_lock)
                    homs = _hdrAlign is { } c && c.Key == alignKey ? CloneAll(c.Homs) : null!;
                if (homs == null)
                {
                    homs = new Mat?[imgs.Count];
                    var alignNotes = new List<string>();
                    using var refKey = Keys(imgs[refIdx]);
                    for (int i = 0; i < imgs.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new StackProgress(L.T("Hizalanıyor"), i, imgs.Count));
                        if (i == refIdx) continue;
                        using var key = Keys(imgs[i]);
                        homs[i] = FeatureHomography(refKey, key);
                        if (homs[i] == null) alignNotes.Add(L.T("{0}: hizalanamadı, hizalamasız kullanıldı", Path.GetFileName(files[i])));
                    }
                    lock (_lock)
                    {
                        if (_hdrAlign is { } old) foreach (var w in old.Homs) w?.Dispose();
                        _hdrAlign = (alignKey, refIdx, CloneAll(homs), alignNotes);
                    }
                    notes.AddRange(alignNotes);
                }
                else lock (_lock) notes.AddRange(_hdrAlign!.Value.Notes);
                for (int i = 0; i < imgs.Count; i++)
                {
                    using var h = homs[i];
                    if (h == null) continue;
                    var warped = new Mat();
                    Cv2.WarpPerspective(imgs[i], warped, h, size, InterpolationFlags.Cubic | InterpolationFlags.WarpInverseMap, BorderTypes.Reflect101);
                    using var ones = new Mat(size, MatType.CV_8UC1, Scalar.All(255));
                    using var valid = new Mat();
                    Cv2.WarpPerspective(ones, valid, h, size, InterpolationFlags.Nearest | InterpolationFlags.WarpInverseMap, BorderTypes.Constant, Scalar.All(0));
                    Cv2.Min(common, valid, common);
                    imgs[i].Dispose();
                    imgs[i] = warped;
                }
            }

            // Hayalet giderme: referansla uyuşmayan (hareket eden) bölgelerde diğer karelerin ağırlığı düşürülür
            List<Mat?>? keep = null;
            if (o.Deghost)
            {
                progress?.Report(new StackProgress(L.T("Hareketli nesneler ayıklanıyor"), 0, 1));
                keep = imgs.Select((m, i) => i == refIdx ? null : GhostKeepMask(imgs[refIdx], m)).ToList();
            }

            progress?.Report(new StackProgress(L.T("Pozlamalar kaynaştırılıyor"), imgs.Count, imgs.Count));
            var (wc, ws, we, clip, sat, detail) = o.HdrStyle switch
            {
                HdrStyle.Vivid => (1.0f, 1.5f, 1.0f, 1.6, 1.15, 0.15),
                HdrStyle.Dramatic => (1.5f, 1.3f, 0.8f, 2.8, 1.08, 0.45),
                _ => (1.0f, 1.0f, 1.0f, 0.0, 1.03, 0.0),
            };
            using var fused = Mertens(imgs, wc, ws, we, keep);
            if (keep != null) foreach (var k in keep) k?.Dispose();
            using var stretched = AutoLevels(fused, 0.0005, 0.9995);
            using var styled = LocalContrast(stretched, clip, sat, detail);
            using var cropped = o.CropToCommon ? CropToMask(styled, common, 0.0) : styled.Clone();
            var outp = ToOutput(cropped);
            outp.Used = imgs.Count;
            outp.Notes.AddRange(notes);
            return outp;
        }
        finally { foreach (var m in imgs) m.Dispose(); }
    }

    /// <summary>
    /// Mertens pozlama kaynaştırma (32 bit, ağırlık üsleri ayarlanabilir): ağırlık = kontrast^wc · doygunluk^ws · iyi pozlanmışlık^we,
    /// kareler arasında normalleştirilir ve Laplace piramidinde harmanlanır.
    /// </summary>
    private static Mat Mertens(List<Mat> imgs, double wc, double ws, double we, List<Mat?>? keep = null)
    {
        int n = imgs.Count;
        var size = imgs[0].Size();
        int levels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Min(size.Width, size.Height) / 8.0)), 2, 9);
        var weights = new Mat[n];
        using var sum = new Mat(size, MatType.CV_32FC1, Scalar.All(0));
        for (int i = 0; i < n; i++)
        {
            var img = imgs[i];
            using var gray = new Mat();
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            using var lap = new Mat();
            Cv2.Laplacian(gray, lap, MatType.CV_32F, 3);
            using Mat contrast = Cv2.Abs(lap);
            var ch = Cv2.Split(img);
            using var mean = new Mat();
            Cv2.Add(ch[0], ch[1], mean); Cv2.Add(mean, ch[2], mean);
            mean.ConvertTo(mean, MatType.CV_32F, 1 / 3.0);
            using var sat = new Mat(size, MatType.CV_32FC1, Scalar.All(0));
            using var well = new Mat(size, MatType.CV_32FC1, Scalar.All(1));
            foreach (var c in ch)
            {
                using var d = new Mat();
                Cv2.Subtract(c, mean, d);
                using var d2 = new Mat();
                Cv2.Multiply(d, d, d2);
                Cv2.Add(sat, d2, sat);
                using var e = new Mat();
                c.ConvertTo(e, MatType.CV_32F, 1, -0.5);
                Cv2.Multiply(e, e, e);
                e.ConvertTo(e, MatType.CV_32F, -1 / (2 * 0.2 * 0.2));
                Cv2.Exp(e, e);
                Cv2.Multiply(well, e, well);
                c.Dispose();
            }
            sat.ConvertTo(sat, MatType.CV_32F, 1 / 3.0);
            Cv2.Sqrt(sat, sat);
            var w = new Mat();
            contrast.ConvertTo(w, MatType.CV_32F, 1, 1e-4);
            if (Math.Abs(wc - 1) > 1e-6) Cv2.Pow(w, wc, w);
            using (var s1 = new Mat()) { sat.ConvertTo(s1, MatType.CV_32F, 1, 1e-4); if (Math.Abs(ws - 1) > 1e-6) Cv2.Pow(s1, ws, s1); Cv2.Multiply(w, s1, w); }
            if (Math.Abs(we - 1) > 1e-6) Cv2.Pow(well, we, well);
            Cv2.Multiply(w, well, w);
            if (keep?[i] is { } km) Cv2.Multiply(w, km, w);
            w.ConvertTo(w, MatType.CV_32F, 1, 1e-12);
            Cv2.Add(sum, w, sum);
            weights[i] = w;
        }
        Mat[]? acc = null;
        try
        {
            for (int i = 0; i < n; i++)
            {
                Cv2.Divide(weights[i], sum, weights[i]);
                var gw = new Mat[levels]; var gi = new Mat[levels];
                gw[0] = weights[i]; gi[0] = imgs[i].Clone();
                for (int k = 1; k < levels; k++)
                {
                    gw[k] = new Mat(); Cv2.PyrDown(gw[k - 1], gw[k]);
                    gi[k] = new Mat(); Cv2.PyrDown(gi[k - 1], gi[k]);
                }
                acc ??= Enumerable.Range(0, levels).Select(k => new Mat(gi[k].Size(), MatType.CV_32FC3, Scalar.All(0))).ToArray();
                for (int k = 0; k < levels; k++)
                {
                    using var lap = new Mat();
                    if (k < levels - 1)
                    {
                        using var up = new Mat();
                        Cv2.PyrUp(gi[k + 1], up, gi[k].Size());
                        Cv2.Subtract(gi[k], up, lap);
                    }
                    else gi[k].CopyTo(lap);
                    using var w3 = new Mat();
                    Cv2.Merge([gw[k], gw[k], gw[k]], w3);
                    Cv2.Multiply(lap, w3, w3);
                    Cv2.Add(acc[k], w3, acc[k]);
                }
                for (int k = 0; k < levels; k++) { gi[k].Dispose(); if (k > 0) gw[k].Dispose(); }
            }
            var r = acc![levels - 1].Clone();
            for (int k = levels - 2; k >= 0; k--)
            {
                var up = new Mat();
                Cv2.PyrUp(r, up, acc[k].Size());
                r.Dispose();
                Cv2.Add(up, acc[k], up);
                r = up;
            }
            return r;
        }
        finally
        {
            foreach (var w in weights) w?.Dispose();
            if (acc != null) foreach (var a in acc) a.Dispose();
        }
    }

    private sealed class KeySet(KeyPoint[] points, Mat desc) : IDisposable
    {
        public KeyPoint[] Points { get; } = points;
        public Mat Desc { get; } = desc;
        public double Scale { get; init; }
        public void Dispose() => Desc.Dispose();
    }

    /// <summary>Pozlamadan bağımsız ORB öznitelikleri (histogram eşitlenmiş gri üzerinde).</summary>
    private static KeySet Keys(Mat bgr)
    {
        double scale = Math.Min(1.0, 2000.0 / Math.Max(bgr.Width, bgr.Height));
        using var g = new Mat();
        Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
        using var s = new Mat();
        Cv2.Resize(g, s, new Size(0, 0), scale, scale, InterpolationFlags.Area);
        using var g8 = new Mat();
        s.ConvertTo(g8, MatType.CV_8U, 255);
        Cv2.EqualizeHist(g8, g8);
        using var orb = ORB.Create(5000);
        var desc = new Mat();
        orb.DetectAndCompute(g8, null, out var kp, desc);
        return new KeySet(kp, desc) { Scale = scale };
    }

    /// <summary>Referans koordinatı → kare koordinatı homografisi (tam ölçek). Yetersiz eşleşmede null.</summary>
    private static Mat? FeatureHomography(KeySet reference, KeySet frame)
    {
        if (reference.Desc.Empty() || frame.Desc.Empty()) return null;
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var knn = matcher.KnnMatch(reference.Desc, frame.Desc, 2);
        var src = new List<Point2d>(); var dst = new List<Point2d>();
        foreach (var m in knn)
        {
            if (m.Length < 2 || m[0].Distance > 0.75 * m[1].Distance) continue;
            var a = reference.Points[m[0].QueryIdx].Pt; var b = frame.Points[m[0].TrainIdx].Pt;
            src.Add(new Point2d(a.X / reference.Scale, a.Y / reference.Scale));
            dst.Add(new Point2d(b.X / frame.Scale, b.Y / frame.Scale));
        }
        if (src.Count < 20) return null;
        var h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 3.0);
        if (h.Empty()) { h.Dispose(); return null; }
        return h;
    }

    /// <summary>Kanal ortak alt/üst yüzdeliklerine göre doğrusal germe.</summary>
    private static Mat AutoLevels(Mat img, double lo, double hi)
    {
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(Math.Max(1, gray.Width / 4), Math.Max(1, gray.Height / 4)), 0, 0, InterpolationFlags.Area);
        small.GetArray(out float[] v);
        Array.Sort(v);
        double a = v[(int)(lo * (v.Length - 1))], b = v[(int)(hi * (v.Length - 1))];
        if (b - a < 1e-4) { a = 0; b = 1; }
        var r = new Mat();
        img.ConvertTo(r, MatType.CV_32FC3, 1 / (b - a), -a / (b - a));
        Cv2.Min(r, 1.0, r);
        Cv2.Max(r, 0.0, r);
        return r;
    }

    /// <summary>Lab uzayında görünüm: L kanalında CLAHE (yerel kontrast) ve geniş yarıçaplı netleştirme (ayrıntı), a/b'de doygunluk.</summary>
    private static Mat LocalContrast(Mat bgr, double clip, double saturation, double detail)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var ch = Cv2.Split(lab);
        try
        {
            if (clip > 0)
            {
                using var l16 = new Mat();
                ch[0].ConvertTo(l16, MatType.CV_16U, 655.35);
                using var clahe = Cv2.CreateCLAHE(clip, new Size(8, 8));
                using var eq = new Mat();
                clahe.Apply(l16, eq);
                using var lEq = new Mat();
                eq.ConvertTo(lEq, MatType.CV_32F, 1 / 655.35);
                // CLAHE'yi orijinalle %70 oranında karıştır (aşırı "HDR görünümü"nü önler)
                Cv2.AddWeighted(lEq, 0.7, ch[0], 0.3, 0, ch[0]);
            }
            if (detail > 0)
            {
                using var blur = new Mat();
                Cv2.GaussianBlur(ch[0], blur, new Size(0, 0), Math.Max(4, Math.Min(bgr.Width, bgr.Height) / 120.0));
                Cv2.AddWeighted(ch[0], 1 + detail, blur, -detail, 0, ch[0]);
            }
            if (Math.Abs(saturation - 1) > 1e-3)
            {
                ch[1].ConvertTo(ch[1], MatType.CV_32F, saturation);
                ch[2].ConvertTo(ch[2], MatType.CV_32F, saturation);
            }
            using var merged = new Mat();
            Cv2.Merge(ch, merged);
            var r = new Mat();
            Cv2.CvtColor(merged, r, ColorConversionCodes.Lab2BGR);
            Cv2.Min(r, 1.0, r);
            Cv2.Max(r, 0.0, r);
            return r;
        }
        finally { foreach (var c in ch) c.Dispose(); }
    }

    /// <summary>
    /// Karenin referansla uyuştuğu yerlerde 1, hareket nedeniyle uyuşmadığı yerlerde 0'a yakın maske.
    /// Parlaklık farkı, iki karenin de iyi pozlandığı piksellerden kestirilen kazançla telafi edilir.
    /// </summary>
    private static Mat GhostKeepMask(Mat reference, Mat frame)
    {
        double scale = Math.Min(1.0, 1200.0 / Math.Max(frame.Width, frame.Height));
        using var gr = new Mat(); using var gf = new Mat();
        Cv2.CvtColor(reference, gr, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(frame, gf, ColorConversionCodes.BGR2GRAY);
        using var sr = new Mat(); using var sf = new Mat();
        Cv2.Resize(gr, sr, new Size(0, 0), scale, scale, InterpolationFlags.Area);
        Cv2.Resize(gf, sf, new Size(0, 0), scale, scale, InterpolationFlags.Area);
        // Ortak iyi pozlanmış alan
        using var okR = new Mat(); using var okF = new Mat(); using var ok = new Mat();
        Cv2.InRange(sr, new Scalar(0.06), new Scalar(0.94), okR);
        Cv2.InRange(sf, new Scalar(0.06), new Scalar(0.94), okF);
        Cv2.BitwiseAnd(okR, okF, ok);
        double mr = Cv2.Mean(sr, ok).Val0, mf = Cv2.Mean(sf, ok).Val0;
        double gain = mf > 1e-4 ? mr / mf : 1;
        using var norm = new Mat();
        sf.ConvertTo(norm, MatType.CV_32F, gain);
        using var diff = new Mat();
        Cv2.Absdiff(norm, sr, diff);
        using var bad = new Mat();
        Cv2.Threshold(diff, bad, 0.09, 1, ThresholdTypes.Binary);
        using var badOk = new Mat();
        using var okF32 = new Mat();
        ok.ConvertTo(okF32, MatType.CV_32F, 1 / 255.0);
        Cv2.Multiply(bad, okF32, badOk);
        // Küçük gürültüyü at, hareket bölgesini genişlet ve yumuşat
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(badOk, badOk, MorphTypes.Open, k3);
        int r = Math.Max(3, (int)(Math.Min(sr.Width, sr.Height) / 80.0));
        using var kd = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1));
        Cv2.Dilate(badOk, badOk, kd);
        Cv2.GaussianBlur(badOk, badOk, new Size(0, 0), r);
        using var keepSmall = new Mat();
        badOk.ConvertTo(keepSmall, MatType.CV_32F, -1, 1);
        Cv2.Max(keepSmall, 0.02, keepSmall);
        var keep = new Mat();
        Cv2.Resize(keepSmall, keep, frame.Size(), 0, 0, InterpolationFlags.Linear);
        return keep;
    }

    // =====================================================================
    // Panorama
    // =====================================================================

    private StudioOutput Panorama(IReadOnlyList<string> files, StudioOptions o, IProgress<StackProgress>? progress, CancellationToken ct)
    {
        var key = KeyOf("P", files, o.MaxDimension, o.PanoMode, o.WaveCorrection, o.PanoConfidence.ToString("0.00"));
        Mat pano;
        double focal = 0;
        int used = files.Count;
        lock (_lock)
        {
            if (_pano is { } c && c.Key == key) { pano = c.Pano.Clone(); focal = c.Focal; }
            else pano = null!;
        }
        if (pano == null)
        {
            var imgs = new List<Mat>();
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new StackProgress(L.T("Kareler okunuyor"), i, files.Count));
                    using var f = Load(files[i], o.MaxDimension);
                    var m8 = new Mat();
                    f.ConvertTo(m8, MatType.CV_8UC3, 255);
                    imgs.Add(m8);
                }
                focal = FocalPixels(files[0], imgs[0].Width, imgs[0].Height, o.DefaultFocal35);
                progress?.Report(new StackProgress(L.T("Kareler eşleştiriliyor ve dikiliyor"), 0, 1));
                using var stitcher = Stitcher.Create(o.PanoMode == PanoMode.Flat ? Stitcher.Mode.Scans : Stitcher.Mode.Panorama);
                stitcher.PanoConfidenceThresh = o.PanoConfidence;
                stitcher.WaveCorrection = o.WaveCorrection && o.PanoMode == PanoMode.Spherical;
                stitcher.RegistrationResol = 0.6;
                stitcher.SeamEstimationResol = 0.1;
                stitcher.CompositingResol = -1;
                pano = new Mat();
                var status = stitcher.Stitch(imgs, pano);
                ct.ThrowIfCancellationRequested();
                if (status != Stitcher.Status.OK || pano.Empty())
                {
                    pano.Dispose();
                    throw new InvalidOperationException(status switch
                    {
                        Stitcher.Status.ErrorNeedMoreImgs => L.T("Kareler arasında yeterli örtüşme bulunamadı. Kareler yaklaşık %30 örtüşmeli; güven eşiğini düşürmeyi deneyin."),
                        Stitcher.Status.ErrorHomographyEstFail => L.T("Kareler arasındaki geometri kestirilemedi. Örtüşmeyi artırın ya da Düz yüzey kipini deneyin."),
                        _ => L.T("Kamera açıları çözülemedi. Kareleri döndürerek (kaydırmadan) çekin ya da Düz yüzey kipini deneyin."),
                    });
                }
            }
            finally { foreach (var m in imgs) m.Dispose(); }
            lock (_lock)
            {
                _pano?.Pano.Dispose();
                _pano = (key, pano.Clone(), focal);
            }
        }

        try
        {
            progress?.Report(new StackProgress(L.T("Sonuç oluşturuluyor"), 1, 1));
            using var f32 = new Mat();
            pano.ConvertTo(f32, MatType.CV_32FC3, 1 / 255.0);
            Mat final = f32;
            Mat? cropped = null;
            int cropX = 0, cropY = 0;
            if (o.CropToCommon)
            {
                using var gray = new Mat();
                Cv2.CvtColor(pano, gray, ColorConversionCodes.BGR2GRAY);
                using var mask = new Mat();
                Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.Binary);
                Cv2.Erode(mask, mask, null, iterations: 2);
                cropped = CropToMask(f32, mask, 0.0, out var rect);
                cropX = rect.X; cropY = rect.Y;
                final = cropped;
            }

            // Küresel panorama eşdikdörtgendir: x = f·boylam, y = f·enlem. Tam küre 2πf × πf piksel.
            NexView.Core.Metadata.PanoramaInfo? geo = null;
            if (o.PanoMode == PanoMode.Spherical)
            {
                int fullW = Math.Max(final.Width, (int)Math.Round(2 * Math.PI * focal));
                int fullH = Math.Max(final.Height, (int)Math.Round(Math.PI * focal));
                if (final.Width > fullW * 0.92) fullW = final.Width; // 360°'ye çok yakın: tam tur kabul et
                fullH = Math.Max(fullH, fullW / 2);
                geo = new NexView.Core.Metadata.PanoramaInfo(fullW, fullH, (fullW - final.Width) / 2, (fullH - final.Height) / 2, final.Width, final.Height);
            }

            StudioOutput outp;
            if (geo != null && o.PanoOutput != PanoOutput.Panorama)
            {
                using var planet = LittlePlanet(final, geo, o.PanoOutput == PanoOutput.Tunnel, o.PlanetZoom, o.PlanetRotation);
                outp = ToOutput(planet);
            }
            else
            {
                outp = ToOutput(final);
                outp.Pano = geo;
            }
            cropped?.Dispose();
            outp.Used = used;
            if (o.PanoMode == PanoMode.Flat && o.PanoOutput != PanoOutput.Panorama)
                outp.Notes.Add(L.T("Küçük gezegen ve tünel yalnızca küresel izdüşümde oluşturulabilir."));
            return outp;
        }
        finally { pano.Dispose(); }
    }

    /// <summary>
    /// Stereografik izdüşüm: küçük gezegen (aşağı bakış, zemin ortada) ya da tünel (yukarı bakış, gökyüzü ortada).
    /// zoom: düzlemdeki yarıçap ölçeği (büyüdükçe daha çok ufuk görünür), rotation: derece.
    /// </summary>
    private static Mat LittlePlanet(Mat equirect, NexView.Core.Metadata.PanoramaInfo geo, bool tunnel, double zoom, double rotation)
    {
        int n = Math.Clamp(Math.Max(equirect.Height * 2, Math.Min(equirect.Width, 2400)), 800, 6000);
        var mx = new float[n * n];
        var my = new float[n * n];
        double half = n / 2.0, rot = rotation * Math.PI / 180, k = Math.Clamp(zoom, 0.5, 12);
        bool full = geo.IsFull360;
        int w = equirect.Width, h = equirect.Height;
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                double dx = (x + 0.5 - half) / half, dy = (y + 0.5 - half) / half;
                double r = Math.Sqrt(dx * dx + dy * dy) * k;
                double c = 2 * Math.Atan(r / 2);                         // nadirden (ya da zenitten) açı
                double lat = tunnel ? Math.PI / 2 - c : -Math.PI / 2 + c;
                double lon = Math.Atan2(dx, tunnel ? -dy : dy) + rot;
                double u = (lon / (2 * Math.PI) + 0.5) * geo.FullWidth;
                u %= geo.FullWidth; if (u < 0) u += geo.FullWidth;
                double v = (Math.PI / 2 - lat) / Math.PI * geo.FullHeight;
                double px = u - geo.CropLeft, py = v - geo.CropTop;
                if (full) { px %= w; if (px < 0) px += w; }
                int i = y * n + x;
                if (px < 0 || px > w - 1) { mx[i] = -1; my[i] = -1; continue; }
                mx[i] = (float)px;
                my[i] = (float)Math.Clamp(py, 0, h - 1);                  // üst/alt eksikse kenar uzatılır
            }
        });
        using var mapX = new Mat(n, n, MatType.CV_32FC1);
        using var mapY = new Mat(n, n, MatType.CV_32FC1);
        mapX.SetArray(mx);
        mapY.SetArray(my);
        var dst = new Mat();
        Cv2.Remap(equirect, dst, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.All(0));
        Cv2.Min(dst, 1.0, dst);
        Cv2.Max(dst, 0.0, dst);
        return dst;
    }

    /// <summary>İlk karenin 35 mm eşdeğeri odak uzaklığından piksel cinsinden odak (36 mm = uzun kenar).</summary>
    private static double FocalPixels(string path, int w, int h, double default35)
    {
        double f35 = default35;
        try
        {
            var meta = NexView.Core.Metadata.MetadataService.Read(path);
            var e = meta.Entries.FirstOrDefault(x => x.Name.Contains("Focal", StringComparison.OrdinalIgnoreCase) && x.Name.Contains("35"));
            if (e != null)
            {
                var num = new string(e.Value.TakeWhile(ch => char.IsDigit(ch) || ch == '.' || ch == ',').ToArray()).Replace(',', '.');
                if (double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is > 5 and < 1500) f35 = v;
            }
        }
        catch { }
        return f35 / 36.0 * Math.Max(w, h);
    }

    // =====================================================================
    // Ortak
    // =====================================================================

    /// <summary>16 bit RGB → 32 bit BGR (0..1).</summary>
    private Mat Load(string path, int maxDimension)
    {
        bool cacheable = maxDimension is > 0 and <= CacheMaxDimension;
        if (cacheable)
            lock (_lock)
                if (_frames.TryGetValue((path, maxDimension), out var hit))
                {
                    var f = new Mat();
                    hit.ConvertTo(f, MatType.CV_32FC3, 1 / 65535.0);
                    return f;
                }
        var m = LoadRaw(path, maxDimension);
        if (cacheable)
            lock (_lock)
            {
                if (_frames.Count >= 30) { foreach (var x in _frames.Values) x.Dispose(); _frames.Clear(); }
                var c16 = new Mat();
                m.ConvertTo(c16, MatType.CV_16UC3, 65535);
                _frames[(path, maxDimension)] = c16;
            }
        return m;
    }

    private unsafe Mat LoadRaw(string path, int maxDimension)
    {
        var (rgb, w, h) = load(path, maxDimension);
        using var m16 = new Mat(h, w, MatType.CV_16UC3);
        long step = m16.Step();
        fixed (ushort* p = rgb)
        {
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(p + (long)y * w * 3, (byte*)m16.Data + y * step, step, (long)w * 3 * 2);
        }
        using var bgr = new Mat();
        Cv2.CvtColor(m16, bgr, ColorConversionCodes.RGB2BGR);
        var f = new Mat();
        bgr.ConvertTo(f, MatType.CV_32FC3, 1 / 65535.0);
        return f;
    }

    /// <summary>32 bit BGR (0..1) → 16 bit RGB çıktı.</summary>
    private static unsafe StudioOutput ToOutput(Mat bgr32)
    {
        using var m16 = new Mat();
        bgr32.ConvertTo(m16, MatType.CV_16UC3, 65535);
        using var rgb = new Mat();
        Cv2.CvtColor(m16, rgb, ColorConversionCodes.BGR2RGB);
        int w = rgb.Width, h = rgb.Height;
        var data = new ushort[w * h * 3];
        long step = rgb.Step();
        fixed (ushort* p = data)
        {
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy((byte*)rgb.Data + y * step, p + (long)y * w * 3, (long)w * 3 * 2, (long)w * 3 * 2);
        }
        return new StudioOutput { Rgb = data, Width = w, Height = h };
    }

    /// <summary>Maskenin (0/255) tamamen dolu olduğu en büyük eksen hizalı dikdörtgene kırpar; ek kenar payı oranı margin.</summary>
    private static Mat CropToMask(Mat img, Mat mask8, double margin) => CropToMask(img, mask8, margin, out _);

    private static Mat CropToMask(Mat img, Mat mask8, double margin, out Rect rect)
    {
        rect = new Rect(0, 0, img.Width, img.Height);
        const int S = 4;
        int gw = mask8.Width / S, gh = mask8.Height / S;
        if (gw < 4 || gh < 4) return img.Clone();
        using var small = new Mat();
        Cv2.Resize(mask8, small, new Size(gw, gh), 0, 0, InterpolationFlags.Area);
        small.GetArray(out byte[] v);
        var heights = new int[gw];
        int bestArea = 0, bx = 0, by = 0, bw = gw, bh = gh;
        var stack = new Stack<int>();
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++) heights[x] = v[y * gw + x] >= 254 ? heights[x] + 1 : 0;
            stack.Clear();
            for (int x = 0; x <= gw; x++)
            {
                int hh = x == gw ? 0 : heights[x];
                while (stack.Count > 0 && heights[stack.Peek()] >= hh)
                {
                    int top = stack.Pop();
                    int left = stack.Count == 0 ? 0 : stack.Peek() + 1;
                    int area = heights[top] * (x - left);
                    if (area > bestArea) { bestArea = area; bx = left; bw = x - left; bh = heights[top]; by = y - bh + 1; }
                }
                stack.Push(x);
            }
        }
        if (bestArea == 0 || (bw == gw && bh == gh && margin <= 0)) return img.Clone();
        int mx = (int)(bw * S * margin / 2), my = (int)(bh * S * margin / 2);
        rect = new Rect(bx * S + mx, by * S + my, Math.Max(16, bw * S - 2 * mx), Math.Max(16, bh * S - 2 * my));
        rect = rect.Intersect(new Rect(0, 0, img.Width, img.Height));
        return new Mat(img, rect).Clone();
    }
}
