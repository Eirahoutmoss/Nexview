namespace NexView.Core.Infrastructure;

public sealed class AppSettings
{
    public string ViewerBackground { get; set; } = "Dark";
    public bool ShowSidebar { get; set; } = true;
    public bool ShowInspector { get; set; } = true;
    public bool ShowFilmstrip { get; set; } = true;
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }
    public int ThumbnailCacheMaxMB { get; set; } = 512;
    public int MaxDisplayDimension { get; set; } = 8192;
    public string Language { get; set; } = "auto";
    public bool AutoStretchFits { get; set; } = true;
    public string ConvertPreset { get; set; } = "custom";
    public string ConvertTarget { get; set; } = "JPEG";
    public int ConvertQuality { get; set; } = 90;
    public string? ConvertFolder { get; set; }
    public int ConvertOutputMode { get; set; }
    public List<string>? AssociatedGroups { get; set; }
    public bool RestoreSession { get; set; } = true;
    public string? LastPath { get; set; }

    public static AppSettings Load() => JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
    public void Save() => JsonStore.Save(AppPaths.SettingsFile, this);
}
