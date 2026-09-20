using System;
using System.Globalization;
using System.Windows.Data;
using IptvPlayer.App.Services;

namespace IptvPlayer.App.Converters;

/// <summary>
/// Value converter that retrieves cached BitmapSource from LogoCacheService.
/// </summary>
public sealed class CachedLogoConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string uri && !string.IsNullOrWhiteSpace(uri))
        {
            var decodeWidth = 80;
            if (parameter is int intWidth && intWidth > 0)
            {
                decodeWidth = intWidth;
            }
            return LogoCacheService.Instance.GetCachedLogo(uri, decodeWidth);
        }

        if (value is Uri directUri)
        {
            return LogoCacheService.Instance.GetCachedLogo(directUri.AbsoluteUri);
        }

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
