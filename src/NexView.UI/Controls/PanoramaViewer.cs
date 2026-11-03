using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NexView.Core.Imaging;
using NexView.Core.Metadata;

namespace NexView.UI.Controls;

/// <summary>
/// 360° panorama görüntüleyici: eşdikdörtgen kaynaktan doğrusal (rectilinear) perspektif görünüm üretir.
/// Sürükle: bak, tekerlek: yakınlaştır, çift tık: sıfırla. Etkileşim sırasında yarı çözünürlükte, durunca tam kalitede çizer.
/// </summary>
public sealed class PanoramaViewer : Control
{
    private int[]? _src;
    private int _sw, _sh;
    private PanoramaInfo? _geo;
    private double _yaw, _pitch, _fov = 90;
    private WriteableBitmap? _bmp;
    private bool _dirty = true, _interacting;
    private Point? _drag;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _spin;
    private const int Background = unchecked((int)0xFF0B1222);
    private int _spinDir = 1;

    public PanoramaViewer()
    {
        Focusable = true;
        ClipToBounds = true;
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _settle.Tick += (_, _) => { _settle.Stop(); _interacting = false; Invalidate(); };
        _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _spin.Tick += (_, _) =>
        {
            if (_geo is { IsFull360: false })
            {
                // Kısmi panoramada uçlar arasında gidip gel
                var before = _yaw;
                _yaw += _spinDir * 0.12 * Math.PI / 180;
                ClampView();
                if (Math.Abs(_yaw - before) < 1e-6) _spinDir = -_spinDir;
            }
            else _yaw += 0.12 * Math.PI / 180;
            _interacting = true;
            Invalidate();
        };
    }

    public event EventHandler? ViewChanged;
    public double FieldOfView => _fov;
    public double YawDegrees => (_yaw * 180 / Math.PI % 360 + 360) % 360;
    public double PitchDegrees => _pitch * 180 / Math.PI;

    public bool AutoRotate
    {
        get => _spin.IsEnabled;
        set { if (value) _spin.Start(); else { _spin.Stop(); Settle(); } }
    }

    /// <summary>BGRA kaynak ve tam küre geometrisi (geometri bu görüntünün piksel ölçeğinde olmalı).</summary>
    public void SetImage(DecodedImage img, PanoramaInfo geo)
    {
        _sw = img.Width; _sh = img.Height;
        var ints = new int[_sw * _sh];
        for (int y = 0; y < _sh; y++)
            MemoryMarshal.Cast<byte, int>(img.Pixels.AsSpan(y * img.Stride, _sw * 4)).CopyTo(ints.AsSpan(y * _sw, _sw));
        _src = ints;
        _geo = geo;
        Reset();
    }

    public void Reset()
    {
        _yaw = 0; _pitch = 0;
        // Dar panoramalarda başlangıç görüşü panoramanın tamamına yakın olsun
        _fov = 100;
        Invalidate();
    }

    public void ZoomBy(double factor)
    {
        _fov = Math.Clamp(_fov * factor, 15, 120);
        _interacting = true;
        Settle();
        Invalidate();
    }

    /// <summary>Kısmi panoramada bakış ve görüş açısı, görüntünün kapsadığı alanın içinde tutulur (boşluğa bakılmaz).</summary>
    private void ClampView()
    {
        if (_geo == null || Bounds.Width < 2) return;
        double hCover = _geo.HorizontalDegrees, vCover = _geo.VerticalDegrees;
        double aspect = Bounds.Height / Bounds.Width;
        double maxFov = Math.Min(120, _geo.IsFull360 ? 120 : hCover);
        // Dikey görüş açısı dikey kapsamı aşmasın
        double vFovAt(double hf) => 2 * Math.Atan(Math.Tan(hf * Math.PI / 360) * aspect) * 180 / Math.PI;
        while (maxFov > 15 && vFovAt(maxFov) > vCover) maxFov -= 1;
        _fov = Math.Clamp(_fov, Math.Min(15, maxFov), Math.Max(15, maxFov));
        double vHalf = vFovAt(_fov) / 2, hHalf = _fov / 2;
        // Dikey merkez: kırpılmış alanın kürenin neresinde olduğu
        double centerLat = 90 - (_geo.CropTop + _geo.Height / 2.0) / _geo.FullHeight * 180;
        double pitchMax = centerLat + vCover / 2 - vHalf, pitchMin = centerLat - vCover / 2 + vHalf;
        double pitchDeg = _pitch * 180 / Math.PI;
        pitchDeg = pitchMin > pitchMax ? centerLat : Math.Clamp(pitchDeg, pitchMin, pitchMax);
        _pitch = pitchDeg * Math.PI / 180;
        if (!_geo.IsFull360)
        {
            double centerLon = (_geo.CropLeft + _geo.Width / 2.0) / _geo.FullWidth * 360 - 180;
            double yawMax = centerLon + hCover / 2 - hHalf, yawMin = centerLon - hCover / 2 + hHalf;
            double yawDeg = _yaw * 180 / Math.PI;
            yawDeg = yawMin > yawMax ? centerLon : Math.Clamp(yawDeg, yawMin, yawMax);
            _yaw = yawDeg * Math.PI / 180;
        }
    }

    private void Invalidate()
    {
        ClampView();
        _dirty = true;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Settle() { _settle.Stop(); _settle.Start(); }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromUInt32(unchecked((uint)Background))), new Rect(Bounds.Size));
        if (_src == null || _geo == null || Bounds.Width < 2 || Bounds.Height < 2) return;
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double q = _interacting ? 0.5 : 1;
        int w = Math.Max(2, (int)(Bounds.Width * scaling * q)), h = Math.Max(2, (int)(Bounds.Height * scaling * q));
        if (_bmp == null || _bmp.PixelSize.Width != w || _bmp.PixelSize.Height != h)
        {
            _bmp?.Dispose();
            _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _dirty = true;
        }
        if (_dirty) { Draw(_bmp, w, h, bilinear: !_interacting); _dirty = false; }
        ctx.DrawImage(_bmp, new Rect(0, 0, w, h), new Rect(Bounds.Size));
    }

    private void Draw(WriteableBitmap bmp, int w, int h, bool bilinear)
    {
        var src = _src!; var g = _geo!;
        int sw = _sw, sh = _sh;
        bool full = g.IsFull360;
        double f = w / 2.0 / Math.Tan(_fov * Math.PI / 360);
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw), cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        double kx = g.FullWidth / (2 * Math.PI), ky = g.FullHeight / Math.PI;
        var outPx = new int[w * h];
        Parallel.For(0, h, y =>
        {
            double vy = y + 0.5 - h / 2.0;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                double vx = x + 0.5 - w / 2.0, vz = f;
                // Yunuslama (x ekseni), sonra sapma (y ekseni)
                double y1 = vy * cp - vz * sp, z1 = vy * sp + vz * cp;
                double x2 = vx * cy + z1 * sy, z2 = -vx * sy + z1 * cy;
                double lon = Math.Atan2(x2, z2);
                double lat = Math.Atan2(-y1, Math.Sqrt(x2 * x2 + z2 * z2));
                double u = (lon + Math.PI) * kx - g.CropLeft;
                double v = (Math.PI / 2 - lat) * ky - g.CropTop;
                if (full) { u %= sw; if (u < 0) u += sw; }
                if (u < 0 || v < 0 || u > sw - 1 || v > sh - 1) { outPx[row + x] = Background; continue; }
                outPx[row + x] = bilinear ? Bilinear(src, sw, sh, u, v, full) : src[(int)v * sw + (int)u] | unchecked((int)0xFF000000);
            }
        });
        using var fb = bmp.Lock();
        for (int y = 0; y < h; y++) Marshal.Copy(outPx, y * w, fb.Address + y * fb.RowBytes, w);
    }

    private static int Bilinear(int[] s, int w, int h, double u, double v, bool wrap)
    {
        int x0 = (int)u, y0 = (int)v;
        int x1 = x0 + 1 >= w ? (wrap ? 0 : w - 1) : x0 + 1, y1 = Math.Min(y0 + 1, h - 1);
        double fx = u - x0, fy = v - y0;
        int a = s[y0 * w + x0], b = s[y0 * w + x1], c = s[y1 * w + x0], d = s[y1 * w + x1];
        int Ch(int shift)
        {
            double t = ((a >> shift) & 255) * (1 - fx) + ((b >> shift) & 255) * fx;
            double bt = ((c >> shift) & 255) * (1 - fx) + ((d >> shift) & 255) * fx;
            return (int)(t + (bt - t) * fy + 0.5) & 255;
        }
        return unchecked((int)0xFF000000) | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2) { Reset(); e.Handled = true; return; }
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } start) return;
        var p = e.GetPosition(this);
        double perPixel = _fov * Math.PI / 180 / Math.Max(1, Bounds.Width);
        _yaw -= (p.X - start.X) * perPixel;
        _pitch = Math.Clamp(_pitch + (p.Y - start.Y) * perPixel, -Math.PI / 2 * 0.98, Math.PI / 2 * 0.98);
        _drag = p;
        _interacting = true;
        Settle();
        Invalidate();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomBy(Math.Pow(0.9, e.Delta.Y));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = _fov * Math.PI / 180 / 12;
        switch (e.Key)
        {
            case Key.Left: _yaw -= step; break;
            case Key.Right: _yaw += step; break;
            case Key.Up: _pitch = Math.Min(_pitch + step, Math.PI / 2 * 0.98); break;
            case Key.Down: _pitch = Math.Max(_pitch - step, -Math.PI / 2 * 0.98); break;
            case Key.Add or Key.OemPlus: ZoomBy(0.85); return;
            case Key.Subtract or Key.OemMinus: ZoomBy(1 / 0.85); return;
            default: return;
        }
        _interacting = true;
        Settle();
        Invalidate();
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) { ClampView(); _dirty = true; }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _spin.Stop(); _settle.Stop();
        _bmp?.Dispose(); _bmp = null;
    }
}
