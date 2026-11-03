using Avalonia.Data.Converters;

namespace NexView.UI.Controls;

public static class Converters
{
    /// <summary>Pencere yüksekliğinden kenar payı düşer (iletişim kutuları için en fazla yükseklik).</summary>
    public static readonly IValueConverter MinusMargin = new FuncValueConverter<double, double>(h => Math.Max(200, h - 80));
}
