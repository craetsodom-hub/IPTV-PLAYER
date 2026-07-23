using System.Windows.Data;
using System.Windows.Markup;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.App.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = UiLocalization.Current,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
