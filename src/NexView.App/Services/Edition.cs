using NexView.Core.Localization;

namespace NexView.App.Services;

/// <summary>Derleme zamanında seçilen sürüm (ör. AFOT topluluk sürümü).</summary>
public static class Edition
{
#if AFOT_EDITION
    public const string Name = "AFOT";
    public static string? Dedication => L.T("AFOT (Adıyaman Fotoğraf Topluluğu) için geliştirilmiştir.");
    public static string Label => L.T("AFOT sürümü");
#else
    public const string Name = "";
    public static string? Dedication => null;
    public static string Label => "";
#endif
    public static bool HasDedication => Dedication != null;
}
