using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;

namespace IptvPlayer.App.Controls;

public sealed class DirectVlcVideoHost : HwndHost
{
    private const int WindowStyleChild = 0x40000000;
    private const int WindowStyleVisible = 0x10000000;
    private const int WindowStyleClipChildren = 0x02000000;
    private const int WindowStyleClipSiblings = 0x04000000;
    private const string HostWindowClassName = "static";
    private Rect? _lastPosition;
    private Rect? _pendingPosition;
    private int _positionDeferrals;
#if PLAYBACK_DIAGNOSTICS
    private WorkerWindow? _workerWindow;
    private Task _diagnosticLastWorkerStopped = Task.CompletedTask;
    public long AppliedPositionUpdates { get; private set; }
    public long DuplicatePositionUpdatesSkipped { get; private set; }
    private long _nativeSizeMessages;
    private long _nativePositionMessages;
    public long NativeSizeMessages => _workerWindow?.NativeSizeMessages ?? _nativeSizeMessages;
    public long NativePositionMessages => _workerWindow?.NativePositionMessages ?? _nativePositionMessages;
    public long PostedPositionUpdatesApplied => _workerWindow?.PositionUpdatesApplied ?? 0;
    public long PostedPositionGeneration => _workerWindow?.PositionGeneration ?? 0;
    public long PostedPositionGenerationApplied => _workerWindow?.PositionGenerationApplied ?? 0;
    public int PostedPositionError => _workerWindow?.PositionError ?? 0;
    internal Task DiagnosticWorkerStopped => _workerWindow?.Stopped ?? _diagnosticLastWorkerStopped;

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0005 /* WM_SIZE */) _nativeSizeMessages++;
        if (msg == 0x0047 /* WM_WINDOWPOSCHANGED */) _nativePositionMessages++;
        // Isolate HwndHost's bounds rewrite while retaining the same owner,
        // input hooks, embedded child, and positions requested by WPF layout.
        if (msg == 0x0046 /* WM_WINDOWPOSCHANGING */
            && Environment.GetCommandLineArgs().Contains("--native-host-no-position-override", StringComparer.Ordinal))
            return IntPtr.Zero;
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }
#endif

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _lastPosition = null;
#if PLAYBACK_DIAGNOSTICS
        var postedWorker = Environment.GetCommandLineArgs().Contains("--native-host-worker-posted", StringComparer.Ordinal);
        if (postedWorker || Environment.GetCommandLineArgs().Contains("--native-host-worker", StringComparer.Ordinal))
        {
            _workerWindow = new WorkerWindow(hwndParent.Handle, postedWorker);
            _diagnosticLastWorkerStopped = _workerWindow.Stopped;
            return new HandleRef(this, _workerWindow.Handle);
        }
#endif
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

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        if (_positionDeferrals > 0)
        {
            _pendingPosition = rcBoundingBox;
            return;
        }
        // HwndHost sends SetWindowPos on every layout update, including identical
        // bounds. Track the last request rather than querying an asynchronous move.
        if (_lastPosition == rcBoundingBox)
        {
#if PLAYBACK_DIAGNOSTICS
            DuplicatePositionUpdatesSkipped++;
#endif
            return;
        }
#if PLAYBACK_DIAGNOSTICS
        AppliedPositionUpdates++;
        if (_workerWindow is { UsesPostedPositioning: true } worker)
        {
            worker.PostPosition(rcBoundingBox);
            _lastPosition = rcBoundingBox;
            return;
        }
#endif
        base.OnWindowPositionChanged(rcBoundingBox);
        _lastPosition = rcBoundingBox;
    }

    internal IDisposable DeferPositionUpdates()
    {
        VerifyAccess();
        _positionDeferrals++;
        return new PositionUpdateScope(this);
    }

    private sealed class PositionUpdateScope(DirectVlcVideoHost owner) : IDisposable
    {
        private DirectVlcVideoHost? _owner = owner;
        public void Dispose()
        {
            var host = _owner;
            if (host is null) return;
            host.VerifyAccess();
            _owner = null;
            if (--host._positionDeferrals == 0 && host._pendingPosition is { } position)
            {
                host._pendingPosition = null;
                host.OnWindowPositionChanged(position);
            }
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _lastPosition = null;
        _pendingPosition = null;
#if PLAYBACK_DIAGNOSTICS
        if (_workerWindow is { } worker)
        {
            _workerWindow = null;
            worker.Dispose();
            return;
        }
#endif
        DestroyWindow(hwnd.Handle);
    }

#if PLAYBACK_DIAGNOSTICS
    // Experimental only. This is still a WS_CHILD of the app's HWND, never a
    // standalone player. Its queue lets us test dependency on WPF window work.
    private sealed class WorkerWindow : IDisposable
    {
        private const uint ApplyPositionMessage = 0x8001;
        private readonly TaskCompletionSource<IntPtr> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _positionGate = new();
        private readonly WindowProcedure _windowProcedure;
        private readonly string _windowClassName = "WhoseIptvEmbeddedPlaybackDiagnostic." + Guid.NewGuid().ToString("N");
        private PositionRequest? _pendingPosition;
        private bool _positionMessagePosted;
        private IntPtr _handle;
        private uint _threadId;
        private int _closing;
        private long _nativeSizeMessages;
        private long _nativePositionMessages;
        private long _positionUpdatesApplied;
        private long _positionGeneration;
        private long _positionGenerationApplied;
        private int _positionError;
        public IntPtr Handle { get; }
        public bool UsesPostedPositioning { get; }
        public Task Stopped => _stopped.Task;
        public long NativeSizeMessages => Interlocked.Read(ref _nativeSizeMessages);
        public long NativePositionMessages => Interlocked.Read(ref _nativePositionMessages);
        public long PositionUpdatesApplied => Interlocked.Read(ref _positionUpdatesApplied);
        public long PositionGeneration => Interlocked.Read(ref _positionGeneration);
        public long PositionGenerationApplied => Interlocked.Read(ref _positionGenerationApplied);
        public int PositionError => Volatile.Read(ref _positionError);

        public WorkerWindow(IntPtr parent, bool postedPositioning)
        {
            UsesPostedPositioning = postedPositioning;
            _windowProcedure = OnWindowMessage;
            var thread = new Thread(() => Run(parent)) { IsBackground = true, Name = "Embedded video host test" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!_created.Task.Wait(TimeSpan.FromSeconds(3)))
            {
                Dispose();
                throw new InvalidOperationException("Embedded worker host creation timed out");
            }
            Handle = _created.Task.GetAwaiter().GetResult();
            if (Handle == IntPtr.Zero)
            {
                Dispose();
                throw new InvalidOperationException("Embedded worker host creation failed");
            }
        }

        public void PostPosition(Rect position)
        {
            lock (_positionGate)
            {
                if (Volatile.Read(ref _closing) != 0) return;
                var generation = Interlocked.Increment(ref _positionGeneration);
                // Match the physical-pixel integer conversion in HwndHost.
                _pendingPosition = new((int)position.X, (int)position.Y,
                    (int)position.Width, (int)position.Height, generation);
                if (_positionMessagePosted) return;
                _positionMessagePosted = true;
                if (!PostMessage(Handle, ApplyPositionMessage, IntPtr.Zero, IntPtr.Zero))
                {
                    _positionMessagePosted = false;
                    Volatile.Write(ref _positionError, Marshal.GetLastWin32Error());
                    throw new InvalidOperationException("Embedded worker position request could not be posted");
                }
            }
        }

        private void Run(IntPtr parent)
        {
            IntPtr handle = IntPtr.Zero;
            IntPtr instance = IntPtr.Zero;
            var classRegistered = false;
            try
            {
                // Ensure the queue exists before publishing its ID for shutdown.
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                Volatile.Write(ref _threadId, GetCurrentThreadId());
                var dpi = GetWindowDpiAwarenessContext(parent);
                if (dpi != IntPtr.Zero) SetThreadDpiAwarenessContext(dpi);
                var windowClassName = HostWindowClassName;
                if (UsesPostedPositioning)
                {
                    instance = GetModuleHandle(null);
                    var windowClass = new NativeWindowClass
                    {
                        WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                        Instance = instance,
                        ClassName = _windowClassName,
                    };
                    if (RegisterClass(ref windowClass) == 0)
                        throw new InvalidOperationException("Embedded worker host class registration failed");
                    classRegistered = true;
                    windowClassName = _windowClassName;
                }
                handle = CreateWindowEx(0x00000004 /* WS_EX_NOPARENTNOTIFY */, windowClassName,
                    string.Empty, WindowStyleChild | WindowStyleVisible | WindowStyleClipChildren | WindowStyleClipSiblings,
                    0, 0, 0, 0, parent, IntPtr.Zero, instance, IntPtr.Zero);
                Interlocked.Exchange(ref _handle, handle);
                _created.TrySetResult(handle);
                if (handle == IntPtr.Zero) return;
                while (Volatile.Read(ref _closing) == 0 && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch { _created.TrySetResult(IntPtr.Zero); }
            finally
            {
                if (handle != IntPtr.Zero && IsWindow(handle)) DestroyWindow(handle);
                Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (classRegistered) UnregisterClass(_windowClassName, instance);
                GC.KeepAlive(_windowProcedure);
                _stopped.TrySetResult();
            }
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (message == 0x0005 /* WM_SIZE */) Interlocked.Increment(ref _nativeSizeMessages);
                if (message == 0x0047 /* WM_WINDOWPOSCHANGED */) Interlocked.Increment(ref _nativePositionMessages);
                if (message == ApplyPositionMessage)
                {
                    PositionRequest? position;
                    lock (_positionGate)
                    {
                        position = _pendingPosition;
                        _pendingPosition = null;
                        _positionMessagePosted = false;
                    }
                    if (Volatile.Read(ref _closing) == 0 && position is { } requested)
                    {
                        // Execute on the owner thread; do not send a resize from WPF.
                        // Keep HwndHost's flags except its unnecessary ASYNC flag.
                        if (SetWindowPos(hwnd, IntPtr.Zero, requested.X, requested.Y,
                            requested.Width, requested.Height, 0x0004 | 0x0100 | 0x0010))
                        {
                            Interlocked.Increment(ref _positionUpdatesApplied);
                            Interlocked.Exchange(ref _positionGenerationApplied, requested.Generation);
                        }
                        else Volatile.Write(ref _positionError, Marshal.GetLastWin32Error());
                    }
                    return IntPtr.Zero;
                }
                if (message == 0x0002 /* WM_DESTROY */)
                {
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                }
            }
            catch { /* Exceptions must not cross the native window procedure. */ }
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0) return;
            lock (_positionGate) _pendingPosition = null;
            var handle = Interlocked.CompareExchange(ref _handle, IntPtr.Zero, IntPtr.Zero);
            if (UsesPostedPositioning && handle != IntPtr.Zero)
                PostMessage(handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            var threadId = Volatile.Read(ref _threadId);
            if (threadId != 0) PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            // Never join from WPF: the owner thread destroys its window in finally.
        }

        private sealed record PositionRequest(int X, int Y, int Width, int Height, long Generation);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeWindowClass
        {
            public uint Style;
            public IntPtr WindowProcedure;
            public int ClassExtraBytes;
            public int WindowExtraBytes;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref NativeMessage message);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref NativeWindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
#endif

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
