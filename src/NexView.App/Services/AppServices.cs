using NexView.Codecs.Magick;
using NexView.Core.Caching;
using NexView.Core.Conversion;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Library;

namespace NexView.App.Services;

/// <summary>Uygulama genelindeki servisler (basit bileşim kökü).</summary>
public sealed class AppServices
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public UserLibrary Library { get; } = UserLibrary.Load();
    public CodecManager Codecs { get; } = new();
    public ThumbnailCache Thumbnails { get; }
    public IImageConverter Converter { get; } = new MagickConverter();
    public Core.Editing.IImageEditor Editor { get; } = new MagickEditor();

    public AppServices()
    {
        Codecs.Register(new MagickCodec());
        Codecs.Register(new MagickCodec(alternative: true));
        Thumbnails = new ThumbnailCache(Codecs);
        Task.Run(() => Thumbnails.Trim(Settings.ThumbnailCacheMaxMB * 1024L * 1024L));
    }

    public void Shutdown()
    {
        Settings.Save();
        Library.Save();
        Log.Info("NexView kapandı.");
    }
}
