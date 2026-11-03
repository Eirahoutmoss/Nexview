using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Core.Metadata;
using NexView.Mobile.Services;
using NexView.UI;

namespace NexView.Mobile.ViewModels;

/// <summary>Görünümün sağladığı dosya seçici erişimi.</summary>
public interface IPickerHost
{
    IStorageProvider? Storage { get; }
}

public enum MobilePage { Gallery, Tools, Settings }

public sealed partial class MobileViewModel : ObservableObject
{
    private readonly MobileServices _s;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _toastCts;
    private IReadOnlyList<MobileItem> _convertTargets = [];
    private string _sessionDir;

    public MobileViewModel(MobileServices services)
    {
        _s = services;
        _sessionDir = Path.Combine(_s.Platform.CacheDir, "session");
        Directory.CreateDirectory(_sessionDir);
        Convert = new MobileConvertViewModel(_s.Converter, _s.Settings.ConvertPreset, _s.Settings.ConvertTarget, _s.Settings.ConvertQuality);
        foreach (var (code, name) in L.Available) Language.Add(code == "auto" ? L.T(name) : name, code);
        Language.Select(_s.Settings.Language);
        Language.Changed += () =>
        {
            _s.Settings.Language = Language.Value as string ?? "auto";
            _s.Settings.Save();
            Toast(L.T("Dil değişikliği uygulama yeniden açıldığında geçerli olur."));
        };
        if (_s.Editor != null)
        {
            Edit = new MobileEditViewModel(_s.Editor, _s.Converter.CanWrite, NewOutputDir);
            Edit.Message += Toast;
            Edit.Closed += () => IsEditorOpen = false;
            Edit.Saved += OnEditSavedAsync;
        }
        Items.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasItems)); OnPropertyChanged(nameof(ItemCountText)); };

        MobileServices.SharedReceived += files => Dispatcher.UIThread.Post(() => AddFiles(files, openFirst: true));
        List<string> pending;
        lock (MobileServices.PendingShared) { pending = MobileServices.PendingShared.ToList(); MobileServices.PendingShared.Clear(); }
        if (pending.Count > 0) AddFiles(pending, openFirst: true);
    }

    public IPickerHost? Host { get; set; }
    public MobileConvertViewModel Convert { get; }
    public ChoiceGroup Language { get; } = new();
    public ObservableCollection<MobileItem> Items { get; } = new();
    public ObservableCollection<InfoLine> Info { get; } = new();

    public bool HasItems => Items.Count > 0;
    public string ItemCountText => L.T("{0} görüntü", Items.Count);
    public string EditionLabel => _s.EditionLabel;
    public bool HasEdition => !string.IsNullOrEmpty(_s.EditionLabel);
    public string VersionText => $"NexView {typeof(MobileViewModel).Assembly.GetName().Version?.ToString(3)} · {_s.Platform.Name}";
    public bool CanCapture => _s.Platform.CanCapture;
    public MobileEditViewModel? Edit { get; }
    public bool CanEdit => Edit != null;
    public bool CanScan => Edit != null && _s.Platform.CanCapture;
    private DateTime _lastBack;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsGallery), nameof(IsTools), nameof(IsSettings))]
    private MobilePage _page;
    public bool IsGallery => Page == MobilePage.Gallery;
    public bool IsTools => Page == MobilePage.Tools;
    public bool IsSettings => Page == MobilePage.Settings;

    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isSelectMode;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isViewerOpen;
    [ObservableProperty] private bool _isChromeVisible = true;
    [ObservableProperty] private MobileItem? _current;
    [ObservableProperty] private int _currentIndex = -1;
    [ObservableProperty] private Bitmap? _currentBitmap;
    [ObservableProperty] private PixelSize _originalSize;
    [ObservableProperty] private int _rotation;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private bool _isInfoOpen;
    [ObservableProperty] private bool _isConvertOpen;
    [ObservableProperty] private string _convertTitle = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _busyProgress;
    [ObservableProperty] private string? _toastText;
    [ObservableProperty] private double? _latitude;
    [ObservableProperty] private double? _longitude;
    [ObservableProperty] private string _cacheText = "";

    public bool HasLocation => Latitude != null && Longitude != null;
    partial void OnLatitudeChanged(double? value) => OnPropertyChanged(nameof(HasLocation));
    partial void OnPageChanged(MobilePage value) { if (value == MobilePage.Settings) RefreshCache(); }

    // ---------- Gezinme ----------

    [RelayCommand] private void ShowGallery() => Page = MobilePage.Gallery;
    [RelayCommand] private void ShowTools() => Page = MobilePage.Tools;
    [RelayCommand] private void ShowSettings() => Page = MobilePage.Settings;

    /// <summary>Geri tuşu: açık katmanı kapatır. Kapatacak bir şey yoksa false.</summary>
    public bool Back()
    {
        if (IsBusy) return true;
        if (IsEditorOpen && Edit != null)
        {
            if (Edit.IsBusy) return true;
            if (Edit.IsTextPromptOpen) { Edit.CancelTextCommand.Execute(null); return true; }
            if (Edit.IsDirty && (DateTime.UtcNow - _lastBack).TotalSeconds > 3)
            {
                _lastBack = DateTime.UtcNow;
                Toast(L.T("Değişiklikleri atmak için tekrar geri basın."));
                return true;
            }
            Edit.CancelCommand.Execute(null);
            return true;
        }
        if (IsConvertOpen) { IsConvertOpen = false; return true; }
        if (IsInfoOpen) { IsInfoOpen = false; return true; }
        if (IsViewerOpen) { CloseViewer(); return true; }
        if (IsSelectMode) { ToggleSelectMode(); return true; }
        if (Page != MobilePage.Gallery) { Page = MobilePage.Gallery; return true; }
        return false;
    }

    // ---------- İçe aktarma ----------

    [RelayCommand]
    private async Task PickPhotos()
    {
        var sp = Host?.Storage;
        if (sp == null) return;
        var types = new FilePickerFileType(L.T("Görüntüler"))
        {
            Patterns = FormatRegistry.AllExtensions.Select(e => "*" + e).ToList(),
            MimeTypes = ["image/*"],
        };
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, FileTypeFilter = [types, FilePickerFileTypes.All] });
        await ImportAsync(files);
    }

    [RelayCommand]
    private async Task PickFolder()
    {
        var sp = Host?.Storage;
        if (sp == null) return;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.Count == 0) return;
        var files = new List<IStorageFile>();
        await foreach (var it in folders[0].GetItemsAsync())
            if (it is IStorageFile f && FormatRegistry.FromExtension(Path.GetExtension(f.Name)) != null) files.Add(f);
        if (files.Count == 0) { Toast(L.T("Bu klasörde görüntü bulunamadı.")); return; }
        files.Sort((a, b) => ((IComparer<string>)NexView.Core.Library.NaturalStringComparer.Instance).Compare(a.Name, b.Name));
        await ImportAsync(files);
    }

    [RelayCommand]
    private async Task Capture()
    {
        try
        {
            var path = await _s.Platform.CapturePhotoAsync();
            if (path != null) AddFiles([path], openFirst: true);
        }
        catch (Exception ex) { Toast(L.T("Kamera açılamadı: {0}", ex.Message)); }
    }

    private async Task ImportAsync(IReadOnlyList<IStorageFile> files)
    {
        if (files.Count == 0) return;
        var paths = new List<string>();
        await RunBusy(L.T("Görüntüler alınıyor…"), async (progress, ct) =>
        {
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                try
                {
                    var local = f.TryGetLocalPath();
                    if (local != null && File.Exists(local)) { paths.Add(local); }
                    else
                    {
                        var dest = OutputPaths.Unique(Path.Combine(_sessionDir, OutputPaths.SanitizeFileName(f.Name)));
                        await using (var src = await f.OpenReadAsync())
                        await using (var dst = File.Create(dest))
                            await src.CopyToAsync(dst, ct);
                        paths.Add(dest);
                    }
                }
                catch (Exception ex) { Log.Warn($"Mobil içe aktarma: {f.Name}: {ex.Message}"); }
                finally { f.Dispose(); }
                progress((i + 1) / (double)files.Count);
            }
        });
        AddFiles(paths, openFirst: paths.Count == 1);
    }

    public void AddFiles(IReadOnlyList<string> paths, bool openFirst)
    {
        MobileItem? first = null;
        foreach (var p in paths)
        {
            var existing = Items.FirstOrDefault(i => string.Equals(i.Path, p, StringComparison.Ordinal));
            if (existing != null) { first ??= existing; continue; }
            var item = new MobileItem(p);
            Items.Add(item);
            first ??= item;
        }
        Page = MobilePage.Gallery;
        if (openFirst && first != null) Open(first);
        else if (paths.Count > 1) Toast(L.T("{0} görüntü eklendi", paths.Count));
    }

    public void EnsureThumbnail(MobileItem item) => item.EnsureThumbnail(_s.Thumbnails);

    // ---------- Seçim ----------

    [RelayCommand]
    private void ToggleSelectMode()
    {
        IsSelectMode = !IsSelectMode;
        if (!IsSelectMode) foreach (var i in Items) i.IsSelected = false;
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectAll()
    {
        var all = Items.All(i => i.IsSelected);
        foreach (var i in Items) i.IsSelected = !all;
        UpdateSelection();
    }

    [RelayCommand]
    private void ItemTapped(MobileItem item)
    {
        if (IsSelectMode) { item.IsSelected = !item.IsSelected; UpdateSelection(); }
        else Open(item);
    }

    public void ItemHeld(MobileItem item)
    {
        if (!IsSelectMode) IsSelectMode = true;
        item.IsSelected = true;
        UpdateSelection();
    }

    private void UpdateSelection() => SelectedCount = Items.Count(i => i.IsSelected);

    [RelayCommand]
    private void RemoveSelected()
    {
        foreach (var i in Items.Where(i => i.IsSelected).ToList()) Items.Remove(i);
        IsSelectMode = false;
        UpdateSelection();
    }

    [RelayCommand]
    private void ClearAll()
    {
        Items.Clear();
        IsSelectMode = false;
        UpdateSelection();
    }

    private IReadOnlyList<MobileItem> Targets()
    {
        if (IsViewerOpen && Current != null) return [Current];
        var sel = Items.Where(i => i.IsSelected).ToList();
        return sel.Count > 0 ? sel : Items.ToList();
    }

    // ---------- Görüntüleyici ----------

    private void Open(MobileItem item)
    {
        IsViewerOpen = true;
        IsChromeVisible = true;
        _ = LoadAsync(item);
    }

    [RelayCommand]
    private void CloseViewer()
    {
        _loadCts?.Cancel();
        IsViewerOpen = false;
        IsInfoOpen = false;
        var old = CurrentBitmap;
        CurrentBitmap = null;
        old?.Dispose();
        Current = null;
    }

    [RelayCommand] private void Next() => Step(1);
    [RelayCommand] private void Previous() => Step(-1);
    public void Step(int delta)
    {
        if (Current == null || Items.Count < 2) return;
        var i = Items.IndexOf(Current) + delta;
        if (i < 0 || i >= Items.Count) return;
        _ = LoadAsync(Items[i]);
    }

    [RelayCommand] private void ToggleChrome() => IsChromeVisible = !IsChromeVisible;
    [RelayCommand] private void Rotate() => Rotation = (Rotation + 90) % 360;

    private async Task LoadAsync(MobileItem item)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        Current = item;
        CurrentIndex = Items.IndexOf(item);
        PositionText = $"{CurrentIndex + 1} / {Items.Count}";
        Rotation = 0;
        LoadError = null;
        IsLoading = true;
        try
        {
            var detection = FormatDetector.Detect(item.Path);
            var metaTask = Task.Run(() => MetadataService.Read(item.Path), ct);
            var options = new DecodeOptions(_s.Settings.MaxDisplayDimension > 0 ? Math.Min(_s.Settings.MaxDisplayDimension, 4096) : 4096);
            var result = await Task.Run(() => _s.Codecs.Decode(item.Path, detection, options, ct), ct);
            var bmp = await Task.Run(() => BitmapFactory.Create(result.Image), ct);
            if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
            var old = CurrentBitmap;
            OriginalSize = new PixelSize(result.Image.OriginalWidth, result.Image.OriginalHeight);
            CurrentBitmap = bmp;
            old?.Dispose();
            var meta = await metaTask;
            if (ct.IsCancellationRequested) return;
            FillInfo(item, detection, result, meta);
            _s.Library.AddRecent(item.Path);
        }
        catch (OperationCanceledException) { }
        catch (DecodeException ex) { if (!ct.IsCancellationRequested) LoadError = ex.UserReason; }
        catch (Exception ex) { if (!ct.IsCancellationRequested) LoadError = L.T("Görüntü açılamadı: {0}", ex.Message); }
        finally { if (_loadCts == cts) IsLoading = false; }
    }

    private void FillInfo(MobileItem item, DetectionResult det, DecodeResult r, ImageMetadata m)
    {
        Info.Clear();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) Info.Add(new InfoLine(label, value)); }
        var p = r.Properties;
        Add(L.T("Dosya"), item.DisplayName);
        Add(L.T("Format"), det.Format?.DisplayName ?? p.CodecFormat);
        Add(L.T("Boyut"), $"{r.Image.OriginalWidth} × {r.Image.OriginalHeight} px · {MegaPixels(r.Image.OriginalWidth, r.Image.OriginalHeight)}");
        Add(L.T("Dosya boyutu"), FormatBytes(item.SizeBytes));
        if (p.BitDepth > 0) Add(L.T("Bit derinliği"), $"{p.BitDepth} bit · {p.ColorSpace}");
        if (det.ExtensionMismatch) Add(L.T("Uyarı"), L.T("Dosya uzantısı içerikle uyuşmuyor"));
        Add(L.T("Çekim tarihi"), m.DateTaken?.ToString("g"));
        Add(L.T("Kamera"), string.Join(" ", new[] { m.CameraMake, m.CameraModel }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()));
        Add(L.T("Lens"), m.Lens);
        var exposure = string.Join("  ", new[] { m.FNumber, m.ExposureTime, m.Iso != null ? "ISO " + m.Iso : null, m.FocalLength }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Add(L.T("Pozlama"), exposure);
        Latitude = m.Latitude;
        Longitude = m.Longitude;
        if (HasLocation) Add(L.T("Konum"), MetadataService.FormatCoordinates(m.Latitude!.Value, m.Longitude!.Value));
        Add(L.T("Metadata"), string.Join(", ", new[] { m.HasExif ? "EXIF" : null, m.HasIptc ? "IPTC" : null, m.HasXmp ? "XMP" : null }.Where(s => s != null)));
        Add(L.T("Çözücü"), r.DecoderName);
    }

    private static string MegaPixels(int w, int h) => $"{w * (double)h / 1_000_000:0.#} MP";

    public static string FormatBytes(long b) => b switch
    {
        >= 1024 * 1024 => $"{b / 1024d / 1024:0.0} MB",
        >= 1024 => $"{b / 1024d:0} KB",
        _ => $"{b} B",
    };

    [RelayCommand] private void ShowInfo() => IsInfoOpen = !IsInfoOpen;

    [RelayCommand]
    private void OpenMap()
    {
        if (!HasLocation) return;
        var lat = Latitude!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lon = Longitude!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _s.Platform.OpenUri($"geo:{lat},{lon}?q={lat},{lon}");
    }

    // ---------- Düzenleme ve belge tarama ----------

    [RelayCommand] private Task EditCurrent() => StartEditAsync(Current, documentMode: false);
    [RelayCommand] private Task DocumentCurrent() => StartEditAsync(Current, documentMode: true);

    private async Task StartEditAsync(MobileItem? item, bool documentMode)
    {
        if (Edit == null || item == null) return;
        IsInfoOpen = false;
        IsEditorOpen = true;
        if (!await Edit.StartAsync(item.Path, item.DisplayName, documentMode)) IsEditorOpen = false;
    }

    /// <summary>Kamerayla belge çeker, köşeleri bulur ve belge düzenleyicisini açar.</summary>
    [RelayCommand]
    private async Task ScanDocument()
    {
        if (Edit == null) return;
        string? path;
        try { path = await _s.Platform.CapturePhotoAsync(); }
        catch (Exception ex) { Toast(L.T("Kamera açılamadı: {0}", ex.Message)); return; }
        if (path == null) return;
        var item = new MobileItem(path);
        Items.Add(item);
        await StartEditAsync(item, documentMode: true);
    }

    private async Task OnEditSavedAsync(string output, bool share)
    {
        var item = new MobileItem(output);
        var index = Current != null ? Items.IndexOf(Current) + 1 : Items.Count;
        Items.Insert(Math.Clamp(index, 0, Items.Count), item);
        await DeliverAsync([output], share);
        Open(item);
    }

    // ---------- Paylaş ve dönüştür ----------

    [RelayCommand]
    private async Task ShareOriginal()
    {
        var t = Targets();
        if (t.Count == 0) return;
        try { await _s.Platform.ShareAsync(t.Select(i => i.Path).ToList()); }
        catch (Exception ex) { Toast(ex.Message); }
    }

    /// <summary>Konum ve cihaz bilgisi silinmiş, küçültülmüş JPEG olarak paylaşır.</summary>
    [RelayCommand] private Task ShareClean() => RunPresetAsync("share", share: true);
    [RelayCommand] private Task OfficialDoc() => RunPresetAsync("official", share: false);
    [RelayCommand] private Task EmailReady() => RunPresetAsync("email", share: true);

    [RelayCommand]
    private Task RemoveLocation()
    {
        var o = new ConversionOptions { Quality = 95, Metadata = MetadataPolicy.RemoveGps };
        return RunAsync(Targets(), src =>
        {
            var c = o.Clone();
            var f = FormatRegistry.FromPath(src);
            c.Target = f != null && _s.Converter.CanWrite(f) && f.Id != "PDF" ? f : FormatRegistry.Jpeg;
            return c;
        }, share: false, L.T("Konum bilgisi siliniyor…"));
    }

    private Task RunPresetAsync(string presetId, bool share)
    {
        var o = Presets.Resolve(presetId) ?? new ConversionOptions();
        return RunAsync(Targets(), _ => o, share, L.T("Hazırlanıyor…"));
    }

    [RelayCommand]
    private void OpenConvert()
    {
        _convertTargets = Targets();
        if (_convertTargets.Count == 0) { Toast(L.T("Önce görüntü ekleyin.")); return; }
        ConvertTitle = _convertTargets.Count == 1 ? _convertTargets[0].DisplayName : L.T("{0} görüntü", _convertTargets.Count);
        IsInfoOpen = false;
        IsConvertOpen = true;
    }

    [RelayCommand] private void CloseConvert() => IsConvertOpen = false;
    [RelayCommand] private Task ConvertSave() => ConvertRun(false);
    [RelayCommand] private Task ConvertShare() => ConvertRun(true);

    private async Task ConvertRun(bool share)
    {
        var o = Convert.Build();
        _s.Settings.ConvertPreset = Convert.PresetId;
        _s.Settings.ConvertTarget = o.Target.Id;
        _s.Settings.ConvertQuality = o.Quality;
        _s.Settings.Save();
        IsConvertOpen = false;
        if (o.Target.Id == "PDF") await MakePdfAsync(_convertTargets, o, share);
        else await RunAsync(_convertTargets, _ => o, share, L.T("Dönüştürülüyor…"));
    }

    [RelayCommand]
    private Task MakePdf() => MakePdfAsync(Targets(), Presets.Resolve("pdf")!, share: false);

    private async Task MakePdfAsync(IReadOnlyList<MobileItem> items, ConversionOptions o, bool share)
    {
        if (items.Count == 0) { Toast(L.T("Önce görüntü ekleyin.")); return; }
        var outDir = NewOutputDir();
        var name = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].DisplayName) : $"NexView_{DateTime.Now:yyyyMMdd_HHmmss}";
        var output = Path.Combine(outDir, OutputPaths.SanitizeFileName(name) + ".pdf");
        ConversionResult? r = null;
        await RunBusy(L.T("PDF oluşturuluyor…"), (progress, ct) =>
        {
            r = _s.Converter.CreatePdf(items.Select(i => i.Path).ToList(), output, o,
                new Progress<int>(n => progress(n / (double)items.Count)), ct);
            return Task.CompletedTask;
        });
        if (r is not { Status: ConversionStatus.Success, Output: { } path }) { Toast(L.T("PDF oluşturulamadı: {0}", r?.Message ?? "")); return; }
        await DeliverAsync([path], share);
    }

    private async Task RunAsync(IReadOnlyList<MobileItem> items, Func<string, ConversionOptions> options, bool share, string busyText)
    {
        if (items.Count == 0) { Toast(L.T("Önce görüntü ekleyin.")); return; }
        var outDir = NewOutputDir();
        var outputs = new List<string>();
        var failed = new List<string>();
        await RunBusy(busyText, (progress, ct) =>
        {
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var it = items[i];
                var output = new OutputOptions { Folder = outDir, Conflict = ConflictPolicy.Rename };
                var r = _s.Converter.Convert(it.Path, options(it.Path), output, ct);
                if (r.Status == ConversionStatus.Success && r.Output != null) outputs.Add(r.Output);
                else failed.Add($"{it.DisplayName}: {r.Message}");
                progress((i + 1) / (double)items.Count);
            }
            return Task.CompletedTask;
        });
        if (failed.Count > 0) Log.Warn("Mobil dönüştürme hataları: " + string.Join(" | ", failed));
        if (outputs.Count == 0) { Toast(failed.Count > 0 ? failed[0] : L.T("İşlem iptal edildi.")); return; }
        await DeliverAsync(outputs, share);
        if (failed.Count > 0) Toast(L.T("{0} dosya başarısız oldu.", failed.Count));
    }

    private async Task DeliverAsync(IReadOnlyList<string> outputs, bool share)
    {
        try
        {
            if (share) { await _s.Platform.ShareAsync(outputs); return; }
            string? where = null;
            foreach (var o in outputs)
                where = await _s.Platform.SaveToGalleryAsync(o, Path.GetFileName(o), MimeTypes.For(o)) ?? where;
            var total = outputs.Sum(o => new FileInfo(o).Length);
            Toast(outputs.Count == 1
                ? L.T("Kaydedildi: {0} ({1})", where ?? Path.GetFileName(outputs[0]), FormatBytes(total))
                : L.T("{0} dosya kaydedildi: {1}", outputs.Count, where ?? ""));
        }
        catch (Exception ex) { Toast(L.T("Kaydedilemedi: {0}", ex.Message)); }
    }

    private string NewOutputDir()
    {
        var root = Path.Combine(_s.Platform.CacheDir, "out");
        // Eski çıktıları temizle (paylaşım uygulamaları dosyayı okuduktan sonra gereksiz).
        try
        {
            if (Directory.Exists(root))
                foreach (var d in Directory.GetDirectories(root))
                    if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddHours(-6)) Directory.Delete(d, true);
        }
        catch { }
        var dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- Meşgul / bildirim ----------

    private CancellationTokenSource? _busyCts;

    private async Task RunBusy(string text, Func<Action<double>, CancellationToken, Task> work)
    {
        _busyCts = new CancellationTokenSource();
        var ct = _busyCts.Token;
        BusyText = text;
        BusyProgress = 0;
        IsBusy = true;
        try
        {
            await Task.Run(() => work(p => Dispatcher.UIThread.Post(() => BusyProgress = p * 100), ct), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Mobil işlem", ex); Toast(ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void CancelBusy() => _busyCts?.Cancel();

    public async void Toast(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastText = text;
        try { await Task.Delay(3500, cts.Token); ToastText = null; } catch (OperationCanceledException) { }
    }

    // ---------- Güncelleme ----------

    private NexView.Core.Updates.UpdateInfo? _update;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;

    [RelayCommand]
    private async Task CheckUpdates()
    {
        UpdateStatus = L.T("Denetleniyor…");
        UpdateAvailable = false;
        try
        {
            var current = typeof(MobileViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);
            _update = await NexView.Core.Updates.UpdateChecker.CheckAsync(current, CancellationToken.None);
            UpdateAvailable = _update?.Find(".apk") != null;
            UpdateStatus = _update == null ? L.T("NexView güncel ({0}).", current.ToString(3))
                : UpdateAvailable ? L.T("Yeni sürüm var: {0}", _update.Title)
                : L.T("Yeni sürüm var ama Android paketi henüz yayımlanmamış: {0}", _update.Title);
        }
        catch (Exception ex)
        {
            Log.Warn("Güncelleme denetlenemedi", ex);
            UpdateStatus = L.T("Güncelleme denetlenemedi. İnternet bağlantınızı kontrol edin.");
        }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_update?.Find(".apk") is not { } asset) return;
        string? file = null;
        await RunBusy(L.T("İndiriliyor: {0}", asset.Name), async (progress, ct) =>
            file = await NexView.Core.Updates.UpdateChecker.DownloadAsync(asset, Path.Combine(_s.Platform.CacheDir, "update"), new Progress<double>(progress), ct));
        if (file == null) return;
        try { await _s.Platform.InstallPackageAsync(file); }
        catch (Exception ex) { Toast(ex.Message); }
    }

    // ---------- Ayarlar ----------

    private void RefreshCache()
    {
        long size = 0;
        try
        {
            size = _s.Thumbnails.GetSizeBytes();
            var c = _s.Platform.CacheDir;
            if (Directory.Exists(c)) size += new DirectoryInfo(c).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { }
        CacheText = FormatBytes(size);
    }

    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            _s.Thumbnails.Clear();
            var used = Items.Select(i => i.Path).ToHashSet();
            foreach (var dir in new[] { Path.Combine(_s.Platform.CacheDir, "out"), _sessionDir })
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        if (!used.Contains(f)) try { File.Delete(f); } catch { }
        }
        catch { }
        RefreshCache();
        Toast(L.T("Önbellek temizlendi."));
    }

    public string ReadableFormatsText => string.Join(" · ", FormatRegistry.All
        .Where(f => _s.Codecs.GetSupport(f) != FormatSupport.Unsupported && f.Id != "PDF").Select(f => f.Id));
    public string WritableFormatsText => string.Join(" · ", Convert.Format.Items.Select(c => c.Label));
}
