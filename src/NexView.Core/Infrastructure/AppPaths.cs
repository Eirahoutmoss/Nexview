namespace NexView.Core.Infrastructure;

/// <summary>
/// Uygulama veri klasörleri. Sıra: NEXVIEW_DATA_DIR ortam değişkeni → taşınabilir mod (exe yanında portable.txt) → kullanıcı profili.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<(string Dir, bool Portable)> Root = new(Resolve);

    public static string DataDir => Root.Value.Dir;
    public static bool IsPortable => Root.Value.Portable;

    public static string LogsDir => Ensure("logs");
    public static string ThumbnailCacheDir => Ensure(Path.Combine("cache", "thumbs"));
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LibraryFile => Path.Combine(DataDir, "library.json");

    private static (string, bool) Resolve()
    {
        string dir;
        var portable = false;
        var env = Environment.GetEnvironmentVariable("NEXVIEW_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            dir = env;
        }
        else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            dir = Path.Combine(AppContext.BaseDirectory, "Data");
            portable = true;
        }
        else
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexView");
        }
        Directory.CreateDirectory(dir);
        return (dir, portable);
    }

    private static string Ensure(string relative)
    {
        var path = Path.Combine(DataDir, relative);
        Directory.CreateDirectory(path);
        return path;
    }
}
