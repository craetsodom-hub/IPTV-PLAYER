using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace IptvPlayer.App.Views;

public partial class FullscreenHudWindow : Window
{
    public FullscreenHudWindow()
    {
        InitializeComponent();
        FlowDirection = System.Windows.FlowDirection.LeftToRight;
    }

    public event EventHandler? ActivityDetected;

    public event EventHandler? ExitRequested;

    public void SetChromeVisible(bool visible)
    {
        SetElementVisible(TopChrome, visible);
        SetElementVisible(BottomChrome, visible);
    }

    private static void SetElementVisible(UIElement element, bool visible)
    {
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = visible;

        var animation = new DoubleAnimation
        {
            To = visible ? 1d : 0d,
            Duration = visible ? TimeSpan.FromMilliseconds(180) : TimeSpan.FromMilliseconds(320),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        element.BeginAnimation(OpacityProperty, animation);
    }

    private void FullscreenHudWindow_OnMouseMove(object sender, MouseEventArgs e)
        => ActivityDetected?.Invoke(this, EventArgs.Empty);

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        ExitRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void VodTimelineSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled || slider.ActualWidth <= 0d)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindVisualParent<Thumb>(source) is not null)
        {
            return;
        }

        var ratio = GetTimelineSeekRatio(slider, e);
        slider.Value = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private static double GetTimelineSeekRatio(Slider slider, MouseButtonEventArgs e)
    {
        slider.ApplyTemplate();

        if (slider.Template.FindName("PART_Track", slider) is Track track && track.ActualWidth > 0d)
        {
            return Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0d, 1d);
        }

        return Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0d, 1d);
    }

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T typedParent)
            {
                return typedParent;
            }

            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }

        return null;
    }

    private void FullscreenHudWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
