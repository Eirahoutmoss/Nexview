using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NexView.App.ViewModels;

namespace NexView.App.Views;

public partial class StudioWindow : Window
{
    public StudioWindow() : this(null!, null!) { }

    public StudioWindow(StudioViewModel vm, IUiHost host)
    {
        InitializeComponent();
        DataContext = vm;
        if (vm == null) return;
        vm.PickFiles = host.PickFilesAsync;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 }) await vm.AddAsync(paths);
        });
        vm.Open360Requested += path => new PanoramaWindow(vm.Services, path).Show(this);
        Closing += (_, _) => vm.Shutdown();
    }
}
