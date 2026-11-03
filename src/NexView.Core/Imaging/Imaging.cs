using NexView.Core.Formats;

namespace NexView.Core.Imaging;

/// <summary>Ekrana hazır piksel tamponu: BGRA 8 bit, ön çarpımsız alfa.</summary>
public sealed class DecodedImage(int width, int height, int stride, byte[] pixels, int originalWidth, int originalHeight)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public byte[] Pixels { get; } = pixels;
    public int OriginalWidth { get; } = originalWidth;
    public int OriginalHeight { get; } = originalHeight;
    public bool IsDownsampled => Width != OriginalWidth || Height != OriginalHeight;
}

public sealed class ImageProperties
{
    public string CodecFormat { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    public int Channels { get; init; }
    public string ColorSpace { get; init; } = "";
    public bool HasAlpha { get; init; }
    public string? IccProfile { get; init; }
    public string? Compression { get; init; }
    public int FrameCount { get; init; } = 1;
    public double DpiX { get; init; }
    public double DpiY { get; init; }
    public string? DensityUnit { get; init; }
}

public sealed record DecodeOptions(int MaxDimension = 8192, bool SkipPrimary = false, bool AutoStretch = false);

public sealed record AnimationFrame(DecodedImage Image, int DelayMs);

/// <summary>Animasyonlu formatlar için isteğe bağlı yetenek.</summary>
public interface IAnimationDecoder
{
    /// <summary>Kareleri birleştirilmiş (coalesced) olarak döndürür; maxBytes aşılırsa null.</summary>
    IReadOnlyList<AnimationFrame>? DecodeFrames(string path, ImageFormat? format, int maxDimension, long maxBytes, CancellationToken ct);
}

public sealed class DecodeResult(DecodedImage image, ImageProperties properties, string decoderName)
{
    public DecodedImage Image { get; } = image;
    public ImageProperties Properties { get; } = properties;
    public string DecoderName { get; } = decoderName;
    public bool UsedFallback { get; set; }
    public bool IsPreview { get; init; }
}

/// <summary>Bir codec eklentisi. Yeni format desteği UI'a dokunmadan yeni bir IImageCodec ile eklenir.</summary>
public interface IImageCodec
{
    string Name { get; }
    /// <summary>Küçük değer önce denenir.</summary>
    int Priority { get; }
    bool CanRead(ImageFormat format);
    bool CanWrite(ImageFormat format);
    DecodeResult Decode(string path, ImageFormat? format, DecodeOptions options, CancellationToken ct);
    /// <summary>Hızlı önizleme (ör. RAW içindeki gömülü JPEG). Yoksa null.</summary>
    DecodeResult? TryDecodePreview(string path, ImageFormat? format, int maxDimension, CancellationToken ct);
    /// <summary>JPEG ya da PNG baytları döndürür.</summary>
    byte[] CreateThumbnail(string path, ImageFormat? format, int maxSize, CancellationToken ct);
}

public sealed class DecodeException(ImageFormat? format, string userReason, IReadOnlyList<string> attempts)
    : Exception(userReason + (attempts.Count > 0 ? " | " + string.Join(" | ", attempts) : ""))
{
    public ImageFormat? Format { get; } = format;
    public string UserReason { get; } = userReason;
    public IReadOnlyList<string> Attempts { get; } = attempts;
}
