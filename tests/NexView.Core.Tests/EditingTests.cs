using ImageMagick;
using NexView.Codecs.Magick;
using NexView.Core.Editing;
using NexView.Core.Formats;

namespace NexView.Core.Tests;

public class EditingTests
{
    private static string Make(uint w = 800, uint h = 600)
    {
        var p = Path.Combine(Path.GetTempPath(), "nexview-edit-" + Guid.NewGuid().ToString("N") + ".png");
        using var img = new MagickImage("plasma:", w, h);
        img.Write(p, MagickFormat.Png);
        return p;
    }

    private static readonly MagickEditor Editor = new();

    [Fact]
    public void IdentityRecipeKeepsSize()
    {
        using var s = Editor.Open(Make(), 400, CancellationToken.None);
        var d = s.RenderPreview(new EditRecipe(), PreviewStage.Full, true, CancellationToken.None);
        Assert.Equal(400, d.Width);
        Assert.Equal(300, d.Height);
    }

    [Fact]
    public void RotateAndCrop()
    {
        using var s = Editor.Open(Make(), 400, CancellationToken.None);
        var r = new EditRecipe { Rotation = 90, Crop = new NRect(0, 0, 0.5, 0.5) };
        var d = s.RenderPreview(r, PreviewStage.Full, true, CancellationToken.None);
        Assert.Equal(150, d.Width);
        Assert.Equal(200, d.Height);
        var before = s.RenderPreview(r, PreviewStage.BeforeCrop, true, CancellationToken.None);
        Assert.Equal(300, before.Width);
    }

    [Fact]
    public void StraightenCropsInside()
    {
        using var s = Editor.Open(Make(), 400, CancellationToken.None);
        var d = s.RenderPreview(new EditRecipe { Straighten = 10 }, PreviewStage.Full, true, CancellationToken.None);
        Assert.True(d.Width < 400 && d.Width > 250);
    }

    [Fact]
    public void PerspectiveProducesQuadSize()
    {
        using var s = Editor.Open(Make(), 400, CancellationToken.None);
        var r = new EditRecipe { DocumentQuad = [new(0.1, 0.1), new(0.9, 0.1), new(0.9, 0.9), new(0.1, 0.9)] };
        var d = s.RenderPreview(r, PreviewStage.Full, true, CancellationToken.None);
        Assert.InRange(d.Width, 315, 325);
        Assert.InRange(d.Height, 235, 245);
    }

    [Fact]
    public void BlackBoxRedacts()
    {
        using var s = Editor.Open(Make(), 400, CancellationToken.None);
        var r = new EditRecipe();
        r.Annotations.Add(new Annotation(AnnotationKind.BlackBox, 0, 0, 0.5, 0.5));
        var d = s.RenderPreview(r, PreviewStage.Full, true, CancellationToken.None);
        int i = (10 * d.Width + 10) * 4;
        Assert.Equal(0, d.Pixels[i] + d.Pixels[i + 1] + d.Pixels[i + 2]);
    }

    [Fact]
    public void ToneLutIsMonotonic()
    {
        var lut = ToneProcessor.BuildLut(new EditRecipe { Exposure = 0.7, Contrast = 40, Brightness = -20 });
        for (int i = 1; i < 256; i++) Assert.True(lut[i] >= lut[i - 1]);
        var id = ToneProcessor.BuildLut(new EditRecipe());
        for (int i = 0; i < 256; i++) Assert.InRange(id[i], i - 1, i + 1);
    }

    [Fact]
    public void SavesNewFileWithAnnotations()
    {
        var src = Make();
        using var s = Editor.Open(src, 400, CancellationToken.None);
        var r = new EditRecipe { Brightness = 20, Grayscale = true };
        r.Annotations.Add(new Annotation(AnnotationKind.Arrow, 0.1, 0.1, 0.5, 0.5));
        r.Annotations.Add(new Annotation(AnnotationKind.Rectangle, 0.2, 0.2, 0.6, 0.7, "#22D3EE"));
        r.Annotations.Add(new Annotation(AnnotationKind.Text, 0.1, 0.8, 0, 0, "#FFFFFF", "Merhaba Dünya ğüşıöç"));
        r.Annotations.Add(new Annotation(AnnotationKind.Highlight, 0.6, 0.1, 0.9, 0.2, "#FFD60A"));
        r.Annotations.Add(new Annotation(AnnotationKind.Pixelate, 0.6, 0.6, 0.9, 0.9));
        var res = s.Save(r, src, FormatRegistry.Jpeg, 90, true, CancellationToken.None);
        Assert.NotEqual(src, res.Output);
        Assert.True(File.Exists(res.Output));
        using var outImg = new MagickImage(res.Output!);
        Assert.Equal(800u, outImg.Width);
    }

    [Fact]
    public void DocumentFilterWhitensBackground()
    {
        int w = 200, h = 100;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 150; px[i + 3] = 255; }
        ToneProcessor.ApplyDocumentFilter(px, w, h, DocumentFilter.Gray);
        Assert.True(px[(50 * w + 100) * 4] > 220);
    }
}

public class StraightenTests
{
    [Fact]
    public void StraightenLeavesNoBlackCorners()
    {
        var p = Path.Combine(Path.GetTempPath(), "nexview-st-" + Guid.NewGuid().ToString("N") + ".png");
        using (var img = new MagickImage(MagickColors.White, 800, 600)) img.Write(p, MagickFormat.Png);
        using var s = new MagickEditor().Open(p, 800, CancellationToken.None);
        var d = s.RenderPreview(new EditRecipe { Straighten = 8 }, PreviewStage.Full, true, CancellationToken.None);
        foreach (var (x, y) in new[] { (0, 0), (d.Width - 1, 0), (0, d.Height - 1), (d.Width - 1, d.Height - 1) })
            Assert.True(d.Pixels[(y * d.Width + x) * 4] > 200, $"{x},{y}");
    }
}
