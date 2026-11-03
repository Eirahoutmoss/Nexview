using System.Text;
using System.Text.RegularExpressions;

namespace NexView.Core.Metadata;

/// <summary>
/// 360° panorama geometrisi (Google GPano XMP): eşdikdörtgen görüntünün tam küre içindeki yeri.
/// Kısmi panoramalarda görüntü, FullWidth × FullHeight boyutlu kürenin (CropLeft, CropTop) konumundaki parçasıdır.
/// </summary>
public sealed record PanoramaInfo(int FullWidth, int FullHeight, int CropLeft, int CropTop, int Width, int Height)
{
    public bool IsFull360 => Width >= FullWidth * 0.98;
    public double HorizontalDegrees => 360.0 * Width / FullWidth;
    public double VerticalDegrees => 180.0 * Height / FullHeight;

    /// <summary>Dosyanın XMP paketinde GPano eşdikdörtgen bilgisi varsa okur.</summary>
    public static PanoramaInfo? TryRead(string path)
    {
        try
        {
            // XMP JPEG/PNG'de başta, büyük TIFF'lerde genellikle piksel verisinden sonra (sonda) bulunur
            using var fs = File.OpenRead(path);
            const int Chunk = 2 * 1024 * 1024;
            string text = ReadChunk(fs, 0, Chunk);
            if (!text.Contains("GPano:", StringComparison.Ordinal) && fs.Length > Chunk)
                text = ReadChunk(fs, Math.Max(Chunk, fs.Length - Chunk), Chunk);
            if (!text.Contains("GPano:", StringComparison.Ordinal)) return null;
            var proj = Value(text, "ProjectionType");
            if (proj != null && !proj.Equals("equirectangular", StringComparison.OrdinalIgnoreCase)) return null;
            int? fw = Int(text, "FullPanoWidthPixels"), fh = Int(text, "FullPanoHeightPixels");
            int? w = Int(text, "CroppedAreaImageWidthPixels"), h = Int(text, "CroppedAreaImageHeightPixels");
            int? l = Int(text, "CroppedAreaLeftPixels"), t = Int(text, "CroppedAreaTopPixels");
            if (fw is not > 0 || fh is not > 0) return proj != null ? new PanoramaInfo(0, 0, 0, 0, 0, 0) : null;
            return new PanoramaInfo(fw.Value, fh.Value, l ?? 0, t ?? 0, w ?? fw.Value, h ?? fh.Value);
        }
        catch { return null; }
    }

    private static string ReadChunk(FileStream fs, long offset, int size)
    {
        fs.Position = offset;
        var buf = new byte[(int)Math.Min(size, fs.Length - offset)];
        int n = fs.Read(buf, 0, buf.Length);
        return Encoding.Latin1.GetString(buf, 0, n);
    }

    /// <summary>
    /// GPano bilgisi yoksa görüntü boyutundan tahmin: 2:1 ise tam küre, değilse kare pikselli eşdikdörtgen
    /// varsayımıyla yatayda en-boy oranına göre (en fazla 360°) bir parça. Görüntü hiçbir zaman kutuplara kadar uzatılmaz.
    /// </summary>
    public PanoramaInfo WithImageSize(int width, int height)
    {
        if (FullWidth > 0) return this;
        double aspect = width / (double)Math.Max(1, height);
        double hdeg = Math.Abs(aspect - 2) < 0.05 ? 360 : Math.Clamp(aspect * 60, 60, 359);
        int fullW = (int)Math.Round(width * 360 / hdeg);
        int fullH = Math.Max(height, fullW / 2);
        return new PanoramaInfo(fullW, fullH, (fullW - width) / 2, (fullH - height) / 2, width, height);
    }

    private static string? Value(string text, string name)
    {
        var m = Regex.Match(text, $@"GPano:{name}\s*=\s*""([^""]*)""|<GPano:{name}>([^<]*)</GPano:{name}>");
        if (!m.Success) return null;
        return (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
    }

    private static int? Int(string text, string name) =>
        int.TryParse(Value(text, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Bu geometri için GPano XMP paketi (Google Fotoğraflar, Facebook ve 360° görüntüleyiciler tanır).</summary>
    public string ToXmp() => $"""
<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
 <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
  <rdf:Description rdf:about="" xmlns:GPano="http://ns.google.com/photos/1.0/panorama/"
    GPano:ProjectionType="equirectangular"
    GPano:UsePanoramaViewer="True"
    GPano:CroppedAreaImageWidthPixels="{Width}"
    GPano:CroppedAreaImageHeightPixels="{Height}"
    GPano:FullPanoWidthPixels="{FullWidth}"
    GPano:FullPanoHeightPixels="{FullHeight}"
    GPano:CroppedAreaLeftPixels="{CropLeft}"
    GPano:CroppedAreaTopPixels="{CropTop}"
    GPano:StitchingSoftware="NexView"/>
 </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>
""";
}
