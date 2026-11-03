using NexView.UI;
using NexView.UI.Controls;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.App.Services;
using NexView.Core.Conversion;
using NexView.Core.Editing;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;

namespace NexView.App.ViewModels;


public sealed record AspectItem(string Name, double? Ratio);
public sealed record MarkupItem(AnnotationKind Kind, string Name);
public sealed record ColorItem(string Hex);
public sealed record FilterItem(DocumentFilter Filter, string Name);

/// <summary>Tahribatsız düzenleme oturumu: tarif + önizleme + geri al/yinele + kaydet.</summary>
public sealed partial class EditViewModel : ObservableObject, NexView.UI.Editing.IEditOverlayModel
{
    private const int PreviewMax = 2048;
    private readonly MainViewModel _main;
    private readonly AppServices _s;
    private readonly IImageEditor _editor;
    private IEditSession? _session;
    private EditRecipe _recipe = new();
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private DateTime _lastCommit;
    private bool _loading;
    private bool _rendering;
    private bool _renderPending;
    private Bitmap? _compareBitmap;
    private ThumbnailItem? _item;

    public EditViewModel(MainViewModel main, AppServices services, IImageEditor editor)
    {
        _main = main;
        _s = services;
        _editor = editor;
        Aspects =
        [
            new(L.T("Serbest"), null), new(L.T("Orijinal"), 0), new("1:1", 1), new("4:3", 4.0 / 3), new("3:2", 1.5),
            new("16:9", 16.0 / 9), new("9:16", 9.0 / 16), new("A4", 1 / Math.Sqrt(2)), new(L.T("Biyometrik 50×60"), 50.0 / 60),
        ];
        MarkupTools =
        [
            new(AnnotationKind.Arrow, L.T("Ok")), new(AnnotationKind.Rectangle, L.T("Kutu")), new(AnnotationKind.Ellipse, L.T("Elips")),
            new(AnnotationKind.Text, L.T("Metin")), new(AnnotationKind.Highlight, L.T("Vurgu")), new(AnnotationKind.Blur, L.T("Bulanıklaştır")),
            new(AnnotationKind.Pixelate, L.T("Pikselleştir")), new(AnnotationKind.BlackBox, L.T("Siyah kutu")),
        ];
        Colors = [new("#FF3B30"), new("#FFD60A"), new("#22D3EE"), new("#34C759"), new("#FFFFFF"), new("#000000")];
        Filters = [new(DocumentFilter.None, L.T("Yok")), new(DocumentFilter.Enhance, L.T("İyileştir")), new(DocumentFilter.Gray, L.T("Gri")), new(DocumentFilter.BlackWhite, L.T("Siyah-beyaz"))];
        _aspect = Aspects[0];
        _markupTool = MarkupTools[0];
        _color = Colors[0];
        _filter = Filters[0];
    }

    public IReadOnlyList<AspectItem> Aspects { get; }
    public IReadOnlyList<MarkupItem> MarkupTools { get; }
    public IReadOnlyList<ColorItem> Colors { get; }
    public IReadOnlyList<FilterItem> Filters { get; }
    public ObservableCollection<Annotation> Annotations { get; } = new();

    public EditRecipe Recipe => _recipe;
    public int PreviewWidth { get; private set; }
    public int PreviewHeight { get; private set; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdjustTool), nameof(IsCropTool), nameof(IsMarkupTool), nameof(IsDocumentTool), nameof(OverlayActive))]
    private EditTool _tool = EditTool.Adjust;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompareOff), nameof(IsCompareOriginal), nameof(IsCompareSplit), nameof(OverlayActive))]
    private CompareMode _compare = CompareMode.Off;

    [ObservableProperty] private double _splitPosition = 0.5;

    // Ton
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private double _sharpness;
    [ObservableProperty] private bool _grayscale;

    // Kırpma
    [ObservableProperty] private AspectItem _aspect;
    [ObservableProperty] private double _straighten;
    [ObservableProperty] private NRect _cropRect = NRect.Full;

    // İşaretleme
    [ObservableProperty] private MarkupItem _markupTool;
    [ObservableProperty] private ColorItem _color;
    [ObservableProperty] private double _markupSize = 1.0;

    // Belge
    [ObservableProperty] private bool _documentPerspective;
    [ObservableProperty] private NPoint[] _quad = DefaultQuad();
    [ObservableProperty] private FilterItem _filter;

    public bool IsAdjustTool => Tool == EditTool.Adjust;
    public bool IsCropTool => Tool == EditTool.Crop;
    public bool IsMarkupTool => Tool == EditTool.Markup;
    public bool IsDocumentTool => Tool == EditTool.Document;
    public bool IsCompareOff => Compare == CompareMode.Off;
    public bool IsCompareOriginal => Compare == CompareMode.Original;
    public bool IsCompareSplit => Compare == CompareMode.Split;
    public bool OverlayActive => IsActive && (Tool != EditTool.Adjust || Compare == CompareMode.Split);
    public bool HasAnnotations => Annotations.Count > 0;
    public AnnotationKind MarkupKind => MarkupTool.Kind;
    public string MarkupColor => Color.Hex;

    public string ExposureText => $"{Exposure:+0.0;-0.0;0} EV";
    public double? CropRatioNormalized
    {
        get
        {
            if (Aspect.Ratio is not { } r || PreviewWidth == 0) return null;
            if (r == 0) return 1.0;
            return r * PreviewHeight / PreviewWidth;
        }
    }

    private static NPoint[] DefaultQuad() => [new(0.06, 0.06), new(0.94, 0.06), new(0.94, 0.94), new(0.06, 0.94)];

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(OverlayActive));

    // ---------- Oturum ----------

    public async Task StartAsync(ThumbnailItem item)
    {
        if (IsActive && ReferenceEquals(item, _item)) return;
        await CloseSessionAsync();
        _item = item;
        IsBusy = true;
        try
        {
            _session = await Task.Run(() => _editor.Open(item.Path, PreviewMax, CancellationToken.None));
            LoadRecipe(new EditRecipe());
            _undo.Clear(); _redo.Clear(); UpdateUndo();
            IsDirty = false;
            Tool = EditTool.Adjust;
            Compare = CompareMode.Off;
            IsActive = true;
            await RenderAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Düzenleme açılamadı: {item.Path}", ex);
            _main.ShowToast(L.T("Bu görüntü düzenlenemiyor: {0}", ex.Message));
            IsActive = false;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Düzenlemeden çıkar. Kaydedilmemiş değişiklik varsa sorar. false: kullanıcı vazgeçti.</summary>
    public async Task<bool> ExitAsync(bool ask)
    {
        if (!IsActive) return true;
        if (ask && IsDirty)
        {
            var choice = await _main.ShowDialogAsync(new DialogViewModel(
                L.T("Kaydedilmemiş değişiklikler"), L.T("Düzenlemeleri yeni bir dosya olarak kaydetmek ister misiniz?"),
                L.T("Kaydet"), L.T("Kaydetmeden çık")));
            if (choice == DialogChoice.Cancel) return false;
            if (choice == DialogChoice.Primary && !await SaveCoreAsync(null)) return false;
        }
        await CloseSessionAsync();
        return true;
    }

    private Task CloseSessionAsync()
    {
        IsActive = false;
        _session?.Dispose();
        _session = null;
        SetCompareBitmap(null);
        _main.SetEditCompare(null, 0.5);
        return Task.CompletedTask;
    }

    private void LoadRecipe(EditRecipe r)
    {
        _loading = true;
        try
        {
            _recipe = r;
            Exposure = r.Exposure; Brightness = r.Brightness; Contrast = r.Contrast; Saturation = r.Saturation;
            Temperature = r.Temperature; Tint = r.Tint; Sharpness = r.Sharpness; Grayscale = r.Grayscale;
            Straighten = r.Straighten; CropRect = r.Crop;
            DocumentPerspective = r.DocumentQuad != null;
            Quad = r.DocumentQuad ?? DefaultQuad();
            Filter = Filters.First(f => f.Filter == r.DocumentFilter);
            Annotations.Clear();
            foreach (var a in r.Annotations) Annotations.Add(a);
            OnPropertyChanged(nameof(HasAnnotations));
        }
        finally { _loading = false; }
    }

    // ---------- Değişiklikler ----------

    private void Changed(bool render = true)
    {
        if (_loading || !IsActive) return;
        Commit();
        IsDirty = !_recipe.IsIdentity;
        if (render) _ = RenderAsync();
    }

    private void Commit()
    {
        var json = _recipe.ToJson();
        if (_undo.Count > 0 && (DateTime.UtcNow - _lastCommit).TotalMilliseconds < 600) _undo[^1] = json;
        else _undo.Add(json);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        _lastCommit = DateTime.UtcNow;
        UpdateUndo();
    }

    private void UpdateUndo()
    {
        CanUndo = _undo.Count > 0;
        CanRedo = _redo.Count > 0;
    }

    partial void OnExposureChanged(double value) { _recipe.Exposure = value; OnPropertyChanged(nameof(ExposureText)); Changed(); }
    partial void OnBrightnessChanged(double value) { _recipe.Brightness = value; Changed(); }
    partial void OnContrastChanged(double value) { _recipe.Contrast = value; Changed(); }
    partial void OnSaturationChanged(double value) { _recipe.Saturation = value; Changed(); }
    partial void OnTemperatureChanged(double value) { _recipe.Temperature = value; Changed(); }
    partial void OnTintChanged(double value) { _recipe.Tint = value; Changed(); }
    partial void OnSharpnessChanged(double value) { _recipe.Sharpness = value; Changed(); }
    partial void OnGrayscaleChanged(bool value) { _recipe.Grayscale = value; Changed(); }
    partial void OnStraightenChanged(double value) { _recipe.Straighten = value; Changed(); }
    partial void OnCropRectChanged(NRect value) { _recipe.Crop = value; Changed(render: false); }
    partial void OnFilterChanged(FilterItem value) { _recipe.DocumentFilter = value.Filter; Changed(); }
    partial void OnQuadChanged(NPoint[] value) { if (DocumentPerspective) { _recipe.DocumentQuad = value; Changed(render: false); } }

    partial void OnDocumentPerspectiveChanged(bool value)
    {
        _recipe.DocumentQuad = value ? Quad : null;
        Changed();
    }

    partial void OnAspectChanged(AspectItem value)
    {
        OnPropertyChanged(nameof(CropRatioNormalized));
        if (_loading || CropRatioNormalized is not { } r) return;
        // Ortalanmış en büyük kırpma
        double w = 1, h = w / r;
        if (h > 1) { h = 1; w = r; }
        CropRect = new NRect((1 - w) / 2, (1 - h) / 2, w, h);
    }

    partial void OnToolChanged(EditTool value)
    {
        if (!IsActive) return;
        _ = RenderAsync();
    }

    partial void OnCompareChanged(CompareMode value)
    {
        if (!IsActive) return;
        _ = RenderAsync();
    }

    partial void OnSplitPositionChanged(double value) => _main.SetEditCompare(Compare == CompareMode.Split ? _compareBitmap : null, value);

    /// <summary>Kaplama katmanı bir sürüklemeyi bitirdiğinde (kırpma, köşe) önizlemeyi yeniler.</summary>
    public void CommitInteraction() => _ = RenderAsync();

    public void AddAnnotation(Annotation a)
    {
        _recipe.Annotations.Add(a);
        Annotations.Add(a);
        OnPropertyChanged(nameof(HasAnnotations));
        Changed(render: a.IsRedaction);
    }

    public async Task AddTextAsync(double x, double y)
    {
        var dlg = new DialogViewModel(L.T("Metin ekle"), L.T("Görüntüye yazılacak metin:"), L.T("Ekle"))
        {
            HasInput = true,
            Validator = t => string.IsNullOrWhiteSpace(t) ? L.T("Metin boş olamaz.") : null,
        };
        if (await _main.ShowDialogAsync(dlg) == DialogChoice.Primary)
            AddAnnotation(new Annotation(AnnotationKind.Text, x, y, x, y, Color.Hex, dlg.Input.Trim(), MarkupSize));
    }

    // ---------- Önizleme ----------

    private PreviewStage Stage => Tool switch
    {
        EditTool.Crop => PreviewStage.BeforeCrop,
        EditTool.Document => PreviewStage.BeforeDocument,
        _ => PreviewStage.Full,
    };

    public async Task RenderAsync()
    {
        if (_session == null) return;
        if (_rendering) { _renderPending = true; return; }
        _rendering = true;
        try
        {
            do
            {
                _renderPending = false;
                var session = _session;
                if (session == null) break;
                var recipe = _recipe.Clone();
                var stage = Stage;
                var compare = Compare;
                var (main, cmp) = await Task.Run(() =>
                {
                    var m = session.RenderPreview(recipe, stage, compare != CompareMode.Original, CancellationToken.None);
                    var c = compare == CompareMode.Split ? session.RenderPreview(recipe, stage, false, CancellationToken.None) : null;
                    return (BitmapFactory.Create(m), c == null ? null : BitmapFactory.Create(c));
                });
                if (_session == null) { main.Dispose(); cmp?.Dispose(); break; }
                PreviewWidth = main.PixelSize.Width;
                PreviewHeight = main.PixelSize.Height;
                OnPropertyChanged(nameof(CropRatioNormalized));
                _main.ShowEditPreview(main);
                SetCompareBitmap(cmp);
                _main.SetEditCompare(cmp, SplitPosition);
            } while (_renderPending);
        }
        catch (Exception ex)
        {
            Log.Warn("Önizleme oluşturulamadı", ex);
        }
        finally { _rendering = false; }
    }

    private void SetCompareBitmap(Bitmap? b)
    {
        var old = _compareBitmap;
        _compareBitmap = b;
        if (old != null && !ReferenceEquals(old, b))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => old.Dispose(), Avalonia.Threading.DispatcherPriority.Background);
    }

    // ---------- Komutlar ----------

    [RelayCommand] private void SetTool(EditTool tool) => Tool = tool;
    [RelayCommand] private void SetCompare(CompareMode mode) => Compare = mode;

    [RelayCommand]
    private void Auto()
    {
        if (_session == null) return;
        var full = _session.RenderPreview(new EditRecipe { Rotation = _recipe.Rotation, Crop = _recipe.Crop }, PreviewStage.Full, false, CancellationToken.None);
        var (black, white) = ToneProcessor.AutoLevels(full.Pixels, full.Width, full.Height);
        _recipe.LevelsBlack = black;
        _recipe.LevelsWhite = white;
        Changed();
    }

    [RelayCommand]
    private void ResetTone()
    {
        _loading = true;
        Exposure = Brightness = Contrast = Saturation = Temperature = Tint = Sharpness = 0;
        Grayscale = false;
        _loading = false;
        _recipe.Exposure = _recipe.Brightness = _recipe.Contrast = _recipe.Saturation = _recipe.Temperature = _recipe.Tint = _recipe.Sharpness = 0;
        _recipe.Grayscale = false;
        _recipe.LevelsBlack = 0; _recipe.LevelsWhite = 255;
        Changed();
    }

    [RelayCommand]
    private void Rotate(string dir)
    {
        _recipe.Rotation = ((_recipe.Rotation + (dir == "left" ? 270 : 90)) % 360);
        _recipe.Crop = NRect.Full;
        _loading = true; CropRect = NRect.Full; _loading = false;
        Changed();
    }

    [RelayCommand]
    private void FlipH() { _recipe.FlipH = !_recipe.FlipH; Changed(); }

    [RelayCommand]
    private void FlipV() { _recipe.FlipV = !_recipe.FlipV; Changed(); }

    [RelayCommand]
    private void ResetCrop()
    {
        _loading = true;
        CropRect = NRect.Full; Straighten = 0; Aspect = Aspects[0];
        _loading = false;
        _recipe.Crop = NRect.Full; _recipe.Straighten = 0; _recipe.Rotation = 0; _recipe.FlipH = _recipe.FlipV = false;
        Changed();
    }

    [RelayCommand]
    private void RemoveLastAnnotation()
    {
        if (_recipe.Annotations.Count == 0) return;
        var last = _recipe.Annotations[^1];
        _recipe.Annotations.RemoveAt(_recipe.Annotations.Count - 1);
        Annotations.Remove(last);
        OnPropertyChanged(nameof(HasAnnotations));
        Changed(render: last.IsRedaction);
    }

    [RelayCommand]
    private void ClearAnnotations()
    {
        var hadRedaction = _recipe.Annotations.Any(a => a.IsRedaction);
        _recipe.Annotations.Clear();
        Annotations.Clear();
        OnPropertyChanged(nameof(HasAnnotations));
        Changed(render: hadRedaction);
    }

    [RelayCommand]
    private void ResetDocument()
    {
        _loading = true;
        Quad = DefaultQuad();
        _loading = false;
        if (DocumentPerspective) { _recipe.DocumentQuad = Quad; Changed(); }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        var prev = _undo.Count > 0 ? System.Text.Json.JsonSerializer.Deserialize<EditRecipe>(_undo[^1])! : new EditRecipe();
        LoadRecipe(prev);
        IsDirty = !_recipe.IsIdentity;
        UpdateUndo();
        _ = RenderAsync();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        var json = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(json);
        LoadRecipe(System.Text.Json.JsonSerializer.Deserialize<EditRecipe>(json)!);
        IsDirty = !_recipe.IsIdentity;
        UpdateUndo();
        _ = RenderAsync();
    }

    [RelayCommand]
    private async Task Save() => await SaveCoreAsync(null);

    [RelayCommand]
    private async Task SaveAs()
    {
        if (_item == null || _main.Host == null) return;
        var target = DefaultTarget(_item.Path);
        var path = await _main.Host.PickSaveFileAsync(Path.GetFileName(target.Path), Path.GetDirectoryName(target.Path));
        if (path != null) await SaveCoreAsync(path);
    }

    [RelayCommand]
    private async Task Cancel()
    {
        if (await ExitAsync(ask: true)) _main.EndEditing();
    }

    private (string Path, ImageFormat Format) DefaultTarget(string source)
    {
        var fmt = FormatDetector.Detect(source).Format;
        if (fmt == null || fmt.IsRaw || fmt.Family == FormatFamily.Hdr || fmt.Id is "HEIC" or "HEIF" or "PSD" or "PSB" or "GIF" or "ICO" || !_s.Converter.CanWrite(fmt))
            fmt = FormatRegistry.Jpeg;
        var dir = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source) + " " + L.T("(düzenlendi)");
        return (Path.Combine(dir, name + OutputPaths.Extension(fmt)), fmt);
    }

    private async Task<bool> SaveCoreAsync(string? path)
    {
        if (_session == null || _item == null) return false;
        var (defPath, defFmt) = DefaultTarget(_item.Path);
        var target = path ?? defPath;
        var fmt = path == null ? defFmt : FormatRegistry.FromPath(path) ?? defFmt;
        if (!_s.Converter.CanWrite(fmt)) { fmt = FormatRegistry.Jpeg; target = Path.ChangeExtension(target, ".jpg"); }
        IsBusy = true;
        try
        {
            var recipe = _recipe.Clone();
            var session = _session;
            var r = await Task.Run(() => session.Save(recipe, target, fmt, 92, true, CancellationToken.None));
            _s.Library.AddConverted([r.Output!]);
            IsDirty = false;
            _main.ShowToast(L.T("Kaydedildi: {0}", Path.GetFileName(r.Output)));
            await CloseSessionAsync();
            await _main.EndEditingAndOpenAsync(r.Output!);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Düzenleme kaydedilemedi", ex);
            _main.ShowToast(L.T("Kaydedilemedi: {0}", ex.Message));
            return false;
        }
        finally { IsBusy = false; }
    }
}
