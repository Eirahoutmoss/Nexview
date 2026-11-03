namespace NexView.Core.Imaging;

/// <summary>
/// RAW dosyalarının içindeki en büyük gömülü JPEG önizlemeyi bulur.
/// Kayıpsız JPEG (SOF3, sensör verisi) atlanır; yalnızca baseline/progressive kabul edilir.
/// </summary>
public static class EmbeddedJpegExtractor
{
    private const long MaxScanBytes = 512L * 1024 * 1024;

    public static byte[]? ExtractLargest(string path, int minBytes = 16 * 1024)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var len = (int)Math.Min(fs.Length, MaxScanBytes);
            data = new byte[len];
            fs.ReadExactly(data, 0, len);
        }
        return ExtractLargest(data, minBytes);
    }

    public static byte[]? ExtractLargest(byte[] data, int minBytes = 16 * 1024)
    {
        int bestStart = -1, bestLen = 0;
        for (int i = 0; i < data.Length - 3; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;
            int end = FindEnd(data, i, out var sof);
            if (end <= 0) continue;
            if (sof is 0xC0 or 0xC1 or 0xC2)
            {
                int len = end - i;
                if (len > bestLen) { bestLen = len; bestStart = i; }
            }
            i = end - 1;
        }
        if (bestStart < 0 || bestLen < minBytes) return null;
        return data.AsSpan(bestStart, bestLen).ToArray();
    }

    private static int FindEnd(byte[] d, int start, out byte sof)
    {
        sof = 0;
        int p = start + 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) return -1;
            byte m = d[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0xD9) return p + 2;
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
            if (m == 0xD8) return -1;
            int segLen = (d[p + 2] << 8) | d[p + 3];
            if (segLen < 2) return -1;
            if (m is >= 0xC0 and <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC && sof == 0) sof = m;
            p += 2 + segLen;
            if (m == 0xDA)
            {
                while (p + 1 < d.Length)
                {
                    if (d[p] == 0xFF)
                    {
                        byte n = d[p + 1];
                        if (n == 0x00 || (n >= 0xD0 && n <= 0xD7)) { p += 2; continue; }
                        if (n == 0xFF) { p++; continue; }
                        break;
                    }
                    p++;
                }
            }
        }
        return -1;
    }
}
