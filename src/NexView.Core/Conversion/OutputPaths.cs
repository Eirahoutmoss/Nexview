using NexView.Core.Formats;

namespace NexView.Core.Conversion;

public static class OutputPaths
{
    public static string Extension(ImageFormat f) => f.Id switch
    {
        "JPEG" => ".jpg",
        "TIFF" => ".tif",
        "HEIC" => ".heic",
        "PDF" => ".pdf",
        _ => f.PrimaryExtension,
    };

    /// <summary>Hedef yolu üretir. Orijinal dosyanın üzerine asla yazılmaz.</summary>
    public static string? Build(string source, ImageFormat target, OutputOptions o)
    {
        var folder = string.IsNullOrWhiteSpace(o.Folder) ? Path.GetDirectoryName(Path.GetFullPath(source))! : o.Folder!;
        if (!string.IsNullOrWhiteSpace(o.Subfolder)) folder = Path.Combine(folder, o.Subfolder);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(source) + o.Suffix;
        var ext = Extension(target);
        var path = Path.Combine(folder, name + ext);
        if (!Exists(path, source)) return path;
        if (o.Conflict == ConflictPolicy.Skip) return null;
        for (int i = 1; i < 10000; i++)
        {
            var p = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!Exists(p, source)) return p;
        }
        throw new IOException("Uygun bir çıktı adı bulunamadı.");
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 10000; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
        throw new IOException("Uygun bir çıktı adı bulunamadı.");
    }

    private static bool Exists(string candidate, string source) =>
        File.Exists(candidate) || string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }
}
