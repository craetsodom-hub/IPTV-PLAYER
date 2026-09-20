using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace IptvPlayer.App.Views;

public partial class PlayerControlsOverlayWindow : Window
{
    private const int WindowLongExtendedStyle = -20;
    private const long WindowExtendedStyleTransparent = 0x00000020L;
    private const long WindowExtendedStyleToolWindow = 0x00000080L;
    private const long WindowExtendedStyleNoActivate = 0x08000000L;
    private bool _controlsVisible;

    public PlayerControlsOverlayWindow()
    {
        InitializeComponent();
        FlowDirection = FlowDirection.LeftToRight;
        SourceInitialized += PlayerControlsOverlayWindow_OnSourceInitialized;
    }

    public event EventHandler? ActivityDetected;

    public event EventHandler? ScreenshotRequested;

    public event EventHandler? RecordRequested;

    public event EventHandler? DisplayModeRequested;

    public event EventHandler? FullscreenRequested;

    public bool IsPointerOverControls => ControlsBar.IsMouseOver || ControlsBar.IsMouseCaptureWithin;

    public void SetRecordingState(bool isRecording)
        => ControlsBar.IsRecording = isRecording;

    public void SetDisplayModeState(bool isStretch)
        => ControlsBar.IsStretchDisplayMode = isStretch;

    public void SetControlsVisible(bool visible, bool immediate = false)
    {
        if (_controlsVisible == visible && !immediate)
        {
            return;
        }

        _controlsVisible = visible;
        ControlsBar.IsHitTestVisible = visible;
        UpdateClickThroughState();

        ControlsBar.BeginAnimation(OpacityProperty, null);
        if (immediate)
        {
            ControlsBar.Opacity = visible ? 1d : 0d;
            return;
        }

        ControlsBar.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = visible ? 1d : 0d,
                Duration = visible ? TimeSpan.FromMilliseconds(140) : TimeSpan.FromMilliseconds(260),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void PlayerControlsOverlayWindow_OnSourceInitialized(object? sender, EventArgs e)
        => UpdateClickThroughState();

    private void UpdateClickThroughState()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, WindowLongExtendedStyle).ToInt64();
        style |= WindowExtendedStyleToolWindow | WindowExtendedStyleNoActivate;
        style = _controlsVisible
            ? style & ~WindowExtendedStyleTransparent
            : style | WindowExtendedStyleTransparent;
        _ = SetWindowLongPtr(handle, WindowLongExtendedStyle, new IntPtr(style));
    }

    private void ControlsBar_OnActivityDetected(object? sender, EventArgs e)
        => ActivityDetected?.Invoke(this, EventArgs.Empty);

    private void ControlsBar_OnScreenshotRequested(object? sender, EventArgs e)
        => ScreenshotRequested?.Invoke(this, EventArgs.Empty);

    private void ControlsBar_OnRecordRequested(object? sender, EventArgs e)
        => RecordRequested?.Invoke(this, EventArgs.Empty);

    private void ControlsBar_OnDisplayModeRequested(object? sender, EventArgs e)
        => DisplayModeRequested?.Invoke(this, EventArgs.Empty);

    private void ControlsBar_OnFullscreenRequested(object? sender, EventArgs e)
        => FullscreenRequested?.Invoke(this, EventArgs.Empty);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, newValue)
            : new IntPtr(SetWindowLong32(window, index, newValue.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int newValue);
}
