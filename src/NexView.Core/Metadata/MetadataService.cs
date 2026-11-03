using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Xmp;
using NexView.Core.Infrastructure;

namespace NexView.Core.Metadata;

public sealed record MetadataEntry(string Group, string Name, string Value);

public sealed class ImageMetadata
{
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? Lens { get; set; }
    public string? Iso { get; set; }
    public string? ExposureTime { get; set; }
    public string? FNumber { get; set; }
    public string? FocalLength { get; set; }
    public string? ExposureBias { get; set; }
    public string? WhiteBalance { get; set; }
    public string? Flash { get; set; }
    public DateTime? DateTaken { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Orientation { get; set; }
    public bool HasExif { get; set; }
    public bool HasIptc { get; set; }
    public bool HasXmp { get; set; }
    public List<MetadataEntry> Entries { get; } = new();

    public bool HasGps => Latitude.HasValue && Longitude.HasValue;
    public bool HasCameraInfo => CameraModel != null || CameraMake != null || Iso != null || ExposureTime != null;

    public string? CameraDisplay
    {
        get
        {
            if (CameraModel == null) return CameraMake;
            if (CameraMake == null) return CameraModel;
            var firstWord = CameraMake.Split(' ')[0];
            return CameraModel.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase) ? CameraModel : $"{CameraMake} {CameraModel}";
        }
    }
}

/// <summary>EXIF / IPTC / XMP okuyucu (MetadataExtractor).</summary>
public static class MetadataService
{
    private const int MaxValueLength = 300;

    public static ImageMetadata Read(string path)
    {
        var m = new ImageMetadata();
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"Metadata okunamadı: {path}: {ex.Message}");
            return m;
        }

        try
        {
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();

            m.HasExif = ifd0 != null || sub != null;
            m.HasIptc = dirs.OfType<IptcDirectory>().Any();
            m.HasXmp = dirs.OfType<XmpDirectory>().Any();

            m.CameraMake = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagMake));
            m.CameraModel = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagModel));
            m.Lens = Clean(sub?.GetDescription(ExifDirectoryBase.TagLensModel));
            m.Iso = Clean(sub?.GetDescription(ExifDirectoryBase.TagIsoEquivalent));
            m.ExposureTime = Clean(sub?.GetDescription(ExifDirectoryBase.TagExposureTime))?.Replace(" sec", " sn");
            m.FNumber = Clean(sub?.GetDescription(ExifDirectoryBase.TagFNumber));
            m.FocalLength = Clean(sub?.GetDescription(ExifDirectoryBase.TagFocalLength));
            m.ExposureBias = Clean(sub?.GetDescription(ExifDirectoryBase.TagExposureBias));
            m.WhiteBalance = Clean(sub?.GetDescription(ExifDirectoryBase.TagWhiteBalanceMode));
            m.Flash = Clean(sub?.GetDescription(ExifDirectoryBase.TagFlash));

            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt)) m.DateTaken = dt;
            else if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt2)) m.DateTaken = dt2;

            if (ifd0 != null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o)) m.Orientation = o;

            if (gps != null && gps.TryGetGeoLocation(out var loc) && !loc.IsZero)
            {
                m.Latitude = loc.Latitude;
                m.Longitude = loc.Longitude;
            }

            foreach (var dir in dirs)
            {
                foreach (var tag in dir.Tags)
                {
                    var value = tag.Description ?? "";
                    if (value.Length > MaxValueLength) value = value[..MaxValueLength] + "…";
                    m.Entries.Add(new MetadataEntry(dir.Name, tag.Name, value));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Metadata ayrıştırılamadı: {path}: {ex.Message}");
        }
        return m;
    }

    /// <summary>Yalnızca EXIF yönlendirme değerini okur (1-8).</summary>
    public static int? ReadOrientation(string path)
    {
        try
        {
            var dir = ImageMetadataReader.ReadMetadata(path).OfType<ExifIfd0Directory>().FirstOrDefault();
            return dir != null && dir.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) ? o : null;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatCoordinates(double lat, double lon) =>
        string.Create(CultureInfo.GetCultureInfo("tr-TR"),
            $"{Math.Abs(lat):0.00000}° {(lat >= 0 ? "K" : "G")}, {Math.Abs(lon):0.00000}° {(lon >= 0 ? "D" : "B")}");

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }
}
