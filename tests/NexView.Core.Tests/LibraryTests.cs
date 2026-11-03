using NexView.Core.Library;

namespace NexView.Core.Tests;

public class LibraryTests
{
    [Fact]
    public void NaturalSort()
    {
        var list = new List<string> { "img10.jpg", "img2.jpg", "img1.jpg", "Img3.jpg" };
        list.Sort(NaturalStringComparer.Instance);
        Assert.Equal(["img1.jpg", "img2.jpg", "Img3.jpg", "img10.jpg"], list);
    }
}

public class LocalizationTests
{
    [Fact]
    public void EnglishLoadsAndFallsBack()
    {
        Core.Localization.L.Initialize("en");
        try
        {
            Assert.Equal("Cancel", Core.Localization.L.T("İptal"));
            Assert.Equal("3 images", Core.Localization.L.T("{0} görüntü", 3));
            Assert.Equal("çevrilmemiş", Core.Localization.L.T("çevrilmemiş"));
        }
        finally { Core.Localization.L.Initialize("tr"); }
    }
}
