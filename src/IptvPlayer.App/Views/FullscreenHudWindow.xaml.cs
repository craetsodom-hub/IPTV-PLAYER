using System.Windows;
using System.Windows.Input;

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

    public event EventHandler? ScreenshotRequested;

    public event EventHandler? RecordRequested;

    public event EventHandler? DisplayModeRequested;

    public bool IsPointerOverControls
        => FullscreenPlayerControlBar.IsMouseOver || FullscreenPlayerControlBar.IsMouseCaptureWithin;

    public void SetChromeVisible(bool visible)
    {
        SetElementVisible(BottomChrome, visible);
        Cursor = visible ? Cursors.Arrow : Cursors.None;
        ForceCursor = true;
    }

    private static void SetElementVisible(UIElement element, bool visible)
    {
        element.IsHitTestVisible = visible;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = visible ? 1d : 0d;
    }

    private void FullscreenHudWindow_OnMouseMove(object sender, MouseEventArgs e)
        => ActivityDetected?.Invoke(this, EventArgs.Empty);

    public void SetRecordingState(bool isRecording)
        => FullscreenPlayerControlBar.IsRecording = isRecording;

    public void SetDisplayModeState(bool isStretch)
        => FullscreenPlayerControlBar.IsStretchDisplayMode = isStretch;

    private void PlayerControlBar_OnActivityDetected(object? sender, EventArgs e)
        => ActivityDetected?.Invoke(this, EventArgs.Empty);

    private void PlayerControlBar_OnScreenshotRequested(object? sender, EventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        ScreenshotRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PlayerControlBar_OnRecordRequested(object? sender, EventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        RecordRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PlayerControlBar_OnDisplayModeRequested(object? sender, EventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        DisplayModeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PlayerControlBar_OnFullscreenRequested(object? sender, EventArgs e)
    {
        ActivityDetected?.Invoke(this, EventArgs.Empty);
        ExitRequested?.Invoke(this, EventArgs.Empty);
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
