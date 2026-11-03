using NexView.Core.Formats;

namespace NexView.Mobile.Services;

public static class MimeTypes
{
    public static string For(string path)
    {
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "application/pdf";
        return FormatRegistry.FromPath(path)?.MimeType ?? "application/octet-stream";
    }
}
