using NexView.Core.Formats;

namespace NexView.Core.Stacking;

public enum StackMode { StarTrails, Mean, Median, SigmaClip }

public sealed class StackOptions
{
    public StackMode Mode { get; set; } = StackMode.SigmaClip;
    public bool Align { get; set; } = true;
    public ImageFormat Output { get; set; } = FormatRegistry.Tiff;
    public int Quality { get; set; } = 95;
    public double SigmaKappa { get; set; } = 2.5;
    /// <summary>Referans kare dizini (null: ortadaki kare).</summary>
    public int? ReferenceIndex { get; set; }
}

public sealed record StackProgress(string Stage, int Done, int Total);

public sealed class StackResult
{
    public string? Output { get; set; }
    public int Used { get; set; }
    public List<string> Skipped { get; } = new();
    public TimeSpan Elapsed { get; set; }
}

public interface IStacker
{
    StackResult Run(IReadOnlyList<string> files, string outputPath, StackOptions options, IProgress<StackProgress>? progress, CancellationToken ct);
}

public readonly record struct Star(double X, double Y, double Flux);

/// <summary>Benzerlik dönüşümü: x' = A·x − B·y + Tx, y' = B·x + A·y + Ty.</summary>
public readonly record struct Similarity(double A, double B, double Tx, double Ty, int Inliers)
{
    public (double X, double Y) Apply(double x, double y) => (A * x - B * y + Tx, B * x + A * y + Ty);
    public double Scale => Math.Sqrt(A * A + B * B);
    public double AngleDegrees => Math.Atan2(B, A) * 180 / Math.PI;
}
