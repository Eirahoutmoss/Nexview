namespace NexView.Core.Formats;

public enum FormatFamily { Raster, Heif, Raw, Layered, Hdr }

/// <summary>Plan §9 format durumları. Gerçek değer yüklü codec'lerin bildirdiği kapasiteye göre hesaplanır.</summary>
public enum FormatSupport { Unsupported, Partial, ViewOnly, ReadOnly, ReadWrite }

public sealed record ImageFormat(
    string Id,
    string DisplayName,
    FormatFamily Family,
    string MimeType,
    string[] Extensions,
    bool MayHaveAlpha = false,
    bool MayAnimate = false)
{
    public bool IsRaw => Family == FormatFamily.Raw;
    public string PrimaryExtension => Extensions[0];
}
