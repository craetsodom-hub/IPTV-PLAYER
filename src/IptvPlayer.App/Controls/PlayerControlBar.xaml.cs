using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.App.Controls;

public partial class PlayerControlBar : UserControl
{
    private Slider? _activeSlider;

    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.Register(
        nameof(IsRecording),
        typeof(bool),
        typeof(PlayerControlBar),
        new PropertyMetadata(false, OnPresentationStateChanged));

    public static readonly DependencyProperty IsFullscreenProperty = DependencyProperty.Register(
        nameof(IsFullscreen),
        typeof(bool),
        typeof(PlayerControlBar),
        new PropertyMetadata(false, OnPresentationStateChanged));

    public static readonly DependencyProperty IsStretchDisplayModeProperty = DependencyProperty.Register(
        nameof(IsStretchDisplayMode),
        typeof(bool),
        typeof(PlayerControlBar),
        new PropertyMetadata(false, OnPresentationStateChanged));

    public static readonly DependencyProperty RecordingToolTipProperty = DependencyProperty.Register(
        nameof(RecordingToolTip),
        typeof(string),
        typeof(PlayerControlBar),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FullscreenToolTipProperty = DependencyProperty.Register(
        nameof(FullscreenToolTip),
        typeof(string),
        typeof(PlayerControlBar),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FullscreenIconGlyphProperty = DependencyProperty.Register(
        nameof(FullscreenIconGlyph),
        typeof(string),
        typeof(PlayerControlBar),
        new PropertyMetadata("\uE740"));

    public static readonly DependencyProperty DisplayModeToolTipProperty = DependencyProperty.Register(
        nameof(DisplayModeToolTip),
        typeof(string),
        typeof(PlayerControlBar),
        new PropertyMetadata(string.Empty));

    public PlayerControlBar()
    {
        InitializeComponent();
        FlowDirection = FlowDirection.LeftToRight;
        Loaded += PlayerControlBar_OnLoaded;
        Unloaded += PlayerControlBar_OnUnloaded;
        UpdateLocalizedPresentation();
    }

    public event EventHandler? ActivityDetected;

    public event EventHandler? ScreenshotRequested;

    public event EventHandler? RecordRequested;

    public event EventHandler? DisplayModeRequested;

    public event EventHandler? FullscreenRequested;

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    public bool IsFullscreen
    {
        get => (bool)GetValue(IsFullscreenProperty);
        set => SetValue(IsFullscreenProperty, value);
    }

    public bool IsStretchDisplayMode
    {
        get => (bool)GetValue(IsStretchDisplayModeProperty);
        set => SetValue(IsStretchDisplayModeProperty, value);
    }

    public string RecordingToolTip
    {
        get => (string)GetValue(RecordingToolTipProperty);
        private set => SetValue(RecordingToolTipProperty, value);
    }

    public string FullscreenToolTip
    {
        get => (string)GetValue(FullscreenToolTipProperty);
        private set => SetValue(FullscreenToolTipProperty, value);
    }

    public string FullscreenIconGlyph
    {
        get => (string)GetValue(FullscreenIconGlyphProperty);
        private set => SetValue(FullscreenIconGlyphProperty, value);
    }

    public string DisplayModeToolTip
    {
        get => (string)GetValue(DisplayModeToolTipProperty);
        private set => SetValue(DisplayModeToolTipProperty, value);
    }

    private static void OnPresentationStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not PlayerControlBar controlBar)
        {
            return;
        }

        controlBar.UpdateLocalizedPresentation();
        controlBar.TimelineControlsGapRow.Height = new GridLength(controlBar.IsFullscreen ? 6d : 0d);
        controlBar.FullscreenIconGlyph = controlBar.IsFullscreen ? "\uE73F" : "\uE740";
    }

    private void PlayerControlBar_OnLoaded(object sender, RoutedEventArgs e)
    {
        UiLocalization.Current.CultureChanged -= Localization_OnCultureChanged;
        UiLocalization.Current.CultureChanged += Localization_OnCultureChanged;
        UpdateLocalizedPresentation();
    }

    private void PlayerControlBar_OnUnloaded(object sender, RoutedEventArgs e)
        => UiLocalization.Current.CultureChanged -= Localization_OnCultureChanged;

    private void Localization_OnCultureChanged(object? sender, EventArgs e)
        => UpdateLocalizedPresentation();

    private void UpdateLocalizedPresentation()
    {
        var localization = UiLocalization.Current;
        RecordingToolTip = localization.GetString(IsRecording ? "StopRecording" : "Record");
        DisplayModeToolTip = localization.GetString(IsStretchDisplayMode ? "DisplayStretch" : "DisplayFit");
        FullscreenToolTip = localization.GetString(IsFullscreen ? "ExitFullscreen" : "Fullscreen");
    }

    private void PlayerControlBar_OnMouseActivity(object sender, MouseEventArgs e)
        => ActivityDetected?.Invoke(this, EventArgs.Empty);

    private void PlayerControlBar_OnPointerInteraction(object sender, MouseButtonEventArgs e)
        => ActivityDetected?.Invoke(this, EventArgs.Empty);

    private void InteractiveSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        _activeSlider = slider;
        _ = slider.CaptureMouse();
        UpdateSliderValueFromPointer(slider, e);
        e.Handled = true;
    }

    private void InteractiveSlider_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider || !ReferenceEquals(slider, _activeSlider))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSliderInteraction(slider);
            return;
        }

        UpdateSliderValueFromPointer(slider, e);
        e.Handled = true;
    }

    private void InteractiveSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !ReferenceEquals(slider, _activeSlider))
        {
            return;
        }

        UpdateSliderValueFromPointer(slider, e);
        EndSliderInteraction(slider);
        e.Handled = true;
    }

    private void InteractiveSlider_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _activeSlider))
        {
            _activeSlider = null;
        }
    }

    private static void UpdateSliderValueFromPointer(Slider slider, MouseEventArgs e)
    {
        slider.ApplyTemplate();
        if (slider.Template.FindName("PART_Track", slider) is not Track track ||
            track.ActualWidth <= 0d)
        {
            return;
        }

        var thumbWidth = track.Thumb?.ActualWidth ?? 0d;
        var availableWidth = track.ActualWidth - thumbWidth;
        if (availableWidth <= 0d)
        {
            return;
        }

        var pointerX = e.GetPosition(track).X;
        var ratio = Math.Clamp((pointerX - (thumbWidth / 2d)) / availableWidth, 0d, 1d);
        if (track.IsDirectionReversed)
        {
            ratio = 1d - ratio;
        }

        slider.Value = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
    }

    private void EndSliderInteraction(Slider slider)
    {
        _activeSlider = null;
        if (slider.IsMouseCaptured)
        {
            slider.ReleaseMouseCapture();
        }
    }

    private void ScreenshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        ScreenshotRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void RecordButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        RecordRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void DisplayModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        DisplayModeRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void FullscreenButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
