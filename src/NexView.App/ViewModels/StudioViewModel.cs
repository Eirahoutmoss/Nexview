using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.App.Services;
using NexView.Codecs.Magick;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Core.Metadata;
using NexView.Core.Stacking;
using NexView.Studio;
using NexView.UI;

namespace NexView.App.ViewModels;

public sealed record StudioKindItem(StudioKind Kind, string Name, string Guide, int MinFrames);
public sealed record StudioOption(object Value, string Name);

/// <summary>Stüdyodaki bir kare: küçük resim, EXIF özeti, dahil/hariç.</summary>
public sealed partial class StudioFrame : ObservableObject
{
    public StudioFrame(string path) { Path = path; Name = System.IO.Path.GetFileName(path); }
    public string Path { get; }
    public string Name { get; }
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _badge = "";
    [ObservableProperty] private int _index;
    public double? Ev { get; set; }
    public DateTime? Taken { get; set; }
    public string? Iso { get; set; }
    public string? Aperture { get; set; }
    public string? Shutter { get; set; }
    public string? Focal { get; set; }
}

/// <summary>Çoklu çekim stüdyosu: tür seçimi → kareler → analiz → önizleme → son işleme.</summary>
public sealed partial class StudioViewModel : ObservableObject
{
    private const int PreviewMax = 1400;
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private readonly OpenCvStudioEngine _engine = new(MagickRaster16.Load);
    private const int LivePreviewMax = 1000;
    private CancellationTokenSource? _debounce;
    private Task? _running;
    private bool _liveJob;
    private readonly IStacker _stacker = new MagickStacker();
    private CancellationTokenSource? _cts;

    public StudioViewModel(AppServices services, MainViewModel main, IEnumerable<string> initial)
    {
        _s = services;
        _main = main;
        Kinds =
        [
            new(StudioKind.Macro, L.T("Makro"), L.T("Odağı adım adım kaydırarak çekilmiş kareler. Her bölgenin en net olduğu kare seçilir; alan derinliği genişler. Tripod, sabit pozlama ve küçük odak adımları önerilir; kareler odak sırasıyla olmalı."), 2),
            new(StudioKind.Hdr, L.T("HDR"), L.T("Aynı sahnenin farklı pozlamalarla (ör. −2, 0, +2 EV) çekilmiş kareleri. Işık ve gölge ayrıntısı tek görüntüde birleşir. Tripod önerilir; elde çekimde kareler otomatik hizalanır."), 2),
            new(StudioKind.Panorama, L.T("Panorama"), L.T("Kamerayı objektif ekseni etrafında döndürerek, kareler arası yaklaşık %30 örtüşmeyle ve sabit pozlamayla çekin. Sıra önemli değildir; kareler otomatik eşleştirilir."), 2),
            new(StudioKind.Astro, L.T("Astro"), L.T("Tripodla, aynı ayarlarla çekilmiş gece kareleri. Yıldızlara göre hizalanır ve gürültü azaltılır. Kalibrasyon (dark/flat) yapılmaz."), 2),
        ];
        MacroMethods = [new(MacroMethod.Hybrid, L.T("Karma (önerilen)")), new(MacroMethod.Pyramid, L.T("Ayrıntı (piramit)")), new(MacroMethod.DepthMap, L.T("Pürüzsüz (derinlik haritası)"))];
        HdrStyles = [new(HdrStyle.Natural, L.T("Doğal")), new(HdrStyle.Vivid, L.T("Canlı")), new(HdrStyle.Dramatic, L.T("Dramatik"))];
        PanoOutputs = [new(PanoOutput.Panorama, L.T("Panorama")), new(PanoOutput.LittlePlanet, L.T("Küçük gezegen")), new(PanoOutput.Tunnel, L.T("Tünel"))];
        PanoModes = [new(PanoMode.Spherical, L.T("Küresel (manzara, 360°)")), new(PanoMode.Flat, L.T("Düz yüzey (cephe, tarama)"))];
        AstroModes =
        [
            new(StackMode.SigmaClip, L.T("Sigma kırpma")), new(StackMode.Median, L.T("Medyan")),
            new(StackMode.Mean, L.T("Ortalama")), new(StackMode.StarTrails, L.T("Yıldız izi")),
        ];
        Resolutions = [new(0, L.T("Tam çözünürlük")), new(6000, "6000 px"), new(4000, "4000 px"), new(2500, "2500 px")];
        Outputs = [new(FormatRegistry.Tiff, "TIFF 16 bit"), new(FormatRegistry.Png, "PNG 16 bit"), new(FormatRegistry.Jpeg, "JPEG")];
        _kind = Kinds[0];
        _selMacro = MacroMethods[0];
        _selHdr = HdrStyles[0];
        _selPano = PanoModes[0];
        _selPanoOut = PanoOutputs[0];
        _selAstro = AstroModes[0];
        _resolution = Resolutions[0];
        _output = Outputs[0];
        Frames.CollectionChanged += (_, _) => { Renumber(); Invalidate(); };
        _ = AddAsync(initial);
    }

    public IReadOnlyList<StudioKindItem> Kinds { get; }
    public IReadOnlyList<StudioOption> MacroMethods { get; }
    public IReadOnlyList<StudioOption> HdrStyles { get; }
    public IReadOnlyList<StudioOption> PanoModes { get; }
    public IReadOnlyList<StudioOption> PanoOutputs { get; }
    public AppServices Services => _s;
    /// <summary>Pencere 360° görüntüleyiciyi açar.</summary>
    public event Action<string>? Open360Requested;
    public IReadOnlyList<StudioOption> AstroModes { get; }
    public IReadOnlyList<StudioOption> Resolutions { get; }
    public IReadOnlyList<StudioOption> Outputs { get; }
    public ObservableCollection<StudioFrame> Frames { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    public Func<Task<IReadOnlyList<string>>>? PickFiles { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMacro), nameof(IsHdr), nameof(IsPano), nameof(IsAstro), nameof(CanPreview), nameof(RenderText))]
    private StudioKindItem _kind;
    [ObservableProperty] private StudioOption _selMacro;
    [ObservableProperty] private StudioOption _selHdr;
    [ObservableProperty] private StudioOption _selPano;
    [ObservableProperty] private StudioOption _selAstro;
    [ObservableProperty] private StudioOption _resolution;
    [ObservableProperty] private StudioOption _output;
    [ObservableProperty] private bool _align = true;
    [ObservableProperty] private bool _cropToCommon = true;
    [ObservableProperty] private bool _waveCorrection = true;
    [ObservableProperty] private double _smoothness = 4;
    [ObservableProperty] private double _confidence = 1.0;
    [ObservableProperty] private double _haloSuppression = 0.5;
    [ObservableProperty] private bool _deghost = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPlanet))] private StudioOption _selPanoOut;
    [ObservableProperty] private double _planetZoom = 3.5;
    [ObservableProperty] private double _planetRotation;
    [ObservableProperty] private bool _livePreview;
    [ObservableProperty] private bool _canView360;
    [ObservableProperty] private string _view360Text = "";
    [ObservableProperty] private bool _openWhenDone = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowOverlay), nameof(CanEdit))] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _stageText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreview))] private Bitmap? _previewBitmap;
    [ObservableProperty] private Bitmap? _compareBitmap;
    [ObservableProperty] private PixelSizeInfo _previewSize;
    [ObservableProperty] private bool _compareOn;
    [ObservableProperty] private double _splitPosition = 0.5;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _lastOutput;
    [ObservableProperty] private string _previewState = "";

    public bool IsMacro => Kind.Kind == StudioKind.Macro;
    public bool IsHdr => Kind.Kind == StudioKind.Hdr;
    public bool IsPano => Kind.Kind == StudioKind.Panorama;
    public bool IsAstro => Kind.Kind == StudioKind.Astro;
    public bool IsIdle => !IsBusy;
    public bool HasPreview => PreviewBitmap != null;
    public bool CanPreview => !IsAstro;
    public bool IsPlanet => SelPanoOut.Value is PanoOutput o && o != PanoOutput.Panorama;
    /// <summary>Tam ekran meşgul katmanı yalnızca son işleme ve elle başlatılan önizlemede gösterilir.</summary>
    public bool ShowOverlay => IsBusy && !_liveJob;
    /// <summary>Canlı önizleme hesaplanırken ayarlar değiştirilebilir (yeni değer eskisini iptal eder).</summary>
    public bool CanEdit => !IsBusy || _liveJob;
    public bool HasFrames => Frames.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasResult => LastOutput != null;
    public string FrameCountText => L.T("{0} / {1} kare seçili", Included.Count, Frames.Count);
    public string RenderText => L.T("{0} oluştur", Kind.Name);

    private List<StudioFrame> Included => Frames.Where(f => f.Include).ToList();

    partial void OnKindChanged(StudioKindItem value) { Analyze(); Invalidate(); }
    partial void OnSelMacroChanged(StudioOption value) => Invalidate();
    partial void OnSelHdrChanged(StudioOption value) => Invalidate();
    partial void OnSelPanoChanged(StudioOption value) => Invalidate();
    partial void OnAlignChanged(bool value) => Invalidate();
    partial void OnCropToCommonChanged(bool value) => Invalidate();
    partial void OnSmoothnessChanged(double value) => Invalidate();
    partial void OnConfidenceChanged(double value) => Invalidate();
    partial void OnWaveCorrectionChanged(bool value) => Invalidate();
    partial void OnHaloSuppressionChanged(double value) => Invalidate();
    partial void OnDeghostChanged(bool value) => Invalidate();
    partial void OnSelPanoOutChanged(StudioOption value) => Invalidate();
    partial void OnPlanetZoomChanged(double value) => Invalidate();
    partial void OnPlanetRotationChanged(double value) => Invalidate();
    partial void OnLivePreviewChanged(bool value) { if (value) SchedulePreview(); }
    partial void OnLastOutputChanged(string? value) => OnPropertyChanged(nameof(HasResult));
    partial void OnCompareOnChanged(bool value) => _ = LoadCompareAsync();

    private void Invalidate()
    {
        if (LivePreview) { SchedulePreview(); return; }
        PreviewState = PreviewBitmap != null ? L.T("Ayarlar değişti; önizlemeyi yenileyin.") : "";
    }

    private void Renumber()
    {
        for (int i = 0; i < Frames.Count; i++) Frames[i].Index = i + 1;
        OnPropertyChanged(nameof(HasFrames));
        OnPropertyChanged(nameof(FrameCountText));
    }

    // ---------- Kareler ----------

    [RelayCommand]
    private async Task AddFiles()
    {
        if (PickFiles == null) return;
        await AddAsync(await PickFiles());
    }

    public async Task AddAsync(IEnumerable<string> paths)
    {
        var added = new List<StudioFrame>();
        foreach (var p in paths)
        {
            if (Frames.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            if (FormatRegistry.FromPath(p) == null || !File.Exists(p)) continue;
            var f = new StudioFrame(p);
            f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StudioFrame.Include)) { OnPropertyChanged(nameof(FrameCountText)); Analyze(); Invalidate(); } };
            Frames.Add(f);
            added.Add(f);
        }
        if (added.Count == 0) return;
        await Task.WhenAll(added.Select(LoadFrameAsync));
        if (Kind.Kind == StudioKind.Hdr) SortByExposureCore();
        Analyze();
    }

    private async Task LoadFrameAsync(StudioFrame f)
    {
        try
        {
            var meta = await Task.Run(() => MetadataService.Read(f.Path));
            f.Taken = meta.DateTaken;
            f.Iso = meta.Iso; f.Aperture = meta.FNumber; f.Shutter = meta.ExposureTime; f.Focal = meta.FocalLength;
            f.Ev = ComputeEv(meta);
            f.Info = string.Join("  ", new[] { meta.ExposureTime, meta.FNumber, meta.Iso != null ? "ISO " + meta.Iso : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var bytes = await _s.Thumbnails.GetAsync(f.Path, FormatRegistry.FromPath(f.Path), CancellationToken.None);
            if (bytes != null) f.Thumbnail = await Task.Run(() => { using var ms = new MemoryStream(bytes); return new Bitmap(ms); });
        }
        catch (Exception ex) { Log.Warn($"Stüdyo karesi okunamadı: {f.Path}", ex); f.Badge = L.T("okunamadı"); }
    }

    /// <summary>EV100 = log2(N²/t) − log2(ISO/100). Bilinmiyorsa null.</summary>
    private static double? ComputeEv(ImageMetadata m)
    {
        double? t = ParseNumber(m.ExposureTime, fraction: true), n = ParseNumber(m.FNumber, fraction: false), iso = ParseNumber(m.Iso, fraction: false);
        if (t is not > 0 || n is not > 0) return null;
        return Math.Log2(n.Value * n.Value / t.Value) - Math.Log2((iso ?? 100) / 100.0);
    }

    private static double? ParseNumber(string? s, bool fraction)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => char.IsDigit(c) || c is '.' or ',' or '/').ToArray()).Replace(',', '.');
        if (fraction && t.Contains('/'))
        {
            var parts = t.Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0) return a / b;
            return null;
        }
        t = t.Split('/')[0];
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [RelayCommand] private void Remove(StudioFrame f) { Frames.Remove(f); Analyze(); }
    [RelayCommand] private void MoveLeft(StudioFrame f) { var i = Frames.IndexOf(f); if (i > 0) Frames.Move(i, i - 1); }
    [RelayCommand] private void MoveRight(StudioFrame f) { var i = Frames.IndexOf(f); if (i >= 0 && i < Frames.Count - 1) Frames.Move(i, i + 1); }
    [RelayCommand] private void Clear() { Frames.Clear(); Analyze(); ClearPreview(); _engine.ClearCache(); CanView360 = false; }

    [RelayCommand]
    private void SortByTime()
    {
        var sorted = Frames.OrderBy(f => f.Taken ?? DateTime.MaxValue).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Reorder(sorted);
    }

    [RelayCommand] private void SortByExposure() => SortByExposureCore();

    private void SortByExposureCore()
    {
        if (Frames.All(f => f.Ev == null)) return;
        Reorder(Frames.OrderByDescending(f => f.Ev ?? 0).ToList());
    }

    private void Reorder(List<StudioFrame> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            var cur = Frames.IndexOf(order[i]);
            if (cur != i) Frames.Move(cur, i);
        }
        Analyze();
    }

    // ---------- Analiz ----------

    /// <summary>EXIF'e dayalı ön denetim: türe göre uyarılar ve kare rozetleri.</summary>
    private void Analyze()
    {
        Warnings.Clear();
        var inc = Included;
        foreach (var f in Frames) f.Badge = "";
        if (Frames.Count > 0 && inc.Count < Kind.MinFrames)
            Warnings.Add(L.T("{0} için en az {1} kare seçin.", Kind.Name, Kind.MinFrames));

        var evs = inc.Where(f => f.Ev != null).Select(f => f.Ev!.Value).ToList();
        if (IsHdr)
        {
            if (evs.Count >= 2)
            {
                var refEv = evs.OrderBy(x => x).ElementAt(evs.Count / 2);
                foreach (var f in inc.Where(f => f.Ev != null))
                {
                    var d = refEv - f.Ev!.Value; // pozitif: daha parlak (daha çok ışık)
                    f.Badge = Math.Abs(d) < 0.15 ? "0 EV" : $"{d:+0.#;-0.#} EV";
                }
                var range = evs.Max() - evs.Min();
                if (range < 0.9) Warnings.Add(L.T("Kareler arasındaki pozlama farkı çok küçük ({0:0.#} EV). HDR için en az ±1 EV aralık önerilir.", range));
                if (evs.Count != evs.Distinct().Count()) Warnings.Add(L.T("Aynı pozlamada birden çok kare var; birini dışarıda bırakabilirsiniz."));
            }
            else if (inc.Count >= 2) Warnings.Add(L.T("Pozlama bilgisi okunamadı; karelerin farklı pozlamalarla çekildiğinden emin olun."));
        }
        else if (evs.Count >= 2 && evs.Max() - evs.Min() > 0.4)
        {
            Warnings.Add(IsPano
                ? L.T("Kareler farklı pozlamalarla çekilmiş ({0:0.#} EV fark). Telafi edilir, ancak manuel sabit pozlama daha iyi sonuç verir.", evs.Max() - evs.Min())
                : L.T("Kareler farklı pozlamalarla çekilmiş ({0:0.#} EV fark). Bu tür için sabit pozlama gerekir.", evs.Max() - evs.Min()));
        }

        if (IsMacro || IsAstro)
        {
            var focals = inc.Select(f => f.Focal).Where(x => x != null).Distinct().Count();
            if (focals > 1) Warnings.Add(L.T("Kareler farklı odak uzaklıklarında (zoom) çekilmiş."));
        }
        var times = inc.Where(f => f.Taken != null).Select(f => f.Taken!.Value).OrderBy(t => t).ToList();
        if (times.Count >= 2 && !IsAstro && (times[^1] - times[0]).TotalMinutes > 10)
            Warnings.Add(L.T("Kareler {0:0} dakikalık bir aralıkta çekilmiş; aynı seriden olmayabilirler.", (times[^1] - times[0]).TotalMinutes));
        if (IsPano && inc.Count > 25) Warnings.Add(L.T("Çok sayıda kare var; işlem uzun sürebilir. Çalışma çözünürlüğünü düşürmeyi düşünün."));
        OnPropertyChanged(nameof(HasWarnings));
    }

    // ---------- Önizleme ve son işleme ----------

    private StudioOptions BuildOptions(int maxDim) => new()
    {
        Kind = Kind.Kind,
        MaxDimension = maxDim,
        Align = Align,
        CropToCommon = CropToCommon,
        MacroMethod = (MacroMethod)SelMacro.Value,
        Smoothness = Smoothness,
        HdrStyle = (HdrStyle)SelHdr.Value,
        PanoMode = (PanoMode)SelPano.Value,
        WaveCorrection = WaveCorrection,
        PanoConfidence = Confidence,
        HaloSuppression = HaloSuppression,
        Deghost = Deghost,
        PanoOutput = (PanoOutput)SelPanoOut.Value,
        PlanetZoom = PlanetZoom,
        PlanetRotation = PlanetRotation,
    };

    [RelayCommand]
    private async Task Preview()
    {
        if (IsBusy) return;
        await PreviewCore(PreviewMax);
    }

    private async Task PreviewCore(int maxDim)
    {
        var files = Included.Select(f => f.Path).ToList();
        if (files.Count < Kind.MinFrames || IsAstro) return;
        var o = BuildOptions(maxDim);
        var r = await RunAsync(L.T("Önizleme"), ct => _engine.Run(files, o, ProgressSink(), ct));
        if (r == null) return;
        ShowResult(r);
        PreviewState = _liveJob
            ? L.T("Canlı önizleme ({0}×{1})", r.Width, r.Height)
            : L.T("Önizleme ({0}×{1}). Son işleme seçilen çözünürlükte yapılır.", r.Width, r.Height);
    }

    /// <summary>Canlı önizleme: son değişiklikten kısa süre sonra, sürmekte olan önizlemeyi iptal edip yeniden hesaplar.</summary>
    private async void SchedulePreview()
    {
        if (!LivePreview || IsAstro) return;
        _debounce?.Cancel();
        var d = _debounce = new CancellationTokenSource();
        try { await Task.Delay(450, d.Token); } catch (OperationCanceledException) { return; }
        if (IsBusy && !_liveJob) return; // son işleme sürüyor
        if (IsBusy) { _cts?.Cancel(); try { if (_running != null) await _running; } catch { } }
        if (d.IsCancellationRequested || IsBusy) return;
        _liveJob = true;
        try { _running = PreviewCore(LivePreviewMax); await _running; }
        finally
        {
            _liveJob = false;
            OnPropertyChanged(nameof(ShowOverlay));
            OnPropertyChanged(nameof(CanEdit));
        }
    }

    private async Task StopLiveAsync()
    {
        _debounce?.Cancel();
        if (IsBusy && _liveJob) { _cts?.Cancel(); try { if (_running != null) await _running; } catch { } }
    }

    [RelayCommand] private void Open360() { if (LastOutput != null) Open360Requested?.Invoke(LastOutput); }

    public void Shutdown()
    {
        _debounce?.Cancel();
        _cts?.Cancel();
        _engine.Dispose();
    }

    [RelayCommand]
    private async Task Render()
    {
        await StopLiveAsync();
        var files = Included.Select(f => f.Path).ToList();
        if (files.Count < Kind.MinFrames || IsBusy) return;
        var folder = Path.GetDirectoryName(files[0])!;
        var fmt = (ImageFormat)Output.Value;
        var name = $"{Kind.Name}-{DateTime.Now:yyyyMMdd-HHmmss}{OutputPaths.Extension(fmt)}";
        var outPath = Path.Combine(folder, name);
        var exifSource = files[files.Count / 2];

        if (IsAstro)
        {
            var so = new StackOptions { Mode = (StackMode)SelAstro.Value, Align = Align, Output = fmt, Quality = 95 };
            var sr = await RunAsync(L.T("Stack"), ct => _stacker.Run(files, outPath, so, ProgressSink(), ct));
            if (sr?.Output == null) return;
            foreach (var s in sr.Skipped) Notes.Add(s);
            await FinishAsync(sr.Output, sr.Used, sr.Elapsed);
            return;
        }

        var o = BuildOptions((int)Resolution.Value);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? saved = null;
        var r = await RunAsync(L.T("Son işleme"), ct =>
        {
            var res = _engine.Run(files, o, ProgressSink(), ct);
            Report(L.T("Kaydediliyor"), 1, 1);
            saved = MagickRaster16.Save(res.Rgb, res.Width, res.Height, outPath, fmt, 95, exifSource, res.Pano?.ToXmp());
            return res;
        });
        if (r == null || saved == null) return;
        CanView360 = r.Pano != null;
        if (r.Pano is { } pg)
            View360Text = pg.IsFull360 ? L.T("360° görüntüle") : L.T("Panoramayı gez ({0:0}° × {1:0}°)", pg.HorizontalDegrees, pg.VerticalDegrees);
        ShowResult(r);
        PreviewState = L.T("Son sonuç ({0}×{1}).", r.Width, r.Height);
        await FinishAsync(saved, r.Used, sw.Elapsed);
    }

    private async Task FinishAsync(string output, int used, TimeSpan elapsed)
    {
        LastOutput = output;
        ResultText = L.T("{0} kare birleştirildi ({1:mm\\:ss}): {2}", used, elapsed, Path.GetFileName(output));
        _s.Library.AddConverted([output]);
        if (OpenWhenDone) await _main.OpenPathsAsync([output]);
    }

    private async Task<T?> RunAsync<T>(string title, Func<CancellationToken, T> work) where T : class
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        Notes.Clear();
        ResultText = null;
        StageText = title;
        Progress = 0;
        try
        {
            var r = await Task.Run(() => work(ct), ct);
            if (r is StudioOutput so) foreach (var n in so.Notes) Notes.Add(n);
            return r;
        }
        catch (OperationCanceledException) { if (!_liveJob) ResultText = L.T("İptal edildi."); return null; }
        catch (Exception ex)
        {
            Log.Warn($"Stüdyo işlemi başarısız ({Kind.Kind})", ex);
            ResultText = ex is InvalidOperationException ? ex.Message : L.T("İşlem başarısız: {0}", ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
            StageText = null;
            OnPropertyChanged(nameof(HasNotes));
        }
    }

    private IProgress<StackProgress> ProgressSink() => new Progress<StackProgress>(p => Report(p.Stage, p.Done, p.Total));

    private void Report(string stage, int done, int total) => Dispatcher.UIThread.Post(() =>
    {
        StageText = total > 1 ? $"{stage}  {done} / {total}" : stage;
        Progress = total == 0 ? 0 : (double)done / total;
    });

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    private void ShowResult(StudioOutput r)
    {
        var bmp = BitmapFactory.Create(ToPreview(r.Rgb, r.Width, r.Height, 2400));
        var old = PreviewBitmap;
        PreviewBitmap = bmp;
        PreviewSize = new PixelSizeInfo(r.Width, r.Height);
        old?.Dispose();
        if (CompareOn) _ = LoadCompareAsync();
    }

    private void ClearPreview()
    {
        var a = PreviewBitmap; var b = CompareBitmap;
        PreviewBitmap = null; CompareBitmap = null;
        a?.Dispose(); b?.Dispose();
        PreviewState = "";
    }

    /// <summary>Karşılaştırma: ortadaki kare (hizalamasız), sonucun en-boy oranına sığdırılmış.</summary>
    private async Task LoadCompareAsync()
    {
        if (!CompareOn || PreviewBitmap == null) { var c = CompareBitmap; CompareBitmap = null; c?.Dispose(); return; }
        var inc = Included;
        if (inc.Count == 0) return;
        var path = inc[inc.Count / 2].Path;
        try
        {
            var target = PreviewBitmap.PixelSize;
            var bmp = await Task.Run(() =>
            {
                var (rgb, w, h) = MagickRaster16.Load(path, Math.Max(target.Width, target.Height));
                var img = ToPreview(rgb, w, h, Math.Max(target.Width, target.Height));
                return BitmapFactory.Create(FitTo(img, target.Width, target.Height));
            });
            var old = CompareBitmap;
            CompareBitmap = bmp;
            old?.Dispose();
        }
        catch (Exception ex) { Log.Warn("Karşılaştırma karesi yüklenemedi", ex); }
    }

    /// <summary>16 bit RGB → 8 bit BGRA, uzun kenar en fazla max (kutu ortalaması).</summary>
    private static DecodedImage ToPreview(ushort[] rgb, int w, int h, int max)
    {
        int k = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)max));
        int ow = Math.Max(1, w / k), oh = Math.Max(1, h / k);
        var px = new byte[ow * oh * 4];
        Parallel.For(0, oh, y =>
        {
            for (int x = 0; x < ow; x++)
            {
                long r = 0, g = 0, b = 0; int n = 0;
                for (int dy = 0; dy < k; dy++)
                    for (int dx = 0; dx < k; dx++)
                    {
                        int sx = x * k + dx, sy = y * k + dy;
                        if (sx >= w || sy >= h) continue;
                        int i = (sy * w + sx) * 3;
                        r += rgb[i]; g += rgb[i + 1]; b += rgb[i + 2]; n++;
                    }
                int o = (y * ow + x) * 4;
                px[o] = (byte)(b / n >> 8); px[o + 1] = (byte)(g / n >> 8); px[o + 2] = (byte)(r / n >> 8); px[o + 3] = 255;
            }
        });
        return new DecodedImage(ow, oh, ow * 4, px, ow, oh);
    }

    /// <summary>Ortadan kırpıp tw×th boyutuna en yakın komşu ile ölçekler (karşılaştırma için kaba eşleme).</summary>
    private static DecodedImage FitTo(DecodedImage s, int tw, int th)
    {
        double scale = Math.Max(tw / (double)s.Width, th / (double)s.Height);
        double ox = (s.Width * scale - tw) / 2, oy = (s.Height * scale - th) / 2;
        var px = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int sx = Math.Clamp((int)((x + ox) / scale), 0, s.Width - 1), sy = Math.Clamp((int)((y + oy) / scale), 0, s.Height - 1);
                Buffer.BlockCopy(s.Pixels, sy * s.Stride + sx * 4, px, (y * tw + x) * 4, 4);
            }
        return new DecodedImage(tw, th, tw * 4, px, tw, th);
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (LastOutput != null) PlatformShell.ShowInFolder(LastOutput);
    }
}

public readonly record struct PixelSizeInfo(int Width, int Height)
{
    public override string ToString() => Width > 0 ? $"{Width} × {Height}" : "";
}
