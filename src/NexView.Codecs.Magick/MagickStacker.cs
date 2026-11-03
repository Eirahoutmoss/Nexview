using System.Diagnostics;
using ImageMagick;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Core.Stacking;

namespace NexView.Codecs.Magick;

/// <summary>
/// Basit astro stack: kareleri (isteğe bağlı) yıldızlara göre hizalar ve birleştirir.
/// Yıldız izi: en parlak piksel. Ortalama/medyan/sigma kırpma: gürültü azaltma.
/// Tam kalibrasyon (dark/flat/bias) kapsam dışıdır.
/// </summary>
public sealed class MagickStacker : IStacker
{
    private const int DetectMax = 1600;

    public StackResult Run(IReadOnlyList<string> files, string outputPath, StackOptions o, IProgress<StackProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new StackResult();
        if (files.Count < 2) throw new InvalidOperationException(L.T("Stack için en az iki kare gerekir."));

        int refIndex = Math.Clamp(o.ReferenceIndex ?? files.Count / 2, 0, files.Count - 1);
        int W, H;
        IExifProfile? exif;
        List<Star>? refStars = null;
        double detectScale;
        using (var reference = Read(files[refIndex]))
        {
            W = (int)reference.Width; H = (int)reference.Height;
            exif = reference.GetExifProfile();
            detectScale = Math.Min(1.0, (double)DetectMax / Math.Max(W, H));
            if (o.Align) refStars = DetectStars(reference, detectScale);
        }
        if (o.Align && (refStars == null || refStars.Count < 6))
            throw new InvalidOperationException(L.T("Referans karede yeterli yıldız bulunamadı. Hizalamayı kapatıp yeniden deneyin."));

        int n = W * H * 3;
        float[]? sum = null; ushort[]? count = null; ushort[]? max = null;
        var temps = new List<string>();
        if (o.Mode == StackMode.StarTrails) max = new ushort[n];
        else if (o.Mode == StackMode.Mean) { sum = new float[n]; count = new ushort[W * H]; }

        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new StackProgress(o.Align ? L.T("Hizalanıyor ve ekleniyor") : L.T("Ekleniyor"), i, files.Count));
                var name = Path.GetFileName(files[i]);
                ushort[] px;
                try
                {
                    using var img = Read(files[i]);
                    if (img.Width != W || img.Height != H) { result.Skipped.Add(L.T("{0}: boyut farklı", name)); continue; }
                    if (o.Align && i != refIndex)
                    {
                        var stars = DetectStars(img, detectScale);
                        var t = StarAligner.Estimate(refStars!, stars);
                        if (t is not { } s) { result.Skipped.Add(L.T("{0}: hizalanamadı", name)); continue; }
                        Apply(img, s, detectScale, W, H);
                    }
                    px = ToRgb16(img);
                }
                catch (MagickException ex)
                {
                    Log.Warn($"Stack karesi okunamadı: {files[i]}", ex);
                    result.Skipped.Add(L.T("{0}: okunamadı", name));
                    continue;
                }

                switch (o.Mode)
                {
                    case StackMode.StarTrails:
                        for (int k = 0; k < n; k++) if (px[k] > max![k]) max[k] = px[k];
                        break;
                    case StackMode.Mean:
                        Parallel.For(0, H, y =>
                        {
                            for (int x = 0; x < W; x++)
                            {
                                int p = y * W + x, k = p * 3;
                                if (px[k] == 0 && px[k + 1] == 0 && px[k + 2] == 0) continue; // hizalama dışı alan
                                sum![k] += px[k]; sum[k + 1] += px[k + 1]; sum[k + 2] += px[k + 2];
                                count![p]++;
                            }
                        });
                        break;
                    default:
                        var tmp = Path.Combine(Path.GetTempPath(), $"nexview-stack-{Guid.NewGuid():N}.raw");
                        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                            fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()));
                        temps.Add(tmp);
                        break;
                }
                result.Used++;
            }

            if (result.Used < 2) throw new InvalidOperationException(L.T("Birleştirilebilecek en az iki kare bulunamadı."));
            progress?.Report(new StackProgress(L.T("Birleştiriliyor"), files.Count, files.Count));

            ushort[] output = o.Mode switch
            {
                StackMode.StarTrails => max!,
                StackMode.Mean => FinishMean(sum!, count!, W, H),
                _ => CombineFromDisk(temps, W, H, o, ct),
            };

            using var outImg = new MagickImage();
            outImg.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(output.AsSpan()).ToArray(),
                new PixelReadSettings((uint)W, (uint)H, StorageType.Short, PixelMapping.RGB));
            outImg.ColorSpace = ColorSpace.sRGB;
            if (exif != null) { exif.SetValue(ExifTag.Orientation, (ushort)1); outImg.SetProfile(exif); }
            outImg.Orientation = OrientationType.TopLeft;
            if (o.Output.Id is "JPEG" or "WEBP") outImg.Depth = 8; else outImg.Depth = 16;
            var bytes = MagickConverter.Encode(outImg, new ConversionOptions { Target = o.Output, Quality = o.Quality, TiffCompression = "ZIP" }, o.Quality);
            var final = OutputPaths.Unique(outputPath);
            MagickConverter.WriteSafely(bytes, final);
            result.Output = final;
        }
        finally
        {
            foreach (var t in temps) { try { File.Delete(t); } catch { } }
        }
        result.Elapsed = sw.Elapsed;
        return result;
    }

    private static MagickImage Read(string path)
    {
        var img = MagickHelpers.ReadOriented(path, FormatDetector.Detect(path).Format);
        MagickHelpers.NormalizeColor(img);
        if (img.HasAlpha) img.Alpha(AlphaOption.Off);
        return img;
    }

    private static List<Star> DetectStars(MagickImage img, double scale)
    {
        using var small = (MagickImage)img.Clone();
        if (scale < 1) small.Resize(new Percentage(scale * 100));
        small.Grayscale();
        int w = (int)small.Width, h = (int)small.Height;
        using var pixels = small.GetPixelsUnsafe();
        var raw = pixels.ToArray()!;
        int ch = (int)small.ChannelCount;
        var lum = new float[w * h];
        for (int i = 0; i < lum.Length; i++) lum[i] = raw[i * ch] / (float)Quantum.Max;
        return StarAligner.DetectStars(lum, w, h);
    }

    private static void Apply(MagickImage img, Similarity s, double scale, int W, int H)
    {
        // Küçük ölçekteki dönüşümü tam çözünürlüğe taşı: öteleme ölçekle büyür, dönme/ölçek aynı kalır
        double tx = s.Tx / scale, ty = s.Ty / scale;
        img.VirtualPixelMethod = VirtualPixelMethod.Black;
        img.SetArtifact("distort:viewport", $"{W}x{H}+0+0");
        img.Distort(DistortMethod.AffineProjection, [s.A, s.B, -s.B, s.A, tx, ty]);
        img.RemoveArtifact("distort:viewport");
        img.ResetPage();
    }

    private static ushort[] ToRgb16(MagickImage img)
    {
        using var pixels = img.GetPixelsUnsafe();
        var raw = pixels.ToShortArray("RGB") ?? throw new InvalidDataException("Piksel verisi alınamadı.");
        return raw;
    }

    private static ushort[] FinishMean(float[] sum, ushort[] count, int W, int H)
    {
        var o = new ushort[sum.Length];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int p = y * W + x, k = p * 3, c = count[p];
                if (c == 0) continue;
                o[k] = (ushort)Math.Min(65535, sum[k] / c);
                o[k + 1] = (ushort)Math.Min(65535, sum[k + 1] / c);
                o[k + 2] = (ushort)Math.Min(65535, sum[k + 2] / c);
            }
        });
        return o;
    }

    /// <summary>Medyan / sigma kırpmalı ortalama: kareleri diskten şerit şerit okuyarak bellek kullanımını sınırlar.</summary>
    private static ushort[] CombineFromDisk(List<string> temps, int W, int H, StackOptions o, CancellationToken ct)
    {
        int frames = temps.Count;
        var output = new ushort[W * H * 3];
        int rowBytes = W * 3 * 2;
        int strip = Math.Max(1, (int)Math.Min(H, 256L * 1024 * 1024 / ((long)rowBytes * frames)));
        var streams = temps.Select(t => new FileStream(t, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)).ToArray();
        try
        {
            for (int y0 = 0; y0 < H; y0 += strip)
            {
                ct.ThrowIfCancellationRequested();
                int rows = Math.Min(strip, H - y0);
                int count = rows * W * 3;
                var bufs = new ushort[frames][];
                for (int f = 0; f < frames; f++)
                {
                    bufs[f] = new ushort[count];
                    streams[f].Position = (long)y0 * rowBytes;
                    streams[f].ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(bufs[f].AsSpan()));
                }
                Parallel.For(0, rows * W, () => (Vals: new double[frames], Idx: new int[frames]), (p, _, st) =>
                {
                    var (vals, idx) = st;
                    int k = p * 3;
                    int valid = 0;
                    for (int f = 0; f < frames; f++)
                        if (bufs[f][k] != 0 || bufs[f][k + 1] != 0 || bufs[f][k + 2] != 0) idx[valid++] = f;
                    if (valid == 0) return st;
                    for (int c = 0; c < 3; c++)
                    {
                        for (int v = 0; v < valid; v++) vals[v] = bufs[idx[v]][k + c];
                        double r = o.Mode == StackMode.Median ? Median(vals, valid) : SigmaClip(vals, valid, o.SigmaKappa);
                        output[(long)y0 * W * 3 + k + c] = (ushort)Math.Clamp(r, 0, 65535);
                    }
                    return st;
                }, _ => { });
            }
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
        return output;
    }

    private static double Median(double[] v, int n)
    {
        Array.Sort(v, 0, n);
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
    }

    private static double SigmaClip(double[] v, int n, double kappa)
    {
        int lo = 0, hi = n;
        Array.Sort(v, 0, n);
        for (int iter = 0; iter < 3 && hi - lo > 2; iter++)
        {
            double mean = 0;
            for (int i = lo; i < hi; i++) mean += v[i];
            mean /= hi - lo;
            double var = 0;
            for (int i = lo; i < hi; i++) var += (v[i] - mean) * (v[i] - mean);
            double sd = Math.Sqrt(var / (hi - lo));
            if (sd < 1e-9) break;
            int nlo = lo, nhi = hi;
            while (nlo < nhi && v[nlo] < mean - kappa * sd) nlo++;
            while (nhi > nlo && v[nhi - 1] > mean + kappa * sd) nhi--;
            if (nlo == lo && nhi == hi) break;
            lo = nlo; hi = nhi;
        }
        double s = 0;
        for (int i = lo; i < hi; i++) s += v[i];
        return hi > lo ? s / (hi - lo) : v[n / 2];
    }
}
