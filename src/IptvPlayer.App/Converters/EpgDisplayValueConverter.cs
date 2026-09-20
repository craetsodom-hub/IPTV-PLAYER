using System.Globalization;
using System.Windows.Data;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.App.Converters;

public sealed class EpgDisplayValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fallbackResourceKey = parameter?.ToString();
        var fallback = string.IsNullOrWhiteSpace(fallbackResourceKey)
            ? string.Empty
            : UiLocalization.Current.GetString(fallbackResourceKey);
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var visibleCharacters = text
            .Where(character => !char.IsControl(character)
                                && char.GetUnicodeCategory(character) != UnicodeCategory.Format)
            .ToArray();
        var visibleText = new string(visibleCharacters).Trim();
        return string.IsNullOrWhiteSpace(visibleText) ? fallback : visibleText;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
