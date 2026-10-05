#if PLAYBACK_DIAGNOSTICS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace IptvPlayer.Player.Vlc.Services;

// Captures native Prepare completion intervals, not physical presentation times.
// LibVLCSharp's managed Log event uses Task.Run and cannot measure these intervals.
internal sealed class NativeRenderTimingProbe : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly LogCallback _callback;
    private readonly ConcurrentDictionary<uint, long> _previous = new();
    private readonly ConcurrentQueue<(long Tick, double Milliseconds)> _intervals = new();
    private readonly ConcurrentQueue<NativeTimingEvent> _events = new();

    internal sealed record NativeTimingEvent(long Tick, string Module, string Kind);

    internal NativeRenderTimingProbe(LibVLC libVlc)
    {
        _libVlc = libVlc;
        _callback = OnLog;
        libvlc_log_set(libVlc.NativeReference, _callback, IntPtr.Zero);
    }

    private void OnLog(IntPtr opaque, int level, IntPtr context, IntPtr format, IntPtr arguments)
    {
        try
        {
            // Inspect only the engine's fixed format template. Never dereference
            // va_list, format provider data, change scheduling, or do file I/O here.
            var template = Marshal.PtrToStringUTF8(format);
            if (template is null) return;
            string? kind = template.StartsWith("picture is too late to be displayed", StringComparison.Ordinal) ? "late-picture-drop"
                : template.Contains("buffer deadlock prevented", StringComparison.Ordinal) ? "decoder-buffer-wait"
                : template.Contains("pictures leaked", StringComparison.Ordinal) ? "picture-leak"
                : template.Contains("PCR", StringComparison.Ordinal) && (template.Contains("late", StringComparison.Ordinal) || template.Contains("reset", StringComparison.Ordinal)) ? "input-clock"
                : template.Contains("SwapChain", StringComparison.Ordinal) || template.Contains("ResizeBuffers", StringComparison.Ordinal) ? "swap-chain"
                : null;
            if (kind is not null)
            {
                libvlc_log_get_context(context, out var eventModule, out _, out _);
                _events.Enqueue(new(Stopwatch.GetTimestamp(), Marshal.PtrToStringUTF8(eventModule) ?? "", kind));
                if (_events.Count > 2048) _events.TryDequeue(out _);
            }
            if (!template.StartsWith("waited ", StringComparison.Ordinal)
                || !template.EndsWith(" ms for the render fence", StringComparison.Ordinal)) return;
            libvlc_log_get_context(context, out var module, out _, out _);
            if (Marshal.PtrToStringUTF8(module) != "direct3d11") return;
            var tick = Stopwatch.GetTimestamp();
            var thread = GetCurrentThreadId();
            if (_previous.TryGetValue(thread, out var previous))
            {
                _intervals.Enqueue((previous, Stopwatch.GetElapsedTime(previous, tick).TotalMilliseconds));
                if (_intervals.Count > 60_000) _intervals.TryDequeue(out _);
            }
            _previous[thread] = tick;
        }
        catch { /* Exceptions cannot escape an unmanaged callback. */ }
    }

    internal double[] ReadIntervals(long sinceTick)
        => _intervals.Where(item => item.Tick >= sinceTick).Select(item => item.Milliseconds).ToArray();

    internal NativeTimingEvent[] ReadEvents(long sinceTick)
        => _events.Where(item => item.Tick >= sinceTick).ToArray();

    public void Dispose()
    {
        libvlc_log_unset(_libVlc.NativeReference);
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(IntPtr opaque, int level, IntPtr context, IntPtr format, IntPtr arguments);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_log_set(IntPtr instance, LogCallback callback, IntPtr opaque);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_log_unset(IntPtr instance);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_log_get_context(IntPtr context, out IntPtr module, out IntPtr file, out uint line);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
#endif
