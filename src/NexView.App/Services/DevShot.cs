using NexView.Core.Editing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using NexView.App.ViewModels;

namespace NexView.App.Services;

/// <summary>Geliştirme doğrulaması için pencerenin ekran görüntüsünü PNG olarak kaydeder (--devshot).</summary>
public static class DevShot
{
    public static bool WantsSetup(string? shotPath) => WantsWindow(shotPath, "setup");

    public static bool WantsWindow(string? shotPath, string name)
    {
        if (shotPath == null) return false;
        return ReadArgs(shotPath).Any(l => l == "window=" + name);
    }

    public static List<string> ReadArgs(string shotPath)
    {
        var f = Path.Combine(Path.GetDirectoryName(shotPath) ?? ".", "shot-args.txt");
        return File.Exists(f) ? File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
    }

    /// <summary>shot-args.txt içindeki "anahtar=değer" satırlarına göre arayüz durumunu ayarlar (yalnızca geliştirme).</summary>
    public static async Task ApplyOptionsAsync(MainViewModel vm, string file)
    {
        if (!File.Exists(file)) return;
        var e = vm.Edit;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double[] Nums(string v) => v.Split(',').Select(x => double.Parse(x, inv)).ToArray();
        foreach (var line in File.ReadAllLines(file))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;
            var (k, v) = (parts[0].Trim(), parts[1].Trim());
            switch (k)
            {
                case "tab" when v == "convert": vm.ShowConvertTabCommand.Execute(null); break;
                case "tab" when v == "batch": vm.OpenBatchCommand.Execute(null); break;
                case "tab" when v == "settings": vm.ShowSettings = true; break;
                case "preset": vm.Convert.SelectedPreset = vm.Convert.Presets.FirstOrDefault(p => p.Id == v) ?? vm.Convert.SelectedPreset; break;
                case "format": vm.Convert.TargetFormat = vm.Convert.TargetFormats.FirstOrDefault(f => f.Format.Id == v) ?? vm.Convert.TargetFormat; break;
                case "action" when v == "convert": vm.Convert.ConvertCurrentCommand.Execute(null); break;
                case "action" when v == "batch": vm.Convert.StartBatchCommand.Execute(null); break;
                case "action" when v == "rename": vm.RenameCommand.Execute(null); break;
                case "stretch": vm.AutoStretch = v == "1"; break;
                case "tab" when v == "edit": await vm.ShowEditTabCommand.ExecuteAsync(null); break;
                case "edit.tool": e.Tool = Enum.Parse<EditTool>(v, true); break;
                case "edit.brightness": e.Brightness = double.Parse(v, inv); break;
                case "edit.contrast": e.Contrast = double.Parse(v, inv); break;
                case "edit.saturation": e.Saturation = double.Parse(v, inv); break;
                case "edit.temperature": e.Temperature = double.Parse(v, inv); break;
                case "edit.straighten": e.Straighten = double.Parse(v, inv); break;
                case "edit.crop": { var n = Nums(v); e.CropRect = new Core.Editing.NRect(n[0], n[1], n[2], n[3]); break; }
                case "edit.doc": e.DocumentPerspective = v == "1"; break;
                case "edit.filter": e.Filter = e.Filters.First(f => f.Filter.ToString() == v); break;
                case "edit.compare": e.Compare = Enum.Parse<CompareMode>(v, true); break;
                case "edit.annot":
                {
                    var parts2 = v.Split(',');
                    var n = parts2.Skip(1).Take(4).Select(x => double.Parse(x, inv)).ToArray();
                    var kind = Enum.Parse<Core.Editing.AnnotationKind>(parts2[0]);
                    e.AddAnnotation(new Core.Editing.Annotation(kind, n[0], n[1], n[2], n[3], parts2.Length > 5 ? parts2[5] : "#FF3B30", parts2.Length > 6 ? parts2[6] : null));
                    break;
                }
                case "edit.render": await e.RenderAsync(); break;
                case "hittest":
                    if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
                    {
                        await Task.Delay(500);
                        var vp = w.FindControl<Avalonia.Controls.Control>("Viewport")!;
                        var center = vp.TranslatePoint(new Avalonia.Point(vp.Bounds.Width / 2, vp.Bounds.Height / 2), w)!.Value;
                        var hit = Avalonia.Input.InputExtensions.InputHitTest(w, center);
                        NexView.Core.Infrastructure.Log.Info($"devshot hittest: {hit?.GetType().Name} ({(hit as Avalonia.StyledElement)?.Name})");
                    }
                    break;
                case "batchmode": vm.SetBatchModeCommand.Execute(v); break;
                case "action" when v == "stack": await vm.Stack.StartCommand.ExecuteAsync(null); break;
                case "shortcuts": vm.ShowShortcuts = true; break;
                case "action" when v == "editsave": await e.SaveCommand.ExecuteAsync(null); break;
            }
        }
    }

    public static void Capture(Window window, string path)
    {
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(window);
        rtb.Save(path);
    }
}
