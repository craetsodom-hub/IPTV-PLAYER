using System.Windows;

namespace IptvPlayer.App.Controls;

/// <summary>The startup card's original gradient sweep, using the shared animation dispatcher.</summary>
public sealed class SmoothLoadingBar : SmoothLoadingIndicator
{
    protected override bool IsBar => true;
    protected override Size MeasureOverride(Size availableSize)
        => new(Math.Min(316, availableSize.Width), Math.Min(4, availableSize.Height));
}
