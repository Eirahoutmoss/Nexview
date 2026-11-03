using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NexView.Core.Caching;
using NexView.Core.Formats;

namespace NexView.App.ViewModels;

public sealed partial class ThumbnailItem : ObservableObject
{
    private int _state; // 0: yok, 1: yükleniyor, 2: bitti

    public ThumbnailItem(string path, bool isFavorite)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Format = FormatRegistry.FromPath(path);
        _isFavorite = isFavorite;
    }

    public string Path { get; }
    public string FileName { get; }
    public ImageFormat? Format { get; }
    public bool IsRaw => Format?.IsRaw == true;
    public string Badge => Format?.Id ?? System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _thumbnailFailed;

    public async void EnsureThumbnail(ThumbnailCache cache)
    {
        if (_state != 0) return;
        _state = 1;
        try
        {
            var bytes = await cache.GetAsync(Path, Format, CancellationToken.None);
            if (bytes != null)
            {
                Thumbnail = await Task.Run(() =>
                {
                    using var ms = new MemoryStream(bytes);
                    return new Bitmap(ms);
                });
            }
            else ThumbnailFailed = true;
        }
        catch
        {
            ThumbnailFailed = true;
        }
        finally
        {
            _state = 2;
        }
    }

    public void Release()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}

public sealed record InfoRow(string Label, string Value);
