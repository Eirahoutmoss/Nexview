using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;

namespace NexView.Core.Imaging;

/// <summary>Codec'leri öncelik sırasıyla dener; birincil başarısız olursa sonlu sayıda alternatife geçer (plan §27).</summary>
public sealed class CodecManager
{
    private readonly List<IImageCodec> _codecs = new();

    public IReadOnlyList<IImageCodec> Codecs => _codecs;

    public void Register(IImageCodec codec)
    {
        _codecs.Add(codec);
        _codecs.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    public FormatSupport GetSupport(ImageFormat format)
    {
        bool read = _codecs.Any(c => SafeCan(() => c.CanRead(format)));
        bool write = _codecs.Any(c => SafeCan(() => c.CanWrite(format)));
        return read && write ? FormatSupport.ReadWrite : read ? FormatSupport.ReadOnly : FormatSupport.Unsupported;
    }

    public bool CanWrite(ImageFormat format) => _codecs.Any(c => SafeCan(() => c.CanWrite(format)));

    private List<IImageCodec> Candidates(ImageFormat? format) =>
        format == null ? _codecs.ToList() : _codecs.Where(c => SafeCan(() => c.CanRead(format))).ToList();

    public DecodeResult Decode(string path, DetectionResult detection, DecodeOptions options, CancellationToken ct)
    {
        var format = detection.Format;
        var candidates = Candidates(format);
        if (options.SkipPrimary && candidates.Count > 1) candidates.RemoveAt(0);
        if (candidates.Count == 0)
            throw new DecodeException(format, L.T("Bu format için yüklü bir çözücü (decoder) bulunmuyor."), []);

        var attempts = new List<string>();
        foreach (var codec in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = codec.Decode(path, format, options, ct);
                result.UsedFallback = attempts.Count > 0 || options.SkipPrimary;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                attempts.Add($"{codec.Name}: {ex.Message}");
                Log.Warn($"Çözümleme başarısız ({codec.Name}): {path}", ex);
            }
        }
        throw new DecodeException(format,
            L.T("Gerekli çözücü görüntüyü okuyamadı. Dosya bozuk olabilir veya NexView bu varyasyonu henüz desteklemiyor."),
            attempts);
    }

    public DecodeResult? TryDecodePreview(string path, ImageFormat? format, int maxDimension, CancellationToken ct)
    {
        foreach (var codec in Candidates(format))
        {
            try
            {
                var r = codec.TryDecodePreview(path, format, maxDimension, ct);
                if (r != null) return r;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug($"Önizleme alınamadı ({codec.Name}): {ex.Message}"); }
        }
        return null;
    }

    public byte[]? CreateThumbnail(string path, ImageFormat? format, int maxSize, CancellationToken ct)
    {
        foreach (var codec in Candidates(format))
        {
            ct.ThrowIfCancellationRequested();
            try { return codec.CreateThumbnail(path, format, maxSize, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug($"Küçük resim oluşturulamadı ({codec.Name}) {path}: {ex.Message}"); }
        }
        return null;
    }

    public IReadOnlyList<AnimationFrame>? DecodeFrames(string path, ImageFormat? format, int maxDimension, long maxBytes, CancellationToken ct)
    {
        foreach (var codec in Candidates(format).OfType<IAnimationDecoder>())
        {
            try { return codec.DecodeFrames(path, format, maxDimension, maxBytes, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug($"Animasyon okunamadı: {ex.Message}"); }
        }
        return null;
    }

    private static bool SafeCan(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }
}
