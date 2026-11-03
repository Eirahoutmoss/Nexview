using Avalonia.Markup.Xaml;
using NexView.Core.Localization;

namespace NexView.UI.Localization;

/// <summary>XAML çeviri uzantısı: Text="{l:T 'Tüm Görüntüler'}"</summary>
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
