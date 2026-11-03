using System.Text;

namespace NexView.Core.Formats;

public sealed record DetectionResult(ImageFormat? ContentFormat, ImageFormat? ExtensionFormat)
{
    /// <summary>Çözümlemede kullanılacak format: içerik imzası öncelikli.</summary>
    public ImageFormat? Format => ContentFormat ?? ExtensionFormat;

    /// <summary>Dosya uzantısı içerikle uyuşmuyor (ör. aslında PNG olan .jpg).</summary>
    public bool ExtensionMismatch =>
        ContentFormat != null && ExtensionFormat != null && ContentFormat.Id != ExtensionFormat.Id &&
        !(ContentFormat.Family == FormatFamily.Heif && ExtensionFormat.Family == FormatFamily.Heif && ContentFormat.Id != "AVIF" && ExtensionFormat.Id != "AVIF");
}

/// <summary>Dosya imzasına (magic bytes) bakarak formatı belirler; kullanıcı uzantıya güvenmek zorunda kalmaz.</summary>
public static class FormatDetector
{
    private const int HeaderSize = 64;

    public static DetectionResult Detect(string path)
    {
        var byExt = FormatRegistry.FromPath(path);
        try
        {
            Span<byte> buffer = stackalloc byte[HeaderSize];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            int read = fs.ReadAtLeast(buffer, HeaderSize, throwOnEndOfStream: false);
            return new DetectionResult(Sniff(buffer[..read], byExt), byExt);
        }
        catch (Exception)
        {
            return new DetectionResult(null, byExt);
        }
    }

    public static ImageFormat? Sniff(ReadOnlySpan<byte> h, ImageFormat? byExtension)
    {
        if (h.Length < 4) return null;

        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return FormatRegistry.Jpeg;
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return FormatRegistry.Png;
        if (Ascii(h, 0, "GIF87a") || Ascii(h, 0, "GIF89a")) return FormatRegistry.Gif;
        if (Ascii(h, 0, "RIFF") && Ascii(h, 8, "WEBP")) return FormatRegistry.WebP;
        if (Ascii(h, 0, "8BPS") && h.Length > 5) return h[5] == 2 ? FormatRegistry.Psb : FormatRegistry.Psd;
        if (Ascii(h, 0, "DDS ")) return FormatRegistry.Dds;
        if (Ascii(h, 0, "SIMPLE  =")) return FormatRegistry.Fits;
        if (Ascii(h, 0, "FUJIFILMCCD-RAW")) return FormatRegistry.Raf;
        if (h[0] == 0x76 && h[1] == 0x2F && h[2] == 0x31 && h[3] == 0x01) return FormatRegistry.Exr;
        if (h[0] == 0xFF && h[1] == 0x0A) return FormatRegistry.Jxl;
        if (h.Length >= 12 && h[..12].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A])) return FormatRegistry.Jxl;
        if (h.Length >= 8 && h[..8].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0, 0x0C, 0x6A, 0x50, 0x20, 0x20])) return FormatRegistry.Jp2;
        if (h[0] == 0xFF && h[1] == 0x4F && h[2] == 0xFF && h[3] == 0x51) return FormatRegistry.Jp2;

        // ISO BMFF (HEIF / AVIF / CR3)
        if (h.Length >= 12 && Ascii(h, 4, "ftyp"))
        {
            var major = Encoding.ASCII.GetString(h.Slice(8, 4));
            var boxSize = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3];
            var brands = new List<string> { major };
            for (int p = 16; p + 4 <= Math.Min(h.Length, boxSize); p += 4)
                brands.Add(Encoding.ASCII.GetString(h.Slice(p, 4)));
            if (major == "crx ") return FormatRegistry.Cr3;
            if (major is "avif" or "avis") return FormatRegistry.Avif;
            if (major is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis") return FormatRegistry.Heic;
            if (brands.Contains("avif") || brands.Contains("avis")) return FormatRegistry.Avif;
            if (major is "mif1" or "msf1" || brands.Contains("mif1"))
                return byExtension?.Family == FormatFamily.Heif ? byExtension : FormatRegistry.Heif;
        }

        // Olympus / Panasonic (TIFF türevleri, özel imza)
        if (Ascii(h, 0, "IIRO") || Ascii(h, 0, "IIRS") || Ascii(h, 0, "MMOR")) return FormatRegistry.Orf;
        if (h[0] == 'I' && h[1] == 'I' && h[2] == 'U' && h[3] == 0) return FormatRegistry.Rw2;

        // TIFF ve TIFF tabanlı RAW'lar
        bool tiff = (h[0] == 'I' && h[1] == 'I' && h[2] == 0x2A && h[3] == 0) || (h[0] == 'M' && h[1] == 'M' && h[2] == 0 && h[3] == 0x2A);
        if (tiff)
        {
            if (h.Length >= 10 && h[8] == 'C' && h[9] == 'R') return FormatRegistry.Cr2;
            if (byExtension is { IsRaw: true }) return byExtension;
            return FormatRegistry.Tiff;
        }

        if (h[0] == 'B' && h[1] == 'M' && h.Length >= 14) return FormatRegistry.Bmp;
        if (h[0] == 0 && h[1] == 0 && (h[2] == 1 || h[2] == 2) && h[3] == 0 && byExtension?.Id is "ICO" or null) return FormatRegistry.Ico;

        // İmzası olmayan formatlar (TGA vb.) uzantıya göre
        return null;
    }

    private static bool Ascii(ReadOnlySpan<byte> h, int offset, string text)
    {
        if (h.Length < offset + text.Length) return false;
        for (int i = 0; i < text.Length; i++)
            if (h[offset + i] != (byte)text[i]) return false;
        return true;
    }
}
