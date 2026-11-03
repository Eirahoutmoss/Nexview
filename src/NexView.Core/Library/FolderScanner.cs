using NexView.Core.Formats;

namespace NexView.Core.Library;

public static class FolderScanner
{
    public static List<string> GetImages(string folder, bool recursive = false)
    {
        var result = new List<string>();
        if (!Directory.Exists(folder)) return result;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            if (FormatRegistry.FromPath(file) != null) result.Add(file);
        }
        result.Sort(NaturalStringComparer.Instance);
        return result;
    }
}
