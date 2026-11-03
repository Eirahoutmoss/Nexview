using NexView.Core.Formats;

namespace NexView.Core.Conversion;

/// <summary>"Dışa aktar: ... için" hazır ayarları (plan §33–34 + Kurum/Resmi belge + Paylaşmaya hazırla).</summary>
public sealed record ConversionPreset(string Id, string Name, string Description, Action<ConversionOptions> Apply);

public static class Presets
{
    public static IReadOnlyList<ConversionPreset> All { get; } =
    [
        new("custom", "Özel", "Tüm ayarları kendiniz belirleyin.", _ => { }),
        new("share", "Paylaşmaya hazırla", "JPEG, uzun kenar 2048 px, konum ve cihaz bilgisi silinir. WhatsApp ve e-posta için.", o =>
        {
            Reset(o, FormatRegistry.Jpeg, 85);
            o.ResizeMode = ResizeMode.LongEdge; o.ResizeValue = 2048;
            o.Metadata = MetadataPolicy.RemoveAll;
        }),
        new("official", "Kurum / Resmi belge", "JPEG, en fazla 1 MB, uzun kenar 2480 px, metadata temizlenir. e-Devlet ve kurum başvuruları için.", o =>
        {
            Reset(o, FormatRegistry.Jpeg, 88);
            o.ResizeMode = ResizeMode.LongEdge; o.ResizeValue = 2480;
            o.Metadata = MetadataPolicy.RemoveAll;
            o.TargetSizeBytes = 1024 * 1024;
        }),
        new("web", "Web", "WebP, uzun kenar 1920 px, konum silinir.", o =>
        {
            Reset(o, FormatRegistry.WebP, 82);
            o.ResizeMode = ResizeMode.LongEdge; o.ResizeValue = 1920;
            o.Metadata = MetadataPolicy.RemoveGps;
        }),
        new("social", "Sosyal medya", "JPEG, uzun kenar 2048 px, konum silinir.", o =>
        {
            Reset(o, FormatRegistry.Jpeg, 88);
            o.ResizeMode = ResizeMode.LongEdge; o.ResizeValue = 2048;
            o.Metadata = MetadataPolicy.RemoveGps;
        }),
        new("email", "E-posta", "JPEG, uzun kenar 1600 px, dosya başına en fazla 500 KB.", o =>
        {
            Reset(o, FormatRegistry.Jpeg, 82);
            o.ResizeMode = ResizeMode.LongEdge; o.ResizeValue = 1600;
            o.Metadata = MetadataPolicy.RemoveGps;
            o.TargetSizeBytes = 500 * 1024;
        }),
        new("print", "Baskı", "Yüksek kaliteli JPEG (%95, 4:4:4), orijinal boyut, metadata korunur.", o =>
        {
            Reset(o, FormatRegistry.Jpeg, 95);
            o.HighChroma = true;
        }),
        new("archive", "Arşiv", "Kayıpsız TIFF (LZW), orijinal boyut, tüm metadata korunur.", o =>
        {
            Reset(o, FormatRegistry.Tiff, 100);
            o.TiffCompression = "LZW";
        }),
    ];

    /// <summary>Komut satırı hedefini çözer: hazır ayar kimliği (share, official…) ya da format (jpeg, png, pdf…).</summary>
    public static ConversionOptions? Resolve(string target)
    {
        var o = new ConversionOptions();
        var preset = All.FirstOrDefault(p => p.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (preset != null) { preset.Apply(o); return o; }
        var fmt = FormatRegistry.FromId(target) ?? FormatRegistry.FromExtension(target)
                  ?? (target.Equals("pdf", StringComparison.OrdinalIgnoreCase) ? FormatRegistry.Pdf : null);
        if (fmt == null) return null;
        o.Target = fmt;
        o.Quality = 90;
        return o;
    }

    private static void Reset(ConversionOptions o, ImageFormat target, int quality)
    {
        var d = new ConversionOptions();
        o.Target = target; o.Quality = quality; o.Lossless = false;
        o.ResizeMode = ResizeMode.None; o.ResizeValue = d.ResizeValue; o.ShrinkOnly = true;
        o.Metadata = MetadataPolicy.Keep; o.TargetSizeBytes = null;
        o.Progressive = false; o.HighChroma = false; o.PngCompression = d.PngCompression; o.TiffCompression = d.TiffCompression; o.BitDepth = null;
    }
}
