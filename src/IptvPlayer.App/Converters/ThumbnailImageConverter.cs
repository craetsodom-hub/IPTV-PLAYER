using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace IptvPlayer.App.Converters;

/// <summary>
/// Decodes remote/local image thumbnails with explicit decode dimensions to prevent
/// full-resolution bitmaps from consuming excessive memory in the UI.
/// </summary>
public sealed class ThumbnailImageConverter : IValueConverter
{
    private const int DefaultDecodePixelWidth = 80;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return null;
        }

        Uri? uri = null;
        if (value is Uri directUri)
        {
            uri = directUri;
        }
        else if (value is string uriString && !string.IsNullOrWhiteSpace(uriString))
        {
            if (!Uri.TryCreate(uriString.Trim(), UriKind.Absolute, out uri))
            {
                return null;
            }
        }

        if (uri is null)
        {
            return null;
        }

        var decodeWidth = DefaultDecodePixelWidth;
        if (parameter is int intWidth && intWidth > 0)
        {
            decodeWidth = intWidth;
        }
        else if (parameter is string paramStr && int.TryParse(paramStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth) && parsedWidth > 0)
        {
            decodeWidth = parsedWidth;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.CacheOption = BitmapCacheOption.OnDemand;
            bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
            bitmap.EndInit();

            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
