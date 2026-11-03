using NexView.Core.Caching;
using NexView.Core.Conversion;
using NexView.Core.Editing;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Library;
using NexView.Core.Stacking;

namespace NexView.Mobile.Services;

/// <summary>Mobil platform hizmetleri (Android etkinliği ya da masaüstü önizlemesi uygular).</summary>
public interface IMobilePlatform
{
    string Name { get; }
    /// <summary>Uygulamanın geçici çalışma klasörü.</summary>
    string CacheDir { get; }
    /// <summary>Dosyayı galeriye (Resimler/NexView) ya da İndirilenler'e (PDF) kaydeder; kullanıcıya gösterilecek konumu döndürür.</summary>
    Task<string?> SaveToGalleryAsync(string path, string displayName, string mimeType);
    Task ShareAsync(IReadOnlyList<string> paths);
    /// <summary>Kamerayla fotoğraf çeker; yerel dosya yolu ya da null.</summary>
    Task<string?> CapturePhotoAsync();
    bool CanCapture { get; }
    void OpenUri(string uri);
    /// <summary>İndirilen uygulama paketini (APK) kurar; gerekirse kullanıcıdan izin ister.</summary>
    Task InstallPackageAsync(string path);
}

/// <summary>Mobil uygulamanın bileşim kökü: codec'ler platforma göre başlıkta takılır.</summary>
public sealed class MobileServices
{
    public required IMobilePlatform Platform { get; init; }
    public required CodecManager Codecs { get; init; }
    public required IImageConverter Converter { get; init; }
    public IImageEditor? Editor { get; init; }
    public IStacker? Stacker { get; init; }
    public string EditionLabel { get; init; } = "";
    public AppSettings Settings { get; } = AppSettings.Load();
    public UserLibrary Library { get; } = UserLibrary.Load();

    private ThumbnailCache? _thumbs;
    public ThumbnailCache Thumbnails => _thumbs ??= new ThumbnailCache(Codecs);

    /// <summary>Başlık tarafından uygulama başlamadan önce atanır.</summary>
    public static Func<MobileServices>? Factory { get; set; }

    /// <summary>Paylaşım niyetiyle gelen ve uygulama hazır olmadan bekleyen dosyalar.</summary>
    public static List<string> PendingShared { get; } = new();
    public static event Action<IReadOnlyList<string>>? SharedReceived;
    public static void Receive(IReadOnlyList<string> files)
    {
        if (SharedReceived != null) SharedReceived(files);
        else lock (PendingShared) PendingShared.AddRange(files);
    }
}
