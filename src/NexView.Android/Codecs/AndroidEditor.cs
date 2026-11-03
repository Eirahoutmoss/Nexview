using Android.Graphics;
using NexView.Core.Conversion;
using NexView.Core.Editing;
using NexView.Core.Formats;
using ImageFormat = NexView.Core.Formats.ImageFormat;
using NexView.Core.Imaging;
using NexView.Core.Localization;

namespace NexView.Droid.Codecs;

/// <summary>Android düzenleyicisi: geometri ve ton saf C# (EditGeometry, ToneProcessor), işaretler Android Canvas ile çizilir.</summary>
public sealed class AndroidEditor : IImageEditor
{
    /// <summary>Kaydetmede bellek sınırı (≈ 24 MP); daha büyük görüntüler bu boyuta küçültülür.</summary>
    public const long SaveMaxPixels = 24L * 1024 * 1024;

    public IEditSession Open(string path, int previewMaxDimension, CancellationToken ct) => new AndroidEditSession(path, previewMaxDimension);
}

internal sealed class AndroidEditSession : IEditSession
{
    private readonly DecodedImage _preview;
    private readonly ImageFormat? _format;

    public AndroidEditSession(string path, int previewMax)
    {
        SourcePath = path;
        _format = FormatDetector.Detect(path).Format;
        var d = AndroidImaging.Decode(path, _format, (w, h) => AndroidImaging.Fit(w, h, previewMax));
        using var bmp = d.Bitmap;
        _preview = AndroidImaging.ToDecodedImage(bmp, d.OriginalWidth, d.OriginalHeight);
        bmp.Recycle();
        SourceWidth = d.OriginalWidth;
        SourceHeight = d.OriginalHeight;
    }

    public string SourcePath { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    private static DecodedImage Writable(DecodedImage src, DecodedImage result) =>
        ReferenceEquals(src, result) ? new DecodedImage(src.Width, src.Height, src.Stride, (byte[])src.Pixels.Clone(), src.Width, src.Height) : result;

    public DecodedImage RenderPreview(EditRecipe recipe, PreviewStage stage, bool includeTone, CancellationToken ct)
    {
        var img = Writable(_preview, EditGeometry.Apply(_preview, recipe, stage));
        ct.ThrowIfCancellationRequested();
        if (includeTone)
        {
            ToneProcessor.Apply(img.Pixels, img.Width, img.Height, recipe);
            if (stage == PreviewStage.Full)
                foreach (var a in recipe.Annotations.Where(a => a.IsRedaction)) ToneProcessor.Redact(img.Pixels, img.Width, img.Height, a);
        }
        return img;
    }

    public ConversionResult Save(EditRecipe recipe, string outputPath, ImageFormat format, int quality, bool keepMetadata, CancellationToken ct)
    {
        var srcBytes = new FileInfo(SourcePath).Length;
        var d = AndroidImaging.Decode(SourcePath, _format, (w, h) =>
        {
            var px = (double)w * h;
            if (px <= AndroidEditor.SaveMaxPixels) return null;
            var k = Math.Sqrt(AndroidEditor.SaveMaxPixels / px);
            return ((int)(w * k), (int)(h * k));
        });
        DecodedImage img;
        using (var src = d.Bitmap)
        {
            img = AndroidImaging.ToDecodedImage(src, d.OriginalWidth, d.OriginalHeight);
            src.Recycle();
        }
        ct.ThrowIfCancellationRequested();
        img = Writable(img, EditGeometry.Apply(img, recipe, PreviewStage.Full));
        ToneProcessor.Apply(img.Pixels, img.Width, img.Height, recipe);
        foreach (var a in recipe.Annotations.Where(a => a.IsRedaction)) ToneProcessor.Redact(img.Pixels, img.Width, img.Height, a);
        ct.ThrowIfCancellationRequested();

        var bmp = AndroidImaging.FromDecoded(img);
        img = null!;
        DrawAnnotations(bmp, recipe.Annotations.Where(a => !a.IsRedaction).ToList());
        if (format.Id == "JPEG" && bmp.HasAlpha) bmp = AndroidConverter.Flatten(bmp);

        var cf = format.Id switch
        {
            "PNG" => Bitmap.CompressFormat.Png!,
#pragma warning disable CA1422
            "WEBP" => Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R ? Bitmap.CompressFormat.WebpLossy! : Bitmap.CompressFormat.Webp!,
#pragma warning restore CA1422
            _ => Bitmap.CompressFormat.Jpeg!,
        };
        var bytes = AndroidImaging.Compress(bmp, cf, quality);
        var final = OutputPaths.Unique(outputPath);
        var tmp = final + ".part";
        File.WriteAllBytes(tmp, bytes);
        if (keepMetadata) AndroidConverter.CopyExif(SourcePath, tmp, false, bmp.Width, bmp.Height);
        File.Move(tmp, final, true);
        var result = new ConversionResult(SourcePath, final, ConversionStatus.Success, null, srcBytes, new FileInfo(final).Length, bmp.Width, bmp.Height);
        bmp.Recycle();
        return result;
    }

    public void Dispose() { }

    // ---------- İşaretler (MagickEditor ile aynı ölçüler) ----------

    private static void DrawAnnotations(Bitmap bmp, IReadOnlyList<Annotation> list)
    {
        if (list.Count == 0) return;
        double W = bmp.Width, H = bmp.Height, diag = Math.Sqrt(W * W + H * H);
        using var canvas = new Canvas(bmp);
        foreach (var a in list)
        {
            var color = Color.ParseColor(a.Color.Length == 7 ? a.Color : "#FF3B30");
            float x1 = (float)(a.X1 * W), y1 = (float)(a.Y1 * H), x2 = (float)(a.X2 * W), y2 = (float)(a.Y2 * H);
            float sw = (float)Math.Max(2, diag * 0.004 * a.Size);
            using var stroke = new Paint(PaintFlags.AntiAlias) { Color = color, StrokeWidth = sw, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
            stroke.SetStyle(Paint.Style.Stroke);
            using var fill = new Paint(PaintFlags.AntiAlias) { Color = color };
            fill.SetStyle(Paint.Style.Fill);
            var box = new RectF(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            switch (a.Kind)
            {
                case AnnotationKind.Arrow:
                {
                    double ang = Math.Atan2(y2 - y1, x2 - x1), head = sw * 4.5;
                    float bx = (float)(x2 - Math.Cos(ang) * head * 0.8), by = (float)(y2 - Math.Sin(ang) * head * 0.8);
                    canvas.DrawLine(x1, y1, bx, by, stroke);
                    using var path = new Android.Graphics.Path();
                    path.MoveTo(x2, y2);
                    path.LineTo((float)(x2 - Math.Cos(ang - 0.45) * head), (float)(y2 - Math.Sin(ang - 0.45) * head));
                    path.LineTo((float)(x2 - Math.Cos(ang + 0.45) * head), (float)(y2 - Math.Sin(ang + 0.45) * head));
                    path.Close();
                    canvas.DrawPath(path, fill);
                    break;
                }
                case AnnotationKind.Rectangle: canvas.DrawRect(box, stroke); break;
                case AnnotationKind.Ellipse: canvas.DrawOval(box, stroke); break;
                case AnnotationKind.Highlight:
                    fill.Alpha = (int)(255 * 0.38);
                    canvas.DrawRect(box, fill);
                    break;
                case AnnotationKind.Text when !string.IsNullOrWhiteSpace(a.Text):
                {
                    float fs = (float)Math.Max(12, diag * 0.028 * a.Size);
                    float off = Math.Max(1, fs / 18);
                    using var tp = new Paint(PaintFlags.AntiAlias) { TextSize = fs, Color = Color.Argb(0x99, 0, 0, 0) };
                    tp.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Bold));
                    canvas.DrawText(a.Text!, x1 + off, y1 + fs + off, tp);
                    tp.Color = color;
                    canvas.DrawText(a.Text!, x1, y1 + fs, tp);
                    break;
                }
            }
        }
    }
}
