using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Mobile.Services;
using NexView.Mobile.ViewModels;
using NexView.Mobile.Views;

namespace NexView.Mobile;

/// <summary>Android (ve ileride iOS) uygulama kökü: tek görünümlü yaşam döngüsü.</summary>
public partial class MobileApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = (MobileServices.Factory ?? throw new InvalidOperationException("MobileServices.Factory atanmadı."))();
        L.Initialize(services.Settings.Language);
        Log.Info($"NexView mobil başlatıldı ({services.Platform.Name})");
        var view = new MobileView { DataContext = new MobileViewModel(services) };
        if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = view;
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Avalonia.Controls.Window { Content = view, Width = 412, Height = 915, Title = "NexView Mobile" };
        base.OnFrameworkInitializationCompleted();
    }
}
