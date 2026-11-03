using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NexView.Core.Infrastructure;

namespace NexView.Platform.Windows;

/// <summary>Windows'a özgü kabuk işlemleri (pano görüntüsü, Geri Dönüşüm Kutusu, yazdırma). Diğer platformlarda false döner.</summary>
public static class WindowsShell
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    // ---------- Pano ----------

    private const uint CF_DIB = 8, CF_HDROP = 15, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    private static bool Open()
    {
        for (int i = 0; i < 20; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    private static IntPtr ToGlobal(ReadOnlySpan<byte> data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        var p = GlobalLock(h);
        unsafe { data.CopyTo(new Span<byte>((void*)p, data.Length)); }
        GlobalUnlock(h);
        return h;
    }

    /// <summary>PNG (alfa korunur) ve CF_DIB (uyumluluk) olarak panoya koyar.</summary>
    public static bool SetClipboardImage(byte[] png, byte[] bmpFile)
    {
        if (!IsSupported || !Open()) return false;
        try
        {
            EmptyClipboard();
            var hPng = ToGlobal(png);
            if (hPng != IntPtr.Zero && SetClipboardData(RegisterClipboardFormat("PNG"), hPng) == IntPtr.Zero) GlobalFree(hPng);
            if (bmpFile.Length > 14)
            {
                var hDib = ToGlobal(bmpFile.AsSpan(14));
                if (hDib != IntPtr.Zero && SetClipboardData(CF_DIB, hDib) == IntPtr.Zero) GlobalFree(hDib);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Panoya yazılamadı", ex);
            return false;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Panodan görüntü baytı (PNG ya da BMP dosyası) veya dosya listesi okur.</summary>
    public static (byte[]? Image, string[]? Files) GetClipboardImage()
    {
        if (!IsSupported || !Open()) return (null, null);
        try
        {
            uint png = RegisterClipboardFormat("PNG");
            if (IsClipboardFormatAvailable(png) && Read(GetClipboardData(png)) is { Length: > 8 } p) return (p, null);
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var h = GetClipboardData(CF_HDROP);
                uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
                var files = new string[n];
                for (uint i = 0; i < n; i++)
                {
                    var sb = new StringBuilder(1024);
                    DragQueryFile(h, i, sb, (uint)sb.Capacity);
                    files[i] = sb.ToString();
                }
                if (files.Length > 0) return (null, files);
            }
            if (IsClipboardFormatAvailable(CF_DIB) && Read(GetClipboardData(CF_DIB)) is { Length: > 40 } dib)
                return (DibToBmp(dib), null);
            return (null, null);
        }
        catch (Exception ex)
        {
            Log.Warn("Pano okunamadı", ex);
            return (null, null);
        }
        finally { CloseClipboard(); }
    }

    private static byte[]? Read(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        var size = (int)GlobalSize(h);
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(p, data, 0, size);
            return data;
        }
        finally { GlobalUnlock(h); }
    }

    private static byte[] DibToBmp(byte[] dib)
    {
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);
        int masks = compression == 3 && headerSize == 40 ? 12 : 0;
        int palette = (clrUsed != 0 ? clrUsed : bitCount <= 8 ? 1 << bitCount : 0) * 4;
        int offset = 14 + headerSize + masks + palette;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    // ---------- Geri Dönüşüm Kutusu ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public static bool MoveToRecycleBin(string path)
    {
        if (!IsSupported) return false;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x0003, // FO_DELETE
            pFrom = path + '\0',
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    public static void OpenRecycleBin()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Geri Dönüşüm Kutusu açılamadı", ex); }
    }

    // ---------- Yazdırma ----------

    public static bool Print(string path)
    {
        if (!IsSupported) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { Verb = "print", UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Yazdırma başlatılamadı", ex);
            return false;
        }
    }
}
