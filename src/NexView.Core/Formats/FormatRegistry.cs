namespace NexView.Core.Formats;

/// <summary>NexView'in tanıdığı formatlar. Okunup okunamayacağına codec'ler karar verir.</summary>
public static class FormatRegistry
{
    public static readonly ImageFormat Jpeg = new("JPEG", "JPEG", FormatFamily.Raster, "image/jpeg", [".jpg", ".jpeg", ".jpe", ".jfif"]);
    public static readonly ImageFormat Png = new("PNG", "PNG", FormatFamily.Raster, "image/png", [".png"], MayHaveAlpha: true, MayAnimate: true);
    public static readonly ImageFormat Gif = new("GIF", "GIF", FormatFamily.Raster, "image/gif", [".gif"], MayHaveAlpha: true, MayAnimate: true);
    public static readonly ImageFormat Bmp = new("BMP", "BMP", FormatFamily.Raster, "image/bmp", [".bmp", ".dib"]);
    public static readonly ImageFormat Tiff = new("TIFF", "TIFF", FormatFamily.Raster, "image/tiff", [".tif", ".tiff"], MayHaveAlpha: true);
    public static readonly ImageFormat WebP = new("WEBP", "WebP", FormatFamily.Raster, "image/webp", [".webp"], MayHaveAlpha: true, MayAnimate: true);
    public static readonly ImageFormat Avif = new("AVIF", "AVIF", FormatFamily.Heif, "image/avif", [".avif"], MayHaveAlpha: true);
    public static readonly ImageFormat Heic = new("HEIC", "HEIC (Apple)", FormatFamily.Heif, "image/heic", [".heic", ".heics"], MayHaveAlpha: true);
    public static readonly ImageFormat Heif = new("HEIF", "HEIF", FormatFamily.Heif, "image/heif", [".heif", ".hif"], MayHaveAlpha: true);
    public static readonly ImageFormat Ico = new("ICO", "Windows Simgesi (ICO)", FormatFamily.Raster, "image/x-icon", [".ico", ".cur"], MayHaveAlpha: true);
    public static readonly ImageFormat Tga = new("TGA", "Targa (TGA)", FormatFamily.Raster, "image/x-tga", [".tga", ".icb", ".vda", ".vst"], MayHaveAlpha: true);
    public static readonly ImageFormat Dds = new("DDS", "DirectDraw Surface (DDS)", FormatFamily.Raster, "image/vnd-ms.dds", [".dds"], MayHaveAlpha: true);
    public static readonly ImageFormat Psd = new("PSD", "Photoshop (PSD)", FormatFamily.Layered, "image/vnd.adobe.photoshop", [".psd"], MayHaveAlpha: true);
    public static readonly ImageFormat Psb = new("PSB", "Photoshop Büyük Belge (PSB)", FormatFamily.Layered, "image/vnd.adobe.photoshop", [".psb"], MayHaveAlpha: true);
    public static readonly ImageFormat Jxl = new("JXL", "JPEG XL", FormatFamily.Raster, "image/jxl", [".jxl"], MayHaveAlpha: true);
    public static readonly ImageFormat Jp2 = new("JP2", "JPEG 2000", FormatFamily.Raster, "image/jp2", [".jp2", ".j2k", ".jpx", ".jpf", ".j2c"], MayHaveAlpha: true);
    public static readonly ImageFormat Exr = new("EXR", "OpenEXR", FormatFamily.Hdr, "image/x-exr", [".exr"], MayHaveAlpha: true);

    public static readonly ImageFormat Dng = new("DNG", "Adobe DNG", FormatFamily.Raw, "image/x-adobe-dng", [".dng"]);
    public static readonly ImageFormat Cr2 = new("CR2", "Canon RAW (CR2)", FormatFamily.Raw, "image/x-canon-cr2", [".cr2"]);
    public static readonly ImageFormat Cr3 = new("CR3", "Canon RAW (CR3)", FormatFamily.Raw, "image/x-canon-cr3", [".cr3"]);
    public static readonly ImageFormat Nef = new("NEF", "Nikon RAW (NEF)", FormatFamily.Raw, "image/x-nikon-nef", [".nef", ".nrw"]);
    public static readonly ImageFormat Arw = new("ARW", "Sony RAW (ARW)", FormatFamily.Raw, "image/x-sony-arw", [".arw", ".srf", ".sr2"]);
    public static readonly ImageFormat Raf = new("RAF", "Fujifilm RAW (RAF)", FormatFamily.Raw, "image/x-fuji-raf", [".raf"]);
    public static readonly ImageFormat Orf = new("ORF", "Olympus RAW (ORF)", FormatFamily.Raw, "image/x-olympus-orf", [".orf"]);
    public static readonly ImageFormat Rw2 = new("RW2", "Panasonic RAW (RW2)", FormatFamily.Raw, "image/x-panasonic-rw2", [".rw2"]);
    public static readonly ImageFormat Srw = new("SRW", "Samsung RAW (SRW)", FormatFamily.Raw, "image/x-samsung-srw", [".srw"]);
    public static readonly ImageFormat Pef = new("PEF", "Pentax RAW (PEF)", FormatFamily.Raw, "image/x-pentax-pef", [".pef"]);
    public static readonly ImageFormat Fits = new("FITS", "FITS (astronomi)", FormatFamily.Hdr, "image/fits", [".fits", ".fit", ".fts"]);

    /// <summary>Yalnızca çıktı (okuma için Ghostscript gerekir; listede yok).</summary>
    public static readonly ImageFormat Pdf = new("PDF", "PDF belgesi", FormatFamily.Raster, "application/pdf", [".pdf"]);

    public static IReadOnlyList<ImageFormat> All { get; } =
    [
        Jpeg, Png, Gif, Bmp, Tiff, WebP, Avif, Heic, Heif, Ico, Tga, Dds, Psd, Psb, Jxl, Jp2, Exr,
        Dng, Cr2, Cr3, Nef, Arw, Raf, Orf, Rw2, Srw, Pef, Fits,
    ];

    private static readonly Dictionary<string, ImageFormat> ByExtension =
        All.SelectMany(f => f.Extensions.Select(e => (e, f)))
           .ToDictionary(x => x.e, x => x.f, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ImageFormat> ById =
        All.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    public static ImageFormat? FromExtension(string extension) =>
        ByExtension.TryGetValue(extension.StartsWith('.') ? extension : "." + extension, out var f) ? f : null;

    public static ImageFormat? FromPath(string path) => FromExtension(Path.GetExtension(path));

    public static ImageFormat? FromId(string id) => ById.TryGetValue(id, out var f) ? f : null;

    public static IEnumerable<string> AllExtensions => ByExtension.Keys;
}
