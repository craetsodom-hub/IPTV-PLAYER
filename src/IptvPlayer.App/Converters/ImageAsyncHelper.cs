using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IptvPlayer.App.Services;

namespace IptvPlayer.App.Converters;

/// <summary>
/// Attached property helper that connects WPF Image controls to LogoCacheService.
/// Loads cached logos in 0ms from RAM or disk, and automatically updates the Image
/// without leaks when a background download finishes.
/// </summary>
public static class ImageAsyncHelper
{
    public static readonly DependencyProperty SourceUriProperty =
        DependencyProperty.RegisterAttached(
            "SourceUri",
            typeof(string),
            typeof(ImageAsyncHelper),
            new PropertyMetadata(null, OnSourceUriChanged));

    public static string? GetSourceUri(Image target) => (string?)target.GetValue(SourceUriProperty);
    public static void SetSourceUri(Image target, string? value) => target.SetValue(SourceUriProperty, value);

    private static readonly ConcurrentDictionary<string, List<WeakReference<Image>>> _pendingImages =
        new(StringComparer.OrdinalIgnoreCase);

    static ImageAsyncHelper()
    {
        LogoCacheService.Instance.LogoLoaded += OnLogoLoaded;
    }

    private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
        {
            return;
        }

        var newUri = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(newUri))
        {
            image.Source = null;
            return;
        }

        var cached = LogoCacheService.Instance.GetCachedLogo(newUri);
        if (cached is not null)
        {
            image.Source = cached;
            return;
        }

        image.Source = null;
        RegisterPendingImage(newUri.Trim(), image);
    }

    private static void RegisterPendingImage(string uri, Image image)
    {
        _pendingImages.AddOrUpdate(
            uri,
            _ => [new WeakReference<Image>(image)],
            (_, list) =>
            {
                lock (list)
                {
                    // Clean up dead references while adding new one
                    list.RemoveAll(wr => !wr.TryGetTarget(out Image? _));
                    list.Add(new WeakReference<Image>(image));
                }
                return list;
            });
    }

    private static void OnLogoLoaded(string url, BitmapSource bitmap)
    {
        if (!_pendingImages.TryRemove(url, out var list))
        {
            return;
        }

        lock (list)
        {
            foreach (var weakRef in list)
            {
                if (weakRef.TryGetTarget(out var image))
                {
                    image.Dispatcher.InvokeAsync(() =>
                    {
                        var currentUri = GetSourceUri(image);
                        if (string.Equals(currentUri, url, StringComparison.OrdinalIgnoreCase))
                        {
                            image.Source = bitmap;
                        }
                    }, DispatcherPriority.Background);
                }
            }
        }
    }
}
