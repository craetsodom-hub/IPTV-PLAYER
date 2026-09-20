using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IptvPlayer.App.Controls;

public sealed class DirectVlcVideoHost : HwndHost
{
    private const int WindowStyleChild = 0x40000000;
    private const int WindowStyleVisible = 0x10000000;
    private const int WindowStyleClipChildren = 0x02000000;
    private const int WindowStyleClipSiblings = 0x04000000;
    private const string HostWindowClassName = "static";

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var handle = CreateWindowEx(
            0,
            HostWindowClassName,
            string.Empty,
            WindowStyleChild | WindowStyleVisible | WindowStyleClipChildren | WindowStyleClipSiblings,
            0,
            0,
            0,
            0,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        return new HandleRef(this, handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
        => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentWindow,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
