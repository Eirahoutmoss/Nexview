using Avalonia.Controls;
using Avalonia.Input;
using NexView.App.Services;
using NexView.Core.Formats;
using NexView.Core.Imaging;
using NexView.Core.Infrastructure;
using NexView.Core.Localization;
using NexView.Core.Metadata;

namespace NexView.App.Views;

/// <summary>360° panorama penceresi: GPano XMP'li ya da 2:1 eşdikdörtgen görüntüyü küre içinden gösterir.</summary>
public partial class PanoramaWindow : Window
{
    public PanoramaWindow() : this(null!, null!) { }

    public PanoramaWindow(AppServices services, string path)
    {
        InitializeComponent();
        if (services == null) return;
        Title = $"NexView 360° — {Path.GetFileName(path)}";
        Spin.IsCheckedChanged += (_, _) => Viewer.AutoRotate = Spin.IsChecked == true;
        ResetBtn.Click += (_, _) => Viewer.Reset();
        FullBtn.Click += (_, _) => ToggleFull();
        Viewer.ViewChanged += (_, _) => UpdateInfo();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) ToggleFull();
            else if (e.Key == Key.Escape) { if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal; else Close(); }
        };
        Opened += async (_, _) =>
        {
            try
            {
                var (img, geo) = await Task.Run(() =>
                {
                    var r = services.Codecs.Decode(path, FormatDetector.Detect(path), new DecodeOptions(8192), CancellationToken.None);
                    var info = (PanoramaInfo.TryRead(path) ?? new PanoramaInfo(0, 0, 0, 0, 0, 0)).WithImageSize(r.Image.OriginalWidth, r.Image.OriginalHeight);
                    // Geometri özgün piksellerde; çözülen görüntü küçültülmüş olabilir
                    double k = r.Image.Width / (double)Math.Max(1, info.Width);
                    var scaled = new PanoramaInfo((int)Math.Round(info.FullWidth * k), (int)Math.Round(info.FullHeight * k),
                        (int)Math.Round(info.CropLeft * k), (int)Math.Round(info.CropTop * k), r.Image.Width, r.Image.Height);
                    return (r.Image, scaled);
                });
                Viewer.SetImage(img, geo);
                Status.IsVisible = false;
                Viewer.Focus();
            }
            catch (Exception ex)
            {
                Log.Warn($"360° görüntü açılamadı: {path}", ex);
                Status.Text = L.T("Görüntü açılamadı: {0}", ex.Message);
            }
        };
    }

    private void ToggleFull() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    private void UpdateInfo() =>
        Info.Text = L.T("Görüş {0:0}° · yön {1:0}° · eğim {2:+0;-0;0}°", Viewer.FieldOfView, Viewer.YawDegrees, Viewer.PitchDegrees);
}
