using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace NexView.Core.Localization;

/// <summary>
/// Gettext tarzı yerelleştirme: anahtar Türkçe kaynak metnin kendisidir.
/// Türkçe dışındaki diller gömülü JSON'dan (Localization/{dil}.json) yüklenir; çeviri yoksa Türkçe döner.
/// </summary>
public static class L
{
    private static Dictionary<string, string>? _map;

    public static string Language { get; private set; } = "tr";
    public static IReadOnlyList<(string Code, string Name)> Available { get; } = [("auto", "Sistem dili"), ("tr", "Türkçe"), ("en", "English")];

    public static void Initialize(string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) || language == "auto"
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en")
            : language;
        Language = lang;
        _map = null;
        if (lang == "tr") return;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"NexView.i18n.{lang}.json");
            if (s != null) _map = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
        }
        catch
        {
            _map = null;
        }
    }

    public static string T(string key) =>
        _map != null && _map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : key;

    public static string T(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return key; }
    }
}
