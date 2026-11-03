using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NexView.Mobile.ViewModels;

namespace NexView.Mobile.Views;

public partial class MobileView : UserControl, IPickerHost
{
    private MobileItem? _heldItem;
    private bool _fitEdit = true;

    public MobileView()
    {
        InitializeComponent();
        Viewport.Swiped += (_, d) => Vm?.Step(d);
        Viewport.Tapped2 += (_, _) => Vm?.ToggleChromeCommand.Execute(null);
        EditOverlay.Viewport = EditViewport;
    }

    private MobileViewModel? Vm => DataContext as MobileViewModel;
    public IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm != null)
        {
            Vm.Host = this;
            if (Vm.Edit != null)
                Vm.Edit.PropertyChanged += (_, pe) =>
                {
                    if (pe.PropertyName == nameof(MobileEditViewModel.IsTextPromptOpen) && Vm.Edit.IsTextPromptOpen)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => EditTextBox.Focus(), Avalonia.Threading.DispatcherPriority.Background);
                    else if (pe.PropertyName == nameof(MobileEditViewModel.Preview) && Vm.Edit.Preview != null && _fitEdit)
                    {
                        _fitEdit = false;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => EditViewport.FitToWindow(), Avalonia.Threading.DispatcherPriority.Background);
                    }
                    else if (pe.PropertyName == nameof(MobileEditViewModel.Tool) || pe.PropertyName == nameof(MobileEditViewModel.IsActive))
                        _fitEdit = true;
                };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        top.BackRequested += OnBackRequested;
        if (top.InsetsManager is { } im)
        {
            im.DisplayEdgeToEdgePreference = true;
            void Apply() => Root.Margin = im.SafeAreaPadding;
            im.SafeAreaChanged += (_, _) => Apply();
            Apply();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested -= OnBackRequested;
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Back() == true) e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Vm is not { } vm) return;
        if (e.Key == Key.Escape && vm.Back()) e.Handled = true;
        else if (vm.IsViewerOpen && e.Key == Key.Right) vm.Step(1);
        else if (vm.IsViewerOpen && e.Key == Key.Left) vm.Step(-1);
    }

    private void OnItemAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: MobileItem item }) Vm?.EnsureThumbnail(item);
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MobileItem item }) return;
        if (ReferenceEquals(_heldItem, item)) { _heldItem = null; return; }
        Vm?.ItemTappedCommand.Execute(item);
    }

    private void OnItemHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || sender is not Control { DataContext: MobileItem item }) return;
        _heldItem = item;
        Vm?.ItemHeld(item);
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => Vm?.CloseConvertCommand.Execute(null);
}
