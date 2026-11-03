using System.Diagnostics;
using NexView.Core.Infrastructure;

namespace NexView.App.Services;

/// <summary>İşletim sistemine özgü kabuk işlemleri. Windows dışı platformlar için güvenli geri dönüş içerir.</summary>
public static class PlatformShell
{
    public static void ShowInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", ["-R", path]);
            else
                Process.Start("xdg-open", [Path.GetDirectoryName(path) ?? path]);
        }
        catch (Exception ex) { Log.Warn("Klasör açılamadı", ex); }
    }

    public static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Bağlantı açılamadı", ex); }
    }

    public static string? ScreenshotsFolder()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        foreach (var name in new[] { "Screenshots", "Ekran Görüntüleri" })
        {
            var p = Path.Combine(pictures, name);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
