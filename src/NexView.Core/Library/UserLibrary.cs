using NexView.Core.Infrastructure;

namespace NexView.Core.Library;

public sealed class RecentItem
{
    public string Path { get; set; } = "";
    public DateTime OpenedAt { get; set; }
}

/// <summary>Favoriler ve son açılanlar. Dosyalar taşınmaz; yalnızca yol referansı tutulur.</summary>
public sealed class UserLibrary
{
    public const int MaxRecent = 50;

    public List<string> Favorites { get; set; } = new();
    public List<RecentItem> Recent { get; set; } = new();
    public List<string> Converted { get; set; } = new();

    public void AddConverted(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            Converted.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
            Converted.Insert(0, p);
        }
        if (Converted.Count > 1000) Converted.RemoveRange(1000, Converted.Count - 1000);
        Save();
    }

    public void ReplacePath(string oldPath, string newPath)
    {
        for (int i = 0; i < Favorites.Count; i++) if (string.Equals(Favorites[i], oldPath, StringComparison.OrdinalIgnoreCase)) Favorites[i] = newPath;
        foreach (var r in Recent) if (string.Equals(r.Path, oldPath, StringComparison.OrdinalIgnoreCase)) r.Path = newPath;
        for (int i = 0; i < Converted.Count; i++) if (string.Equals(Converted[i], oldPath, StringComparison.OrdinalIgnoreCase)) Converted[i] = newPath;
        Save();
    }

    public void Forget(string path)
    {
        Favorites.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        Recent.RemoveAll(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        Converted.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public static UserLibrary Load() => JsonStore.Load<UserLibrary>(AppPaths.LibraryFile);
    public void Save() => JsonStore.Save(AppPaths.LibraryFile, this);

    public bool IsFavorite(string path) => Favorites.Contains(path, StringComparer.OrdinalIgnoreCase);

    public bool ToggleFavorite(string path)
    {
        var idx = Favorites.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) Favorites.RemoveAt(idx);
        else Favorites.Add(path);
        Save();
        return idx < 0;
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, new RecentItem { Path = path, OpenedAt = DateTime.Now });
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        Save();
    }
}
