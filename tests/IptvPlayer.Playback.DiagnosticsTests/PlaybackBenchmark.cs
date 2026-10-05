using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using IptvPlayer.Infrastructure.Services;
using LibVLCSharp.Shared;

internal static class PlaybackBenchmark
{
    // Local-only replay experiment. No top-level video window is shown and the
    // production UI/host is not modified. Results do not measure screen presentation.
    public static int Run(string[] args)
    {
        var input = args[0] == "--live" ? SavedChannel(args[1]) : new Uri(Path.GetFullPath(args[1]));
        var profile = args[2];
        var seconds = int.Parse(args[3]);
        var output = Path.GetFullPath(args[4]);
        var hw = profile == "software" ? "none" : profile is "dxva2" or "direct3d9-dxva2" ? "dxva2" : "d3d11va";
        var threads = profile == "one-thread" ? 1 : 0;
        var deinterlace = profile == "no-deinterlace" ? 0 : -1;
        var options = new List<string> { "--verbose=4", "--network-caching=4000", "--live-caching=4000", "--file-caching=4000",
            $"--avcodec-hw={hw}", "--direct3d11-hw-blending", $"--deinterlace={deinterlace}", "--deinterlace-mode=auto" };
        if (profile is "direct3d9" or "direct3d9-dxva2") options.Add("--vout=direct3d9");
        if (profile == "no-hw-blending") { options.Remove("--direct3d11-hw-blending"); options.Add("--no-direct3d11-hw-blending"); }
        Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        var events = new ConcurrentQueue<object>();
        var parent = Native.CreateWindowEx(0, "static", "Local playback benchmark", 0, 0, 0, 1280, 720, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var child = Native.CreateWindowEx(0, "static", "", 0x50000000, 0, 0, 1280, 720, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (parent == IntPtr.Zero || child == IntPtr.Zero) throw new InvalidOperationException("Missing embedded host");
        try
        {
            var work = Task.Run(async () =>
            {
                using var vlc = new LibVLC(options.ToArray());
                var watch = Stopwatch.StartNew();
                vlc.Log += (_, e) =>
                {
                    // Replay only accepts a filesystem path. Still omit arbitrary native text.
                    var evt = IptvPlayer.Player.Vlc.Services.NativePlaybackDiagnosticClassifier.Classify(e.Message, e.Module, 1);
                    if (evt.HasValue) events.Enqueue(new { elapsedMs = watch.Elapsed.TotalMilliseconds, diagnostic = evt.Value });
                };
                using var media = new Media(vlc, input);
                if (args[0] == "--live") media.AddOption(":http-reconnect");
                media.AddOption(":file-caching=4000");
                media.AddOption(":network-caching=4000");
                media.AddOption(":live-caching=4000");
                media.AddOption($":avcodec-hw={hw}");
                media.AddOption($":avcodec-threads={threads}");
                media.AddOption($":deinterlace={deinterlace}");
                media.AddOption(":deinterlace-mode=auto");
                using var player = new MediaPlayer(vlc) { Hwnd = child };
                // All runs use identical audio output; silence the output, not the decoder.
                player.Mute = true;
                if (!player.Play(media)) throw new InvalidOperationException("Playback did not start");
                var rows = new List<object>();
                var baseline = default(MediaStats);
                var end = default(MediaStats);
                var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime.TotalMilliseconds;
                GetSystemTimes(out var idlePrevious, out var kernelPrevious, out var userPrevious);
                while (watch.Elapsed.TotalSeconds < seconds && player.State is not VLCState.Ended and not VLCState.Error)
                {
                    await Task.Delay(500);
                    var stats = media.Statistics;
                    if (watch.Elapsed.TotalSeconds < 5) baseline = stats;
                    end = stats;
                    GetSystemTimes(out var idleNow, out var kernelNow, out var userNow);
                    var total = (kernelNow - kernelPrevious) + (userNow - userPrevious);
                    var systemCpuPercent = total == 0 ? 0 : 100d * (total - (idleNow - idlePrevious)) / total;
                    idlePrevious = idleNow; kernelPrevious = kernelNow; userPrevious = userNow;
                    rows.Add(new { elapsedMs = watch.Elapsed.TotalMilliseconds, state = player.State.ToString(),
                        stats.DecodedVideo, stats.DisplayedPictures, stats.LostPictures, stats.PlayedAudioBuffers,
                        stats.LostAudioBuffers, stats.DemuxCorrupted, stats.DemuxDiscontinuity,
                        cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpuStart, systemCpuPercent });
                }
                var result = new { profile, seconds = watch.Elapsed.TotalSeconds,
                    embedded = player.Hwnd == child, state = player.State.ToString(),
                    afterStartup = new { displayed = end.DisplayedPictures - baseline.DisplayedPictures,
                        lost = end.LostPictures - baseline.LostPictures, decoded = end.DecodedVideo - baseline.DecodedVideo,
                        audio = end.PlayedAudioBuffers - baseline.PlayedAudioBuffers, lostAudio = end.LostAudioBuffers - baseline.LostAudioBuffers },
                    cpuPercent = (process.TotalProcessorTime.TotalMilliseconds - cpuStart) / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                    samples = rows, events = events.ToArray() };
                File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                player.Stop();
                Console.WriteLine(JsonSerializer.Serialize(new { result.profile, result.seconds, result.embedded, result.afterStartup, result.cpuPercent }));
            });
            while (!work.IsCompleted)
            {
                while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
                { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
                Thread.Sleep(5);
            }
            work.GetAwaiter().GetResult();
        }
        finally { Native.DestroyWindow(child); Native.DestroyWindow(parent); }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    public static async Task<int> CaptureAsync(string[] args)
    {
        var uri = SavedChannel(args[1]);
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(args[2])));
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) { Console.WriteLine("Provider status: " + (int)response.StatusCode); return 3; }
            var type = response.Content.Headers.ContentType?.MediaType ?? "unknown";
            if (type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("HLS playlist; raw TS capture unavailable"); return 4; }
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var file = File.Create(Path.GetFullPath(args[3]));
            try { await input.CopyToAsync(file, deadline.Token); } catch (OperationCanceledException) { }
            Console.WriteLine("Local sample captured: " + file.Length + " bytes; content type " + type);
        }
        catch (Exception e) { Console.WriteLine("Capture failed: " + e.GetType().Name); return 5; }
        return 0;
    }

    private static Uri SavedChannel(string channelId)
    {
        // Reuse the user's already saved channel without printing/decrypting its
        // URL into a command line, file, or diagnostic report.
        var catalog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IptvPlayer", "catalog", "sources.json");
        var bytes = ProtectedCatalogFile.Read(catalog);
        using var document = JsonDocument.Parse(bytes);
        string? uri = null;
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == channelId
                    && element.TryGetProperty("streamUri", out var stream)) uri = stream.GetString();
                foreach (var property in element.EnumerateObject()) Visit(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Visit(item);
        }
        Visit(document.RootElement);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        return uri is null ? throw new InvalidOperationException("Saved channel not found") : new Uri(uri);
    }
}
