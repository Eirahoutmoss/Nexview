using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NexView.Codecs.Magick;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Library;
using NexView.Core.Localization;
using NexView.Mobile.Services;
using NexView.Mobile.ViewModels;
using NexView.Mobile.Views;

namespace NexView.App.Services;

/// <summary>Mobil arayüzün masaüstü önizlemesi (--mobile ya da devshot window=mobile). Yalnızca geliştirme amaçlı.</summary>
public static class MobilePreview
{
    public static void Show(IClassicDesktopStyleApplicationLifetime desktop, string? shotPath)
    {
        L.Initialize(AppSettings.Load().Language);
        var codecs = new CodecManager();
        codecs.Register(new MagickCodec());
        codecs.Register(new MagickCodec(alternative: true));
        var services = new MobileServices
        {
            Platform = new DesktopMobilePlatform(),
            Codecs = codecs,
            Converter = new MagickConverter(),
            Editor = new MagickEditor(),
            Stacker = new MagickStacker(),
            EditionLabel = Edition.Dedication ?? "",
        };
        var vm = new MobileViewModel(services);
        var window = new Window
        {
            Title = "NexView Mobile (önizleme)",
            Width = 412, Height = 900,
            CanResize = true,
            Content = new MobileView { DataContext = vm },
        };
        desktop.MainWindow = window;
        if (shotPath == null) return;

        window.Opened += async (_, _) =>
        {
            try
            {
                await ApplyAsync(vm, DevShot.ReadArgs(shotPath));
                await Task.Delay(1500);
                DevShot.Capture(window, shotPath);
            }
            catch (Exception ex) { Log.Error("mobil devshot", ex); }
            desktop.Shutdown();
        };
    }

    private static async Task ApplyAsync(MobileViewModel vm, List<string> args)
    {
        string? Get(string key) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..];
        if (Get("mobile.files") is { } files)
        {
            var paths = files.Split(';').SelectMany(p => Directory.Exists(p) ? FolderScanner.GetImages(p) : File.Exists(p) ? [p] : []).ToList();
            vm.AddFiles(paths, openFirst: false);
            await Task.Delay(1500);
        }
        if (Get("mobile.select") is { } sel)
            foreach (var i in sel.Split(',').Select(int.Parse).Where(i => i < vm.Items.Count)) vm.ItemHeld(vm.Items[i]);
        switch (Get("mobile.page"))
        {
            case "tools": vm.ShowToolsCommand.Execute(null); break;
            case "settings": vm.ShowSettingsCommand.Execute(null); break;
        }
        if (Get("mobile.open") is { } open && int.TryParse(open, out var idx) && idx < vm.Items.Count)
        {
            vm.ItemTappedCommand.Execute(vm.Items[idx]);
            for (var i = 0; i < 100 && (vm.IsLoading || vm.CurrentBitmap == null) && vm.LoadError == null; i++) await Task.Delay(100);
        }
        if (Get("mobile.edit") is { } edit && vm.Edit != null)
        {
            if (edit == "document") await vm.DocumentCurrentCommand.ExecuteAsync(null);
            else await vm.EditCurrentCommand.ExecuteAsync(null);
            var tool = Get("mobile.tool");
            var idx2 = tool switch { "crop" => 1, "markup" => 2, "document" => 3, _ => -1 };
            if (idx2 >= 0) vm.Edit.Tools.Items[idx2].PickCommand.Execute(null);
            if (Get("mobile.annot") == "1")
            {
                vm.Edit.AddAnnotation(new NexView.Core.Editing.Annotation(NexView.Core.Editing.AnnotationKind.Arrow, 0.2, 0.2, 0.5, 0.5));
                vm.Edit.AddAnnotation(new NexView.Core.Editing.Annotation(NexView.Core.Editing.AnnotationKind.Ellipse, 0.55, 0.4, 0.85, 0.7, "#FFD60A"));
                vm.Edit.AddAnnotation(new NexView.Core.Editing.Annotation(NexView.Core.Editing.AnnotationKind.Pixelate, 0.1, 0.65, 0.4, 0.9));
            }
            if (Get("mobile.split") == "1") vm.Edit.ToggleCompareCommand.Execute(null);
            await Task.Delay(1500);
        }
        if (Get("mobile.info") == "1") vm.ShowInfoCommand.Execute(null);
        if (Get("mobile.convert") == "1") vm.OpenConvertCommand.Execute(null);
        if (Get("mobile.toast") is { } t) Dispatcher.UIThread.Post(() => vm.Toast(t));
    }
}

/// <summary>Masaüstü önizlemesi için mobil platform karşılığı.</summary>
internal sealed class DesktopMobilePlatform : IMobilePlatform
{
    public string Name => "Desktop preview";
    public string CacheDir { get; } = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "mobile-preview")).FullName;
    public bool CanCapture => false;

    public Task<string?> SaveToGalleryAsync(string path, string displayName, string mimeType)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NexView");
        Directory.CreateDirectory(dir);
        var dest = NexView.Core.Conversion.OutputPaths.Unique(Path.Combine(dir, displayName));
        File.Copy(path, dest);
        return Task.FromResult<string?>(dest);
    }

    public Task ShareAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0) OpenUri(Path.GetDirectoryName(paths[0])!);
        return Task.CompletedTask;
    }

    public Task<string?> CapturePhotoAsync() => Task.FromResult<string?>(null);
    public Task InstallPackageAsync(string path) { OpenUri(Path.GetDirectoryName(path)!); return Task.CompletedTask; }

    public void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }
}
