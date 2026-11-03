using ImageMagick;
using ImageMagick.Drawing;
using NexView.Core.Conversion;
using NexView.Core.Editing;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Localization;

namespace NexView.Codecs.Magick;

public sealed class MagickEditor : IImageEditor
{
    public IEditSession Open(string path, int previewMaxDimension, CancellationToken ct) => new MagickEditSession(path, previewMaxDimension, ct);
}

internal sealed class MagickEditSession : IEditSession
{
    private readonly MagickImage _preview;

    public MagickEditSession(string path, int previewMax, CancellationToken ct)
    {
        SourcePath = path;
        using var full = MagickHelpers.ReadOriented(path, FormatDetector.Detect(path).Format);
        ct.ThrowIfCancellationRequested();
        SourceWidth = (int)full.Width;
        SourceHeight = (int)full.Height;
        MagickHelpers.NormalizeColor(full);
        _preview = (MagickImage)full.Clone();
        _preview.Resize(new MagickGeometry((uint)previewMax, (uint)previewMax) { Greater = true });
        _preview.Depth = 8;
    }

    public string SourcePath { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public DecodedImage RenderPreview(EditRecipe recipe, PreviewStage stage, bool includeTone, CancellationToken ct)
    {
        using var img = (MagickImage)_preview.Clone();
        ApplyGeometry(img, recipe, stage);
        ct.ThrowIfCancellationRequested();
        var (px, w, h) = ToBgra(img);
        if (includeTone)
        {
            ToneProcessor.Apply(px, w, h, recipe);
            if (stage == PreviewStage.Full)
                foreach (var a in recipe.Annotations.Where(a => a.IsRedaction)) ToneProcessor.Redact(px, w, h, a);
        }
        return new DecodedImage(w, h, w * 4, px, w, h);
    }

    public ConversionResult Save(EditRecipe recipe, string outputPath, ImageFormat format, int quality, bool keepMetadata, CancellationToken ct)
    {
        var srcBytes = new FileInfo(SourcePath).Length;
        using var full = MagickHelpers.ReadOriented(SourcePath, FormatDetector.Detect(SourcePath).Format);
        var exif = keepMetadata ? full.GetExifProfile() : null;
        MagickHelpers.NormalizeColor(full);
        ApplyGeometry(full, recipe, PreviewStage.Full);
        ct.ThrowIfCancellationRequested();

        var (px, w, h) = ToBgra(full);
        ToneProcessor.Apply(px, w, h, recipe);
        foreach (var a in recipe.Annotations.Where(a => a.IsRedaction)) ToneProcessor.Redact(px, w, h, a);
        ct.ThrowIfCancellationRequested();

        using var result = new MagickImage();
        result.ReadPixels(px, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
        result.ColorSpace = ColorSpace.sRGB;
        DrawAnnotations(result, recipe.Annotations.Where(a => !a.IsRedaction).ToList());

        if (!ConversionOptions.SupportsAlpha(format) || IsOpaque(px))
        {
            result.BackgroundColor = MagickColors.White;
            result.Alpha(AlphaOption.Remove);
        }
        result.Depth = 8;
        if (exif != null)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            result.SetProfile(exif);
        }
        result.Orientation = OrientationType.TopLeft;

        var bytes = MagickConverter.Encode(result, new ConversionOptions { Target = format, Quality = quality }, quality);
        var final = OutputPaths.Unique(outputPath);
        MagickConverter.WriteSafely(bytes, final);
        return new ConversionResult(SourcePath, final, ConversionStatus.Success, null, srcBytes, bytes.Length, w, h);
    }

    public void Dispose() => _preview.Dispose();

    // ---------- Geometri ----------

    internal static void ApplyGeometry(MagickImage img, EditRecipe r, PreviewStage stage)
    {
        if (stage == PreviewStage.BeforeDocument) return;
        if (r.DocumentQuad is { Length: 4 } q) Perspective(img, q);
        var rot = ((r.Rotation % 360) + 360) % 360;
        if (rot != 0) img.Rotate(rot);
        if (r.FlipH) img.Flop();
        if (r.FlipV) img.Flip();
        if (Math.Abs(r.Straighten) > 0.01) Straighten(img, r.Straighten);
        if (stage == PreviewStage.Full && !r.Crop.IsFull)
        {
            var c = r.Crop.Normalize();
            double W = img.Width, H = img.Height;
            var g = new MagickGeometry((int)Math.Round(c.X * W), (int)Math.Round(c.Y * H),
                (uint)Math.Max(1, Math.Round(c.W * W)), (uint)Math.Max(1, Math.Round(c.H * H)));
            img.Crop(g);
            img.ResetPage();
        }
    }

    private static void Perspective(MagickImage img, NPoint[] q)
    {
        double W = img.Width, H = img.Height;
        var p = q.Select(pt => (X: Math.Clamp(pt.X, 0, 1) * W, Y: Math.Clamp(pt.Y, 0, 1) * H)).ToArray();
        static double D((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        double dw = Math.Max(D(p[0], p[1]), D(p[3], p[2]));
        double dh = Math.Max(D(p[0], p[3]), D(p[1], p[2]));
        dw = Math.Max(16, Math.Round(dw));
        dh = Math.Max(16, Math.Round(dh));
        img.VirtualPixelMethod = VirtualPixelMethod.Edge;
        img.SetArtifact("distort:viewport", $"{dw}x{dh}+0+0");
        img.Distort(DistortMethod.Perspective,
        [
            p[0].X, p[0].Y, 0, 0,
            p[1].X, p[1].Y, dw, 0,
            p[2].X, p[2].Y, dw, dh,
            p[3].X, p[3].Y, 0, dh,
        ]);
        img.RemoveArtifact("distort:viewport");
        img.ResetPage();
    }

    private static void Straighten(MagickImage img, double degrees)
    {
        double w = img.Width, h = img.Height, a = Math.Abs(degrees) * Math.PI / 180;
        img.BackgroundColor = MagickColors.Black;
        img.Rotate(degrees);
        img.ResetPage();
        double k = Math.Min(w / (w * Math.Cos(a) + h * Math.Sin(a)), h / (w * Math.Sin(a) + h * Math.Cos(a)));
        img.Crop((uint)Math.Max(1, Math.Floor(w * k)), (uint)Math.Max(1, Math.Floor(h * k)), Gravity.Center);
        img.ResetPage();
    }

    private static (byte[] Px, int W, int H) ToBgra(MagickImage img)
    {
        if (!img.HasAlpha) img.Alpha(AlphaOption.Opaque);
        using var pixels = img.GetPixelsUnsafe();
        var px = pixels.ToByteArray("BGRA") ?? throw new InvalidDataException(L.T("Piksel verisi alınamadı."));
        return (px, (int)img.Width, (int)img.Height);
    }

    private static bool IsOpaque(byte[] px)
    {
        for (int i = 3; i < px.Length; i += 16) if (px[i] < 255) return false;
        return true;
    }

    // ---------- İşaretler ----------

    /// <summary>Çizgi kalınlığı ve yazı boyutu görüntü köşegenine göre ölçeklenir (önizleme ile aynı formül).</summary>
    public static double StrokeWidth(double w, double h, double size) => Math.Max(2, Math.Sqrt(w * w + h * h) * 0.004 * size);
    public static double FontSize(double w, double h, double size) => Math.Max(12, Math.Sqrt(w * w + h * h) * 0.028 * size);

    private static readonly string TextFont = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
    }.FirstOrDefault(File.Exists) ?? "Arial";

    private static void DrawAnnotations(MagickImage img, IReadOnlyList<Annotation> list)
    {
        if (list.Count == 0) return;
        double W = img.Width, H = img.Height;
        foreach (var a in list)
        {
            var color = new MagickColor(a.Color);
            double x1 = a.X1 * W, y1 = a.Y1 * H, x2 = a.X2 * W, y2 = a.Y2 * H;
            double sw = StrokeWidth(W, H, a.Size);
            var d = new Drawables();
            switch (a.Kind)
            {
                case AnnotationKind.Arrow:
                {
                    double ang = Math.Atan2(y2 - y1, x2 - x1), head = sw * 4.5;
                    double bx = x2 - Math.Cos(ang) * head * 0.8, by = y2 - Math.Sin(ang) * head * 0.8;
                    d.StrokeColor(color).StrokeWidth(sw).StrokeLineCap(LineCap.Round).FillColor(MagickColors.Transparent).Line(x1, y1, bx, by);
                    d.StrokeColor(MagickColors.Transparent).FillColor(color).Polygon(
                        new PointD(x2, y2),
                        new PointD(x2 - Math.Cos(ang - 0.45) * head, y2 - Math.Sin(ang - 0.45) * head),
                        new PointD(x2 - Math.Cos(ang + 0.45) * head, y2 - Math.Sin(ang + 0.45) * head));
                    break;
                }
                case AnnotationKind.Rectangle:
                    d.StrokeColor(color).StrokeWidth(sw).FillColor(MagickColors.Transparent)
                     .Rectangle(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
                    break;
                case AnnotationKind.Ellipse:
                    d.StrokeColor(color).StrokeWidth(sw).FillColor(MagickColors.Transparent)
                     .Ellipse((x1 + x2) / 2, (y1 + y2) / 2, Math.Abs(x2 - x1) / 2, Math.Abs(y2 - y1) / 2, 0, 360);
                    break;
                case AnnotationKind.Highlight:
                {
                    var c = new MagickColor(color) { A = (ushort)(Quantum.Max * 0.38) };
                    d.StrokeColor(MagickColors.Transparent).FillColor(c)
                     .Rectangle(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
                    break;
                }
                case AnnotationKind.Text when !string.IsNullOrWhiteSpace(a.Text):
                {
                    double fs = FontSize(W, H, a.Size);
                    double off = Math.Max(1, fs / 18);
                    new Drawables().Font(TextFont).FontPointSize(fs).StrokeColor(MagickColors.Transparent)
                        .FillColor(new MagickColor("#00000099")).Text(x1 + off, y1 + fs + off, a.Text!).Draw(img);
                    d.Font(TextFont).FontPointSize(fs).StrokeColor(MagickColors.Transparent).FillColor(color)
                     .Text(x1, y1 + fs, a.Text!);
                    break;
                }
                default:
                    continue;
            }
            d.Draw(img);
        }
    }
}
