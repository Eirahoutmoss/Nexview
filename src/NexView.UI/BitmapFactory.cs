using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NexView.Core.Imaging;

namespace NexView.UI;

public static class BitmapFactory
{
    public static WriteableBitmap Create(DecodedImage img)
    {
        var wb = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        int rowBytes = img.Width * 4;
        if (fb.RowBytes == img.Stride)
        {
            Marshal.Copy(img.Pixels, 0, fb.Address, img.Stride * img.Height);
        }
        else
        {
            for (int y = 0; y < img.Height; y++)
                Marshal.Copy(img.Pixels, y * img.Stride, fb.Address + y * fb.RowBytes, rowBytes);
        }
        return wb;
    }
}
