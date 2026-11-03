using NexView.Codecs.Magick;
using NexView.Core.Conversion;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Platform.Windows;

namespace NexView.App.Services;

/// <summary>Arayüzsüz komutlar: hızlı dönüştürme (sağ tık menüsü), kayıt, kurulum ve kaldırma.</summary>
public static class CommandLine
{
    public static int? TryRun(StartupArgs a)
    {
        if (a.Convert == null && !a.Register && !a.Unregister && !a.Install && !a.Uninstall) return null;
        L.Initialize(AppSettings.Load().Language);
        try
        {
            if (a.Convert != null) return Convert(a.Convert, a.Files);
            if (!OperatingSystem.IsWindows()) return 2;
            if (a.Register) return Register(Environment.ProcessPath!);
            if (a.Unregister) { new ShellIntegration(Environment.ProcessPath!).UnregisterAll(); return 0; }
            if (a.Install) return Install(a.Quiet);
            if (a.Uninstall) return Uninstall(a.Quiet);
        }
        catch (Exception ex)
        {
            Log.Error("Komut satırı işlemi başarısız", ex);
            if (OperatingSystem.IsWindows() && !a.Quiet) NativeDialogs.Error(ex.Message);
            return 1;
        }
        return null;
    }

    public static ConversionOptions? OptionsFor(string target) => Presets.Resolve(target);

    private static int Convert(string target, List<string> files)
    {
        var options = OptionsFor(target);
        if (options == null) { Fail(L.T("Bilinmeyen hedef: {0}", target)); return 1; }
        var converter = new MagickConverter();
        var errors = new List<string>();
        foreach (var f in files.Where(File.Exists))
        {
            var r = converter.Convert(f, options, new OutputOptions(), CancellationToken.None);
            if (r.Status == ConversionStatus.Failed) errors.Add($"{Path.GetFileName(f)}: {r.Message}");
            else if (r.Output != null) Log.Info($"Sağ tık dönüştürme: {f} → {r.Output}");
        }
        if (errors.Count > 0) { Fail(L.T("Dönüştürülemeyen dosyalar:") + "\n" + string.Join("\n", errors)); return 1; }
        return 0;
    }

    private static void Fail(string message)
    {
        Log.Warn(message);
        if (OperatingSystem.IsWindows()) NativeDialogs.Error(message);
        else Console.Error.WriteLine(message);
    }

    public static IEnumerable<string> ExtensionsFor(IEnumerable<string> groups)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var set = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ShellIntegration.Groups.Where(g => set.Contains(g.Id)).SelectMany(g => g.Extensions);
    }

    private static int Register(string exe)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        var settings = AppSettings.Load();
        var exts = ExtensionsFor(settings.AssociatedGroups ?? ShellIntegration.DefaultGroups.ToList()).ToList();
        var shell = new ShellIntegration(exe);
        shell.RegisterFileTypes(exts);
        shell.RegisterContextMenu(exts);
        return 0;
    }

    /// <summary>quiet: uygulama içi güncelleme (sessiz kurulum, masaüstü kısayolu durumu korunur, ardından yeni sürüm açılır).</summary>
    private static int Install(bool quiet)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        var exe = Installer.Install(desktopShortcut: !quiet || Installer.HasDesktopShortcut, Version);
        Register(exe);
        if (quiet) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        else NativeDialogs.Info(L.T("NexView kuruldu. Başlat menüsünden açabilirsiniz."));
        return 0;
    }

    private static int Uninstall(bool quiet)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (!quiet && !NativeDialogs.Confirm(L.T("NexView bilgisayarınızdan kaldırılsın mı?"))) return 1;
        new ShellIntegration(Installer.InstalledExe).UnregisterAll();
        var removeData = !quiet && NativeDialogs.Confirm(L.T("Ayarlar, favoriler ve önbellek de silinsin mi?"));
        Installer.Uninstall(removeData);
        if (!quiet) NativeDialogs.Info(L.T("NexView kaldırıldı."));
        return 0;
    }

    public static string Version => typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
