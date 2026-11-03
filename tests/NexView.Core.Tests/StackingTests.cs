using ImageMagick;
using NexView.Codecs.Magick;
using NexView.Core.Formats;
using NexView.Core.Stacking;

namespace NexView.Core.Tests;

public class StackingTests
{
    private static readonly (double X, double Y, double B)[] Field = MakeField();

    private static (double, double, double)[] MakeField()
    {
        var rnd = new Random(7);
        return Enumerable.Range(0, 90).Select(_ => (rnd.NextDouble() * 760 + 20, rnd.NextDouble() * 560 + 20, 0.3 + rnd.NextDouble() * 0.7)).ToArray();
    }

    /// <summary>Gürültülü yıldız alanı üretir; kare, verilen açı ve ötelemeyle kaydırılmıştır.</summary>
    private static string MakeFrame(string dir, int i, double angleDeg, double dx, double dy, int seed)
    {
        int w = 800, h = 600;
        var px = new float[w * h * 3];
        var rnd = new Random(seed);
        double a = angleDeg * Math.PI / 180, cx = w / 2.0, cy = h / 2.0;
        foreach (var (sx, sy, b) in Field)
        {
            double x = Math.Cos(a) * (sx - cx) - Math.Sin(a) * (sy - cy) + cx + dx;
            double y = Math.Sin(a) * (sx - cx) + Math.Cos(a) * (sy - cy) + cy + dy;
            for (int yy = (int)y - 4; yy <= (int)y + 4; yy++)
            for (int xx = (int)x - 4; xx <= (int)x + 4; xx++)
            {
                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                double v = b * Math.Exp(-((xx - x) * (xx - x) + (yy - y) * (yy - y)) / 2.5);
                int k = (yy * w + xx) * 3;
                for (int c = 0; c < 3; c++) px[k + c] += (float)v;
            }
        }
        for (int k = 0; k < px.Length; k++) px[k] = (float)Math.Clamp(px[k] + 0.08 + (rnd.NextDouble() - 0.5) * 0.08, 0, 1);
        var p = Path.Combine(dir, $"kare{i:00}.tif");
        using var img = new MagickImage();
        img.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray(), new PixelReadSettings((uint)w, (uint)h, StorageType.Float, PixelMapping.RGB));
        img.Depth = 16;
        img.Write(p, MagickFormat.Tiff);
        return p;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "nexview-stack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void AlignerRecoversRotationAndShift()
    {
        var dir = TempDir();
        var refP = MakeFrame(dir, 0, 0, 0, 0, 1);
        var movP = MakeFrame(dir, 1, 1.5, 12, -7, 2);
        static List<Star> Stars(string p)
        {
            using var img = new MagickImage(p);
            img.Grayscale();
            int w = (int)img.Width, h = (int)img.Height;
            using var pix = img.GetPixelsUnsafe();
            var raw = pix.ToArray()!;
            int ch = (int)img.ChannelCount;
            var lum = new float[w * h];
            for (int i = 0; i < lum.Length; i++) lum[i] = raw[i * ch] / (float)Quantum.Max;
            return StarAligner.DetectStars(lum, w, h);
        }
        var s = StarAligner.Estimate(Stars(refP), Stars(movP));
        Assert.NotNull(s);
        Assert.InRange(s!.Value.AngleDegrees, -1.7, -1.3);
        Assert.InRange(s.Value.Scale, 0.99, 1.01);
    }

    [Theory]
    [InlineData(StackMode.Mean)]
    [InlineData(StackMode.Median)]
    [InlineData(StackMode.SigmaClip)]
    [InlineData(StackMode.StarTrails)]
    public void StacksAlignedFrames(StackMode mode)
    {
        var dir = TempDir();
        var files = Enumerable.Range(0, 5).Select(i => MakeFrame(dir, i, i * 0.3, i * 4, -i * 3, 100 + i)).ToList();
        var r = new MagickStacker().Run(files, Path.Combine(dir, "stack.tif"),
            new StackOptions { Mode = mode, Align = mode != StackMode.StarTrails, Output = FormatRegistry.Tiff }, null, CancellationToken.None);
        Assert.Equal(5, r.Used);
        Assert.True(File.Exists(r.Output));
        using var img = new MagickImage(r.Output!);
        Assert.Equal(800u, img.Width);
    }

    [Fact]
    public void MeanStackReducesNoise()
    {
        var dir = TempDir();
        var files = Enumerable.Range(0, 6).Select(i => MakeFrame(dir, i, 0, 0, 0, 200 + i)).ToList();
        var r = new MagickStacker().Run(files, Path.Combine(dir, "mean.tif"),
            new StackOptions { Mode = StackMode.Mean, Align = false }, null, CancellationToken.None);
        static double NoiseStd(string p)
        {
            using var img = new MagickImage(p);
            using var crop = (MagickImage)img.Clone();
            crop.Crop(new MagickGeometry(0, 0, 14, 14));   // köşe: yıldız alanı 16 pikselden başlar
            return crop.Statistics().GetChannel(PixelChannel.Red)!.StandardDeviation;
        }
        Assert.True(NoiseStd(r.Output!) < NoiseStd(files[0]) * 0.7);
    }
}
