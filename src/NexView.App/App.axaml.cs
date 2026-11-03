using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NexView.App.Services;
using NexView.App.ViewModels;
using NexView.App.Views;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;

namespace NexView.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime setupDesktop &&
            (Program.IsSetupMode || DevShot.WantsSetup(Program.Args.DevShotPath)))
        {
            L.Initialize(AppSettings.Load().Language);
            var setup = new SetupWindow();
            setupDesktop.MainWindow = setup;
            if (Program.Args.DevShotPath is { } setupShot)
            {
                setup.Opened += async (_, _) =>
                {
                    await Task.Delay(1500);
                    DevShot.Capture(setup, setupShot);
                    setupDesktop.Shutdown();
                };
            }
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime mobileDesktop &&
            (Environment.GetCommandLineArgs().Contains("--mobile") || DevShot.WantsWindow(Program.Args.DevShotPath, "mobile")))
        {
            MobilePreview.Show(mobileDesktop, Program.Args.DevShotPath);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            L.Initialize(services.Settings.Language);
            var vm = new MainViewModel(services);
            Log.Info("Arayüz oluşturuluyor");
            var window = new MainWindow(vm);
            Log.Info("Pencere oluşturuldu");
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => services.Shutdown();

            var args = Program.Args;
            if (args.DevShotPath is { } shot)
            {
                window.Width = 1440;
                window.Height = 900;
                window.Opened += async (_, _) =>
                {
                    try
                    {
                        Log.Info("devshot: pencere açıldı");
                        if (args.Files.Count > 0) await vm.OpenPathsAsync(args.Files);
                        Log.Info("devshot: dosya açıldı");
                        await vm.WaitForIdleAsync();
                        Log.Info("devshot: yükleme bitti");
                        await DevShot.ApplyOptionsAsync(vm, Path.Combine(Path.GetDirectoryName(shot) ?? ".", "shot-args.txt"));
                        Log.Info("devshot: seçenekler uygulandı");
                        await Task.Delay(2500);
                        DevShot.Capture(window, shot);
                        Log.Info("devshot: kaydedildi");
                    }
                    catch (Exception ex) { Log.Error("Devshot başarısız", ex); }
                    finally { desktop.Shutdown(); }
                };
            }
            else
            {
                if (args.Files.Count > 0) window.Opened += async (_, _) =>
                {
                    await vm.OpenPathsAsync(args.Files);
                    await vm.WaitForIdleAsync();
                    await vm.ApplyStartupTabAsync(args.Tab);
                };
                var cts = new CancellationTokenSource();
                desktop.ShutdownRequested += (_, _) => cts.Cancel();
                SingleInstance.StartServer(files => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.ReceiveFromOtherInstance(files)), cts.Token);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
