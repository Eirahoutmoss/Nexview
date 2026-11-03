using NexView.Platform.Windows;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.UI.Controls;
using NexView.UI;
using NexView.App.Services;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Library;
using NexView.Core.Localization;
using NexView.Core.Metadata;

namespace NexView.App.ViewModels;

public enum NavSection { Folder, Favorites, Recent, Raw, Screenshots, Converted }
public enum InspectorTab { Info, Edit, Convert, Batch }

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private const long MaxAnimationBytes = 400L * 1024 * 1024;

    private readonly AppServices _s;
    private List<string> _folderFiles = new();
    private CancellationTokenSource? _loadCts;
    private Task _currentLoad = Task.CompletedTask;
    private string? _lastError;
    private ThumbnailItem? _errorItem;
    private bool _ownsCurrentBitmap = true;
    private List<(Bitmap Bitmap, int Delay)>? _frames;
    private int _frameIndex;
    private readonly DispatcherTimer _animTimer = new();
    private CancellationTokenSource? _toastCts;
    private ImageFormat? _currentFormat;
    private readonly Dictionary<string, DecodeResult> _prefetch = new();
    private CancellationTokenSource? _prefetchCts;
    private const long PrefetchMaxPixels = 40_000_000;

    public MainViewModel(AppServices services)
    {
        _s = services;
        _showSidebar = _s.Settings.ShowSidebar;
        _showInspector = _s.Settings.ShowInspector;
        _showFilmstrip = _s.Settings.ShowFilmstrip;
        _viewerBackground = Enum.TryParse<ViewerBackground>(_s.Settings.ViewerBackground, out var bg) ? bg : ViewerBackground.Dark;
        SupportedFormatsLine = "JPG • PNG • HEIC • RAW • TIFF • WEBP • AVIF • PSD • JXL • FITS";
        Convert = new ConvertViewModel(this, services);
        SettingsPanel = new SettingsViewModel(this, services);
        Edit = new EditViewModel(this, services, services.Editor);
        Stack = new StackViewModel(this, services);
        _animTimer.Tick += OnAnimationTick;
        UpdateEmptyText();
    }

    public IUiHost? Host { get; set; }
    public AppServices Services => _s;
    public ConvertViewModel Convert { get; }
    public SettingsViewModel SettingsPanel { get; }
    public EditViewModel Edit { get; }
    public StackViewModel Stack { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatchConvertMode))]
    private bool _isStackMode;
    public bool IsBatchConvertMode => !IsStackMode;
    [RelayCommand] private void SetBatchMode(string mode) => IsStackMode = mode == "stack";

    public ObservableCollection<ThumbnailItem> Items { get; } = new();
    public ObservableCollection<InfoRow> FileRows { get; } = new();
    public ObservableCollection<InfoRow> CameraRows { get; } = new();
    public ObservableCollection<InfoRow> TechRows { get; } = new();
    public ObservableCollection<MetadataEntry> MetadataEntries { get; } = new();

    public string SupportedFormatsLine { get; }
    public string AppVersion => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}" + (Edition.HasDedication ? $" · {Edition.Name}" : "");
    public bool IsWindows => WindowsShell.IsSupported;

    [ObservableProperty] private ThumbnailItem? _selectedItem;
    /// <summary>Geçerli görüntü 360° panorama (GPano XMP ya da 2:1 eşdikdörtgen).</summary>
    [ObservableProperty] private bool _is360;
    [ObservableProperty] private Bitmap? _currentBitmap;
    [ObservableProperty] private PixelSize _originalSize;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private string? _errorFormat;
    [ObservableProperty] private string? _errorReason;
    [ObservableProperty] private string? _errorFileName;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptySubtitle = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _rotation;
    [ObservableProperty] private bool _flipHorizontal;
    [ObservableProperty] private ViewerBackground _viewerBackground;
    [ObservableProperty] private bool _showSidebar;
    [ObservableProperty] private bool _showInspector;
    [ObservableProperty] private bool _showFilmstrip;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private bool _isCurrentFavorite;
    [ObservableProperty] private bool _hasCamera;
    [ObservableProperty] private bool _hasGps;
    [ObservableProperty] private string? _gpsText;
    [ObservableProperty] private bool _hasFormatWarning;
    [ObservableProperty] private string? _formatWarning;
    [ObservableProperty] private bool _hasInspectorData;
    [ObservableProperty] private bool _hasTech;
    [ObservableProperty] private bool _hasMetadata;
    [ObservableProperty] private string _metadataHeader = "";
    [ObservableProperty] private string _windowCaption = "";
    [ObservableProperty] private string _collectionTitle = "";
    [ObservableProperty] private bool _hasScreenshotsFolder = PlatformShell.ScreenshotsFolder() != null;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showShortcuts;
    [RelayCommand] private void ToggleShortcuts() => ShowShortcuts = !ShowShortcuts;

    /// <summary>Açılışta son görüntülenen dosyayı yeniden açar (ayar açıksa).</summary>
    public async Task RestoreSessionAsync()
    {
        if (!_s.Settings.RestoreSession || string.IsNullOrEmpty(_s.Settings.LastPath)) return;
        if (File.Exists(_s.Settings.LastPath)) await OpenPathsAsync([_s.Settings.LastPath]);
    }
    [ObservableProperty] private bool _autoStretch;
    [ObservableProperty] private bool _showStretchToggle;
    [ObservableProperty] private bool _isAnimated;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _frameText = "";
    [ObservableProperty] private Bitmap? _compareBitmap;
    [ObservableProperty] private double _splitPosition = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderSection), nameof(IsFavoritesSection), nameof(IsRecentSection), nameof(IsRawSection), nameof(IsScreenshotsSection), nameof(IsConvertedSection))]
    private NavSection _section = NavSection.Folder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInfoTab), nameof(IsEditTab), nameof(IsConvertTab), nameof(IsBatchTab))]
    private InspectorTab _tab = InspectorTab.Info;

    public bool IsFolderSection => Section == NavSection.Folder;
    public bool IsFavoritesSection => Section == NavSection.Favorites;
    public bool IsRecentSection => Section == NavSection.Recent;
    public bool IsRawSection => Section == NavSection.Raw;
    public bool IsScreenshotsSection => Section == NavSection.Screenshots;
    public bool IsConvertedSection => Section == NavSection.Converted;
    public bool IsInfoTab => Tab == InspectorTab.Info;
    public bool IsEditTab => Tab == InspectorTab.Edit;
    public bool IsConvertTab => Tab == InspectorTab.Convert;
    public bool IsBatchTab => Tab == InspectorTab.Batch;

    public bool IsEmpty => !HasImage && !HasError && !IsLoading;
    public bool ShowChrome => !IsFullScreen;
    public bool ShowSidebarPanel => ShowSidebar && !IsFullScreen;
    public bool ShowInspectorPanel => ShowInspector && !IsFullScreen;
    public bool ShowFilmstripPanel => ShowFilmstrip && !IsFullScreen && Items.Count > 1;
    public bool HasDialog => Dialog != null;
    public bool HasToast => !string.IsNullOrEmpty(Toast);
    public string PositionText => SelectedItem == null ? "" : $"{Items.IndexOf(SelectedItem) + 1} / {Items.Count}";
    public string BackgroundLabel => ViewerBackground switch
    {
        ViewerBackground.Checkerboard => L.T("Dama"),
        ViewerBackground.Black => L.T("Siyah"),
        ViewerBackground.White => L.T("Beyaz"),
        _ => L.T("Koyu"),
    };

    partial void OnHasImageChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnDialogChanged(DialogViewModel? value) => OnPropertyChanged(nameof(HasDialog));
    partial void OnToastChanged(string? value) => OnPropertyChanged(nameof(HasToast));
    partial void OnShowSidebarChanged(bool value) { _s.Settings.ShowSidebar = value; OnPropertyChanged(nameof(ShowSidebarPanel)); }
    partial void OnShowInspectorChanged(bool value) { _s.Settings.ShowInspector = value; OnPropertyChanged(nameof(ShowInspectorPanel)); }
    partial void OnShowFilmstripChanged(bool value) { _s.Settings.ShowFilmstrip = value; OnPropertyChanged(nameof(ShowFilmstripPanel)); }
    partial void OnViewerBackgroundChanged(ViewerBackground value) { _s.Settings.ViewerBackground = value.ToString(); OnPropertyChanged(nameof(BackgroundLabel)); }
    partial void OnShowSettingsChanged(bool value) { if (value) _ = SettingsPanel.RefreshAsync(); }

    private bool _suppressStretchReload;

    partial void OnAutoStretchChanged(bool value)
    {
        if (!_suppressStretchReload && SelectedItem != null) _currentLoad = LoadAsync(SelectedItem, alternative: false, keepStretch: true);
    }

    partial void OnIsFullScreenChanged(bool value)
    {
        Host?.SetFullScreen(value);
        OnPropertyChanged(nameof(ShowChrome));
        OnPropertyChanged(nameof(ShowSidebarPanel));
        OnPropertyChanged(nameof(ShowInspectorPanel));
        OnPropertyChanged(nameof(ShowFilmstripPanel));
    }

    partial void OnSelectedItemChanged(ThumbnailItem? value)
    {
        OnPropertyChanged(nameof(PositionText));
        Convert.NotifySourceChanged();
        if (value == null) return;
        IsCurrentFavorite = value.IsFavorite;
        _currentLoad = Edit.IsActive ? SwitchWhileEditingAsync(value) : LoadAsync(value, alternative: false);
    }

    private async Task SwitchWhileEditingAsync(ThumbnailItem value)
    {
        await Edit.ExitAsync(ask: true);
        if (Tab == InspectorTab.Edit) Tab = InspectorTab.Info;
        if (ReferenceEquals(SelectedItem, value)) await LoadAsync(value, alternative: false);
    }

    public async Task ApplyStartupTabAsync(string? tab)
    {
        switch (tab)
        {
            case "convert": await ShowConvertTab(); break;
            case "info": await ShowInfoTab(); break;
            case "batch": await OpenBatch(); break;
            case "edit": await WaitForIdleAsync(); await ShowEditTab(); break;
        }
    }

    /// <summary>Komut satırı / sağ tık menüsünden gelen sekme isteği (convert, batch, edit, info).</summary>
    public async Task ApplyStartTabAsync(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;
        await WaitForIdleAsync();
        switch (tab)
        {
            case "convert": await ShowConvertTab(); break;
            case "batch": await OpenBatch(); break;
            case "edit": await ShowEditTab(); break;
            case "info": await ShowInfoTab(); break;
        }
    }

    // ---------- Düzenleme ----------

    internal void ShowEditPreview(Bitmap bitmap)
    {
        StopAnimation();
        Rotation = 0;
        FlipHorizontal = false;
        OriginalSize = bitmap.PixelSize;
        ReplaceBitmap(bitmap, owned: true);
        HasImage = true;
        HasError = false;
        StatusText = L.T("Düzenleniyor: değişiklikler yeni dosyaya kaydedilir");
    }

    internal void SetEditCompare(Bitmap? compare, double split)
    {
        CompareBitmap = compare;
        SplitPosition = split;
    }

    internal void EndEditing()
    {
        if (Tab == InspectorTab.Edit) Tab = InspectorTab.Info;
        if (SelectedItem != null) _currentLoad = LoadAsync(SelectedItem, alternative: false);
    }

    internal async Task EndEditingAndOpenAsync(string path)
    {
        if (Tab == InspectorTab.Edit) Tab = InspectorTab.Info;
        await OpenPathsAsync([path]);
    }

    private async Task<bool> LeaveEditAsync()
    {
        if (!Edit.IsActive) return true;
        if (!await Edit.ExitAsync(ask: true)) return false;
        if (SelectedItem != null) _currentLoad = LoadAsync(SelectedItem, alternative: false);
        return true;
    }

    [RelayCommand]
    private async Task ShowEditTab()
    {
        if (SelectedItem is not { } item || HasError) return;
        Tab = InspectorTab.Edit;
        ShowInspector = true;
        await Edit.StartAsync(item);
        if (!Edit.IsActive && Tab == InspectorTab.Edit) Tab = InspectorTab.Info;
    }

    public Task WaitForIdleAsync() => _currentLoad;

    // ---------- Bildirim ve iletişim kutusu ----------

    public async void ShowToast(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = text;
        try { await Task.Delay(3500, cts.Token); Toast = null; } catch (OperationCanceledException) { }
    }

    public async Task<DialogChoice> ShowDialogAsync(DialogViewModel dialog)
    {
        Dialog = dialog;
        try { return await dialog.Result; }
        finally { if (ReferenceEquals(Dialog, dialog)) Dialog = null; }
    }

    // ---------- Açma ----------

    public async Task HandleDropAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1 && File.Exists(paths[0]))
        {
            await OpenPathsAsync(paths);
            return;
        }
        var files = await Task.Run(() => Expand(paths));
        if (files.Count == 0) { ShowToast(L.T("Bırakılan öğelerde görüntü bulunamadı.")); return; }
        var choice = await ShowDialogAsync(new DialogViewModel(
            L.T("{0} görüntü bulundu", files.Count), L.T("Ne yapmak istersiniz?"), L.T("Hepsini aç"), L.T("Toplu dönüştür")));
        if (choice == DialogChoice.Cancel) return;
        SetCollection(files, L.T("{0} seçili görüntü", files.Count));
        if (choice == DialogChoice.Secondary) await OpenBatch();
    }

    private static List<string> Expand(IEnumerable<string> paths)
    {
        var all = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) all.AddRange(FolderScanner.GetImages(p));
            else if (File.Exists(p)) all.Add(p);
        }
        var list = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list.Sort(NaturalStringComparer.Instance);
        return list;
    }

    public async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0) return;

        if (existing.Count == 1 && Directory.Exists(existing[0]))
        {
            var folder = existing[0];
            var files = await Task.Run(() => FolderScanner.GetImages(folder));
            SetCollection(files, FolderTitle(folder));
            return;
        }

        if (existing.Count == 1)
        {
            var file = existing[0];
            var folder = Path.GetDirectoryName(file) ?? "";
            var files = await Task.Run(() => FolderScanner.GetImages(folder));
            if (!files.Contains(file, StringComparer.OrdinalIgnoreCase)) { files.Add(file); files.Sort(NaturalStringComparer.Instance); }
            SetCollection(files, FolderTitle(folder), file);
            return;
        }

        var many = await Task.Run(() => Expand(existing));
        SetCollection(many, L.T("{0} seçili görüntü", many.Count));
    }

    private static string FolderTitle(string folder) =>
        Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;

    private void SetCollection(List<string> files, string title, string? select = null)
    {
        _folderFiles = files;
        CollectionTitle = title;
        Section = NavSection.Folder;
        UpdateEmptyText();
        SetItems(files, select);
    }

    private void SetItems(IReadOnlyList<string> paths, string? select)
    {
        foreach (var old in Items) old.Release();
        Items.Clear();
        foreach (var p in paths) Items.Add(new ThumbnailItem(p, _s.Library.IsFavorite(p)));
        OnPropertyChanged(nameof(ShowFilmstripPanel));
        Convert.NotifySourceChanged();
        Stack.NotifySourceChanged();

        var target = select == null ? Items.FirstOrDefault()
            : Items.FirstOrDefault(i => string.Equals(i.Path, select, StringComparison.OrdinalIgnoreCase)) ?? Items.FirstOrDefault();
        if (target == null)
        {
            SelectedItem = null;
            ClearImage();
            UpdateEmptyText();
        }
        else if (ReferenceEquals(target, SelectedItem))
        {
            _currentLoad = LoadAsync(target, false);
        }
        else
        {
            SelectedItem = target;
        }
        UpdateCaption();
    }

    private void ClearImage()
    {
        _loadCts?.Cancel();
        StopAnimation();
        ReplaceBitmap(null, owned: true);
        HasImage = false;
        HasError = false;
        IsLoading = false;
        IsPreview = false;
        ShowStretchToggle = false;
        ClearInspector();
        UpdateCaption();
    }

    private void UpdateEmptyText()
    {
        (EmptyTitle, EmptySubtitle) = Section switch
        {
            NavSection.Favorites => (L.T("Henüz favori yok"), L.T("Bir görüntüyü yıldız düğmesiyle favorilere ekleyebilirsiniz.")),
            NavSection.Recent => (L.T("Son açılan görüntü yok"), L.T("Açtığınız görüntüler burada listelenir.")),
            NavSection.Raw => (L.T("Bu listede RAW dosyası yok"), L.T("DNG, CR2, CR3, NEF, ARW, RAF, ORF, RW2, SRW desteklenir.")),
            NavSection.Screenshots => (L.T("Ekran görüntüsü bulunamadı"), L.T("Resimler\\Screenshots klasörü boş.")),
            NavSection.Converted => (L.T("Henüz dönüştürülen dosya yok"), L.T("NexView ile dönüştürdüğünüz dosyalar burada listelenir.")),
            _ => (L.T("Görüntülerinizi buraya bırakın"), L.T("veya")),
        };
    }

    private void UpdateCaption()
    {
        WindowCaption = SelectedItem == null ? "" : $"{SelectedItem.FileName}   ·   {PositionText}";
    }

    // ---------- Yükleme ----------

    private async Task LoadAsync(ThumbnailItem item, bool alternative, bool keepStretch = false)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;
        var path = item.Path;

        StopAnimation();
        IsLoading = true;
        HasError = false;
        IsPreview = false;
        if (!keepStretch)
        {
            Rotation = 0;
            Is360 = false;
            FlipHorizontal = false;
            Host?.ResetView();
        }
        StatusText = "";
        UpdateCaption();

        DetectionResult? detection = null;
        Task<ImageMetadata>? metaTask = null;
        try
        {
            detection = await Task.Run(() => FormatDetector.Detect(path), ct);
            _currentFormat = detection.Format;
            var fmt = detection.Format;
            ShowStretchToggle = fmt != null && (fmt.Family == FormatFamily.Hdr || fmt.IsRaw || fmt.Id == "TIFF");
            if (!keepStretch)
            {
                var stretch = fmt?.Id == "FITS" && _s.Settings.AutoStretchFits;
                _suppressStretchReload = true;
                try { AutoStretch = stretch; } finally { _suppressStretchReload = false; }
            }

            metaTask = Task.Run(() => MetadataService.Read(path), ct);
            var options = new DecodeOptions(_s.Settings.MaxDisplayDimension, SkipPrimary: alternative, AutoStretch: AutoStretch);

            if (!alternative && !AutoStretch && fmt?.IsRaw == true)
            {
                var preview = await Task.Run(() => _s.Codecs.TryDecodePreview(path, fmt, options.MaxDimension, ct), ct);
                if (preview != null && !ct.IsCancellationRequested)
                {
                    var pbmp = await Task.Run(() => BitmapFactory.Create(preview.Image), ct);
                    if (ct.IsCancellationRequested) { pbmp.Dispose(); return; }
                    ShowBitmap(pbmp, preview.Image);
                    IsPreview = true;
                    StatusText = L.T("Gömülü önizleme gösteriliyor, RAW çözümleniyor…");
                }
            }

            var key = PrefetchKey(path, AutoStretch);
            DecodeResult result;
            if (!alternative && _prefetch.Remove(key, out var cached)) result = cached;
            else result = await Task.Run(() => _s.Codecs.Decode(path, detection, options, ct), ct);
            var bitmap = await Task.Run(() => BitmapFactory.Create(result.Image), ct);
            if (ct.IsCancellationRequested) { bitmap.Dispose(); return; }

            ShowBitmap(bitmap, result.Image);
            IsPreview = result.IsPreview;
            StatusText = result.IsPreview ? L.T("Gömülü önizleme (tam RAW çözümlemesi yapılamadı)")
                : AutoStretch ? L.T("Otomatik germe açık (yalnızca görünüm)")
                : result.UsedFallback ? L.T("Alternatif çözücüyle açıldı: {0}", result.DecoderName)
                : result.Image.IsDownsampled ? L.T("Ekran için {0}×{1} önizleme", result.Image.Width, result.Image.Height) : "";
            HasError = false;

            var meta = await metaTask;
            if (ct.IsCancellationRequested) return;
            FillInspector(item, detection, result, meta);
            _s.Library.AddRecent(path);
            int ow = result.Image.OriginalWidth, oh = result.Image.OriginalHeight;
            Is360 = await Task.Run(() => Core.Metadata.PanoramaInfo.TryRead(path) != null || (oh > 0 && Math.Abs(ow / (double)oh - 2) < 0.04 && ow >= 2000));
            _s.Settings.LastPath = path;
            _ = PrefetchNeighborsAsync(item);

            if (result.Properties.FrameCount > 1 && fmt?.MayAnimate == true)
                _ = LoadAnimationAsync(path, fmt, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Log.Warn($"Görüntü açılamadı: {path}", ex);
            var meta = metaTask != null ? await SafeAwait(metaTask) : null;
            ShowError(item, detection, ex, meta);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    private static string PrefetchKey(string path, bool stretch) => path + "|" + stretch;

    /// <summary>Bir sonraki ve önceki görüntüyü arka planda çözer; gezinme anında olur.</summary>
    private async Task PrefetchNeighborsAsync(ThumbnailItem current)
    {
        _prefetchCts?.Cancel();
        var cts = _prefetchCts = new CancellationTokenSource();
        var ct = cts.Token;
        var idx = Items.IndexOf(current);
        if (idx < 0 || Items.Count < 2) return;
        var wanted = new[] { (idx + 1) % Items.Count, (idx - 1 + Items.Count) % Items.Count }
            .Distinct().Where(i => i != idx).Select(i => Items[i]).ToList();
        var stretch = AutoStretch;
        var keep = wanted.Select(w => PrefetchKey(w.Path, stretch)).ToHashSet();
        foreach (var k in _prefetch.Keys.Where(k => !keep.Contains(k)).ToList()) _prefetch.Remove(k);

        foreach (var item in wanted)
        {
            if (ct.IsCancellationRequested) return;
            var key = PrefetchKey(item.Path, stretch);
            if (_prefetch.ContainsKey(key) || item.IsRaw || item.Format?.MayAnimate == true) continue;
            try
            {
                var options = new DecodeOptions(_s.Settings.MaxDisplayDimension, AutoStretch: stretch);
                var r = await Task.Run(() =>
                {
                    var det = FormatDetector.Detect(item.Path);
                    return _s.Codecs.Decode(item.Path, det, options, ct);
                }, ct);
                if (!ct.IsCancellationRequested && (long)r.Image.Width * r.Image.Height <= PrefetchMaxPixels) _prefetch[key] = r;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Debug($"Ön yükleme atlandı: {item.Path}: {ex.Message}"); }
        }
    }

    private static async Task<ImageMetadata?> SafeAwait(Task<ImageMetadata> t)
    {
        try { return await t; } catch { return null; }
    }

    private void ReplaceBitmap(Bitmap? bitmap, bool owned)
    {
        var old = CurrentBitmap;
        var disposeOld = _ownsCurrentBitmap;
        CurrentBitmap = bitmap;
        _ownsCurrentBitmap = owned;
        if (disposeOld && old != null && !ReferenceEquals(old, bitmap)) old.Dispose();
    }

    private void ShowBitmap(Bitmap bitmap, DecodedImage image)
    {
        OriginalSize = new PixelSize(image.OriginalWidth, image.OriginalHeight);
        ReplaceBitmap(bitmap, owned: true);
        HasImage = true;
    }

    private void ShowError(ThumbnailItem item, DetectionResult? detection, Exception ex, ImageMetadata? meta)
    {
        ReplaceBitmap(null, owned: true);
        HasImage = false;
        IsPreview = false;
        StatusText = "";

        var format = detection?.Format;
        ErrorFileName = item.FileName;
        ErrorFormat = format?.DisplayName ?? L.T("Tanınmayan ({0})", Path.GetExtension(item.Path));
        ErrorReason = ex is DecodeException de ? de.UserReason
            : ex is FileNotFoundException ? L.T("Dosya bulunamadı. Taşınmış ya da silinmiş olabilir.")
            : ex is UnauthorizedAccessException ? L.T("Dosyaya erişim izni yok.")
            : L.T("Görüntü okunamadı.");
        _lastError = $"NexView\n{item.Path}\n{ErrorFormat}\n{ErrorReason}\n\n{ex}";
        _errorItem = item;
        HasError = true;

        ClearInspector();
        AddFileRows(item, detection, null);
        if (meta != null) FillMetadata(meta);
        HasInspectorData = true;
    }

    // ---------- Animasyon ----------

    private async Task LoadAnimationAsync(string path, ImageFormat fmt, CancellationToken ct)
    {
        try
        {
            var frames = await Task.Run(() => _s.Codecs.DecodeFrames(path, fmt, 2048, MaxAnimationBytes, ct), ct);
            if (frames == null || frames.Count < 2 || ct.IsCancellationRequested) return;
            var bitmaps = await Task.Run(() => frames.Select(f => (Bitmap: (Bitmap)BitmapFactory.Create(f.Image), f.DelayMs)).ToList(), ct);
            if (ct.IsCancellationRequested) { foreach (var b in bitmaps) b.Bitmap.Dispose(); return; }
            _frames = bitmaps;
            _frameIndex = 0;
            IsAnimated = true;
            ShowFrame(0);
            Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug($"Animasyon yüklenemedi: {ex.Message}"); }
    }

    private void ShowFrame(int index)
    {
        if (_frames == null) return;
        _frameIndex = index;
        ReplaceBitmap(_frames[index].Bitmap, owned: false);
        FrameText = $"{index + 1} / {_frames.Count}";
    }

    private void Play()
    {
        if (_frames == null) return;
        IsPlaying = true;
        _animTimer.Interval = TimeSpan.FromMilliseconds(_frames[_frameIndex].Delay);
        _animTimer.Start();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_frames == null) { _animTimer.Stop(); return; }
        ShowFrame((_frameIndex + 1) % _frames.Count);
        _animTimer.Interval = TimeSpan.FromMilliseconds(_frames[_frameIndex].Delay);
    }

    private void StopAnimation()
    {
        _animTimer.Stop();
        IsPlaying = false;
        IsAnimated = false;
        FrameText = "";
        if (_frames == null) return;
        if (!_ownsCurrentBitmap) { CurrentBitmap = null; _ownsCurrentBitmap = true; }
        foreach (var f in _frames) f.Bitmap.Dispose();
        _frames = null;
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (_frames == null) return;
        if (IsPlaying) { _animTimer.Stop(); IsPlaying = false; }
        else Play();
    }

    [RelayCommand]
    private void NextFrame()
    {
        if (_frames == null) return;
        _animTimer.Stop(); IsPlaying = false;
        ShowFrame((_frameIndex + 1) % _frames.Count);
    }

    // ---------- Bilgi paneli ----------

    private void ClearInspector()
    {
        FileRows.Clear(); CameraRows.Clear(); TechRows.Clear(); MetadataEntries.Clear();
        HasCamera = false; HasGps = false; GpsText = null; HasFormatWarning = false; FormatWarning = null;
        HasInspectorData = false;
        HasTech = false;
        HasMetadata = false;
        MetadataHeader = L.T("Tüm Metadata");
    }

    private void FillInspector(ThumbnailItem item, DetectionResult detection, DecodeResult result, ImageMetadata meta)
    {
        ClearInspector();
        AddFileRows(item, detection, result);

        var p = result.Properties;
        if (result.IsPreview) TechRows.Add(new(L.T("Not"), L.T("Gösterilen: gömülü önizleme")));
        TechRows.Add(new(L.T("Bit derinliği"), p.BitDepth > 0 ? L.T("{0} bit / kanal", p.BitDepth) : "—"));
        TechRows.Add(new(L.T("Kanal"), p.Channels.ToString(Tr)));
        TechRows.Add(new(L.T("Renk alanı"), p.ColorSpace));
        TechRows.Add(new(L.T("ICC profili"), p.IccProfile ?? L.T("Yok")));
        TechRows.Add(new(L.T("Alfa kanalı"), p.HasAlpha ? L.T("Var") : L.T("Yok")));
        if (p.Compression != null) TechRows.Add(new(L.T("Sıkıştırma"), p.Compression));
        if (p.FrameCount > 1) TechRows.Add(new(L.T("Kare / sayfa"), p.FrameCount.ToString(Tr)));
        if (p.DpiX > 0) TechRows.Add(new(L.T("Yoğunluk"), $"{p.DpiX.ToString("0", Tr)} × {p.DpiY.ToString("0", Tr)} {DensityUnit(p.DensityUnit)}"));
        TechRows.Add(new(L.T("Çözücü"), result.DecoderName));
        if (detection.Format is { } f)
            TechRows.Add(new(L.T("Format desteği"), SupportText(_s.Codecs.GetSupport(f), _s.Converter.CanWrite(f))));

        FillMetadata(meta);
        HasTech = TechRows.Count > 0;
        HasInspectorData = true;
    }

    private void AddFileRows(ThumbnailItem item, DetectionResult? detection, DecodeResult? result)
    {
        var fi = new FileInfo(item.Path);
        var format = detection?.Format;
        FileRows.Add(new(L.T("Dosya adı"), fi.Name));
        FileRows.Add(new(L.T("Format"), format?.DisplayName ?? L.T("Bilinmiyor")));
        if (fi.Exists) FileRows.Add(new(L.T("Boyut"), FormatBytes(fi.Length)));
        if (result != null)
        {
            int w = result.Image.OriginalWidth, h = result.Image.OriginalHeight;
            var mp = w * (double)h / 1_000_000;
            FileRows.Add(new(L.T("Çözünürlük"), $"{w} × {h}  ({mp.ToString("0.0", Tr)} MP)"));
            FileRows.Add(new(L.T("En-boy oranı"), AspectRatio(w, h)));
            var p = result.Properties;
            var gray = p.ColorSpace.Contains("Gray", StringComparison.OrdinalIgnoreCase);
            FileRows.Add(new(L.T("Renk"), $"{(gray ? L.T("Gri ton") : "RGB")} / {p.BitDepth} bit{(p.HasAlpha ? " + " + L.T("alfa") : "")}"));
        }
        if (format != null) FileRows.Add(new("MIME", format.MimeType));
        if (fi.Exists) FileRows.Add(new(L.T("Değiştirilme"), fi.LastWriteTime.ToString("dd.MM.yyyy HH:mm", Tr)));
        FileRows.Add(new(L.T("Konum"), fi.DirectoryName ?? ""));

        if (detection is { ExtensionMismatch: true })
        {
            HasFormatWarning = true;
            FormatWarning = L.T("Dosya uzantısı yanıltıcı: '{0}' uzantılı ama içerik aslında {1}. NexView doğru formatla açtı.",
                fi.Extension, detection.ContentFormat!.DisplayName);
        }
    }

    private double? _gpsLat, _gpsLon;

    private void FillMetadata(ImageMetadata meta)
    {
        void Cam(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) CameraRows.Add(new(label, value)); }
        Cam(L.T("Kamera"), meta.CameraDisplay);
        Cam(L.T("Lens"), meta.Lens);
        Cam("ISO", meta.Iso);
        Cam(L.T("Enstantane"), meta.ExposureTime);
        Cam(L.T("Diyafram"), meta.FNumber);
        Cam(L.T("Odak uzaklığı"), meta.FocalLength);
        Cam(L.T("Pozlama telafisi"), meta.ExposureBias);
        Cam(L.T("Beyaz dengesi"), meta.WhiteBalance);
        Cam(L.T("Flaş"), meta.Flash);
        Cam(L.T("Çekim tarihi"), meta.DateTaken?.ToString("dd.MM.yyyy HH:mm", Tr));
        HasCamera = CameraRows.Count > 0;

        if (meta.HasGps)
        {
            HasGps = true;
            GpsText = MetadataService.FormatCoordinates(meta.Latitude!.Value, meta.Longitude!.Value);
            _gpsLat = meta.Latitude; _gpsLon = meta.Longitude;
        }

        foreach (var e in meta.Entries) MetadataEntries.Add(e);
        HasMetadata = MetadataEntries.Count > 0;
        var kinds = new List<string>();
        if (meta.HasExif) kinds.Add("EXIF");
        if (meta.HasIptc) kinds.Add("IPTC");
        if (meta.HasXmp) kinds.Add("XMP");
        MetadataHeader = meta.Entries.Count == 0 ? L.T("Metadata yok")
            : L.T("Tüm Metadata") + $" ({meta.Entries.Count}{(kinds.Count > 0 ? " · " + string.Join(", ", kinds) : "")})";
    }

    private static string SupportText(FormatSupport s, bool canWrite) => s == FormatSupport.Unsupported
        ? L.T("Desteklenmiyor")
        : L.T("Görüntüle ✓") + "  " + (canWrite ? L.T("Bu formata kaydet ✓") : L.T("Bu formata kaydet ✕"));

    private static string DensityUnit(string? unit) => unit switch
    {
        "PixelsPerInch" => "DPI",
        "PixelsPerCentimeter" => L.T("piksel/cm"),
        _ => "",
    };

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v.ToString(u == 0 ? "0" : "0.0", Tr)} {units[u]}";
    }

    public static string AspectRatio(int w, int h)
    {
        if (w <= 0 || h <= 0) return "—";
        int a = w, b = h;
        while (b != 0) (a, b) = (b, a % b);
        int rw = w / a, rh = h / a;
        if (rw <= 32 && rh <= 32) return $"{rw}:{rh}";
        return $"{((double)w / h).ToString("0.00", Tr)}:1";
    }

    // ---------- Gezinme komutları ----------

    [RelayCommand]
    private async Task OpenFile()
    {
        if (Host == null) return;
        var files = await Host.PickFilesAsync();
        if (files.Count > 0) await OpenPathsAsync(files);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        if (Host == null) return;
        var folder = await Host.PickFolderAsync();
        if (folder != null) await OpenPathsAsync([folder]);
    }

    [RelayCommand] private void Next() => Step(+1);
    [RelayCommand] private void Previous() => Step(-1);

    private void Step(int delta)
    {
        if (Items.Count == 0) return;
        var idx = SelectedItem == null ? 0 : Items.IndexOf(SelectedItem);
        idx = (idx + delta + Items.Count) % Items.Count;
        SelectedItem = Items[idx];
    }

    [RelayCommand]
    private void ShowFolder()
    {
        Section = NavSection.Folder;
        UpdateEmptyText();
        SetItems(_folderFiles, SelectedItem?.Path);
    }

    [RelayCommand]
    private void ShowRaw()
    {
        Section = NavSection.Raw;
        UpdateEmptyText();
        SetItems(_folderFiles.Where(p => FormatRegistry.FromPath(p)?.IsRaw == true).ToList(), SelectedItem?.Path);
    }

    [RelayCommand]
    private void ShowFavorites()
    {
        Section = NavSection.Favorites;
        UpdateEmptyText();
        SetItems(_s.Library.Favorites.Where(File.Exists).ToList(), null);
    }

    [RelayCommand]
    private void ShowRecent()
    {
        Section = NavSection.Recent;
        UpdateEmptyText();
        SetItems(_s.Library.Recent.Select(r => r.Path).Where(File.Exists).ToList(), null);
    }

    [RelayCommand]
    private void ShowConverted()
    {
        Section = NavSection.Converted;
        UpdateEmptyText();
        SetItems(_s.Library.Converted.Where(p => File.Exists(p) && FormatRegistry.FromPath(p) != null).ToList(), null);
    }

    [RelayCommand]
    private async Task ShowScreenshots()
    {
        Section = NavSection.Screenshots;
        UpdateEmptyText();
        var folder = PlatformShell.ScreenshotsFolder();
        var files = folder == null ? new List<string>() : await Task.Run(() => FolderScanner.GetImages(folder));
        files.Reverse();
        SetItems(files, null);
    }

    [RelayCommand] private void OpenRecycleBin() => WindowsShell.OpenRecycleBin();

    // ---------- Panel komutları ----------

    [RelayCommand] private async Task ShowInfoTab() { if (!await LeaveEditAsync()) return; Tab = InspectorTab.Info; ShowInspector = true; }
    [RelayCommand] private async Task ShowConvertTab() { if (!await LeaveEditAsync()) return; Tab = InspectorTab.Convert; ShowInspector = true; Convert.NotifySourceChanged(); }
    [RelayCommand] private async Task OpenBatch() { if (!await LeaveEditAsync()) return; Tab = InspectorTab.Batch; ShowInspector = true; Convert.NotifySourceChanged(); }
    [RelayCommand] private void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (SelectedItem == null) return;
        var on = _s.Library.ToggleFavorite(SelectedItem.Path);
        SelectedItem.IsFavorite = on;
        IsCurrentFavorite = on;
    }

    [RelayCommand] private void RotateLeft() { if (Edit.IsActive) Edit.RotateCommand.Execute("left"); else Rotation = (Rotation + 270) % 360; }
    [RelayCommand] private void RotateRight() { if (Edit.IsActive) Edit.RotateCommand.Execute("right"); else Rotation = (Rotation + 90) % 360; }
    [RelayCommand] private void Flip() { if (Edit.IsActive) Edit.FlipHCommand.Execute(null); else FlipHorizontal = !FlipHorizontal; }
    [RelayCommand] private void CycleBackground() => ViewerBackground = (ViewerBackground)(((int)ViewerBackground + 1) % 4);
    [RelayCommand] private void ToggleSidebar() => ShowSidebar = !ShowSidebar;
    [RelayCommand] private void ToggleInspector() => ShowInspector = !ShowInspector;
    [RelayCommand] private void ToggleFilmstrip() => ShowFilmstrip = !ShowFilmstrip;
    [RelayCommand] private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;
    [RelayCommand] private void ToggleStretch() { if (ShowStretchToggle) AutoStretch = !AutoStretch; }

    [RelayCommand]
    private void ExitFullScreen()
    {
        if (IsFullScreen) IsFullScreen = false;
    }

    [RelayCommand]
    private void RetryAlternative()
    {
        if (_errorItem != null) _currentLoad = LoadAsync(_errorItem, alternative: true);
    }

    [RelayCommand]
    private async Task CopyError()
    {
        if (Host != null && _lastError != null)
        {
            await Host.CopyTextAsync(_lastError);
            ShowToast(L.T("Hata ayrıntıları panoya kopyalandı."));
        }
    }

    [RelayCommand]
    private async Task CopyPath()
    {
        if (Host != null && SelectedItem != null)
        {
            await Host.CopyTextAsync(SelectedItem.Path);
            ShowToast(L.T("Dosya yolu kopyalandı."));
        }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (SelectedItem != null) PlatformShell.ShowInFolder(SelectedItem.Path);
    }

    [RelayCommand]
    private void OpenInMap()
    {
        if (_gpsLat is { } lat && _gpsLon is { } lon)
            PlatformShell.OpenUri(string.Create(CultureInfo.InvariantCulture,
                $"https://www.openstreetmap.org/?mlat={lat:0.000000}&mlon={lon:0.000000}#map=15/{lat:0.000000}/{lon:0.000000}"));
    }

    [RelayCommand]
    private async Task CopyCoordinates()
    {
        if (Host != null && _gpsLat is { } lat && _gpsLon is { } lon)
        {
            await Host.CopyTextAsync(string.Create(CultureInfo.InvariantCulture, $"{lat:0.000000}, {lon:0.000000}"));
            ShowToast(L.T("Koordinat kopyalandı."));
        }
    }

    // ---------- Dosya işlemleri ----------

    [RelayCommand]
    private async Task CopyImage()
    {
        if (SelectedItem is not { } item) return;
        if (!WindowsShell.IsSupported)
        {
            await CopyPath();
            return;
        }
        try
        {
            var (png, bmp) = await Task.Run(() => (_s.Converter.RenderPng(item.Path, 8192, CancellationToken.None),
                                                  _s.Converter.RenderBmp(item.Path, 8192, CancellationToken.None)));
            ShowToast(WindowsShell.SetClipboardImage(png, bmp) ? L.T("Görüntü panoya kopyalandı.") : L.T("Pano kullanılamıyor."));
        }
        catch (Exception ex)
        {
            Log.Warn("Görüntü kopyalanamadı", ex);
            ShowToast(L.T("Görüntü kopyalanamadı."));
        }
    }

    [RelayCommand]
    private async Task Paste()
    {
        if (!WindowsShell.IsSupported) return;
        var (image, files) = WindowsShell.GetClipboardImage();
        if (files is { Length: > 0 })
        {
            await HandleDropAsync(files);
            return;
        }
        if (image == null)
        {
            ShowToast(L.T("Panoda görüntü yok."));
            return;
        }
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NexView");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Pano-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await Task.Run(() => _s.Converter.SaveAsPng(image, path));
            await OpenPathsAsync([path]);
            ShowToast(L.T("Panodaki görüntü kaydedildi: {0}", path));
        }
        catch (Exception ex)
        {
            Log.Warn("Pano görüntüsü kaydedilemedi", ex);
            ShowToast(L.T("Panodaki görüntü okunamadı."));
        }
    }

    [RelayCommand]
    private async Task Print()
    {
        if (SelectedItem is not { } item || !WindowsShell.IsSupported) return;
        var path = item.Path;
        if (FormatDetector.Detect(path).Format?.Id is not ("JPEG" or "PNG" or "BMP" or "TIFF" or "GIF"))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "NexView", Path.GetFileNameWithoutExtension(path) + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            var png = await Task.Run(() => _s.Converter.RenderPng(path, 8192, CancellationToken.None));
            await File.WriteAllBytesAsync(tmp, png);
            path = tmp;
        }
        if (!WindowsShell.Print(path)) ShowToast(L.T("Yazdırma başlatılamadı."));
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedItem is not { } item) return;
        var recycle = WindowsShell.IsSupported;
        var choice = await ShowDialogAsync(new DialogViewModel(
            recycle ? L.T("Geri Dönüşüm Kutusu'na taşınsın mı?") : L.T("Kalıcı olarak silinsin mi?"),
            item.FileName,
            recycle ? L.T("Taşı") : L.T("Sil")) { IsDanger = !recycle });
        if (choice != DialogChoice.Primary) return;

        var index = Items.IndexOf(item);
        bool ok;
        try
        {
            if (recycle) ok = WindowsShell.MoveToRecycleBin(item.Path);
            else { File.Delete(item.Path); ok = true; }
        }
        catch (Exception ex)
        {
            Log.Warn("Silinemedi", ex);
            ok = false;
        }
        if (!ok) { ShowToast(L.T("Dosya silinemedi.")); return; }

        _s.Library.Forget(item.Path);
        _folderFiles.RemoveAll(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
        Items.Remove(item);
        item.Release();
        OnPropertyChanged(nameof(ShowFilmstripPanel));
        Convert.NotifySourceChanged();
        if (Items.Count == 0) { SelectedItem = null; ClearImage(); UpdateEmptyText(); }
        else SelectedItem = Items[Math.Min(index, Items.Count - 1)];
        ShowToast(recycle ? L.T("Geri Dönüşüm Kutusu'na taşındı: {0}", item.FileName) : L.T("Silindi: {0}", item.FileName));
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (SelectedItem is not { } item) return;
        var dir = Path.GetDirectoryName(item.Path)!;
        var ext = Path.GetExtension(item.Path);
        var dialog = new DialogViewModel(L.T("Yeniden adlandır"), L.T("Yeni dosya adı ({0} uzantısı korunur):", ext), L.T("Yeniden adlandır"))
        {
            HasInput = true,
            Validator = name =>
            {
                name = name.Trim();
                if (name.Length == 0) return L.T("Ad boş olamaz.");
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return L.T("Ad geçersiz karakter içeriyor.");
                var target = Path.Combine(dir, name + ext);
                if (!string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) return L.T("Bu adla bir dosya zaten var.");
                return null;
            },
        };
        dialog.Input = Path.GetFileNameWithoutExtension(item.Path);
        if (await ShowDialogAsync(dialog) != DialogChoice.Primary) return;

        var newPath = Path.Combine(dir, dialog.Input.Trim() + ext);
        if (string.Equals(newPath, item.Path, StringComparison.Ordinal)) return;
        try
        {
            File.Move(item.Path, newPath);
        }
        catch (Exception ex)
        {
            Log.Warn("Yeniden adlandırılamadı", ex);
            ShowToast(L.T("Yeniden adlandırılamadı: {0}", ex.Message));
            return;
        }
        _s.Library.ReplacePath(item.Path, newPath);
        var fi = _folderFiles.FindIndex(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
        if (fi >= 0) _folderFiles[fi] = newPath;
        var idx = Items.IndexOf(item);
        var replacement = new ThumbnailItem(newPath, _s.Library.IsFavorite(newPath));
        Items[idx] = replacement;
        item.Release();
        SelectedItem = replacement;
        ShowToast(L.T("Yeniden adlandırıldı: {0}", replacement.FileName));
    }
}
