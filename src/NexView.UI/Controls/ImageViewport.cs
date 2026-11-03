using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NexView.UI.Controls;

public enum ViewerBackground { Dark, Checkerboard, Black, White }

/// <summary>
/// Görüntü görüntüleme yüzeyi: sığdır, %100, imleç merkezli yakınlaştırma, kaydırma, 90° döndürme, yatay çevirme.
/// Zoom = görüntünün bir pikselinin ekrandaki fiziksel piksel karşılığı (1.0 = %100).
/// Ağır işlem yapmaz; yalnızca hazır bitmap'i dönüşümle çizer.
/// </summary>
public sealed class ImageViewport : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageViewport, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<PixelSize> OriginalSizeProperty =
        AvaloniaProperty.Register<ImageViewport, PixelSize>(nameof(OriginalSize));

    public static readonly StyledProperty<int> RotationProperty =
        AvaloniaProperty.Register<ImageViewport, int>(nameof(Rotation));

    public static readonly StyledProperty<bool> FlipHorizontalProperty =
        AvaloniaProperty.Register<ImageViewport, bool>(nameof(FlipHorizontal));

    public static readonly StyledProperty<ViewerBackground> BackgroundModeProperty =
        AvaloniaProperty.Register<ImageViewport, ViewerBackground>(nameof(BackgroundMode));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ImageViewport, double>(nameof(Zoom), 1.0, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<Bitmap?> CompareSourceProperty =
        AvaloniaProperty.Register<ImageViewport, Bitmap?>(nameof(CompareSource));

    public static readonly StyledProperty<double> SplitPositionProperty =
        AvaloniaProperty.Register<ImageViewport, double>(nameof(SplitPosition), 0.5);

    public const double MinZoom = 0.01;
    public const double MaxZoom = 32;
    private const double FitPadding = 16;

    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);
    private static IBrush? _checker;

    private bool _fit = true;
    private bool _internalZoom;
    private Vector _offset;
    private Point? _panStart;
    private Vector _panOffsetStart;

    // Dokunmatik: iki parmakla yakınlaştırma ve sığdırılmış görünümde yatay kaydırma (sonraki/önceki).
    private readonly Dictionary<IPointer, Point> _touches = new();
    private double _pinchDist, _pinchZoom;
    private bool _pinched;
    private Point? _swipeStart;
    private int _pressClicks;

    /// <summary>Sığdırılmış görünümde yatay kaydırma: +1 sonraki, -1 önceki.</summary>
    public event EventHandler<int>? Swiped;
    /// <summary>Tek dokunuş (çift dokunuş değil, kaydırma değil).</summary>
    public event EventHandler? Tapped2;

    static ImageViewport()
    {
        AffectsRender<ImageViewport>(CompareSourceProperty, SplitPositionProperty, SourceProperty, RotationProperty, FlipHorizontalProperty, BackgroundModeProperty, ZoomProperty, OriginalSizeProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ImageViewport>(true);
        FocusableProperty.OverrideDefaultValue<ImageViewport>(true);
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public PixelSize OriginalSize { get => GetValue(OriginalSizeProperty); set => SetValue(OriginalSizeProperty, value); }
    public int Rotation { get => GetValue(RotationProperty); set => SetValue(RotationProperty, value); }
    public bool FlipHorizontal { get => GetValue(FlipHorizontalProperty); set => SetValue(FlipHorizontalProperty, value); }
    public ViewerBackground BackgroundMode { get => GetValue(BackgroundModeProperty); set => SetValue(BackgroundModeProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public Bitmap? CompareSource { get => GetValue(CompareSourceProperty); set => SetValue(CompareSourceProperty, value); }
    public double SplitPosition { get => GetValue(SplitPositionProperty); set => SetValue(SplitPositionProperty, value); }

    /// <summary>Yakınlaştırma, kaydırma ya da boyut değiştiğinde (üst katmanlar için).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Döndürme/çevirme yokken görüntünün ekrandaki dikdörtgeni (yerel koordinat).</summary>
    public Rect GetImageScreenRect()
    {
        var img = RotatedSize;
        double s = Zoom / Scaling;
        var cx = Bounds.Width / 2 + _offset.X;
        var cy = Bounds.Height / 2 + _offset.Y;
        return new Rect(cx - img.Width * s / 2, cy - img.Height * s / 2, img.Width * s, img.Height * s);
    }

    private void Changed()
    {
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFitMode => _fit;

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private PixelSize SourcePixels
    {
        get
        {
            var s = OriginalSize;
            if (s.Width > 0 && s.Height > 0) return s;
            return Source?.PixelSize ?? default;
        }
    }

    /// <summary>Döndürme sonrası görüntü boyutu (orijinal piksel).</summary>
    private Size RotatedSize
    {
        get
        {
            var s = SourcePixels;
            return Rotation % 180 != 0 ? new Size(s.Height, s.Width) : new Size(s.Width, s.Height);
        }
    }

    public double ComputeFitZoom()
    {
        var img = RotatedSize;
        if (img.Width <= 0 || img.Height <= 0 || Bounds.Width <= 0) return 1;
        var w = Math.Max(1, Bounds.Width - FitPadding * 2);
        var h = Math.Max(1, Bounds.Height - FitPadding * 2);
        return Math.Min(1.0, Math.Min(w / img.Width, h / img.Height) * Scaling);
    }

    public void FitToWindow()
    {
        _fit = true;
        _offset = default;
        SetZoomInternal(ComputeFitZoom());
        Changed();
    }

    public void ActualSize() => ZoomTo(1.0, null);

    public void ZoomBy(double factor, Point? anchor = null) => ZoomTo(Zoom * factor, anchor);

    public void ZoomTo(double zoom, Point? anchor)
    {
        var old = Zoom;
        var nz = Math.Clamp(zoom, MinZoom, MaxZoom);
        var a = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var cx = Bounds.Width / 2 + _offset.X;
        var cy = Bounds.Height / 2 + _offset.Y;
        var px = a.X - cx;
        var py = a.Y - cy;
        var ratio = old > 0 ? nz / old : 1;
        _offset = new Vector(_offset.X + px - px * ratio, _offset.Y + py - py * ratio);
        _fit = false;
        SetZoomInternal(nz);
        Changed();
    }

    private void SetZoomInternal(double z)
    {
        _internalZoom = true;
        try { SetCurrentValue(ZoomProperty, Math.Clamp(z, MinZoom, MaxZoom)); }
        finally { _internalZoom = false; }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty)
        {
            if (!_internalZoom) _fit = false;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (change.Property == OriginalSizeProperty)
        {
            var o = change.GetOldValue<PixelSize>();
            var n = change.GetNewValue<PixelSize>();
            if (_fit) FitToWindow();
            else if (o.Width > 0 && n.Width > 0 && o.Width != n.Width) SetZoomInternal(Zoom * o.Width / n.Width);
        }
        else if ((change.Property == SourceProperty || change.Property == RotationProperty) && _fit)
        {
            FitToWindow();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fit) FitToWindow();
        else ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        switch (BackgroundMode)
        {
            case ViewerBackground.Black: ctx.FillRectangle(Brushes.Black, bounds); break;
            case ViewerBackground.White: ctx.FillRectangle(Brushes.White, bounds); break;
            default: ctx.FillRectangle(Brushes.Transparent, bounds); break;
        }

        var src = Source;
        if (src is null) return;
        var orig = SourcePixels;
        if (orig.Width <= 0 || orig.Height <= 0) return;

        double s = Zoom / Scaling;
        var img = RotatedSize;
        var cx = bounds.Width / 2 + _offset.X;
        var cy = bounds.Height / 2 + _offset.Y;

        if (BackgroundMode == ViewerBackground.Checkerboard)
        {
            var rect = new Rect(cx - img.Width * s / 2, cy - img.Height * s / 2, img.Width * s, img.Height * s);
            ctx.FillRectangle(Checker, rect);
        }

        var m = Matrix.CreateTranslation(-orig.Width / 2.0, -orig.Height / 2.0)
              * Matrix.CreateRotation(Rotation * Math.PI / 180.0)
              * Matrix.CreateScale(FlipHorizontal ? -1 : 1, 1)
              * Matrix.CreateScale(s, s)
              * Matrix.CreateTranslation(cx, cy);

        var mode = Zoom >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            using (ctx.PushTransform(m))
                ctx.DrawImage(src, new Rect(src.Size), new Rect(0, 0, orig.Width, orig.Height));

            if (CompareSource is { } cmp)
            {
                var rect = new Rect(cx - img.Width * s / 2, cy - img.Height * s / 2, img.Width * s, img.Height * s);
                var splitX = rect.X + rect.Width * Math.Clamp(SplitPosition, 0, 1);
                using (ctx.PushClip(new Rect(rect.X, rect.Y, Math.Max(0, splitX - rect.X), rect.Height)))
                using (ctx.PushTransform(m))
                    ctx.DrawImage(cmp, new Rect(cmp.Size), new Rect(0, 0, orig.Width, orig.Height));
                ctx.DrawLine(new Pen(Brushes.White, 2), new Point(splitX, rect.Y), new Point(splitX, rect.Bottom));
            }
        }
    }

    private static IBrush Checker => _checker ??= CreateChecker();

    private static IBrush CreateChecker()
    {
        const int n = 16;
        var wb = new WriteableBitmap(new PixelSize(n, n), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
        {
            var data = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                byte v = ((x < n / 2) ^ (y < n / 2)) ? (byte)0x3A : (byte)0x2A;
                int i = (y * n + x) * 4;
                data[i] = (byte)(v + 6); data[i + 1] = v; data[i + 2] = v; data[i + 3] = 255;
            }
            for (int y = 0; y < n; y++)
                System.Runtime.InteropServices.Marshal.Copy(data, y * n * 4, fb.Address + y * fb.RowBytes, n * 4);
        }
        return new ImageBrush(wb)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, n, n, RelativeUnit.Absolute),
        };
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is null) return;
        ZoomBy(Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (e.Pointer.Type == PointerType.Touch)
        {
            _touches[e.Pointer] = pt.Position;
            e.Pointer.Capture(this);
            if (_touches.Count == 1) { _swipeStart = pt.Position; _pinched = false; _pressClicks = e.ClickCount; }
            if (_touches.Count == 2 && Source is not null)
            {
                var ps = _touches.Values.ToArray();
                _pinchDist = Math.Max(1, Dist(ps[0], ps[1]));
                _pinchZoom = Zoom;
                _pinched = true;
                _panStart = null;
                e.Handled = true;
                return;
            }
        }
        if (Source is null) return;
        if (pt.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            if (_fit) ZoomTo(1.0, pt.Position);
            else FitToWindow();
            e.Handled = true;
            return;
        }
        if (pt.Properties.IsLeftButtonPressed || pt.Properties.IsMiddleButtonPressed)
        {
            _panStart = pt.Position;
            _panOffsetStart = _offset;
            e.Pointer.Capture(this);
            Cursor = PanCursor;
            Focus();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Type == PointerType.Touch && _touches.ContainsKey(e.Pointer))
        {
            _touches[e.Pointer] = e.GetPosition(this);
            if (_touches.Count >= 2 && Source is not null)
            {
                var ps = _touches.Values.ToArray();
                var mid = new Point((ps[0].X + ps[1].X) / 2, (ps[0].Y + ps[1].Y) / 2);
                ZoomTo(_pinchZoom * Dist(ps[0], ps[1]) / _pinchDist, mid);
                return;
            }
            // Sığdırılmış görünümde tek parmak sürükleme kaydırma hareketidir, görüntüyü kaydırmaz.
            if (_fit && Swiped != null) return;
        }
        if (_panStart is not { } start) return;
        var p = e.GetPosition(this);
        _offset = new Vector(_panOffsetStart.X + p.X - start.X, _panOffsetStart.Y + p.Y - start.Y);
        if (_offset != _panOffsetStart) _fit = false;
        Changed();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Type == PointerType.Touch && _touches.Remove(e.Pointer))
        {
            if (_touches.Count == 1 && _pinched)
            {
                // Bir parmak kaldı: oradan kaydırmaya devam et.
                _panStart = _touches.Values.First();
                _panOffsetStart = _offset;
                if (Zoom <= ComputeFitZoom() * 1.02) FitToWindow();
                return;
            }
            if (_touches.Count == 0)
            {
                var end = e.GetPosition(this);
                if (!_pinched && _swipeStart is { } s0)
                {
                    var dx = end.X - s0.X; var dy = end.Y - s0.Y;
                    if (_fit && Swiped != null && Math.Abs(dx) > 60 && Math.Abs(dx) > Math.Abs(dy) * 1.5)
                        Swiped(this, dx < 0 ? 1 : -1);
                    else if (Math.Abs(dx) < 10 && Math.Abs(dy) < 10 && _pressClicks < 2)
                        Tapped2?.Invoke(this, EventArgs.Empty);
                }
                _swipeStart = null;
                _pinched = false;
            }
        }
        if (_panStart is null) return;
        _panStart = null;
        e.Pointer.Capture(null);
        Cursor = null;
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
