using System.Text.Json;

namespace NexView.Core.Editing;

/// <summary>Normalleştirilmiş (0..1) dikdörtgen.</summary>
public sealed record NRect(double X, double Y, double W, double H)
{
    public static readonly NRect Full = new(0, 0, 1, 1);
    public bool IsFull => X <= 1e-6 && Y <= 1e-6 && W >= 1 - 1e-6 && H >= 1 - 1e-6;
    public NRect Normalize()
    {
        double x = Math.Clamp(Math.Min(X, X + W), 0, 1), y = Math.Clamp(Math.Min(Y, Y + H), 0, 1);
        double r = Math.Clamp(Math.Max(X, X + W), 0, 1), b = Math.Clamp(Math.Max(Y, Y + H), 0, 1);
        return new NRect(x, y, r - x, b - y);
    }
}

public sealed record NPoint(double X, double Y);

public enum AnnotationKind { Arrow, Rectangle, Ellipse, Text, Highlight, Blur, Pixelate, BlackBox }
public enum DocumentFilter { None, Enhance, Gray, BlackWhite }

/// <summary>İşaretleme öğesi. Koordinatlar kırpma sonrası görüntüye göre normalleştirilmiştir.</summary>
public sealed record Annotation(AnnotationKind Kind, double X1, double Y1, double X2, double Y2, string Color = "#FF3B30", string? Text = null, double Size = 1.0)
{
    public bool IsRedaction => Kind is AnnotationKind.Blur or AnnotationKind.Pixelate or AnnotationKind.BlackBox;
    public NRect Bounds => new NRect(X1, Y1, X2 - X1, Y2 - Y1).Normalize();
}

/// <summary>
/// Tahribatsız düzenleme tarifi. Uygulama sırası:
/// belge perspektifi → 90° döndürme → çevirme → ince düzeltme → kırpma → ton ayarları → gizleme → işaretler.
/// </summary>
public sealed class EditRecipe
{
    // Geometri
    public NPoint[]? DocumentQuad { get; set; }          // sol üst, sağ üst, sağ alt, sol alt (yönlendirilmiş orijinalde)
    public DocumentFilter DocumentFilter { get; set; }
    public int Rotation { get; set; }                    // 0, 90, 180, 270
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public double Straighten { get; set; }               // derece, -45..45
    public NRect Crop { get; set; } = NRect.Full;

    // Ton
    public double LevelsBlack { get; set; }              // 0..254
    public double LevelsWhite { get; set; } = 255;       // 1..255
    public double Exposure { get; set; }                 // EV, -2..2
    public double Brightness { get; set; }               // -100..100
    public double Contrast { get; set; }                 // -100..100
    public double Saturation { get; set; }               // -100..100
    public double Temperature { get; set; }              // -100..100
    public double Tint { get; set; }                     // -100..100
    public double Sharpness { get; set; }                // 0..100
    public bool Grayscale { get; set; }

    public List<Annotation> Annotations { get; set; } = new();

    public bool HasGeometry => DocumentQuad != null || Rotation % 360 != 0 || FlipH || FlipV || Math.Abs(Straighten) > 0.01 || !Crop.IsFull;
    public bool HasTone => LevelsBlack > 0 || LevelsWhite < 255 || Exposure != 0 || Brightness != 0 || Contrast != 0 || Saturation != 0 ||
                           Temperature != 0 || Tint != 0 || Sharpness > 0 || Grayscale || DocumentFilter != DocumentFilter.None;
    public bool IsIdentity => !HasGeometry && !HasTone && Annotations.Count == 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public EditRecipe Clone() => JsonSerializer.Deserialize<EditRecipe>(JsonSerializer.Serialize(this, Json), Json)!;
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
