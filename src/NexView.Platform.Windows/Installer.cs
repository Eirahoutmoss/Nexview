using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NexView.Core.Infrastructure;

namespace NexView.Platform.Windows;

/// <summary>Kullanıcı bazlı kurulum (%LOCALAPPDATA%\Programs\NexView). Yönetici izni gerekmez.</summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexView";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NexView");
    public static string InstalledExe => Path.Combine(InstallDir, "NexView.exe");
    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NexView.lnk");
    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NexView.lnk");

    public static bool IsInstalled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return k != null && File.Exists(InstalledExe);
        }
    }

    public static bool IsRunningFromInstallDir =>
        AppContext.BaseDirectory.TrimEnd('\\').StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);

    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    /// <summary>Çalışan uygulamanın dosyalarını kurulum klasörüne kopyalar, kısayolları ve kaldırma kaydını oluşturur.</summary>
    public static string Install(bool desktopShortcut, string version)
    {
        Directory.CreateDirectory(InstallDir);
        CloseRunningInstalledCopies();
        if (!IsRunningFromInstallDir)
        {
            var self = Environment.ProcessPath!;
            bool singleFile = string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
            if (singleFile)
            {
                // Tek dosyalık yayın (kurulum exe'si dahil): yalnızca exe kopyalanır ve NexView.exe olarak adlandırılır
                CopyWithRetry(self, InstalledExe);
            }
            else
            {
                var source = AppContext.BaseDirectory;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(source, file);
                    if (rel.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        rel.Equals("portable.txt", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(InstallDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    CopyWithRetry(file, dest);
                }
            }
        }

        CreateShortcut(StartMenuShortcut, InstalledExe);
        SetDesktopShortcut(desktopShortcut);

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "NexView");
            k.SetValue("DisplayVersion", version);
            k.SetValue("Publisher", "Hasan Güler");
            k.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            k.SetValue("URLInfoAbout", "https://github.com/Eirahoutmoss");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            var size = Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
            k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size), RegistryValueKind.DWord);
        }
        Log.Info($"Kuruldu: {InstallDir}");
        return InstalledExe;
    }

    /// <summary>Kurulu NexView açıksa (dosya kilitli olmasın diye) kapatır.</summary>
    public static void CloseRunningInstalledCopies()
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("NexView"))
        {
            try
            {
                if (p.Id == self) continue;
                var path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase))
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
                }
            }
            catch (Exception ex) { Log.Debug($"Çalışan kopya kapatılamadı: {ex.Message}"); }
            finally { p.Dispose(); }
        }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(300); }
        }
    }

    public static void SetDesktopShortcut(bool on)
    {
        if (on) CreateShortcut(DesktopShortcut, File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath!);
        else TryDelete(DesktopShortcut);
    }

    /// <summary>Kısayolları ve kaldırma kaydını siler; klasörü uygulama kapandıktan sonra siler.</summary>
    public static void Uninstall(bool removeUserData)
    {
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        var targets = new List<string> { InstallDir };
        if (removeUserData) targets.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexView"));
        var script = "timeout /t 2 /nobreak >nul & " + string.Join(" & ", targets.Select(t => $"rmdir /s /q \"{t}\""));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {script}") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        Log.Info("Kaldırıldı");
    }

    public static void CreateShortcut(string lnkPath, string target, string arguments = "")
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell yok");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "NexView — Görüntülerin tüm dünyası";
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"Kısayol oluşturulamadı: {lnkPath}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
