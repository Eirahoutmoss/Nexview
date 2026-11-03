using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NexView.Core.Editing;
using NexView.UI.Editing;

namespace NexView.UI.Controls;

/// <summary>Düzenleme kaplaması: kırpma çerçevesi, belge köşeleri, işaretler ve karşılaştırma ayırıcısı.</summary>
public sealed class EditOverlay : Control
{
    private enum DragKind { None, CropMove, CropHandle, QuadHandle, Shape, Split }

    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
    private static readonly IPen White = new Pen(Brushes.White, 1.5);
    private static readonly IPen Thirds = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), 1);
    private static readonly IPen Cyan = new Pen(new SolidColorBrush(Color.Parse("#22D3EE")), 2);
    private static readonly IPen Dashed = new Pen(Brushes.White, 1.5, new DashStyle([4, 3], 0));
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private ImageViewport? _viewport;
    private IEditOverlayModel? _vm;
    private DragKind _drag;
    private int _handle;
    private Point _start;
    private NRect _cropStart = NRect.Full;
    private NRect? _liveCrop;
    private NPoint[]? _liveQuad;
    private Annotation? _draft;

    /// <summary>Tutamak yakalama yarıçapı (dokunmatikte daha büyük olmalı).</summary>
    public double HandleRadius { get; set; } = 14;

    public ImageViewport? Viewport
    {
        get => _viewport;
        set
        {
            if (_viewport != null) _viewport.ViewChanged -= OnViewChanged;
            _viewport = value;
            if (_viewport != null) _viewport.ViewChanged += OnViewChanged;
        }
    }

    private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) { _vm.PropertyChanged -= OnVmChanged; _vm.Annotations.CollectionChanged -= OnAnnChanged; }
        _vm = DataContext as IEditOverlayModel;
        if (_vm != null) { _vm.PropertyChanged += OnVmChanged; _vm.Annotations.CollectionChanged += OnAnnChanged; }
        InvalidateVisual();
    }

    private void OnVmChanged(object? s, PropertyChangedEventArgs e) => InvalidateVisual();
    private void OnAnnChanged(object? s, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private Rect ImageRect => _viewport?.GetImageScreenRect() ?? default;

    private Point ToScreen(double u, double v)
    {
        var r = ImageRect;
        return new Point(r.X + u * r.Width, r.Y + v * r.Height);
    }

    private (double U, double V) ToNorm(Point p)
    {
        var r = ImageRect;
        if (r.Width <= 0 || r.Height <= 0) return (0, 0);
        return (Math.Clamp((p.X - r.X) / r.Width, 0, 1), Math.Clamp((p.Y - r.Y) / r.Height, 0, 1));
    }

    private Rect ToScreen(NRect n)
    {
        var a = ToScreen(n.X, n.Y);
        var b = ToScreen(n.X + n.W, n.Y + n.H);
        return new Rect(a, b);
    }

    // ---------- Çizim ----------

    public override void Render(DrawingContext ctx)
    {
        if (_vm is not { IsActive: true } vm || _viewport == null) return;
        // Saydam zemin: boş alanlarda da fare olaylarını alabilmek için (Avalonia yalnızca çizilen yerlerde isabet testi yapar)
        if (vm.OverlayActive) FillHitArea(ctx);
        var r = ImageRect;
        if (r.Width <= 0) return;

        switch (vm.Tool)
        {
            case EditTool.Crop: DrawCrop(ctx, r, _liveCrop ?? vm.CropRect); break;
            case EditTool.Document when vm.DocumentPerspective: DrawQuad(ctx, r, _liveQuad ?? vm.Quad); break;
            case EditTool.Markup or EditTool.Adjust:
                foreach (var a in vm.Annotations) DrawAnnotation(ctx, r, a);
                if (_draft != null) DrawAnnotation(ctx, r, _draft);
                break;
        }

        if (vm.Compare == CompareMode.Split)
        {
            var x = r.X + r.Width * vm.SplitPosition;
            var c = new Point(x, r.Y + r.Height / 2);
            ctx.DrawEllipse(Brushes.White, null, c, 11, 11);
            ctx.DrawLine(new Pen(Brushes.Black, 1.5), new Point(x - 4, c.Y), new Point(x + 4, c.Y));
        }
    }

    private void FillHitArea(DrawingContext ctx) => ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    private static void DrawCrop(DrawingContext ctx, Rect r, NRect n)
    {
        var c = new Rect(r.X + n.X * r.Width, r.Y + n.Y * r.Height, n.W * r.Width, n.H * r.Height);
        ctx.FillRectangle(Dim, new Rect(r.X, r.Y, r.Width, c.Y - r.Y));
        ctx.FillRectangle(Dim, new Rect(r.X, c.Bottom, r.Width, r.Bottom - c.Bottom));
        ctx.FillRectangle(Dim, new Rect(r.X, c.Y, c.X - r.X, c.Height));
        ctx.FillRectangle(Dim, new Rect(c.Right, c.Y, r.Right - c.Right, c.Height));
        for (int i = 1; i < 3; i++)
        {
            ctx.DrawLine(Thirds, new Point(c.X + c.Width * i / 3, c.Y), new Point(c.X + c.Width * i / 3, c.Bottom));
            ctx.DrawLine(Thirds, new Point(c.X, c.Y + c.Height * i / 3), new Point(c.Right, c.Y + c.Height * i / 3));
        }
        ctx.DrawRectangle(null, White, c);
        foreach (var p in CropHandles(c)) ctx.FillRectangle(Brushes.White, new Rect(p.X - 5, p.Y - 5, 10, 10));
    }

    private static Point[] CropHandles(Rect c) =>
    [
        c.TopLeft, new(c.Center.X, c.Y), c.TopRight, new(c.Right, c.Center.Y),
        c.BottomRight, new(c.Center.X, c.Bottom), c.BottomLeft, new(c.X, c.Center.Y),
    ];

    private void DrawQuad(DrawingContext ctx, Rect r, NPoint[] q)
    {
        var pts = q.Select(p => ToScreen(p.X, p.Y)).ToArray();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.SetFillRule(FillRule.EvenOdd);
            g.BeginFigure(r.TopLeft, true); g.LineTo(r.TopRight); g.LineTo(r.BottomRight); g.LineTo(r.BottomLeft); g.EndFigure(true);
            g.BeginFigure(pts[0], true); foreach (var p in pts.Skip(1)) g.LineTo(p); g.EndFigure(true);
        }
        ctx.DrawGeometry(Dim, null, geo);
        for (int i = 0; i < 4; i++) ctx.DrawLine(Cyan, pts[i], pts[(i + 1) % 4]);
        foreach (var p in pts) ctx.DrawEllipse(Brushes.White, Cyan, p, 8, 8);
    }

    private void DrawAnnotation(DrawingContext ctx, Rect r, Annotation a)
    {
        var color = Color.Parse(a.Color);
        var brush = new SolidColorBrush(color);
        double diag = Math.Sqrt(r.Width * r.Width + r.Height * r.Height);
        double sw = Math.Max(1.5, diag * 0.004 * a.Size);
        var p1 = ToScreen(a.X1, a.Y1);
        var p2 = ToScreen(a.X2, a.Y2);
        var box = new Rect(p1, p2).Normalize();
        var pen = new Pen(brush, sw, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (a.Kind)
        {
            case AnnotationKind.Arrow:
            {
                double ang = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X), head = sw * 4.5;
                var b = new Point(p2.X - Math.Cos(ang) * head * 0.8, p2.Y - Math.Sin(ang) * head * 0.8);
                ctx.DrawLine(pen, p1, b);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(p2, true);
                    g.LineTo(new Point(p2.X - Math.Cos(ang - 0.45) * head, p2.Y - Math.Sin(ang - 0.45) * head));
                    g.LineTo(new Point(p2.X - Math.Cos(ang + 0.45) * head, p2.Y - Math.Sin(ang + 0.45) * head));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(brush, null, geo);
                break;
            }
            case AnnotationKind.Rectangle: ctx.DrawRectangle(null, pen, box); break;
            case AnnotationKind.Ellipse: ctx.DrawEllipse(null, pen, box.Center, box.Width / 2, box.Height / 2); break;
            case AnnotationKind.Highlight: ctx.FillRectangle(new SolidColorBrush(color, 0.38), box); break;
            case AnnotationKind.Text when a.Text != null:
            {
                double fs = Math.Max(10, diag * 0.028 * a.Size);
                var shadow = new FormattedText(a.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, fs, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
                var ft = new FormattedText(a.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, fs, brush);
                double off = Math.Max(1, fs / 18);
                ctx.DrawText(shadow, new Point(p1.X + off, p1.Y + fs * 0.12 + off));
                ctx.DrawText(ft, new Point(p1.X, p1.Y + fs * 0.12));
                break;
            }
            default:
                // Gizleme alanları önizlemede piksel olarak uygulanır; burada yalnızca sınırı gösterilir
                ctx.DrawRectangle(null, Dashed, box);
                break;
        }
    }

    // ---------- Etkileşim ----------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _viewport?.ZoomBy(Math.Pow(1.2, e.Delta.Y), e.GetPosition(_viewport));
        e.Handled = true;
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is not { IsActive: true } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!vm.OverlayActive) return;
        var p = e.GetPosition(this);
        var r = ImageRect;
        _start = p;
        e.Handled = true;

        if (vm.Compare == CompareMode.Split && Math.Abs(p.X - (r.X + r.Width * vm.SplitPosition)) < HandleRadius)
        {
            _drag = DragKind.Split;
            e.Pointer.Capture(this);
            return;
        }

        switch (vm.Tool)
        {
            case EditTool.Crop:
            {
                var c = ToScreen(vm.CropRect);
                var handles = CropHandles(c);
                _cropStart = vm.CropRect;
                _handle = Array.FindIndex(handles, h => Dist(h, p) < HandleRadius);
                if (_handle >= 0) _drag = DragKind.CropHandle;
                else if (c.Contains(p)) _drag = DragKind.CropMove;
                else
                {
                    var (u, v) = ToNorm(p);
                    _cropStart = new NRect(u, v, 0, 0);
                    _handle = 4;
                    _drag = DragKind.CropHandle;
                }
                break;
            }
            case EditTool.Document when vm.DocumentPerspective:
            {
                var pts = vm.Quad.Select(q => ToScreen(q.X, q.Y)).ToArray();
                _handle = Array.FindIndex(pts, h => Dist(h, p) < HandleRadius + 4);
                if (_handle >= 0) { _drag = DragKind.QuadHandle; _liveQuad = vm.Quad.ToArray(); }
                break;
            }
            case EditTool.Markup:
            {
                var (u, v) = ToNorm(p);
                if (vm.MarkupKind == AnnotationKind.Text)
                {
                    await vm.AddTextAsync(u, v);
                    return;
                }
                _draft = new Annotation(vm.MarkupKind, u, v, u, v, vm.MarkupColor, null, vm.MarkupSize);
                _drag = DragKind.Shape;
                break;
            }
        }
        if (_drag != DragKind.None) e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm == null || _drag == DragKind.None) return;
        var p = e.GetPosition(this);
        var (u, v) = ToNorm(p);
        var r = ImageRect;
        switch (_drag)
        {
            case DragKind.Split:
                _vm.SplitPosition = Math.Clamp((p.X - r.X) / Math.Max(1, r.Width), 0, 1);
                break;
            case DragKind.CropMove:
            {
                double du = (p.X - _start.X) / r.Width, dv = (p.Y - _start.Y) / r.Height;
                var c = _cropStart;
                _liveCrop = c with { X = Math.Clamp(c.X + du, 0, 1 - c.W), Y = Math.Clamp(c.Y + dv, 0, 1 - c.H) };
                break;
            }
            case DragKind.CropHandle:
                _liveCrop = ResizeCrop(_cropStart, _handle, u, v, _vm.CropRatioNormalized);
                break;
            case DragKind.QuadHandle when _liveQuad != null:
                _liveQuad[_handle] = new NPoint(u, v);
                break;
            case DragKind.Shape when _draft != null:
                _draft = _draft with { X2 = u, Y2 = v };
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_vm == null) return;
        switch (_drag)
        {
            case DragKind.CropMove:
            case DragKind.CropHandle:
                if (_liveCrop is { } c && c.W > 0.01 && c.H > 0.01) _vm.CropRect = c;
                break;
            case DragKind.QuadHandle when _liveQuad != null:
                _vm.Quad = _liveQuad.ToArray();
                _vm.CommitInteraction();
                break;
            case DragKind.Shape when _draft != null:
            {
                var b = _draft.Bounds;
                bool big = _draft.Kind == AnnotationKind.Arrow
                    ? Math.Abs(_draft.X2 - _draft.X1) + Math.Abs(_draft.Y2 - _draft.Y1) > 0.01
                    : b.W > 0.005 && b.H > 0.005;
                if (big) _vm.AddAnnotation(_draft);
                break;
            }
        }
        _drag = DragKind.None;
        _liveCrop = null;
        _liveQuad = null;
        _draft = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private static NRect ResizeCrop(NRect s, int handle, double u, double v, double? ratio)
    {
        double l = s.X, t = s.Y, r = s.X + s.W, b = s.Y + s.H;
        switch (handle)
        {
            case 0: l = u; t = v; break;
            case 1: t = v; break;
            case 2: r = u; t = v; break;
            case 3: r = u; break;
            case 4: r = u; b = v; break;
            case 5: b = v; break;
            case 6: l = u; b = v; break;
            case 7: l = u; break;
        }
        var n = new NRect(l, t, r - l, b - t).Normalize();
        if (ratio is { } k && n.W > 0 && n.H > 0)
        {
            bool widthDriven = handle is 3 or 7 || (handle is 0 or 2 or 4 or 6 && n.W / k >= n.H);
            double w = n.W, h = n.H;
            if (widthDriven) h = w / k; else w = h * k;
            if (h > 1) { h = 1; w = h * k; }
            if (w > 1) { w = 1; h = w / k; }
            double x = handle is 0 or 6 or 7 ? (s.X + s.W) - w : n.X;
            double y = handle is 0 or 1 or 2 ? (s.Y + s.H) - h : n.Y;
            if (handle is 1 or 5) x = s.X + s.W / 2 - w / 2;
            if (handle is 3 or 7) y = s.Y + s.H / 2 - h / 2;
            n = new NRect(Math.Clamp(x, 0, 1 - w), Math.Clamp(y, 0, 1 - h), w, h);
        }
        return n;
    }

    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
