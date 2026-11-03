using CommunityToolkit.Mvvm.ComponentModel;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Localization;

namespace NexView.Mobile.ViewModels;

/// <summary>Mobil dönüştürme seçenekleri: hazır ayar, format, kalite, boyut, metadata, dosya sınırı.</summary>
public sealed partial class MobileConvertViewModel : ObservableObject
{
    private bool _applying;

    public MobileConvertViewModel(IImageConverter converter, string presetId, string targetId, int quality)
    {
        foreach (var p in Presets.All) Preset.Add(L.T(p.Name), p.Id);
        ImageFormat[] candidates = [FormatRegistry.Jpeg, FormatRegistry.Png, FormatRegistry.WebP, FormatRegistry.Avif,
            FormatRegistry.Heic, FormatRegistry.Jxl, FormatRegistry.Tiff, FormatRegistry.Bmp, FormatRegistry.Gif, FormatRegistry.Pdf];
        foreach (var f in candidates.Where(converter.CanWrite)) Format.Add(f.Id == "WEBP" ? "WebP" : f.Id, f);
        Size.Add(L.T("Orijinal"), 0).Add("4096", 4096).Add("2560", 2560).Add("2048", 2048).Add("1600", 1600).Add("1080", 1080).Add("800", 800);
        Meta.Add(L.T("Koru"), MetadataPolicy.Keep).Add(L.T("Konumu sil"), MetadataPolicy.RemoveGps).Add(L.T("Tümünü sil"), MetadataPolicy.RemoveAll);
        Limit.Add(L.T("Sınırsız"), 0L).Add("200 KB", 200L * 1024).Add("500 KB", 500L * 1024).Add("1 MB", 1024L * 1024).Add("2 MB", 2048L * 1024);

        Preset.Changed += () => ApplyPreset(Preset.Value as string ?? "custom");
        Format.Changed += OnCustomized;
        Size.Changed += OnCustomized;
        Meta.Changed += OnCustomized;
        Limit.Changed += OnCustomized;

        var custom = new ConversionOptions
        {
            Target = Format.Items.Select(c => (ImageFormat)c.Value).FirstOrDefault(f => f.Id == targetId) ?? FormatRegistry.Jpeg,
            Quality = Math.Clamp(quality, 10, 100),
        };
        Load(custom);
        if (presetId != "custom" && Presets.All.Any(p => p.Id == presetId)) ApplyPreset(presetId);
        else Preset.Select("custom");
    }

    public ChoiceGroup Preset { get; } = new();
    public ChoiceGroup Format { get; } = new();
    public ChoiceGroup Size { get; } = new();
    public ChoiceGroup Meta { get; } = new();
    public ChoiceGroup Limit { get; } = new();

    [ObservableProperty] private int _quality = 90;
    [ObservableProperty] private string _presetDescription = "";
    [ObservableProperty] private bool _showQuality = true;

    public string PresetId => Preset.Value as string ?? "custom";
    public ImageFormat Target => Format.Value as ImageFormat ?? FormatRegistry.Jpeg;

    partial void OnQualityChanged(int value) => OnCustomized();

    private void ApplyPreset(string id)
    {
        var p = Presets.All.FirstOrDefault(x => x.Id == id);
        Preset.Select(id);
        PresetDescription = p == null ? "" : L.T(p.Description);
        if (p == null || id == "custom") return;
        var o = new ConversionOptions();
        p.Apply(o);
        if (!Format.Items.Any(c => Equals(c.Value, o.Target))) o.Target = FormatRegistry.Jpeg;
        Load(o);
    }

    private void Load(ConversionOptions o)
    {
        _applying = true;
        try
        {
            Format.Select(o.Target);
            Quality = o.Quality;
            var size = o.ResizeMode == ResizeMode.LongEdge ? o.ResizeValue : 0;
            if (!Size.Items.Any(c => Equals(c.Value, size))) Size.Add(size.ToString(), size);
            Size.Select(size);
            Meta.Select(o.Metadata);
            Limit.Select(o.TargetSizeBytes ?? 0L);
            ShowQuality = ConversionOptions.IsLossy(o.Target);
        }
        finally { _applying = false; }
    }

    private void OnCustomized()
    {
        ShowQuality = ConversionOptions.IsLossy(Target);
        if (_applying || PresetId == "custom") return;
        Preset.Select("custom");
        PresetDescription = L.T(Presets.All[0].Description);
    }

    public ConversionOptions Build()
    {
        var size = Size.Value is int s ? s : 0;
        var limit = Limit.Value is long l ? l : 0;
        var o = new ConversionOptions
        {
            Target = Target,
            Quality = Quality,
            ResizeMode = size > 0 ? ResizeMode.LongEdge : ResizeMode.None,
            ResizeValue = size > 0 ? size : 1920,
            Metadata = Meta.Value is MetadataPolicy m ? m : MetadataPolicy.Keep,
            TargetSizeBytes = limit > 0 ? limit : null,
        };
        if (PresetId == "print") o.HighChroma = true;
        return o;
    }
}
