using System.Windows;
using System.Windows.Controls;

namespace IptvPlayer.App.Controls;

/// <summary>
/// Reserves a stable aspect-ratio box during WPF's first measure pass.
/// Unlike a Viewbox, this does not transform its child, so it is safe for HWND-backed video.
/// </summary>
public sealed class AspectRatioDecorator : Decorator
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio),
        typeof(double),
        typeof(AspectRatioDecorator),
        new FrameworkPropertyMetadata(
            16d / 9d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        IsValidAspectRatio);

    public static readonly DependencyProperty IsAspectRatioEnabledProperty = DependencyProperty.Register(
        nameof(IsAspectRatioEnabled),
        typeof(bool),
        typeof(AspectRatioDecorator),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    public bool IsAspectRatioEnabled
    {
        get => (bool)GetValue(IsAspectRatioEnabledProperty);
        set => SetValue(IsAspectRatioEnabledProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }

        if (!IsAspectRatioEnabled)
        {
            Child.Measure(constraint);
            return Child.DesiredSize;
        }

        var reservedSize = GetAspectSize(constraint, AspectRatio);
        Child.Measure(reservedSize);
        return reservedSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null)
        {
            return arrangeSize;
        }

        if (!IsAspectRatioEnabled)
        {
            Child.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }

        var contentSize = GetAspectSize(arrangeSize, AspectRatio);
        var horizontalOffset = Math.Max(0d, (arrangeSize.Width - contentSize.Width) / 2d);
        Child.Arrange(new Rect(new Point(horizontalOffset, 0d), contentSize));
        return arrangeSize;
    }

    private static Size GetAspectSize(Size availableSize, double aspectRatio)
    {
        var hasFiniteWidth = double.IsFinite(availableSize.Width);
        var hasFiniteHeight = double.IsFinite(availableSize.Height);

        if (!hasFiniteWidth && !hasFiniteHeight)
        {
            return default;
        }

        if (!hasFiniteWidth)
        {
            return new Size(availableSize.Height * aspectRatio, availableSize.Height);
        }

        var width = Math.Max(0d, availableSize.Width);
        var height = width / aspectRatio;
        if (hasFiniteHeight && height > availableSize.Height)
        {
            height = Math.Max(0d, availableSize.Height);
            width = height * aspectRatio;
        }

        return new Size(width, height);
    }

    private static bool IsValidAspectRatio(object value)
        => value is double ratio && double.IsFinite(ratio) && ratio > 0d;
}
