using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NexView.Core.Formats;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;

namespace NexView.Platform.Windows;

public sealed record FormatGroup(string Id, string Name, string[] Extensions);

/// <summary>
/// Kullanıcı bazlı (HKCU, yönetici izni gerektirmez) Windows kabuk entegrasyonu:
/// "Birlikte aç" listesi, Varsayılan Uygulamalar kaydı ve klasik sağ tık menüsü.
/// Windows, varsayılan uygulamanın programla değiştirilmesine izin vermez; kullanıcı Ayarlar'dan seçer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellIntegration
{
    public const string ProgId = "NexView.Image";
    public const string AppName = "NexView";

    private readonly string _exe;
    private readonly string _classes;
    private readonly string _software;

    /// <param name="softwareRoot">Testler için farklı bir HKCU kökü (ör. Software\NexViewTest).</param>
    public ShellIntegration(string exePath, string softwareRoot = "Software")
    {
        _exe = exePath;
        _software = softwareRoot;
        _classes = softwareRoot + @"\Classes";
    }

    public static IReadOnlyList<FormatGroup> Groups { get; } = BuildGroups();

    private static List<FormatGroup> BuildGroups()
    {
        var list = new List<FormatGroup>();
        foreach (var f in FormatRegistry.All.Where(f => !f.IsRaw))
            list.Add(new FormatGroup(f.Id, f.Id == "JPEG" ? "JPG" : f.Id, f.Extensions));
        list.Add(new FormatGroup("RAW", "RAW", FormatRegistry.All.Where(f => f.IsRaw).SelectMany(f => f.Extensions).ToArray()));
        return list;
    }

    /// <summary>Kurulumda varsayılan olarak işaretli gruplar.</summary>
    public static IReadOnlyList<string> DefaultGroups { get; } =
        ["JPEG", "PNG", "WEBP", "AVIF", "HEIC", "HEIF", "TIFF", "BMP", "GIF", "ICO", "RAW", "JXL", "TGA", "DDS", "PSD"];

    private string Command(string args = "\"%1\"") => $"\"{_exe}\" {args}";

    // ---------- Dosya türleri ----------

    public void RegisterFileTypes(IEnumerable<string> extensions)
    {
        var exts = extensions.Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()).Distinct().ToList();
        UnregisterFileTypes(notify: false);

        using (var prog = Registry.CurrentUser.CreateSubKey($@"{_classes}\{ProgId}"))
        {
            prog.SetValue("", L.T("NexView görüntüsü"));
            prog.SetValue("FriendlyTypeName", L.T("NexView görüntüsü"));
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{_exe}\",0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", Command());
            using (var open = prog.CreateSubKey(@"shell\open")) open.SetValue("FriendlyAppName", AppName);
        }

        using (var app = Registry.CurrentUser.CreateSubKey($@"{_classes}\Applications\NexView.exe"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", Command());
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var e in exts) types.SetValue(e, "");
        }

        using (var caps = Registry.CurrentUser.CreateSubKey($@"{_software}\NexView\Capabilities"))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", L.T("Görüntülerin tüm dünyası: aç, incele, düzenle, dönüştür."));
            caps.SetValue("ApplicationIcon", $"\"{_exe}\",0");
            using var fa = caps.CreateSubKey("FileAssociations");
            foreach (var e in exts) fa.SetValue(e, ProgId);
        }
        using (var reg = Registry.CurrentUser.CreateSubKey($@"{_software}\RegisteredApplications"))
            reg.SetValue(AppName, $@"Software\NexView\Capabilities");

        foreach (var e in exts)
        {
            using var owp = Registry.CurrentUser.CreateSubKey($@"{_classes}\{e}\OpenWithProgids");
            owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        NotifyShell();
        Log.Info($"Dosya türleri kaydedildi: {exts.Count} uzantı");
    }

    public void UnregisterFileTypes(bool notify = true)
    {
        foreach (var e in RegisteredExtensions())
        {
            using var owp = Registry.CurrentUser.OpenSubKey($@"{_classes}\{e}\OpenWithProgids", writable: true);
            owp?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        DeleteTree($@"{_classes}\{ProgId}");
        DeleteTree($@"{_classes}\Applications\NexView.exe");
        DeleteTree($@"{_software}\NexView\Capabilities");
        using (var reg = Registry.CurrentUser.OpenSubKey($@"{_software}\RegisteredApplications", writable: true))
            reg?.DeleteValue(AppName, throwOnMissingValue: false);
        if (notify) NotifyShell();
    }

    public IReadOnlyList<string> RegisteredExtensions()
    {
        using var fa = Registry.CurrentUser.OpenSubKey($@"{_software}\NexView\Capabilities\FileAssociations");
        return fa?.GetValueNames() ?? [];
    }

    public bool IsFileTypesRegistered => RegisteredExtensions().Count > 0;

    // ---------- Sağ tık menüsü ----------

    private static readonly (string Key, string Text, string Args)[] MenuItems =
    [
        ("a_open", "NexView ile aç", "\"%1\""),
        ("b_jpeg", "JPEG'e dönüştür", "--convert jpeg \"%1\""),
        ("c_png", "PNG'ye dönüştür", "--convert png \"%1\""),
        ("d_webp", "WebP'ye dönüştür", "--convert webp \"%1\""),
        ("e_share", "Paylaşmaya hazırla", "--convert share \"%1\""),
        ("f_official", "Kurum / Resmi belge", "--convert official \"%1\""),
        ("g_more", "Dönüştür…", "--tab convert \"%1\""),
        ("h_info", "Dosya bilgisi", "--tab info \"%1\""),
    ];

    public void RegisterContextMenu(IEnumerable<string> extensions)
    {
        UnregisterContextMenu(notify: false);
        foreach (var e in extensions.Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant()).Distinct())
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"{_classes}\SystemFileAssociations\{e}\shell\NexView");
            root.SetValue("MUIVerb", AppName);
            root.SetValue("Icon", $"\"{_exe}\",0");
            root.SetValue("SubCommands", "");
            foreach (var (key, text, args) in MenuItems)
            {
                using var item = root.CreateSubKey($@"shell\{key}");
                item.SetValue("MUIVerb", L.T(text));
                if (key == "a_open") item.SetValue("Icon", $"\"{_exe}\",0");
                if (key is "b_jpeg" or "g_more") item.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord); // üstte ayırıcı
                using var cmd = item.CreateSubKey("command");
                cmd.SetValue("", Command(args));
            }
        }
        using (var dir = Registry.CurrentUser.CreateSubKey($@"{_classes}\Directory\shell\NexView"))
        {
            dir.SetValue("MUIVerb", L.T("NexView'de aç"));
            dir.SetValue("Icon", $"\"{_exe}\",0");
            using var cmd = dir.CreateSubKey("command");
            cmd.SetValue("", Command());
        }
        using (var flag = Registry.CurrentUser.CreateSubKey($@"{_software}\NexView")) flag.SetValue("ContextMenu", 1, RegistryValueKind.DWord);
        NotifyShell();
    }

    public void UnregisterContextMenu(bool notify = true)
    {
        using (var sfa = Registry.CurrentUser.OpenSubKey($@"{_classes}\SystemFileAssociations"))
        {
            if (sfa != null)
                foreach (var e in sfa.GetSubKeyNames())
                    DeleteTree($@"{_classes}\SystemFileAssociations\{e}\shell\NexView");
        }
        DeleteTree($@"{_classes}\Directory\shell\NexView");
        using (var flag = Registry.CurrentUser.OpenSubKey($@"{_software}\NexView", writable: true)) flag?.DeleteValue("ContextMenu", false);
        if (notify) NotifyShell();
    }

    public bool IsContextMenuRegistered
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"{_software}\NexView");
            return k?.GetValue("ContextMenu") is int v && v == 1;
        }
    }

    /// <summary>Kayıtlı yolu farklı olan (ör. taşınmış exe) bir kayıt var mı?</summary>
    public string? RegisteredExePath()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{_classes}\{ProgId}\shell\open\command");
        var v = k?.GetValue("") as string;
        if (v == null) return null;
        var parts = v.Split('"', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    public void UnregisterAll()
    {
        UnregisterContextMenu(notify: false);
        UnregisterFileTypes(notify: false);
        DeleteTree($@"{_software}\NexView");
        NotifyShell();
    }

    /// <summary>Windows Ayarlar → Varsayılan uygulamalar sayfasını NexView için açar.</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}") { UseShellExecute = true });
        }
        catch
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("Varsayılan uygulamalar açılamadı", ex); }
        }
    }

    private static void DeleteTree(string path)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
        catch (Exception ex) { Log.Debug($"Kayıt silinemedi {path}: {ex.Message}"); }
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
