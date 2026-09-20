using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IptvPlayer.App.Views;

public partial class PlayerActionIndicatorWindow : Window
{
    private const int WindowLongExtendedStyle = -20;
    private const long WindowExtendedStyleToolWindow = 0x00000080L;
    private const long WindowExtendedStyleNoActivate = 0x08000000L;

    public PlayerActionIndicatorWindow()
    {
        InitializeComponent();
        FlowDirection = FlowDirection.LeftToRight;
        SourceInitialized += PlayerActionIndicatorWindow_OnSourceInitialized;
    }

    private void PlayerActionIndicatorWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, WindowLongExtendedStyle).ToInt64();
        style |= WindowExtendedStyleToolWindow | WindowExtendedStyleNoActivate;
        _ = SetWindowLongPtr(handle, WindowLongExtendedStyle, new IntPtr(style));
    }

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
