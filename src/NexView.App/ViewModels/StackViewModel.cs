using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.App.Services;
using NexView.Codecs.Magick;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Core.Stacking;

namespace NexView.App.ViewModels;

public sealed record StackModeItem(StackMode Mode, string Name, string Description);
public sealed record StackOutputItem(ImageFormat Format, string Name);


/// <summary>Basit astro stack: geçerli listedeki kareleri hizalayıp birleştirir.</summary>
public sealed partial class StackViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppServices _s;
    private readonly IStacker _stacker = new MagickStacker();
    private CancellationTokenSource? _cts;

    public StackViewModel(MainViewModel main, AppServices services)
    {
        _main = main;
        _s = services;
        Modes =
        [
            new(StackMode.StarTrails, L.T("Yıldız izi"), L.T("Her pikselin en parlak değeri alınır; yıldızlar iz bırakır. Hizalama gerekmez.")),
            new(StackMode.Mean, L.T("Ortalama"), L.T("Gürültüyü azaltır. Kare sayısı arttıkça görüntü temizlenir.")),
            new(StackMode.Median, L.T("Medyan"), L.T("Gürültüyü azaltır; uçak ve uydu izlerini büyük ölçüde siler.")),
            new(StackMode.SigmaClip, L.T("Sigma kırpma"), L.T("Ortalama kadar temiz, medyan kadar dirençli. Önerilen.")),
        ];
        Outputs = [new(FormatRegistry.Tiff, "TIFF 16 bit"), new(FormatRegistry.Png, "PNG 16 bit"), new(FormatRegistry.Jpeg, "JPEG")];
        _mode = Modes[3];
        _output = Outputs[0];
    }


    public IReadOnlyList<StackModeItem> Modes { get; }
    public IReadOnlyList<StackOutputItem> Outputs { get; }
    public ObservableCollection<string> Skipped { get; } = new();

    [ObservableProperty] private StackModeItem _mode;
    [ObservableProperty] private StackOutputItem _output;
    [ObservableProperty] private bool _align = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _stageText;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _lastOutput;

    public bool ShowStart => !IsRunning;
    public bool HasSkipped => Skipped.Count > 0;
    public string ModeDescription => Mode.Description;
    public string SourceText => _main.Items.Count < 2
        ? L.T("Stack için en az iki kare içeren bir klasör açın.")
        : L.T("Geçerli listedeki {0} kare kullanılacak.", _main.Items.Count);
    public string StartText => L.T("Stack oluştur ({0} kare)", _main.Items.Count);

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(ShowStart));

    partial void OnModeChanged(StackModeItem value)
    {
        OnPropertyChanged(nameof(ModeDescription));
        Align = value.Mode != StackMode.StarTrails;
    }

    public void NotifySourceChanged()
    {
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(StartText));
    }

    [RelayCommand]
    private async Task Start()
    {
        var files = _main.Items.Select(i => i.Path).ToList();
        if (files.Count < 2 || IsRunning) return;
        IsRunning = true;
        HasResult = false;
        Skipped.Clear();
        OnPropertyChanged(nameof(HasSkipped));
        _cts = new CancellationTokenSource();
        var options = new StackOptions { Mode = Mode.Mode, Align = Align, Output = Output.Format, Quality = 95 };
        var folder = Path.GetDirectoryName(files[0])!;
        var outPath = Path.Combine(folder, $"Stack-{Mode.Mode}-{DateTime.Now:yyyyMMdd-HHmmss}{OutputPaths.Extension(Output.Format)}");
        var progress = new Progress<StackProgress>(p =>
        {
            StageText = $"{p.Stage}  {p.Done} / {p.Total}";
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
        });
        try
        {
            var ct = _cts.Token;
            var r = await Task.Run(() => _stacker.Run(files, outPath, options, progress, ct), ct);
            foreach (var s in r.Skipped) Skipped.Add(s);
            OnPropertyChanged(nameof(HasSkipped));
            LastOutput = r.Output;
            ResultText = L.T("{0} kare birleştirildi ({1:mm\\:ss}).", r.Used, r.Elapsed);
            if (r.Output != null) _s.Library.AddConverted([r.Output]);
            HasResult = true;
            _main.ShowToast(L.T("Stack hazır: {0}", Path.GetFileName(r.Output)));
        }
        catch (OperationCanceledException)
        {
            ResultText = L.T("İptal edildi.");
            HasResult = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Stack başarısız", ex);
            ResultText = L.T("Stack oluşturulamadı: {0}", ex.Message);
            HasResult = true;
        }
        finally
        {
            IsRunning = false;
            StageText = null;
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task OpenResult()
    {
        if (LastOutput != null && File.Exists(LastOutput)) await _main.OpenPathsAsync([LastOutput]);
    }

    [RelayCommand]
    private void ShowResultInFolder()
    {
        if (LastOutput != null) PlatformShell.ShowInFolder(LastOutput);
    }
}
