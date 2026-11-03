using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Imaging;

namespace NexView.Core.Editing;

public enum PreviewStage { Full, BeforeCrop, BeforeDocument }

/// <summary>Bir dosya için açık düzenleme oturumu (önizleme boyutunda çalışma kopyası tutar).</summary>
public interface IEditSession : IDisposable
{
    string SourcePath { get; }
    int SourceWidth { get; }
    int SourceHeight { get; }
    /// <summary>Tarifi önizleme çözünürlüğünde uygular. includeTone=false ise yalnızca geometri (karşılaştırma için).</summary>
    DecodedImage RenderPreview(EditRecipe recipe, PreviewStage stage, bool includeTone, CancellationToken ct);
    /// <summary>Tam çözünürlükte uygular ve kaydeder. Orijinalin üzerine yazmaz.</summary>
    ConversionResult Save(EditRecipe recipe, string outputPath, ImageFormat format, int quality, bool keepMetadata, CancellationToken ct);
}

public interface IImageEditor
{
    IEditSession Open(string path, int previewMaxDimension, CancellationToken ct);
}
