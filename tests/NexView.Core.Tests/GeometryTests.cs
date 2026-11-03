using NexView.Core.Editing;
using NexView.Core.Imaging;

namespace NexView.Core.Tests;

public class GeometryTests
{
    private static DecodedImage Solid(int w, int h, byte v)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        return new DecodedImage(w, h, w * 4, px, w, h);
    }

    private static void Set(DecodedImage img, int x, int y, byte b, byte g, byte r)
    {
        int i = y * img.Stride + x * 4;
        img.Pixels[i] = b; img.Pixels[i + 1] = g; img.Pixels[i + 2] = r;
    }

    private static (byte B, byte G, byte R) Get(DecodedImage img, int x, int y)
    {
        int i = y * img.Stride + x * 4;
        return (img.Pixels[i], img.Pixels[i + 1], img.Pixels[i + 2]);
    }

    /// <summary>Koyu zemin üzerinde beyaz dörtgen (belge) çizer.</summary>
    private static DecodedImage Document(int w, int h, (double X, double Y)[] q)
    {
        var img = Solid(w, h, 40);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (Inside(q, x + 0.5, y + 0.5)) Set(img, x, y, 235, 235, 235);
        return img;
    }

    private static bool Inside((double X, double Y)[] q, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            if ((q[i].Y > y) != (q[j].Y > y) && x < (q[j].X - q[i].X) * (y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X) inside = !inside;
        return inside;
    }

    [Fact]
    public void Rotate90_MovesTopLeftToTopRight()
    {
        var img = Solid(4, 2, 0);
        Set(img, 0, 0, 255, 0, 0);
        var r = EditGeometry.Rotate90(img, 90);
        Assert.Equal((2, 4), (r.Width, r.Height));
        Assert.Equal(255, Get(r, 1, 0).B);
        var back = EditGeometry.Rotate90(EditGeometry.Rotate90(EditGeometry.Rotate90(r, 90), 90), 90);
        Assert.Equal(255, Get(back, 0, 0).B);
    }

    [Fact]
    public void Flip_And_Crop()
    {
        var img = Solid(10, 10, 0);
        Set(img, 0, 0, 255, 0, 0);
        var f = EditGeometry.Flip(img, true, true);
        Assert.Equal(255, Get(f, 9, 9).B);
        var c = EditGeometry.Crop(f, new NRect(0.5, 0.5, 0.5, 0.5));
        Assert.Equal((5, 5), (c.Width, c.Height));
        Assert.Equal(255, Get(c, 4, 4).B);
    }

    [Fact]
    public void Straighten_LeavesNoBlackCorners()
    {
        var img = Solid(400, 300, 200);
        var s = EditGeometry.Straighten(img, 12);
        Assert.True(s.Width < 400 && s.Height < 300);
        foreach (var (x, y) in new[] { (0, 0), (s.Width - 1, 0), (0, s.Height - 1), (s.Width - 1, s.Height - 1) })
            Assert.True(Get(s, x, y).G > 190);
    }

    [Fact]
    public void DocumentDetector_FindsCorners_AndPerspectiveFlattens()
    {
        (double, double)[] q = [(120, 60), (520, 100), (480, 440), (90, 400)];
        var img = Document(640, 480, q);
        var found = DocumentDetector.Detect(img);
        Assert.NotNull(found);
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(found![i].X * 640, q[i].Item1 - 12, q[i].Item1 + 12);
            Assert.InRange(found[i].Y * 480, q[i].Item2 - 12, q[i].Item2 + 12);
        }

        var flat = EditGeometry.Perspective(img, found!);
        // Düzleştirilmiş belge neredeyse tamamen beyaz olmalı
        int white = 0, total = 0;
        for (int y = 4; y < flat.Height - 4; y += 3)
        for (int x = 4; x < flat.Width - 4; x += 3) { total++; if (Get(flat, x, y).G > 200) white++; }
        Assert.True(white > total * 0.97, $"{white}/{total}");
    }

    [Fact]
    public void DocumentDetector_ReturnsNull_ForUniformImage()
    {
        Assert.Null(DocumentDetector.Detect(Solid(300, 200, 128)));
    }

    [Fact]
    public void Apply_MatchesStageRules()
    {
        var img = Solid(200, 100, 100);
        var r = new EditRecipe { Rotation = 90, Crop = new NRect(0, 0, 0.5, 0.5) };
        Assert.Equal((100, 200), (EditGeometry.Apply(img, r, PreviewStage.BeforeCrop).Width, EditGeometry.Apply(img, r, PreviewStage.BeforeCrop).Height));
        var full = EditGeometry.Apply(img, r, PreviewStage.Full);
        Assert.Equal((50, 100), (full.Width, full.Height));
        Assert.Same(img, EditGeometry.Apply(img, r, PreviewStage.BeforeDocument));
    }
}
