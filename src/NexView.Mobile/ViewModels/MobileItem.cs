using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NexView.Core.Caching;
using NexView.Core.Formats;

namespace NexView.Mobile.ViewModels;

/// <summary>Oturumdaki bir görüntü (yerel kopya ya da gerçek dosya yolu).</summary>
public sealed partial class MobileItem : ObservableObject
{
    private int _state;

    public MobileItem(string path, string? displayName = null)
    {
        Path = path;
        DisplayName = displayName ?? System.IO.Path.GetFileName(path);
        Format = FormatRegistry.FromPath(path);
    }

    public string Path { get; }
    public string DisplayName { get; }
    public ImageFormat? Format { get; }
    public string Badge => Format?.Id ?? System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public long SizeBytes => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _failed;

    public async void EnsureThumbnail(ThumbnailCache cache)
    {
        if (_state != 0) return;
        _state = 1;
        try
        {
            var bytes = await cache.GetAsync(Path, Format, CancellationToken.None);
            if (bytes == null) { Failed = true; return; }
            Thumbnail = await Task.Run(() => { using var ms = new MemoryStream(bytes); return new Bitmap(ms); });
        }
        catch { Failed = true; }
        finally { _state = 2; }
    }
}

public sealed record InfoLine(string Label, string Value);

public sealed partial class Choice(string label, object value, Action<Choice> onPick) : ObservableObject
{
    public string Label { get; } = label;
    public object Value { get; } = value;
    [ObservableProperty] private bool _isOn;
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void Pick() => onPick(this);
}

/// <summary>Tek seçimli "çip" grubu.</summary>
public sealed class ChoiceGroup
{
    public System.Collections.ObjectModel.ObservableCollection<Choice> Items { get; } = new();
    public event Action? Changed;
    public object? Value { get; private set; }

    public ChoiceGroup Add(string label, object value) { Items.Add(new Choice(label, value, OnPick)); return this; }
    private void OnPick(Choice c) { Select(c.Value); Changed?.Invoke(); }
    public void Select(object? value)
    {
        Value = value;
        foreach (var c in Items) c.IsOn = Equals(c.Value, value);
    }
}
