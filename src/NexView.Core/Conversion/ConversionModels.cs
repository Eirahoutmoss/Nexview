using NexView.Core.Formats;

namespace NexView.Core.Conversion;

public enum ResizeMode { None, LongEdge, ShortEdge, Width, Height, Percent }
public enum MetadataPolicy { Keep, RemoveGps, RemoveAll }
public enum ConflictPolicy { Rename, Skip }
public enum PdfPageMode { ImageSize, A4 }
public enum ConversionStatus { Success, Failed, Skipped }

public sealed class ConversionOptions
{
    public ImageFormat Target { get; set; } = FormatRegistry.Jpeg;
    public int Quality { get; set; } = 90;
    public bool Lossless { get; set; }
    public ResizeMode ResizeMode { get; set; } = ResizeMode.None;
    public int ResizeValue { get; set; } = 1920;
    public bool ShrinkOnly { get; set; } = true;
    public MetadataPolicy Metadata { get; set; } = MetadataPolicy.Keep;
    /// <summary>Bayt cinsinden üst sınır; null ise sınırsız.</summary>
    public long? TargetSizeBytes { get; set; }
    public bool Progressive { get; set; }
    public bool HighChroma { get; set; }
    public int PngCompression { get; set; } = 6;
    public string TiffCompression { get; set; } = "LZW";
    public int? BitDepth { get; set; }
    public PdfPageMode PdfPage { get; set; } = PdfPageMode.A4;

    public ConversionOptions Clone() => (ConversionOptions)MemberwiseClone();

    public static bool IsLossy(ImageFormat f) => f.Id is "JPEG" or "WEBP" or "AVIF" or "HEIC" or "HEIF" or "JXL" or "JP2" or "PDF";
    public static bool SupportsLossless(ImageFormat f) => f.Id is "WEBP" or "AVIF" or "JXL" or "JP2";
    public static bool SupportsAlpha(ImageFormat f) => f.Id is not ("JPEG" or "BMP" or "PDF");
}

public sealed class OutputOptions
{
    /// <summary>null: kaynakla aynı klasör.</summary>
    public string? Folder { get; set; }
    /// <summary>Hedef klasör içinde oluşturulacak alt klasör (ör. "NexView"); boşsa kullanılmaz.</summary>
    public string? Subfolder { get; set; }
    public string Suffix { get; set; } = "";
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;
}

public sealed record ConversionResult(
    string Source,
    string? Output,
    ConversionStatus Status,
    string? Message = null,
    long SourceBytes = 0,
    long OutputBytes = 0,
    int Width = 0,
    int Height = 0,
    bool TargetSizeMissed = false);

/// <summary>Kodlayıcı arayüzü. Uygulaması codec eklentisindedir (ör. Magick).</summary>
public interface IImageConverter
{
    bool CanWrite(ImageFormat format);
    ConversionResult Convert(string source, ConversionOptions options, OutputOptions output, CancellationToken ct);
    ConversionResult CreatePdf(IReadOnlyList<string> sources, string outputPath, ConversionOptions options, IProgress<int>? progress, CancellationToken ct);
    /// <summary>Tam çözünürlükte, yönlendirilmiş PNG (pano ve yazdırma için).</summary>
    byte[] RenderPng(string source, int maxDimension, CancellationToken ct);
    /// <summary>Windows panosu (CF_DIB) için 24 bit BMP dosya baytları.</summary>
    byte[] RenderBmp(string source, int maxDimension, CancellationToken ct);
    /// <summary>Herhangi bir görüntü baytını (BMP/PNG/DIB) PNG dosyasına kaydeder.</summary>
    void SaveAsPng(byte[] imageBytes, string outputPath);
    /// <summary>Bellekteki BGRA görüntüyü kodlar ve kaydeder (çakışmada benzersiz ad). metadataSource verilirse EXIF kopyalanır.</summary>
    ConversionResult SaveImage(Imaging.DecodedImage image, string outputPath, ConversionOptions options, string? metadataSource, CancellationToken ct);
}
