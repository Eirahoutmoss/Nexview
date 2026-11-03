using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.Core.Conversion;
using NexView.Core.Editing;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.UI;
using NexView.UI.Editing;

namespace NexView.Mobile.ViewModels;

/// <summary>Mobil düzenleyici: ton, kırpma/döndürme, işaretleme/gizleme, belge düzeltme. Tahribatsız tarif + geri al/yinele.</summary>
public sealed partial class MobileEditViewModel : ObservableObject, IEditOverlayModel
{
    private const int PreviewMax = 1600;
    private readonly IImageEditor _editor;
    private readonly Func<ImageFormat, bool> _canWrite;
    private readonly Func<string> _outputDir;
    private IEditSession? _session;
    private EditRecipe _recipe = new();
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private DateTime _lastCommit;
    private bool _loading, _rendering, _renderPending;
    private TaskCompletionSource<string?>? _textTcs;
    private string _source = "";
    private string _displayName = "";

    /// <summary>Kaydetme bitti: (çıktı dosyası, paylaşılsın mı). İptalde çağrılmaz.</summary>
    public event Func<string, bool, Task>? Saved;
    public event Action? Closed;
    public event Action<string>? Message;

    public MobileEditViewModel(IImageEditor editor, Func<ImageFormat, bool> canWrite, Func<string> outputDir)
    {
        _editor = editor;
        _canWrite = canWrite;
        _outputDir = outputDir;
        Tools.Add(L.T("Ayarla"), EditTool.Adjust).Add(L.T("Kırp"), EditTool.Crop).Add(L.T("İşaretle"), EditTool.Markup).Add(L.T("Belge"), EditTool.Document);
        Tools.Changed += () => Tool = (EditTool)Tools.Value!;

        Param.Add(L.T("Pozlama"), "exposure").Add(L.T("Parlaklık"), "brightness").Add(L.T("Kontrast"), "contrast")
             .Add(L.T("Doygunluk"), "saturation").Add(L.T("Sıcaklık"), "temperature").Add(L.T("Renk tonu"), "tint").Add(L.T("Netlik"), "sharpness");
        Param.Changed += LoadParam;

        Aspect.Add(L.T("Serbest"), 0.0).Add(L.T("Orijinal"), -1.0).Add("1:1", 1.0).Add("4:3", 4.0 / 3).Add("3:4", 3.0 / 4).Add("16:9", 16.0 / 9)
              .Add("9:16", 9.0 / 16).Add("A4", 1 / Math.Sqrt(2)).Add(L.T("Biyometrik"), 50.0 / 60);
        Aspect.Changed += OnAspectChanged;

        Markup.Add(L.T("Ok"), AnnotationKind.Arrow).Add(L.T("Kutu"), AnnotationKind.Rectangle).Add(L.T("Elips"), AnnotationKind.Ellipse)
              .Add(L.T("Metin"), AnnotationKind.Text).Add(L.T("Vurgu"), AnnotationKind.Highlight).Add(L.T("Bulanıklaştır"), AnnotationKind.Blur)
              .Add(L.T("Pikselleştir"), AnnotationKind.Pixelate).Add(L.T("Siyah kutu"), AnnotationKind.BlackBox);
        foreach (var c in (string[])["#FF3B30", "#FFD60A", "#22D3EE", "#34C759", "#FFFFFF", "#000000"]) Colors.Add(c, c);
        Filter.Add(L.T("Yok"), DocumentFilter.None).Add(L.T("İyileştir"), DocumentFilter.Enhance).Add(L.T("Gri"), DocumentFilter.Gray).Add(L.T("Siyah-beyaz"), DocumentFilter.BlackWhite);
        Filter.Changed += () => { _recipe.DocumentFilter = (DocumentFilter)Filter.Value!; Changed(); };
        Markup.Changed += () => OnPropertyChanged(nameof(MarkupKind));
        Colors.Changed += () => OnPropertyChanged(nameof(MarkupColor));
        Reset();
    }

    public ChoiceGroup Tools { get; } = new();
    public ChoiceGroup Param { get; } = new();
    public ChoiceGroup Aspect { get; } = new();
    public ChoiceGroup Markup { get; } = new();
    public ChoiceGroup Colors { get; } = new();
    public ChoiceGroup Filter { get; } = new();
    public ObservableCollection<Annotation> Annotations { get; } = new();

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private Bitmap? _compareBitmap;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAdjust), nameof(IsCrop), nameof(IsMarkup), nameof(IsDocument), nameof(OverlayActive))]
    private EditTool _tool;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OverlayActive), nameof(IsSplit))]
    private CompareMode _compare;
    [ObservableProperty] private double _splitPosition = 0.5;
    [ObservableProperty] private double _adjustValue;
    [ObservableProperty] private double _adjustMin = -100;
    [ObservableProperty] private double _adjustMax = 100;
    [ObservableProperty] private string _adjustText = "0";
    [ObservableProperty] private bool _grayscale;
    [ObservableProperty] private double _straighten;
    [ObservableProperty] private NRect _cropRect = NRect.Full;
    [ObservableProperty] private double _markupSize = 1.0;
    [ObservableProperty] private bool _documentPerspective;
    [ObservableProperty] private NPoint[] _quad = DefaultQuad();
    [ObservableProperty] private bool _isTextPromptOpen;
    [ObservableProperty] private string _textInput = "";
    [ObservableProperty] private string _title = "";

    public bool IsAdjust => Tool == EditTool.Adjust;
    public bool IsCrop => Tool == EditTool.Crop;
    public bool IsMarkup => Tool == EditTool.Markup;
    public bool IsDocument => Tool == EditTool.Document;
    public bool IsSplit => Compare == CompareMode.Split;
    public bool OverlayActive => IsActive && (Tool != EditTool.Adjust || Compare == CompareMode.Split);
    public AnnotationKind MarkupKind => Markup.Value is AnnotationKind k ? k : AnnotationKind.Arrow;
    public string MarkupColor => Colors.Value as string ?? "#FF3B30";
    public int PreviewWidth { get; private set; }
    public int PreviewHeight { get; private set; }

    public double? CropRatioNormalized
    {
        get
        {
            if (Aspect.Value is not double r || r == 0 || PreviewWidth == 0) return null;
            if (r < 0) return 1.0;
            return r * PreviewHeight / PreviewWidth;
        }
    }

    private static NPoint[] DefaultQuad() => [new(0.06, 0.06), new(0.94, 0.06), new(0.94, 0.94), new(0.06, 0.94)];

    private void Reset()
    {
        Tools.Select(EditTool.Adjust);
        Param.Select("exposure");
        Aspect.Select(0.0);
        Markup.Select(AnnotationKind.Arrow);
        Colors.Select("#FF3B30");
        Filter.Select(DocumentFilter.None);
        Tool = EditTool.Adjust;
        Compare = CompareMode.Off;
    }

    // ---------- Oturum ----------

    public async Task<bool> StartAsync(string path, string displayName, bool documentMode)
    {
        _source = path;
        _displayName = displayName;
        Title = displayName;
        IsBusy = true;
        IsActive = true;
        try
        {
            _session?.Dispose();
            _session = await Task.Run(() => _editor.Open(path, PreviewMax, CancellationToken.None));
            _undo.Clear(); _redo.Clear(); UpdateUndo();
            Reset();
            LoadRecipe(new EditRecipe());
            if (documentMode) await EnterDocumentModeAsync();
            IsDirty = documentMode;
            await RenderAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mobil düzenleme açılamadı: {path}", ex);
            Message?.Invoke(L.T("Bu görüntü düzenlenemiyor: {0}", ex.Message));
            await CloseAsync();
            return false;
        }
        finally { IsBusy = false; }
    }

    private async Task EnterDocumentModeAsync()
    {
        var session = _session!;
        var quad = await Task.Run(() =>
        {
            var img = session.RenderPreview(new EditRecipe(), PreviewStage.BeforeDocument, false, CancellationToken.None);
            return DocumentDetector.Detect(img);
        });
        Tools.Select(EditTool.Document);
        Tool = EditTool.Document;
        Quad = quad ?? DefaultQuad();
        DocumentPerspective = true;
        Filter.Select(DocumentFilter.Enhance);
        _recipe.DocumentFilter = DocumentFilter.Enhance;
        if (quad == null) Message?.Invoke(L.T("Belge kenarları bulunamadı; köşeleri elle yerleştirin."));
    }

    private Task CloseAsync()
    {
        IsActive = false;
        IsTextPromptOpen = false;
        _textTcs?.TrySetResult(null);
        _session?.Dispose();
        _session = null;
        var p = Preview; var c = CompareBitmap;
        Preview = null; CompareBitmap = null;
        p?.Dispose(); c?.Dispose();
        Closed?.Invoke();
        return Task.CompletedTask;
    }

    [RelayCommand] private Task Cancel() => CloseAsync();

    private void LoadRecipe(EditRecipe r)
    {
        _loading = true;
        try
        {
            _recipe = r;
            Grayscale = r.Grayscale;
            Straighten = r.Straighten;
            CropRect = r.Crop;
            DocumentPerspective = r.DocumentQuad != null;
            Quad = r.DocumentQuad ?? DefaultQuad();
            Filter.Select(r.DocumentFilter);
            Annotations.Clear();
            foreach (var a in r.Annotations) Annotations.Add(a);
            LoadParam();
        }
        finally { _loading = false; }
    }

    // ---------- Ton parametresi ----------

    private void LoadParam()
    {
        var was = _loading;
        _loading = true;
        try
        {
            (AdjustMin, AdjustMax) = Param.Value as string == "exposure" ? (-2.0, 2.0) : Param.Value as string == "sharpness" ? (0.0, 100.0) : (-100.0, 100.0);
            AdjustValue = GetParam();
            UpdateAdjustText();
        }
        finally { _loading = was; }
    }

    private double GetParam() => (Param.Value as string) switch
    {
        "exposure" => _recipe.Exposure,
        "brightness" => _recipe.Brightness,
        "contrast" => _recipe.Contrast,
        "saturation" => _recipe.Saturation,
        "temperature" => _recipe.Temperature,
        "tint" => _recipe.Tint,
        "sharpness" => _recipe.Sharpness,
        _ => 0,
    };

    partial void OnAdjustValueChanged(double value)
    {
        UpdateAdjustText();
        if (_loading) return;
        switch (Param.Value as string)
        {
            case "exposure": _recipe.Exposure = Math.Round(value, 2); break;
            case "brightness": _recipe.Brightness = Math.Round(value); break;
            case "contrast": _recipe.Contrast = Math.Round(value); break;
            case "saturation": _recipe.Saturation = Math.Round(value); break;
            case "temperature": _recipe.Temperature = Math.Round(value); break;
            case "tint": _recipe.Tint = Math.Round(value); break;
            case "sharpness": _recipe.Sharpness = Math.Round(value); break;
        }
        Changed();
    }

    private void UpdateAdjustText() =>
        AdjustText = Param.Value as string == "exposure" ? $"{AdjustValue:+0.0;-0.0;0} EV" : $"{AdjustValue:+0;-0;0}";

    partial void OnGrayscaleChanged(bool value) { _recipe.Grayscale = value; Changed(); }
    partial void OnStraightenChanged(double value) { _recipe.Straighten = Math.Round(value, 1); Changed(); }
    partial void OnCropRectChanged(NRect value) { _recipe.Crop = value; Changed(render: false); }
    partial void OnQuadChanged(NPoint[] value) { if (DocumentPerspective) { _recipe.DocumentQuad = value; Changed(render: false); } }
    partial void OnDocumentPerspectiveChanged(bool value) { _recipe.DocumentQuad = value ? Quad : null; Changed(); }
    partial void OnToolChanged(EditTool value) { if (IsActive) _ = RenderAsync(); }
    partial void OnCompareChanged(CompareMode value) { if (IsActive) _ = RenderAsync(); }

    private void OnAspectChanged()
    {
        OnPropertyChanged(nameof(CropRatioNormalized));
        if (CropRatioNormalized is not { } r) return;
        double w = 1, h = w / r;
        if (h > 1) { h = 1; w = r; }
        CropRect = new NRect((1 - w) / 2, (1 - h) / 2, w, h);
    }

    // ---------- Değişiklik / geri al ----------

    private void Changed(bool render = true)
    {
        if (_loading || !IsActive) return;
        var json = _recipe.ToJson();
        if (_undo.Count > 0 && (DateTime.UtcNow - _lastCommit).TotalMilliseconds < 600) _undo[^1] = json;
        else _undo.Add(json);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        _lastCommit = DateTime.UtcNow;
        UpdateUndo();
        IsDirty = !_recipe.IsIdentity;
        if (render) _ = RenderAsync();
    }

    private void UpdateUndo() { CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0; }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        LoadRecipe(_undo.Count > 0 ? System.Text.Json.JsonSerializer.Deserialize<EditRecipe>(_undo[^1])! : new EditRecipe());
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

    // ---------- Kaplama ----------

    public void CommitInteraction() => _ = RenderAsync();

    public void AddAnnotation(Annotation a)
    {
        _recipe.Annotations.Add(a);
        Annotations.Add(a);
        Changed(render: a.IsRedaction);
    }

    public async Task AddTextAsync(double u, double v)
    {
        _textTcs?.TrySetResult(null);
        var tcs = _textTcs = new TaskCompletionSource<string?>();
        TextInput = "";
        IsTextPromptOpen = true;
        var text = await tcs.Task;
        IsTextPromptOpen = false;
        if (!string.IsNullOrWhiteSpace(text))
            AddAnnotation(new Annotation(AnnotationKind.Text, u, v, u, v, MarkupColor, text.Trim(), MarkupSize));
    }

    [RelayCommand] private void ConfirmText() => _textTcs?.TrySetResult(TextInput);
    [RelayCommand] private void CancelText() => _textTcs?.TrySetResult(null);

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
                var oldMain = Preview; var oldCmp = CompareBitmap;
                Preview = main;
                CompareBitmap = cmp;
                Dispatcher.UIThread.Post(() => { oldMain?.Dispose(); if (!ReferenceEquals(oldCmp, cmp)) oldCmp?.Dispose(); }, DispatcherPriority.Background);
            } while (_renderPending);
        }
        catch (Exception ex) { Log.Warn("Mobil önizleme oluşturulamadı", ex); }
        finally { _rendering = false; }
    }

    // ---------- Komutlar ----------

    [RelayCommand] private void ToggleCompare() => Compare = Compare == CompareMode.Split ? CompareMode.Off : CompareMode.Split;

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
        _recipe.Exposure = _recipe.Brightness = _recipe.Contrast = _recipe.Saturation = _recipe.Temperature = _recipe.Tint = _recipe.Sharpness = 0;
        _recipe.Grayscale = false;
        _recipe.LevelsBlack = 0; _recipe.LevelsWhite = 255;
        _loading = true; Grayscale = false; _loading = false;
        LoadParam();
        Changed();
    }

    [RelayCommand]
    private void Rotate(string dir)
    {
        _recipe.Rotation = (_recipe.Rotation + (dir == "left" ? 270 : 90)) % 360;
        _recipe.Crop = NRect.Full;
        _loading = true; CropRect = NRect.Full; _loading = false;
        Changed();
    }

    [RelayCommand] private void FlipH() { _recipe.FlipH = !_recipe.FlipH; Changed(); }
    [RelayCommand] private void FlipV() { _recipe.FlipV = !_recipe.FlipV; Changed(); }

    [RelayCommand]
    private void ResetCrop()
    {
        _loading = true;
        CropRect = NRect.Full; Straighten = 0; Aspect.Select(0.0);
        _loading = false;
        _recipe.Crop = NRect.Full; _recipe.Straighten = 0; _recipe.Rotation = 0; _recipe.FlipH = _recipe.FlipV = false;
        OnPropertyChanged(nameof(CropRatioNormalized));
        Changed();
    }

    [RelayCommand]
    private void RemoveLastAnnotation()
    {
        if (_recipe.Annotations.Count == 0) return;
        var last = _recipe.Annotations[^1];
        _recipe.Annotations.RemoveAt(_recipe.Annotations.Count - 1);
        Annotations.Remove(last);
        Changed(render: last.IsRedaction);
    }

    [RelayCommand]
    private void ClearAnnotations()
    {
        var had = _recipe.Annotations.Any(a => a.IsRedaction);
        _recipe.Annotations.Clear();
        Annotations.Clear();
        Changed(render: had);
    }

    [RelayCommand]
    private async Task DetectDocument()
    {
        if (_session == null) return;
        var session = _session;
        var quad = await Task.Run(() => DocumentDetector.Detect(session.RenderPreview(new EditRecipe(), PreviewStage.BeforeDocument, false, CancellationToken.None)));
        if (quad == null) { Message?.Invoke(L.T("Belge kenarları bulunamadı; köşeleri elle yerleştirin.")); return; }
        Quad = quad;
        if (!DocumentPerspective) DocumentPerspective = true;
        else { _recipe.DocumentQuad = quad; Changed(); }
    }

    [RelayCommand]
    private void ResetDocument()
    {
        _loading = true; Quad = DefaultQuad(); _loading = false;
        if (DocumentPerspective) { _recipe.DocumentQuad = Quad; Changed(); }
    }

    [RelayCommand] private Task Save() => SaveCoreAsync(false);
    [RelayCommand] private Task SaveShare() => SaveCoreAsync(true);

    private async Task SaveCoreAsync(bool share)
    {
        if (_session == null) return;
        var fmt = FormatDetector.Detect(_source).Format;
        if (fmt == null || fmt.Id is not ("JPEG" or "PNG" or "WEBP") || !_canWrite(fmt)) fmt = FormatRegistry.Jpeg;
        var name = Path.GetFileNameWithoutExtension(_displayName) + " " + L.T("(düzenlendi)") + OutputPaths.Extension(fmt);
        var target = OutputPaths.Unique(Path.Combine(_outputDir(), OutputPaths.SanitizeFileName(name)));
        IsBusy = true;
        try
        {
            var recipe = _recipe.Clone();
            var session = _session;
            var r = await Task.Run(() => session.Save(recipe, target, fmt, 92, true, CancellationToken.None));
            if (r.Status != ConversionStatus.Success || r.Output == null) { Message?.Invoke(L.T("Kaydedilemedi: {0}", r.Message ?? "")); return; }
            IsDirty = false;
            var output = r.Output;
            await CloseAsync();
            if (Saved != null) await Saved(output, share);
        }
        catch (Exception ex)
        {
            Log.Warn("Mobil düzenleme kaydedilemedi", ex);
            Message?.Invoke(L.T("Kaydedilemedi: {0}", ex.Message));
        }
        finally { IsBusy = false; }
    }
}
