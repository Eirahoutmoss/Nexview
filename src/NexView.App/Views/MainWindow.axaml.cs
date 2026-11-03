using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NexView.UI.Controls;
using NexView.UI;
using NexView.App.ViewModels;
using NexView.Core.Formats;

namespace NexView.App.Views;

public partial class MainWindow : Window, IUiHost
{
    private readonly MainViewModel _vm;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _syncingSlider;

    public MainWindow() : this(null!) { }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        if (vm == null) return;
        vm.Host = this;

        var st = vm.Services.Settings;
        Width = st.WindowWidth;
        Height = st.WindowHeight;
        if (st.WindowMaximized) WindowState = WindowState.Maximized;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        TitleBar.PointerPressed += OnTitleBarPressed;
        Filmstrip.ContainerPrepared += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < _vm.Items.Count) _vm.Items[e.Index].EnsureThumbnail(_vm.Services.Thumbnails);
        };

        Viewport.PropertyChanged += (_, e) =>
        {
            if (e.Property == ImageViewport.ZoomProperty) SyncZoomUi();
        };
        ZoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && !_syncingSlider)
                Viewport.ZoomTo(Math.Exp(ZoomSlider.Value), null);
        };
        SyncZoomUi();
        Overlay.Viewport = Viewport;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Dialog) && vm.Dialog is { HasInput: true })
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { DialogInput.Focus(); DialogInput.SelectAll(); }, Avalonia.Threading.DispatcherPriority.Input);
        };

        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal) { st.WindowWidth = Width; st.WindowHeight = Height; }
            st.WindowMaximized = WindowState == WindowState.Maximized;
        };
    }

    private void SyncZoomUi()
    {
        _syncingSlider = true;
        try
        {
            ZoomSlider.Value = Math.Log(Math.Max(ImageViewport.MinZoom, Viewport.Zoom));
            ZoomText.Text = $"{Viewport.Zoom * 100:0}%";
        }
        finally { _syncingSlider = false; }
    }

    private void OnShortcutsBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender)) _vm.ShowShortcuts = false;
    }

    // ---------- Araç çubuğu ----------

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Viewport.ZoomBy(1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Viewport.ZoomBy(1 / 1.25);
    private void OnFit(object? sender, RoutedEventArgs e) => Viewport.FitToWindow();
    private void OnActual(object? sender, RoutedEventArgs e) => Viewport.ActualSize();

    // ---------- Başlık çubuğu ----------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    // ---------- Klavye ----------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_vm.Dialog is { } dlg)
        {
            if (e.Key == Key.Escape) { dlg.CancelCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Enter) { dlg.PrimaryCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (_vm.ShowShortcuts)
        {
            if (e.Key is Key.Escape or Key.F1) { _vm.ShowShortcuts = false; e.Handled = true; }
            return;
        }
        if (_vm.ShowSettings)
        {
            if (e.Key == Key.Escape) { _vm.ShowSettings = false; e.Handled = true; }
            return;
        }
        if (e.Source is TextBox or NumericUpDown || (e.Source as Visual)?.FindAncestorOfType<NumericUpDown>() != null) return;
        if (e.Source is Visual src && src.FindAncestorOfType<ComboBox>(includeSelf: true) != null && e.Key is Key.Up or Key.Down) return;

        if (_vm.Edit.IsActive)
        {
            bool eh = true;
            switch (e.Key)
            {
                case Key.Z when ctrl: _vm.Edit.UndoCommand.Execute(null); break;
                case Key.Y when ctrl: _vm.Edit.RedoCommand.Execute(null); break;
                case Key.S when ctrl: _vm.Edit.SaveCommand.Execute(null); break;
                case Key.Escape: _vm.Edit.CancelCommand.Execute(null); break;
                case Key.Delete: _vm.Edit.RemoveLastAnnotationCommand.Execute(null); break;
                default: eh = false; break;
            }
            if (eh) { e.Handled = true; return; }
        }

        bool handled = true;
        switch (e.Key)
        {
            case Key.E when !ctrl: _vm.ShowEditTabCommand.Execute(null); break;
            case Key.F1: _vm.ShowShortcuts = true; break;
            case Key.O when ctrl && shift: _vm.OpenFolderCommand.Execute(null); break;
            case Key.O when ctrl: _vm.OpenFileCommand.Execute(null); break;
            case Key.C when ctrl && shift: _vm.CopyPathCommand.Execute(null); break;
            case Key.C when ctrl: _vm.CopyImageCommand.Execute(null); break;
            case Key.V when ctrl: _vm.PasteCommand.Execute(null); break;
            case Key.P when ctrl: _vm.PrintCommand.Execute(null); break;
            case Key.S when ctrl: _vm.ShowConvertTabCommand.Execute(null); break;
            case Key.Left or Key.PageUp: _vm.PreviousCommand.Execute(null); break;
            case Key.Right or Key.PageDown or Key.Space: _vm.NextCommand.Execute(null); break;
            case Key.Home when _vm.Items.Count > 0: _vm.SelectedItem = _vm.Items[0]; break;
            case Key.End when _vm.Items.Count > 0: _vm.SelectedItem = _vm.Items[^1]; break;
            case Key.F when !ctrl: Viewport.FitToWindow(); break;
            case Key.D1 or Key.NumPad1 when !ctrl: Viewport.ActualSize(); break;
            case Key.OemPlus or Key.Add: Viewport.ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract: Viewport.ZoomBy(1 / 1.25); break;
            case Key.R when shift: _vm.RotateLeftCommand.Execute(null); break;
            case Key.R when !ctrl: _vm.RotateRightCommand.Execute(null); break;
            case Key.H when !ctrl: _vm.FlipCommand.Execute(null); break;
            case Key.G when !ctrl: _vm.CycleBackgroundCommand.Execute(null); break;
            case Key.A when !ctrl: _vm.ToggleStretchCommand.Execute(null); break;
            case Key.P when !ctrl: _vm.TogglePlayCommand.Execute(null); break;
            case Key.I when !ctrl: _vm.ShowInfoTabCommand.Execute(null); break;
            case Key.C when !ctrl: _vm.ShowConvertTabCommand.Execute(null); break;
            case Key.B when !ctrl: _vm.OpenBatchCommand.Execute(null); break;
            case Key.T when !ctrl: _vm.ToggleFilmstripCommand.Execute(null); break;
            case Key.S when !ctrl: _vm.ToggleFavoriteCommand.Execute(null); break;
            case Key.F2: _vm.RenameCommand.Execute(null); break;
            case Key.Delete: _vm.DeleteCommand.Execute(null); break;
            case Key.F11: _vm.ToggleFullScreenCommand.Execute(null); break;
            case Key.Escape when _vm.IsFullScreen: _vm.ExitFullScreenCommand.Execute(null); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // ---------- Sürükle-bırak ----------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            _vm.IsDragOver = true;
        }
        else e.DragEffects = DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => _vm.IsDragOver = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        _vm.IsDragOver = false;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is { Count: > 0 }) await _vm.HandleDropAsync(paths);
    }

    // ---------- IUiHost ----------

    private StudioWindow? _studio;

    private void OnOpen360(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm.SelectedItem?.Path is { } path) new PanoramaWindow(_vm.Services, path).Show(this);
    }

    private void OnOpenStudio(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_studio != null) { _studio.Activate(); return; }
        // Görüntüleyicideki liste küçükse (tek bir çekim serisi gibi) karelerle birlikte aç
        var initial = _vm.Items.Count is >= 2 and <= 40 ? _vm.Items.Select(i => i.Path).ToList() : new List<string>();
        _studio = new StudioWindow(new StudioViewModel(_vm.Services, _vm, initial), this);
        _studio.Closed += (_, _) => _studio = null;
        _studio.Show(this);
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var patterns = FormatRegistry.AllExtensions.Select(x => "*" + x).ToArray();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Görüntü Aç",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Tüm görüntüler") { Patterns = patterns },
                new FilePickerFileType("RAW dosyaları") { Patterns = FormatRegistry.All.Where(f => f.IsRaw).SelectMany(f => f.Extensions).Select(x => "*" + x).ToArray() },
                FilePickerFileTypes.All,
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Klasör Seç" });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string? folder)
    {
        var options = new FilePickerSaveOptions
        {
            Title = Core.Localization.L.T("Farklı kaydet"),
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("JPEG") { Patterns = ["*.jpg"] },
                new FilePickerFileType("PNG") { Patterns = ["*.png"] },
                new FilePickerFileType("WebP") { Patterns = ["*.webp"] },
                new FilePickerFileType("AVIF") { Patterns = ["*.avif"] },
                new FilePickerFileType("TIFF") { Patterns = ["*.tif"] },
            ],
        };
        if (folder != null) options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard != null) await Clipboard.SetTextAsync(text);
    }

    public void SetFullScreen(bool on)
    {
        if (on)
        {
            _stateBeforeFullScreen = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
            MainArea.Margin = new Thickness(0);
            ViewerHost.CornerRadius = new CornerRadius(0);
            ViewerHost.BorderThickness = new Thickness(0);
        }
        else
        {
            WindowState = _stateBeforeFullScreen;
            MainArea.Margin = new Thickness(10, 0, 10, 10);
            ViewerHost.CornerRadius = new CornerRadius(14);
            ViewerHost.BorderThickness = new Thickness(1);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Viewport.FitToWindow(), Avalonia.Threading.DispatcherPriority.Background);
    }

    public void ResetView() => Viewport.FitToWindow();

    /// <summary>Başka bir NexView başlatıldığında gelen dosyaları açar ve pencereyi öne getirir.</summary>
    public async void ReceiveFromOtherInstance(string[] files)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        var tab = files.FirstOrDefault(f => f.StartsWith("::tab="))?[6..];
        var paths = files.Where(f => !f.StartsWith("::")).ToArray();
        if (paths.Length > 0) await _vm.OpenPathsAsync(paths);
        await _vm.ApplyStartupTabAsync(tab);
    }
}
