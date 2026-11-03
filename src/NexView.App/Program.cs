using Avalonia;
using NexView.App.Services;
using NexView.Core.Infrastructure;

namespace NexView.App;

public sealed class StartupArgs
{
    public string? DevShotPath { get; private set; }
    public bool NewWindow { get; private set; }
    public string? Convert { get; private set; }
    public string? Tab { get; private set; }
    public bool Register { get; private set; }
    public bool Unregister { get; private set; }
    public bool Install { get; private set; }
    public bool Uninstall { get; private set; }
    public bool Quiet { get; private set; }
    public bool Setup { get; private set; }
    public List<string> Files { get; } = new();

    public static StartupArgs Parse(string[] args)
    {
        var r = new StartupArgs();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--devshot" when i + 1 < args.Length: r.DevShotPath = args[++i]; break;
                case "--convert" when i + 1 < args.Length: r.Convert = args[++i]; break;
                case "--tab" when i + 1 < args.Length: r.Tab = args[++i]; break;
                case "--new-window": r.NewWindow = true; break;
                case "--register": r.Register = true; break;
                case "--unregister": r.Unregister = true; break;
                case "--install": r.Install = true; break;
                case "--uninstall": r.Uninstall = true; break;
                case "--quiet": r.Quiet = true; break;
                case "--setup": r.Setup = true; break;
                default:
                    if (!args[i].StartsWith("--")) r.Files.Add(args[i]);
                    break;
            }
        }
        return r;
    }
}

internal static class Program
{
    public static StartupArgs Args { get; private set; } = new();

    /// <summary>Kurulum modu: --setup ya da exe adında "setup" geçmesi (ör. NexView-0.5.0-Setup.exe).</summary>
    public static bool IsSetupMode { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Args = StartupArgs.Parse(args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("İşlenmeyen hata", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Gözlenmeyen görev hatası", e.Exception); e.SetObserved(); };

        if (CommandLine.TryRun(Args) is int code) return code;

        IsSetupMode = OperatingSystem.IsWindows() && (Args.Setup ||
            Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase));
        if (IsSetupMode) return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        if (Args.DevShotPath == null && !Args.NewWindow && SingleInstance.TrySendToExisting(Args.Files, Args.Tab))
            return 0;

        Log.Info($"NexView başlıyor. Veri klasörü: {AppPaths.DataDir}");
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Ölümcül hata", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
