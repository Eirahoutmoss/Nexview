using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NexView.App.Services;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Platform.Windows;

namespace NexView.App.Views;

/// <summary>Tek dosyalık kurulum sihirbazı (NexView-x.y.z-Setup.exe). Yönetici izni gerekmez.</summary>
public partial class SetupWindow : Window, INotifyPropertyChanged
{
    private int _stage; // 0 seçenekler, 1 kuruluyor, 2 bitti
    private string _doneText = "";

    public SetupWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string VersionText => "v" + CommandLine.Version;
    public string InstallDir => OperatingSystem.IsWindows() ? Installer.InstallDir : "";
    private bool Existing => OperatingSystem.IsWindows() && Installer.IsInstalled;
    public string ModeText => Existing ? L.T("Mevcut kurulum güncellenecek") : L.T("Kurulum konumu");
    public string InstallButtonText => Existing ? L.T("Güncelle") : L.T("Kur");

    public bool Associate { get; set; } = true;
    public bool AddContextMenu { get; set; } = true;
    public bool DesktopShortcut { get; set; } = true;
    public bool LaunchAfter { get; set; } = true;

    public bool IsOptions => _stage == 0;
    public bool IsWorking => _stage == 1;
    public bool IsDone => _stage == 2;
    public string DoneText { get => _doneText; private set { _doneText = value; Raise(); } }

    private void SetStage(int s)
    {
        _stage = s;
        Raise(nameof(IsOptions)); Raise(nameof(IsWorking)); Raise(nameof(IsDone));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        bool update = Existing;
        SetStage(1);
        try
        {
            await Task.Run(() =>
            {
                var exe = Installer.Install(DesktopShortcut, CommandLine.Version);
                var settings = AppSettings.Load();
                var groups = settings.AssociatedGroups ?? ShellIntegration.DefaultGroups.ToList();
                var exts = CommandLine.ExtensionsFor(groups).ToList();
                var shell = new ShellIntegration(exe);
                if (Associate) shell.RegisterFileTypes(exts); else shell.UnregisterFileTypes();
                if (AddContextMenu) shell.RegisterContextMenu(exts); else shell.UnregisterContextMenu();
            });
            DoneText = update ? L.T("NexView güncellendi.") : L.T("NexView kuruldu.");
            SetStage(2);
        }
        catch (Exception ex)
        {
            Log.Error("Kurulum başarısız", ex);
            DoneText = L.T("Kurulum başarısız: {0}", ex.Message);
            SetStage(2);
            LaunchAfter = false;
        }
    }

    private void OnFinish(object? sender, RoutedEventArgs e)
    {
        if (LaunchAfter && OperatingSystem.IsWindows() && File.Exists(Installer.InstalledExe))
        {
            try { Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("NexView başlatılamadı", ex); }
        }
        Close();
    }
}
