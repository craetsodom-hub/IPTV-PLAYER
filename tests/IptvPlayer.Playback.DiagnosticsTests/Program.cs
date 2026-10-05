using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using IptvPlayer.Player.Vlc.Services;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

if (args.FirstOrDefault() == "--benchmark") return PlaybackBenchmark.Run(args);
if (args.FirstOrDefault() == "--live") return PlaybackBenchmark.Run(args);
if (args.FirstOrDefault() == "--capture") return await PlaybackBenchmark.CaptureAsync(args);
#if PLAYBACK_DIAGNOSTICS
if (args.FirstOrDefault() == "--state-store") return await StateStoreTests.RunAsync();
if (args.FirstOrDefault() == "--logo-cache") return await LogoCacheTests.RunAsync();
#endif

var failures = new List<string>();
void Check(bool condition, string description) { if (!condition) failures.Add(description); }
var cases = new (string Message, string Module, string Kind, int? Milliseconds)[]
{
    ("picture is too late to be displayed (missing 38 ms)", "main", "late-picture", 38),
    ("picture is too late to be displayed (missing 9999999999999 ms)", "main", "late-picture", null),
    ("waited 17 ms for the render fence", "direct3d11", "render-fence-wait", 17),
    ("SwapChain Present failed", "direct3d11", "present-failure", null),
    ("Using D3D11VA for hardware decoding", "avcodec", "hardware-decoder-selected", null),
    ("using x deinterlacing mode", "d3d11_filters", "deinterlace-mode-x", null),
    ("using yadif2x deinterlacing mode", "d3d11_filters", "deinterlace-mode-yadif2x", null),
    ("mode auto not available, trying bob", "d3d11_filters", "deinterlace-fallback-bob", null),
    ("using vout display module \"direct3d11\"", "main", "module-selected", null),
    ("PCR reset", "main", "clock-discontinuity", null),
    ("trying to reconnect https://secret.example/user/password?token=PRIVATE", "private-module", "reconnect-attempt", null),
    ("corrupt frame https://secret.example/user/password", "avcodec", "decoder-failure-or-corruption", null)
};
foreach (var item in cases)
{
    var result = NativePlaybackDiagnosticClassifier.Classify(item.Message, item.Module, 42);
    Check(result.HasValue && result.Value.Kind == item.Kind && result.Value.Milliseconds == item.Milliseconds
          && result.Value.SessionId == 42, "Incorrect event classification: " + item.Kind);
    var serialized = result?.ToString() ?? string.Empty;
    Check(!serialized.Contains("secret") && !serialized.Contains("password") && !serialized.Contains("PRIVATE")
          && !serialized.Contains("private-module"), "Sensitive native text escaped the classifier");
}
Check(NativePlaybackDiagnosticClassifier.Classify("https://secret.example/token=PRIVATE", "main", 1) is null,
    "Unrecognized native text must be discarded");
Check(NativePlaybackDiagnosticClassifier.Classify("waited 1 ms for the render fence", "direct3d11", 1) is null,
    "Trivial fence waits must not flood diagnostics");
Check(NativePlaybackDiagnosticClassifier.Classify("allowing 6 thread(s) for decoding", "avcodec", 1)?.DecoderThreads == 6,
    "Native decoder thread count was not captured");

var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["PlaybackDiagnostics:ProbeEnabled"] = "true", ["PlaybackDiagnostics:Enabled"] = "true",
    ["PlaybackDiagnostics:NativeVerbosity"] = "2", ["PlaybackDiagnostics:StatsIntervalMs"] = "100",
    ["PlaybackDiagnostics:LogSamples"] = "true"
}).Build();
var logger = new CaptureLogger();
await using (var probe = new PlaybackDiagnosticsProbe(logger, settings))
{
#if PLAYBACK_DIAGNOSTICS
    Check(probe.Enabled, "Test build must enable its probe");
#elif !DEBUG
    Check(!probe.Enabled, "Normal Release must keep the probe disabled even if config is true");
#endif
    var options = probe.BuildLibVlcArguments(4000, 4000);
    Check(options.Skip(1).SequenceEqual(new[] { "--network-caching=4000", "--live-caching=4000", "--file-caching=4000",
        "--avcodec-hw=d3d11va", "--direct3d11-hw-blending", "--deinterlace=-1", "--deinterlace-mode=auto" }),
        "Diagnostics must not change playback options");
}

if (args.Length == 2 && args[0] == "--smoke")
{
    // Hidden test HWNDs: exercise the production service with a local synthetic clip,
    // without opening a provider connection or an external player window.
    var parent = Native.CreateWindowEx(0, "static", "Diagnostic test host", 0, 0, 0, 320, 180, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    var child = Native.CreateWindowEx(0, "static", "", 0x50000000, 0, 0, 320, 180, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    Check(parent != IntPtr.Zero && child != IntPtr.Zero, "Native test host creation failed");
    try
    {
        var smoke = Task.Run(async () =>
        {
        await using var playback = new VlcPlaybackService(logger, settings);
        playback.SetVideoHostHandle(child);
        await playback.PlayAsync(new Uri(Path.GetFullPath(args[1])));
#if PLAYBACK_DIAGNOSTICS
        var smokeVersion = playback.ReadNativeVersion();
        Console.WriteLine("Native smoke runtime: " + smokeVersion);
        Check(smokeVersion?.StartsWith("3.0.20 ", StringComparison.Ordinal) == true,
            "Smoke runtime differs from the bundled VLC 3.0.20");
#endif
        await Task.Delay(7000);
        var player = playback.NativePlayer as MediaPlayer;
        Check(player?.Hwnd == child, "Player detached from its embedded HWND");
        Check(player?.Media?.Statistics.DisplayedPictures > 0, "Embedded player did not display frames");
        await playback.StopAsync();
        var failedInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.StatusChanged += (_, status) =>
        {
            if (status.State == IptvPlayer.Contracts.Player.PlaybackState.Failed) failedInput.TrySetResult();
        };
        await playback.PlayAsync(new Uri(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!,
            "missing-smoke-input-" + Guid.NewGuid().ToString("N") + ".ts")));
        await failedInput.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var firstFrameMonitor = typeof(VlcPlaybackService).GetField("_firstFrameMonitorTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(playback) as Task;
        Check(firstFrameMonitor is not null, "Failed input did not exercise the first-frame monitor");
        if (firstFrameMonitor is not null)
        {
            await Task.WhenAny(firstFrameMonitor, Task.Delay(2000));
            Check(firstFrameMonitor.IsCompleted, "First-frame monitor kept polling a failed input");
        }
        await playback.StopAsync();
        await using (var missingHost = new VlcPlaybackService(new CaptureLogger(), settings))
        {
            IptvPlayer.Contracts.Player.PlayerStatus? status = null;
            missingHost.StatusChanged += (_, value) => status = value;
            await missingHost.PlayAsync(new Uri(Path.GetFullPath(args[1])));
            var unhostedPlayer = missingHost.NativePlayer as MediaPlayer;
            Check(status?.ErrorCode == "VIDEO_HOST_UNAVAILABLE", "Missing video host did not stop playback startup");
            Check(unhostedPlayer is { IsPlaying: false, VoutCount: 0 }, "Playback started without its embedded host");
        }
        var messages = logger.Messages.ToArray();
#if DEBUG || PLAYBACK_DIAGNOSTICS
        Check(messages.Any(m => m.Contains("Playback frame diagnostics sample.")), "No frame samples captured");
        Check(messages.Any(m => m.Contains("technical profile.")), "No technical profile captured");
        Check(messages.Any(m => m.Contains("Kind=module-selected")), "Native module selection was not captured");
        Check(messages.Any(m => m.Contains("Playback frame diagnostics summary.")), "No session summary captured");
        Check(messages.Any(m => m.Contains("Displayed=") && !m.Contains("Displayed=0;")), "No displayed frames captured");
#else
        Check(!messages.Any(m => m.Contains("Playback frame diagnostics") || m.Contains("Playback native diagnostics")),
            "Normal Release enabled detailed playback diagnostics");
#endif
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var processId);
            if (processId == Environment.ProcessId && Native.IsWindowVisible(window))
                failures.Add("The native playback smoke test opened a visible top-level window");
            return true;
        }, IntPtr.Zero);
        var evidence = Path.Combine(AppContext.BaseDirectory, "smoke-evidence.log");
        File.WriteAllLines(evidence, messages);
        Console.WriteLine("Smoke evidence: " + evidence);
        });
        while (!smoke.IsCompleted)
        {
            while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
            Thread.Sleep(5);
        }
        smoke.GetAwaiter().GetResult();
    }
    finally { Native.DestroyWindow(child); Native.DestroyWindow(parent); }
}

foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostic-check-result.json"),
    System.Text.Json.JsonSerializer.Serialize(new { passed = failures.Count == 0, failures }));
Console.WriteLine(failures.Count == 0 ? "PASS: playback diagnostic checks" : $"FAIL: {failures.Count} checks");
return failures.Count == 0 ? 0 : 1;

sealed class CaptureLogger : ILogger<VlcPlaybackService>
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }
    [DllImport("user32.dll")] public static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref Message message);
    public delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
}
