using NexView.Core.Stacking;

namespace NexView.Studio;

public enum StudioKind { Astro, Macro, Hdr, Panorama }
public enum MacroMethod { Hybrid, Pyramid, DepthMap }
public enum HdrStyle { Natural, Vivid, Dramatic }
public enum PanoMode { Spherical, Flat }
public enum PanoOutput { Panorama, LittlePlanet, Tunnel }


/// <summary>16 bit RGB kare okuyucu (masaüstünde Magick sağlar). maxDimension ≤ 0: tam çözünürlük.</summary>
public delegate (ushort[] Rgb, int Width, int Height) RasterLoader(string path, int maxDimension);

public sealed class StudioOptions
{
    public StudioKind Kind { get; set; } = StudioKind.Macro;
    /// <summary>Çalışma çözünürlüğü (uzun kenar). ≤ 0: tam çözünürlük.</summary>
    public int MaxDimension { get; set; }
    public bool Align { get; set; } = true;
    public bool CropToCommon { get; set; } = true;

    public MacroMethod MacroMethod { get; set; } = MacroMethod.Hybrid;
    /// <summary>Karma yöntemde hale bastırma (0: en keskin, 1: en temiz geçiş).</summary>
    public double HaloSuppression { get; set; } = 0.5;
    /// <summary>Derinlik haritası yöntemi için geçiş yumuşaklığı (1..10).</summary>
    public double Smoothness { get; set; } = 4;

    public HdrStyle HdrStyle { get; set; } = HdrStyle.Natural;
    /// <summary>Hareketli nesnelerin (insan, araç, yaprak) çift görüntü bırakmasını önler.</summary>
    public bool Deghost { get; set; } = true;

    public PanoMode PanoMode { get; set; } = PanoMode.Spherical;
    public bool WaveCorrection { get; set; } = true;
    public double PanoConfidence { get; set; } = 1.0;
    public PanoOutput PanoOutput { get; set; } = PanoOutput.Panorama;
    /// <summary>Küçük gezegen/tünel: görüş genişliği (1 dar … 8 geniş).</summary>
    public double PlanetZoom { get; set; } = 3.5;
    public double PlanetRotation { get; set; }
    /// <summary>EXIF'te yoksa 35 mm eşdeğeri odak uzaklığı (mm).</summary>
    public double DefaultFocal35 { get; set; } = 26;
}

public sealed class StudioOutput
{
    public required ushort[] Rgb { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Used { get; set; }
    /// <summary>Küresel panoramalarda tam küre geometrisi (yoksa null).</summary>
    public NexView.Core.Metadata.PanoramaInfo? Pano { get; set; }
    public List<string> Notes { get; } = new();
}

public interface IStudioEngine
{
    StudioOutput Run(IReadOnlyList<string> files, StudioOptions options, IProgress<StackProgress>? progress, CancellationToken ct);
}
