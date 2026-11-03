using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using NexView.Codecs.Magick;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;

namespace NexView.App.Services;

/// <summary>Yardım → tanılama raporu: günlükler, ayarlar ve sistem/format bilgisi (görüntü içermez).</summary>
public static class Diagnostics
{
    public static string CreateReport(AppServices s)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) desktop = AppPaths.DataDir;
        var zipPath = Path.Combine(desktop, $"NexView-tanilama-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        var sb = new StringBuilder();
        sb.AppendLine($"NexView {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"Tarih: {DateTime.Now:O}");
        sb.AppendLine($"İşletim sistemi: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Codec: {MagickCodec.LibraryVersion}");
        sb.AppendLine($"Veri klasörü: {AppPaths.DataDir} (taşınabilir: {AppPaths.IsPortable})");
        sb.AppendLine($"İşlemci: {Environment.ProcessorCount}, bellek (GC): {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB");
        sb.AppendLine();
        sb.AppendLine("Format desteği:");
        foreach (var f in FormatRegistry.All)
            sb.AppendLine($"  {f.Id,-6} okuma: {s.Codecs.GetSupport(f),-12} yazma: {s.Converter.CanWrite(f)}");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("tanilama.txt");
        using (var w = new StreamWriter(entry.Open(), Encoding.UTF8)) w.Write(sb.ToString());
        if (File.Exists(AppPaths.SettingsFile)) zip.CreateEntryFromFile(AppPaths.SettingsFile, "settings.json");
        foreach (var log in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log").OrderByDescending(File.GetLastWriteTime).Take(7))
        {
            // Açık günlük dosyası paylaşımlı okunur
            using var src = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = zip.CreateEntry("logs/" + Path.GetFileName(log)).Open();
            src.CopyTo(dst);
        }
        return zipPath;
    }
}
