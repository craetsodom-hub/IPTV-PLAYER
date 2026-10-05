using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IptvPlayer.App.Services;

namespace IptvPlayer.App.Converters;

/// <summary>
/// Attached property helper that connects WPF Image controls to LogoCacheService.
/// Uses decoded logos from RAM and updates controls when background loading finishes.
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

    private static readonly object PendingGate = new();
    private static readonly Dictionary<string, List<WeakReference<Image>>> _pendingImages =
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
        if (string.IsNullOrWhiteSpace(newUri) || !Uri.TryCreate(newUri.Trim(), UriKind.Absolute, out var uri))
        {
            image.Source = null;
            return;
        }

        var normalizedUrl = uri.AbsoluteUri;
        image.Source = null;
        // Register before queuing work: a small disk image may finish immediately.
        RegisterPendingImage(normalizedUrl, image);
        var cached = LogoCacheService.Instance.GetCachedLogo(normalizedUrl);
        if (cached is not null)
        {
            image.Source = cached;
            OnLogoLoaded(normalizedUrl, cached);
        }
    }

    private static void RegisterPendingImage(string uri, Image image)
    {
        lock (PendingGate)
        {
            if (!_pendingImages.TryGetValue(uri, out var list)) _pendingImages[uri] = list = [];
            list.RemoveAll(wr => !wr.TryGetTarget(out Image? target) || ReferenceEquals(target, image));
            list.Add(new WeakReference<Image>(image));
        }
    }

    private static void OnLogoLoaded(string url, BitmapSource bitmap)
    {
        List<WeakReference<Image>> list;
        lock (PendingGate)
        {
            if (!_pendingImages.Remove(url, out list!)) return;
        }

        foreach (var weakRef in list)
        {
            if (weakRef.TryGetTarget(out var image))
            {
                image.Dispatcher.InvokeAsync(() =>
                {
                    var currentUri = GetSourceUri(image);
                    if (Uri.TryCreate(currentUri?.Trim(), UriKind.Absolute, out var current)
                        && string.Equals(current.AbsoluteUri, url, StringComparison.OrdinalIgnoreCase))
                    {
                        image.Source = bitmap;
                    }
                }, DispatcherPriority.Background);
            }
        }
    }
}
