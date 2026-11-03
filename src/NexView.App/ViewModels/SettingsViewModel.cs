using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexView.App.Services;
using NexView.Codecs.Magick;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Platform.Windows;
using System.Collections.ObjectModel;

namespace NexView.App.ViewModels;

public sealed partial class FormatToggle(string id, string name, bool isChecked) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isChecked = isChecked;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main, AppServices services)
    {
        _main = main;
        _s = services;
        Languages = L.Available.Select(l => new ChoiceItem(l.Code, l.Code == "auto" ? L.T(l.Name) : l.Name)).ToList();
        _language = Languages.FirstOrDefault(l => l.Value == _s.Settings.Language) ?? Languages[0];
        _autoStretchFits = _s.Settings.AutoStretchFits;
        _restoreSession = _s.Settings.RestoreSession;
        if (OperatingSystem.IsWindows())
        {
            var selected = (_s.Settings.AssociatedGroups ?? ShellIntegration.DefaultGroups.ToList()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in ShellIntegration.Groups) FormatToggles.Add(new FormatToggle(g.Id, g.Name, selected.Contains(g.Id)));
        }
    }

    // ---------- Windows entegrasyonu ----------

    public bool IsWindows => OperatingSystem.IsWindows();
    public ObservableCollection<FormatToggle> FormatToggles { get; } = new();

    [ObservableProperty] private bool _registerFileTypes;
    [ObservableProperty] private bool _contextMenu;
    [ObservableProperty] private bool _desktopShortcut;
    [ObservableProperty] private string _integrationStatus = "";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _canInstall;

    private string CurrentExe => OperatingSystem.IsWindows() && Installer.IsInstalled ? Installer.InstalledExe : Environment.ProcessPath!;

    private void RefreshIntegration()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = new ShellIntegration(CurrentExe);
        RegisterFileTypes = shell.IsFileTypesRegistered;
        ContextMenu = shell.IsContextMenuRegistered;
        DesktopShortcut = Installer.HasDesktopShortcut;
        IsInstalled = Installer.IsInstalled;
        CanInstall = !IsInstalled && !AppPaths.IsPortable;
        var regExe = shell.RegisteredExePath();
        IntegrationStatus = (IsInstalled ? L.T("Kurulu: {0}", Installer.InstallDir) : L.T("Kurulu değil (bu konumdan çalışıyor)"))
            + (regExe != null && !string.Equals(regExe, CurrentExe, StringComparison.OrdinalIgnoreCase) ? "\n" + L.T("Uyarı: dosya kayıtları başka bir konumu gösteriyor; Uygula ile düzeltin.") : "");
    }

    [RelayCommand]
    private void ApplyIntegration()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var groups = FormatToggles.Where(f => f.IsChecked).Select(f => f.Id).ToList();
            _s.Settings.AssociatedGroups = groups;
            _s.Settings.Save();
            var exts = CommandLine.ExtensionsFor(groups).ToList();
            var shell = new ShellIntegration(CurrentExe);
            if (RegisterFileTypes && exts.Count > 0) shell.RegisterFileTypes(exts); else shell.UnregisterFileTypes();
            if (ContextMenu && exts.Count > 0) shell.RegisterContextMenu(exts); else shell.UnregisterContextMenu();
            Installer.SetDesktopShortcut(DesktopShortcut);
            RefreshIntegration();
            _main.ShowToast(L.T("Windows entegrasyonu güncellendi."));
        }
        catch (Exception ex)
        {
            Log.Warn("Entegrasyon uygulanamadı", ex);
            _main.ShowToast(L.T("Entegrasyon uygulanamadı: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private void OpenDefaultApps()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!new ShellIntegration(CurrentExe).IsFileTypesRegistered) { RegisterFileTypes = true; ApplyIntegration(); }
        ShellIntegration.OpenDefaultAppsSettings();
    }

    [RelayCommand]
    private async Task Install()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = await Task.Run(() => Installer.Install(DesktopShortcut || !Installer.HasDesktopShortcut, CommandLine.Version));
            RegisterFileTypes = true;
            ContextMenu = true;
            ApplyIntegration();
            _main.ShowToast(L.T("NexView kuruldu. Başlat menüsünden açabilirsiniz."));
            Log.Info($"Kurulum tamam: {exe}");
        }
        catch (Exception ex)
        {
            Log.Warn("Kurulum başarısız", ex);
            _main.ShowToast(L.T("Kurulum başarısız: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        var choice = await _main.ShowDialogAsync(new DialogViewModel(L.T("NexView kaldırılsın mı?"),
            L.T("Dosya kayıtları, sağ tık menüsü, kısayollar ve kurulum klasörü kaldırılır. Görüntülerinize dokunulmaz."), L.T("Kaldır")) { IsDanger = true });
        if (choice != DialogChoice.Primary) return;
        new ShellIntegration(Installer.InstalledExe).UnregisterAll();
        new ShellIntegration(Environment.ProcessPath!).UnregisterAll();
        Installer.Uninstall(removeUserData: false);
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    public IReadOnlyList<ChoiceItem> Languages { get; }


    [ObservableProperty] private ChoiceItem _language;
    [ObservableProperty] private bool _autoStretchFits;
    [ObservableProperty] private string _cacheText = "";

    public string DataFolder => AppPaths.DataDir + (AppPaths.IsPortable ? "  (" + L.T("taşınabilir mod") + ")" : "");
    public string VersionText => $"NexView {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}"
        + (Edition.HasDedication ? $" ({Edition.Label})" : "") + "  ·  © 2026 Hasan Güler  ·  MIT";
    public string? DedicationText => Edition.Dedication;

    // ---------- Güncelleme ----------

    private Core.Updates.UpdateInfo? _update;
    private CancellationTokenSource? _updateCts;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private double _updateProgress;

    private static Version CurrentVersion => typeof(SettingsViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);

    [RelayCommand]
    private async Task CheckUpdates()
    {
        if (UpdateBusy) return;
        UpdateBusy = true;
        UpdateAvailable = false;
        UpdateStatus = L.T("Denetleniyor…");
        try
        {
            _update = await Core.Updates.UpdateChecker.CheckAsync(CurrentVersion, CancellationToken.None);
            if (_update == null) UpdateStatus = L.T("NexView güncel ({0}).", CurrentVersion.ToString(3));
            else
            {
                UpdateAvailable = true;
                UpdateStatus = L.T("Yeni sürüm var: {0}", _update.Title);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Güncelleme denetlenemedi", ex);
            UpdateStatus = L.T("Güncelleme denetlenemedi. İnternet bağlantınızı kontrol edin.");
        }
        finally { UpdateBusy = false; }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_update == null || UpdateBusy) return;
        var asset = Edition.HasDedication ? null : _update.Find("Setup", ".exe");
        if (asset == null || !OperatingSystem.IsWindows())
        {
            // Taşınabilir sürümler ve kurulum dosyası olmayan sürümler: sürüm sayfası açılır
            PlatformShell.OpenUri(_update.PageUrl);
            return;
        }
        UpdateBusy = true;
        _updateCts = new CancellationTokenSource();
        try
        {
            UpdateStatus = L.T("İndiriliyor: {0}", asset.Name);
            var folder = Path.Combine(Path.GetTempPath(), "NexView-update");
            var file = await Core.Updates.UpdateChecker.DownloadAsync(asset, folder,
                new Progress<double>(p => UpdateProgress = p), _updateCts.Token);
            UpdateStatus = L.T("Kuruluyor; NexView yeniden açılacak…");
            _s.Settings.Save();
            _s.Library.Save();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, "--install --quiet") { UseShellExecute = true });
            await Task.Delay(400);
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                d.Shutdown();
        }
        catch (OperationCanceledException) { UpdateStatus = L.T("İndirme iptal edildi."); }
        catch (Exception ex)
        {
            Log.Warn("Güncelleme kurulamadı", ex);
            UpdateStatus = L.T("Güncelleme kurulamadı: {0}", ex.Message);
        }
        finally { UpdateBusy = false; UpdateProgress = 0; }
    }

    [RelayCommand] private void CancelUpdate() => _updateCts?.Cancel();
    [RelayCommand] private void OpenReleases() => PlatformShell.OpenUri(Core.Updates.UpdateChecker.ReleasesPage);
    public bool HasDedication => Edition.HasDedication;
    public string LibraryText => MagickCodec.LibraryVersion;

    partial void OnLanguageChanged(ChoiceItem value)
    {
        if (_s.Settings.Language == value.Value) return;
        _s.Settings.Language = value.Value;
        _s.Settings.Save();
        _main.ShowToast(L.T("Dil değişikliği uygulama yeniden başlatıldığında geçerli olur."));
    }

    partial void OnAutoStretchFitsChanged(bool value) => _s.Settings.AutoStretchFits = value;

    [ObservableProperty] private bool _restoreSession;
    partial void OnRestoreSessionChanged(bool value) => _s.Settings.RestoreSession = value;

    [RelayCommand]
    private async Task CreateDiagnostics()
    {
        try
        {
            var path = await Task.Run(() => Diagnostics.CreateReport(_s));
            _main.ShowToast(L.T("Tanılama raporu oluşturuldu: {0}", Path.GetFileName(path)));
            PlatformShell.ShowInFolder(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Tanılama raporu oluşturulamadı", ex);
            _main.ShowToast(L.T("Tanılama raporu oluşturulamadı: {0}", ex.Message));
        }
    }

    public async Task RefreshAsync()
    {
        RefreshIntegration();
        CacheText = L.T("Hesaplanıyor…");
        var bytes = await Task.Run(() => _s.Thumbnails.GetSizeBytes());
        CacheText = L.T("Küçük resim önbelleği: {0}", MainViewModel.FormatBytes(bytes));
    }

    [RelayCommand]
    private async Task ClearCache()
    {
        await Task.Run(() => _s.Thumbnails.Clear());
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenDataFolder() => PlatformShell.OpenUri(AppPaths.DataDir);

    [RelayCommand]
    private void OpenLogs() => PlatformShell.OpenUri(AppPaths.LogsDir);

    [RelayCommand]
    private void Close() => _main.ShowSettings = false;
}
