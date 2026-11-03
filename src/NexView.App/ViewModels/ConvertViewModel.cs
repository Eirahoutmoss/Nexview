using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.App.Services;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using RMode = NexView.Core.Conversion.ResizeMode;

namespace NexView.App.ViewModels;

public sealed record PresetItem(string Id, string Name, string Description, ConversionPreset Preset);
public sealed record FormatItem(ImageFormat Format, string Name);
public sealed record ResizeItem(ResizeMode Mode, string Name);
public sealed record ChoiceItem(string Value, string Name);

/// <summary>Tekli ve toplu dönüştürme. Ayarlar iki sekme arasında paylaşılır.</summary>
public sealed partial class ConvertViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private readonly MainViewModel _main;
    private readonly AppServices _s;
    private bool _applyingPreset;
    private CancellationTokenSource? _batchCts;
    private PauseController? _pause;
    private string? _lastBatchFolder;

    public ConvertViewModel(MainViewModel main, AppServices services)
    {
        _main = main;
        _s = services;

        Presets = Core.Conversion.Presets.All.Select(p => new PresetItem(p.Id, L.T(p.Name), L.T(p.Description), p)).ToList();

        var order = new[] { "JPEG", "PNG", "WEBP", "AVIF", "HEIC", "JXL", "TIFF", "BMP", "GIF", "ICO", "JP2", "TGA" };
        var formats = order.Select(FormatRegistry.FromId).OfType<ImageFormat>().Where(f => _s.Converter.CanWrite(f))
            .Select(f => new FormatItem(f, f.Id == "ICO" ? L.T("ICO (simge, çoklu boyut)") : f.DisplayName)).ToList();
        formats.Add(new FormatItem(FormatRegistry.Pdf, L.T("PDF (görüntüleri birleştir)")));
        TargetFormats = formats;

        ResizeModes =
        [
            new(RMode.None, L.T("Orijinal boyut")),
            new(RMode.LongEdge, L.T("Uzun kenar (px)")),
            new(RMode.ShortEdge, L.T("Kısa kenar (px)")),
            new(RMode.Width, L.T("Genişlik (px)")),
            new(RMode.Height, L.T("Yükseklik (px)")),
            new(RMode.Percent, L.T("Yüzde (%)")),
        ];
        TiffCompressions = [new("LZW", "LZW"), new("ZIP", "ZIP (Deflate)"), new("JPEG", "JPEG"), new("None", L.T("Sıkıştırmasız"))];

        _targetFormat = TargetFormats.FirstOrDefault(f => f.Format.Id == _s.Settings.ConvertTarget) ?? TargetFormats[0];
        _quality = Math.Clamp(_s.Settings.ConvertQuality, 1, 100);
        _resizeMode = ResizeModes[0];
        _tiffCompression = TiffCompressions[0];
        _outputFolder = _s.Settings.ConvertFolder;
        _outputMode = Math.Clamp(_s.Settings.ConvertOutputMode, 0, 2);
        _selectedPreset = Presets.FirstOrDefault(p => p.Id == _s.Settings.ConvertPreset) ?? Presets[0];
        if (_selectedPreset.Id != "custom") ApplyPreset(_selectedPreset);
    }

    public IReadOnlyList<PresetItem> Presets { get; }
    public IReadOnlyList<FormatItem> TargetFormats { get; }
    public IReadOnlyList<ResizeItem> ResizeModes { get; }
    public IReadOnlyList<ChoiceItem> TiffCompressions { get; }

    [ObservableProperty] private PresetItem _selectedPreset;
    [ObservableProperty] private FormatItem _targetFormat;
    [ObservableProperty] private int _quality;
    [ObservableProperty] private bool _lossless;
    [ObservableProperty] private ResizeItem _resizeMode;
    [ObservableProperty] private decimal? _resizeValue = 1920;
    [ObservableProperty] private bool _shrinkOnly = true;
    [ObservableProperty] private bool _useTargetSize;
    [ObservableProperty] private decimal? _targetSizeKb = 1024;
    [ObservableProperty] private bool _metaKeep = true;
    [ObservableProperty] private bool _metaRemoveGps;
    [ObservableProperty] private bool _metaRemoveAll;
    [ObservableProperty] private bool _progressive;
    [ObservableProperty] private bool _highChroma;
    [ObservableProperty] private int _pngCompression = 6;
    [ObservableProperty] private ChoiceItem _tiffCompression;
    [ObservableProperty] private bool _pdfA4 = true;
    [ObservableProperty] private int _outputMode; // 0: kaynak klasör, 1: alt klasör, 2: seçilen klasör
    [ObservableProperty] private string? _outputFolder;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _resultDetail;
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty] private string? _lastOutput;

    // Toplu işlem
    [ObservableProperty] private bool _batchRunning;
    [ObservableProperty] private bool _batchPaused;
    [ObservableProperty] private bool _batchFinished;
    [ObservableProperty] private double _batchProgress;
    [ObservableProperty] private string _batchProgressText = "";
    [ObservableProperty] private string? _batchEta;
    [ObservableProperty] private string? _batchCurrent;
    [ObservableProperty] private string? _batchSummary;
    [ObservableProperty] private string? _batchSizeSummary;
    public ObservableCollection<string> BatchFailures { get; } = new();
    public bool HasBatchFailures => BatchFailures.Count > 0;

    public bool PdfImageSize { get => !PdfA4; set { if (value) PdfA4 = false; } }
    partial void OnPdfA4Changed(bool value) => OnPropertyChanged(nameof(PdfImageSize));

    public bool OutputSource { get => OutputMode == 0; set { if (value) OutputMode = 0; } }
    public bool OutputSubfolder { get => OutputMode == 1; set { if (value) OutputMode = 1; } }
    public bool OutputCustom { get => OutputMode == 2; set { if (value) OutputMode = 2; } }

    public bool IsPdf => TargetFormat.Format.Id == "PDF";
    public bool ShowQuality => ConversionOptions.IsLossy(TargetFormat.Format) && !(Lossless && ShowLossless);
    public bool ShowLossless => ConversionOptions.SupportsLossless(TargetFormat.Format);
    public bool ShowResizeValue => ResizeMode.Mode != Core.Conversion.ResizeMode.None;
    public bool ShowJpegOptions => TargetFormat.Format.Id == "JPEG";
    public bool ShowPngOptions => TargetFormat.Format.Id == "PNG";
    public bool ShowTiffOptions => TargetFormat.Format.Id == "TIFF";
    public bool HasAdvanced => ShowJpegOptions || ShowPngOptions || ShowTiffOptions;
    public bool ShowMetadataOptions => !IsPdf;
    public string QualityText => $"%{Quality}";
    public string PresetDescription => SelectedPreset.Description;
    public bool HasResult => ResultText != null;
    public bool ShowBatchStart => !BatchRunning;

    public string SourceText => _main.SelectedItem is { } it
        ? $"{it.FileName}  ·  {MainViewModel.FormatBytes(SafeLength(it.Path))}"
        : L.T("Görüntü seçilmedi");

    public string OutputPreview
    {
        get
        {
            if (_main.SelectedItem is not { } it) return "";
            var name = Path.GetFileNameWithoutExtension(it.FileName) + OutputPaths.Extension(TargetFormat.Format);
            var where = OutputMode switch
            {
                1 => Path.Combine("NexView", name),
                2 => Path.Combine(string.IsNullOrEmpty(OutputFolder) ? "…" : OutputFolder, name),
                _ => name,
            };
            return $"{it.FileName}  →  {where}";
        }
    }

    public string BatchSourceText
    {
        get
        {
            var items = _main.Items;
            if (items.Count == 0) return L.T("Listede görüntü yok. Bir klasör açın ya da dosyaları sürükleyip bırakın.");
            var groups = items.GroupBy(i => i.Badge).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} {g.Count()}");
            return L.T("{0} görüntü", items.Count) + "  ·  " + string.Join(", ", groups);
        }
    }

    public string BatchButtonText => IsPdf ? L.T("Tek PDF'te birleştir ({0})", _main.Items.Count) : L.T("Tümünü dönüştür ({0})", _main.Items.Count);

    public void NotifySourceChanged()
    {
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(OutputPreview));
        OnPropertyChanged(nameof(BatchSourceText));
        OnPropertyChanged(nameof(BatchButtonText));
    }

    private static long SafeLength(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    // ---------- Değişiklik bildirimleri ----------

    partial void OnSelectedPresetChanged(PresetItem value)
    {
        OnPropertyChanged(nameof(PresetDescription));
        _s.Settings.ConvertPreset = value.Id;
        if (!_applyingPreset && value.Id != "custom") ApplyPreset(value);
    }

    partial void OnTargetFormatChanged(FormatItem value)
    {
        _s.Settings.ConvertTarget = value.Format.Id;
        OnPropertyChanged(nameof(IsPdf));
        OnPropertyChanged(nameof(ShowQuality));
        OnPropertyChanged(nameof(ShowLossless));
        OnPropertyChanged(nameof(ShowJpegOptions));
        OnPropertyChanged(nameof(ShowPngOptions));
        OnPropertyChanged(nameof(ShowTiffOptions));
        OnPropertyChanged(nameof(HasAdvanced));
        OnPropertyChanged(nameof(ShowMetadataOptions));
        OnPropertyChanged(nameof(OutputPreview));
        OnPropertyChanged(nameof(BatchButtonText));
        MarkCustom();
    }

    partial void OnQualityChanged(int value) { _s.Settings.ConvertQuality = value; OnPropertyChanged(nameof(QualityText)); MarkCustom(); }
    partial void OnLosslessChanged(bool value) { OnPropertyChanged(nameof(ShowQuality)); MarkCustom(); }
    partial void OnResizeModeChanged(ResizeItem value) { OnPropertyChanged(nameof(ShowResizeValue)); MarkCustom(); }
    partial void OnResizeValueChanged(decimal? value) => MarkCustom();
    partial void OnShrinkOnlyChanged(bool value) => MarkCustom();
    partial void OnUseTargetSizeChanged(bool value) => MarkCustom();
    partial void OnTargetSizeKbChanged(decimal? value) => MarkCustom();
    partial void OnMetaKeepChanged(bool value) => MarkCustom();
    partial void OnMetaRemoveGpsChanged(bool value) => MarkCustom();
    partial void OnMetaRemoveAllChanged(bool value) => MarkCustom();
    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));
    partial void OnBatchRunningChanged(bool value) => OnPropertyChanged(nameof(ShowBatchStart));

    partial void OnOutputModeChanged(int value)
    {
        _s.Settings.ConvertOutputMode = value;
        OnPropertyChanged(nameof(OutputSource));
        OnPropertyChanged(nameof(OutputSubfolder));
        OnPropertyChanged(nameof(OutputCustom));
        OnPropertyChanged(nameof(OutputPreview));
    }

    partial void OnOutputFolderChanged(string? value)
    {
        _s.Settings.ConvertFolder = value;
        OnPropertyChanged(nameof(OutputPreview));
    }

    private void MarkCustom()
    {
        if (_applyingPreset || SelectedPreset.Id == "custom") return;
        _applyingPreset = true;
        try { SelectedPreset = Presets[0]; }
        finally { _applyingPreset = false; }
    }

    private void ApplyPreset(PresetItem item)
    {
        var o = new ConversionOptions();
        item.Preset.Apply(o);
        _applyingPreset = true;
        try
        {
            TargetFormat = TargetFormats.FirstOrDefault(f => f.Format.Id == o.Target.Id) ?? TargetFormats[0];
            Quality = o.Quality;
            Lossless = o.Lossless;
            ResizeMode = ResizeModes.First(r => r.Mode == o.ResizeMode);
            ResizeValue = o.ResizeValue;
            ShrinkOnly = o.ShrinkOnly;
            UseTargetSize = o.TargetSizeBytes != null;
            if (o.TargetSizeBytes is long b) TargetSizeKb = b / 1024;
            MetaKeep = o.Metadata == MetadataPolicy.Keep;
            MetaRemoveGps = o.Metadata == MetadataPolicy.RemoveGps;
            MetaRemoveAll = o.Metadata == MetadataPolicy.RemoveAll;
            Progressive = o.Progressive;
            HighChroma = o.HighChroma;
            PngCompression = o.PngCompression;
            TiffCompression = TiffCompressions.FirstOrDefault(t => t.Value == o.TiffCompression) ?? TiffCompressions[0];
        }
        finally { _applyingPreset = false; }
    }

    public ConversionOptions BuildOptions() => new()
    {
        Target = TargetFormat.Format,
        Quality = Quality,
        Lossless = Lossless && ShowLossless,
        ResizeMode = ResizeMode.Mode,
        ResizeValue = (int)(ResizeValue ?? 0),
        ShrinkOnly = ShrinkOnly,
        TargetSizeBytes = UseTargetSize && TargetSizeKb is > 0 ? (long)(TargetSizeKb.Value * 1024) : null,
        Metadata = MetaRemoveAll ? MetadataPolicy.RemoveAll : MetaRemoveGps ? MetadataPolicy.RemoveGps : MetadataPolicy.Keep,
        Progressive = Progressive,
        HighChroma = HighChroma,
        PngCompression = PngCompression,
        TiffCompression = TiffCompression.Value,
        PdfPage = PdfA4 ? PdfPageMode.A4 : PdfPageMode.ImageSize,
    };

    private OutputOptions BuildOutput() => new()
    {
        Folder = OutputMode == 2 ? OutputFolder : null,
        Subfolder = OutputMode == 1 ? "NexView" : null,
    };

    // ---------- Komutlar ----------

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (_main.Host == null) return;
        var f = await _main.Host.PickFolderAsync();
        if (f != null) { OutputFolder = f; OutputMode = 2; }
    }

    [RelayCommand]
    private async Task ConvertCurrent()
    {
        if (_main.SelectedItem is not { } item || IsBusy) return;
        if (OutputMode == 2 && string.IsNullOrEmpty(OutputFolder)) { await BrowseFolder(); if (string.IsNullOrEmpty(OutputFolder)) return; }
        IsBusy = true;
        ResultText = null;
        var options = BuildOptions();
        var output = BuildOutput();
        try
        {
            var r = await Task.Run(() => _s.Converter.Convert(item.Path, options, output, CancellationToken.None));
            ShowResult(r);
        }
        catch (Exception ex)
        {
            Log.Warn($"Dönüştürme başarısız: {item.Path}", ex);
            ResultOk = false;
            ResultText = L.T("Dönüştürülemedi");
            ResultDetail = ex.Message;
            LastOutput = null;
        }
        finally { IsBusy = false; }
    }

    private void ShowResult(ConversionResult r)
    {
        ResultOk = r.Status == ConversionStatus.Success;
        LastOutput = r.Output;
        if (r.Status == ConversionStatus.Success && r.Output != null)
        {
            _s.Library.AddConverted([r.Output]);
            var change = r.SourceBytes > 0 ? (r.OutputBytes - r.SourceBytes) * 100.0 / r.SourceBytes : 0;
            ResultText = L.T("Kaydedildi: {0}", Path.GetFileName(r.Output));
            ResultDetail = $"{MainViewModel.FormatBytes(r.SourceBytes)} → {MainViewModel.FormatBytes(r.OutputBytes)} ({(change > 0 ? "+" : "")}{change.ToString("0", Tr)}%)"
                           + (r.Width > 0 ? $"  ·  {r.Width} × {r.Height}" : "")
                           + (r.TargetSizeMissed ? "  ·  " + L.T("Hedef boyuta tam inilemedi.") : "");
            _main.ShowToast(ResultText);
        }
        else
        {
            ResultText = r.Status == ConversionStatus.Skipped ? L.T("Atlandı") : L.T("Dönüştürülemedi");
            ResultDetail = r.Message;
        }
    }

    [RelayCommand]
    private void ShowOutputInFolder()
    {
        if (LastOutput != null) PlatformShell.ShowInFolder(LastOutput);
    }

    [RelayCommand]
    private async Task OpenOutput()
    {
        if (LastOutput != null && File.Exists(LastOutput) && !LastOutput.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            await _main.OpenPathsAsync([LastOutput]);
        else if (LastOutput != null) PlatformShell.OpenUri(LastOutput);
    }

    [RelayCommand]
    private async Task StartBatch()
    {
        if (BatchRunning) return;
        var files = _main.Items.Select(i => i.Path).ToList();
        if (files.Count == 0) return;
        if (OutputMode == 2 && string.IsNullOrEmpty(OutputFolder)) { await BrowseFolder(); if (string.IsNullOrEmpty(OutputFolder)) return; }

        var options = BuildOptions();
        var output = BuildOutput();
        BatchFailures.Clear();
        OnPropertyChanged(nameof(HasBatchFailures));
        BatchFinished = false;
        BatchRunning = true;
        BatchPaused = false;
        BatchProgress = 0;
        BatchSummary = null;
        BatchSizeSummary = null;
        _batchCts = new CancellationTokenSource();
        _pause = new PauseController();
        var ct = _batchCts.Token;

        try
        {
            if (options.Target.Id == "PDF")
            {
                var folder = OutputMode == 2 ? OutputFolder! : Path.GetDirectoryName(files[0])!;
                if (OutputMode == 1) folder = Path.Combine(folder, "NexView");
                Directory.CreateDirectory(folder);
                var name = OutputPaths.SanitizeFileName(_main.CollectionTitle);
                var target = Path.Combine(folder, (string.IsNullOrWhiteSpace(name) ? "NexView" : name) + ".pdf");
                var progress = new Progress<int>(n =>
                {
                    BatchProgress = (double)n / files.Count;
                    BatchProgressText = $"{n} / {files.Count}";
                });
                BatchCurrent = L.T("PDF oluşturuluyor…");
                var r = await Task.Run(() => _s.Converter.CreatePdf(files, target, options, progress, ct), ct);
                _lastBatchFolder = folder;
                LastOutput = r.Output;
                if (r.Output != null) _s.Library.AddConverted([r.Output]);
                BatchSummary = L.T("{0} sayfalık PDF oluşturuldu: {1}", files.Count, Path.GetFileName(r.Output));
                BatchSizeSummary = MainViewModel.FormatBytes(r.OutputBytes) + (r.TargetSizeMissed ? "  ·  " + L.T("Hedef boyuta tam inilemedi.") : "");
            }
            else
            {
                var progress = new Progress<BatchProgress>(p =>
                {
                    BatchProgress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                    BatchProgressText = $"{p.Done} / {p.Total}";
                    BatchCurrent = p.Current;
                    BatchEta = p.Remaining is { } t && p.Done < p.Total ? L.T("Tahmini kalan süre: {0}", t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss")) : null;
                });
                var runner = new BatchRunner(_s.Converter);
                var report = await runner.RunAsync(files, options, output, progress, _pause, ct);
                var outputs = report.Results.Where(r => r.Status == ConversionStatus.Success && r.Output != null).Select(r => r.Output!).ToList();
                _s.Library.AddConverted(outputs);
                _lastBatchFolder = outputs.Count > 0 ? Path.GetDirectoryName(outputs[0]) : null;
                LastOutput = outputs.FirstOrDefault();
                foreach (var f in report.Results.Where(r => r.Status != ConversionStatus.Success))
                    BatchFailures.Add($"{Path.GetFileName(f.Source)} — {f.Message}");
                OnPropertyChanged(nameof(HasBatchFailures));
                BatchSummary = (report.Cancelled ? L.T("İptal edildi.") + " " : "") +
                               L.T("{0} başarılı · {1} başarısız · {2} atlandı", report.Succeeded, report.Failed, report.Skipped);
                BatchSizeSummary = $"{MainViewModel.FormatBytes(report.Results.Where(r => r.Status == ConversionStatus.Success).Sum(r => r.SourceBytes))} → {MainViewModel.FormatBytes(report.OutputBytes)}  ·  {report.Elapsed:mm\\:ss}";
                _main.ShowToast(BatchSummary);
            }
        }
        catch (OperationCanceledException)
        {
            BatchSummary = L.T("İptal edildi.");
        }
        catch (Exception ex)
        {
            Log.Warn("Toplu işlem başarısız", ex);
            BatchSummary = L.T("İşlem tamamlanamadı: {0}", ex.Message);
        }
        finally
        {
            BatchRunning = false;
            BatchPaused = false;
            BatchFinished = true;
            BatchEta = null;
            BatchCurrent = null;
        }
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (_pause == null) return;
        if (_pause.IsPaused) _pause.Resume(); else _pause.Pause();
        BatchPaused = _pause.IsPaused;
    }

    [RelayCommand]
    private void CancelBatch()
    {
        _pause?.Resume();
        _batchCts?.Cancel();
    }

    [RelayCommand]
    private void OpenBatchFolder()
    {
        if (LastOutput != null) PlatformShell.ShowInFolder(LastOutput);
        else if (_lastBatchFolder != null) PlatformShell.OpenUri(_lastBatchFolder);
    }

    [RelayCommand]
    private async Task CopyReport()
    {
        if (_main.Host == null) return;
        var text = $"NexView\n{BatchSummary}\n{BatchSizeSummary}\n" + string.Join("\n", BatchFailures);
        await _main.Host.CopyTextAsync(text);
    }
}
