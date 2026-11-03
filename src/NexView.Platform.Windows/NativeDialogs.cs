using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexView.Platform.Windows;

/// <summary>Arayüz açılmadan (komut satırı modunda) kullanılan basit Windows iletişim kutuları.</summary>
[SupportedOSPlatform("windows")]
public static class NativeDialogs
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Error(string text) => MessageBoxW(IntPtr.Zero, text, "NexView", 0x10);
    public static void Info(string text) => MessageBoxW(IntPtr.Zero, text, "NexView", 0x40);
    public static bool Confirm(string text) => MessageBoxW(IntPtr.Zero, text, "NexView", 0x04 | 0x20) == 6;
}
