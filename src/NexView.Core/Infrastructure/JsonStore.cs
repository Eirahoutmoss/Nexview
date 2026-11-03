using System.Text.Json;

namespace NexView.Core.Infrastructure;

internal static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Warn($"Ayar dosyası okunamadı, varsayılanlar kullanılıyor: {path}", ex);
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Dosya kaydedilemedi: {path}", ex);
        }
    }
}
