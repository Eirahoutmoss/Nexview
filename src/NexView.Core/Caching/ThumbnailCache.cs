using System.Security.Cryptography;
using System.Text;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;

namespace NexView.Core.Caching;

/// <summary>Boyut sınırlı, silinebilir, bozulursa kendini yeniden oluşturan disk küçük resim önbelleği.</summary>
public sealed class ThumbnailCache
{
    public const int ThumbSize = 256;
    private const string Version = "v1";

    private readonly CodecManager _codecs;
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    public ThumbnailCache(CodecManager codecs, string? directory = null)
    {
        _codecs = codecs;
        _dir = directory ?? AppPaths.ThumbnailCacheDir;
        Directory.CreateDirectory(_dir);
    }

    public async Task<byte[]?> GetAsync(string path, ImageFormat? format, CancellationToken ct)
    {
        string file;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            file = CacheFile($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{ThumbSize}|{Version}");
            if (File.Exists(file))
            {
                var cached = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                if (cached.Length > 16) return cached;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"Önbellek okunamadı: {ex.Message}");
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = await Task.Run(() => _codecs.CreateThumbnail(path, format, ThumbSize, ct), ct).ConfigureAwait(false);
            if (bytes == null) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, file, overwrite: true);
            }
            catch (Exception ex) { Log.Debug($"Önbelleğe yazılamadı: {ex.Message}"); }
            return bytes;
        }
        finally
        {
            _gate.Release();
        }
    }

    public long GetSizeBytes() =>
        Directory.Exists(_dir) ? new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    public void Clear()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); Directory.CreateDirectory(_dir); }
        catch (Exception ex) { Log.Warn("Önbellek temizlenemedi", ex); }
    }

    /// <summary>En eski erişilen dosyalardan başlayarak önbelleği sınıra indirir.</summary>
    public void Trim(long maxBytes)
    {
        try
        {
            var files = new DirectoryInfo(_dir).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.LastAccessTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= maxBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch { }
            }
        }
        catch (Exception ex) { Log.Debug($"Önbellek kırpılamadı: {ex.Message}"); }
    }

    private string CacheFile(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_dir, hash[..2], hash + ".thumb");
    }
}
