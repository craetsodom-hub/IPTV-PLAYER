using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;

namespace IptvPlayer.Player.Vlc.Services;

internal sealed class PlaybackDiagnosticsProbe : IAsyncDisposable
{
    private const int DefaultSampleIntervalMs = 250;
    private const int MinimumSampleIntervalMs = 100;
    private const int MaximumSampleIntervalMs = 2000;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupExclusionWindow = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly int _sampleIntervalMs;
    private readonly EventHandler<LogEventArgs> _nativeLogHandler;
    private readonly object _sessionGate = new();

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private LibVLC? _attachedLibVlc;
    private Media? _summaryMedia;
    private string? _summaryScheme;
    private long _summaryStarted;
    private long _nextSessionId;
    private long _activeSessionId;

    public PlaybackDiagnosticsProbe(ILogger logger, IConfiguration configuration)
    {
        _logger = logger;
        var probeEnabledSetting = configuration["PlaybackDiagnostics:ProbeEnabled"];
#if DEBUG
        Enabled = bool.TryParse(probeEnabledSetting, out var probeEnabled) && probeEnabled;
#else
        Enabled = false;
#endif
        NativeVerbosity = Math.Clamp(
            int.TryParse(configuration["PlaybackDiagnostics:NativeVerbosity"], out var nativeVerbosity)
                ? nativeVerbosity
                : 0,
            0,
            4);
        SummaryOnly = bool.TryParse(configuration["PlaybackDiagnostics:SummaryOnly"], out var summaryOnly)
            && summaryOnly;
        _sampleIntervalMs = Math.Clamp(
            int.TryParse(configuration["PlaybackDiagnostics:StatsIntervalMs"], out var sampleIntervalMs)
                ? sampleIntervalMs
                : DefaultSampleIntervalMs,
            MinimumSampleIntervalMs,
            MaximumSampleIntervalMs);

        var hardwareDecoding = configuration["PlaybackDiagnostics:HardwareDecoding"]?.Trim().ToLowerInvariant();
        HardwareDecoding = hardwareDecoding is "none" or "dxva2" or "d3d11va"
            ? hardwareDecoding
            : null;

        _nativeLogHandler = OnNativeLog;
    }

    public bool Enabled { get; }

    public int NativeVerbosity { get; }

    public bool SummaryOnly { get; }

    public string? HardwareDecoding { get; }

    public IReadOnlyList<string> BuildLibVlcArguments(int networkCachingMs, int liveCachingMs)
    {
        var arguments = new List<string>
        {
            Enabled && NativeVerbosity > 0 ? $"--verbose={NativeVerbosity}" : "--quiet",
            $"--network-caching={networkCachingMs}",
            $"--live-caching={liveCachingMs}",
        };

        return arguments;
    }

    public void Attach(LibVLC libVlc)
    {
        if (!Enabled || NativeVerbosity <= 0)
        {
            return;
        }

        _attachedLibVlc = libVlc;
        libVlc.Log += _nativeLogHandler;
    }

    public void ApplyMediaOptions(Media media)
    {
        if (Enabled && HardwareDecoding is not null)
        {
            media.AddOption($":avcodec-hw={HardwareDecoding}");
        }

    }

    public void StartSession(Media media, MediaPlayer mediaPlayer, string streamScheme)
    {
        if (!Enabled)
        {
            return;
        }

        CancellationTokenSource sessionCts;
        long sessionId;

        lock (_sessionGate)
        {
            if (_sessionCts is not null || _sessionTask is not null)
            {
                throw new InvalidOperationException("The previous playback diagnostic session was not stopped.");
            }

            sessionId = Interlocked.Increment(ref _nextSessionId);
            Interlocked.Exchange(ref _activeSessionId, sessionId);
            sessionCts = new CancellationTokenSource();
            _sessionCts = sessionCts;
            if (SummaryOnly)
            {
                _summaryMedia = media;
                _summaryScheme = streamScheme;
                _summaryStarted = Stopwatch.GetTimestamp();
                _sessionTask = Task.CompletedTask;
            }
            else
            {
                _sessionTask = MonitorSessionAsync(media, mediaPlayer, streamScheme, sessionId, sessionCts.Token);
            }
        }

        _logger.LogInformation(
            "Playback frame diagnostics started. Session={Session}; Scheme={Scheme}; Mode={Mode}; SampleIntervalMs={SampleIntervalMs}; NativeVerbosity={NativeVerbosity}; HardwareDecoding={HardwareDecoding}",
            sessionId,
            streamScheme,
            SummaryOnly ? "summary-only" : "sampled",
            _sampleIntervalMs,
            NativeVerbosity,
            HardwareDecoding ?? "auto");
    }

    public async Task StopSessionAsync()
    {
        CancellationTokenSource? sessionCts;
        Task? sessionTask;
        Media? summaryMedia;
        string? summaryScheme;
        long summaryStarted;
        long sessionId;

        lock (_sessionGate)
        {
            sessionCts = _sessionCts;
            sessionTask = _sessionTask;
            summaryMedia = _summaryMedia;
            summaryScheme = _summaryScheme;
            summaryStarted = _summaryStarted;
            sessionId = Interlocked.Read(ref _activeSessionId);
            _sessionCts = null;
            _sessionTask = null;
            _summaryMedia = null;
            _summaryScheme = null;
            _summaryStarted = 0;
            Interlocked.Exchange(ref _activeSessionId, 0);
        }

        if (sessionCts is null)
        {
            return;
        }

        sessionCts.Cancel();
        try
        {
            if (sessionTask is not null)
            {
                await sessionTask.ConfigureAwait(false);
            }

            if (summaryMedia is not null && summaryStarted != 0)
            {
                LogSummaryOnly(summaryMedia, summaryScheme ?? "unknown", sessionId, summaryStarted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sessionCts.Dispose();
        }
    }

    private void LogSummaryOnly(Media media, string streamScheme, long sessionId, long started)
    {
        try
        {
            var statistics = media.Statistics;
            var elapsed = Stopwatch.GetElapsedTime(started);
            _logger.LogInformation(
                "Playback frame diagnostics summary. Session={Session}; DurationMs={DurationMs:0}; Samples=1; InputBytes={InputBytes}; DemuxBytes={DemuxBytes}; DecodedVideo={DecodedVideo}; DecodedAudio={DecodedAudio}; Displayed={Displayed}; LostPictures={LostPictures}; PlayedAudio={PlayedAudio}; LostAudio={LostAudio}; DemuxDiscontinuity={DemuxDiscontinuity}; DemuxCorrupted={DemuxCorrupted}; AudioWithoutVideoEpisodes=unmeasured; CounterResets=0; Scheme={Scheme}; Mode=summary-only",
                sessionId,
                elapsed.TotalMilliseconds,
                statistics.ReadBytes,
                statistics.DemuxReadBytes,
                statistics.DecodedVideo,
                statistics.DecodedAudio,
                statistics.DisplayedPictures,
                statistics.LostPictures,
                statistics.PlayedAudioBuffers,
                statistics.LostAudioBuffers,
                statistics.DemuxDiscontinuity,
                statistics.DemuxCorrupted,
                streamScheme);

            foreach (var track in media.Tracks)
            {
                if (track.TrackType != TrackType.Video)
                {
                    continue;
                }

                var video = track.Data.Video;
                var framesPerSecond = video.FrameRateDen == 0
                    ? 0d
                    : (double)video.FrameRateNum / video.FrameRateDen;
                _logger.LogInformation(
                    "Playback frame diagnostics technical profile. Session={Session}; Codec={Codec}; Width={Width}; Height={Height}; FrameRateNum={FrameRateNum}; FrameRateDen={FrameRateDen}; FramesPerSecond={FramesPerSecond:0.###}; Bitrate={Bitrate}",
                    sessionId,
                    media.CodecDescription(track.TrackType, track.Codec),
                    video.Width,
                    video.Height,
                    video.FrameRateNum,
                    video.FrameRateDen,
                    framesPerSecond,
                    track.Bitrate);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Playback frame diagnostics summary could not be captured. Session={Session}", sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);

        if (_attachedLibVlc is not null)
        {
            _attachedLibVlc.Log -= _nativeLogHandler;
            _attachedLibVlc = null;
        }
    }

    private async Task MonitorSessionAsync(
        Media media,
        MediaPlayer mediaPlayer,
        string streamScheme,
        long sessionId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var heartbeatStarted = started;
        var previousSampleTimestamp = started;
        var previous = default(MediaStats);
        var hasBaseline = false;
        var audioWithoutVideoSamples = 0;
        var audioWithoutVideoEpisodes = 0;
        var counterResets = 0;
        var sampleCount = 0;

        long heartbeatReadBytes = 0;
        long heartbeatDemuxBytes = 0;
        long heartbeatDecodedVideo = 0;
        long heartbeatDecodedAudio = 0;
        long heartbeatDisplayed = 0;
        long heartbeatLostPictures = 0;
        long heartbeatPlayedAudio = 0;
        long heartbeatLostAudio = 0;
        long heartbeatDiscontinuities = 0;
        long heartbeatCorrupted = 0;

        long totalReadBytes = 0;
        long totalDemuxBytes = 0;
        long totalDecodedVideo = 0;
        long totalDecodedAudio = 0;
        long totalDisplayed = 0;
        long totalLostPictures = 0;
        long totalPlayedAudio = 0;
        long totalLostAudio = 0;
        long totalDiscontinuities = 0;
        long totalCorrupted = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_sampleIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (sessionId != Interlocked.Read(ref _activeSessionId))
                {
                    return;
                }

                var sampledAt = Stopwatch.GetTimestamp();
                var statistics = media.Statistics;
                sampleCount++;

                if (!hasBaseline)
                {
                    previous = statistics;
                    previousSampleTimestamp = sampledAt;
                    hasBaseline = true;
                    continue;
                }

                var readBytes = Delta(statistics.ReadBytes, previous.ReadBytes);
                var demuxBytes = Delta(statistics.DemuxReadBytes, previous.DemuxReadBytes);
                var decodedVideo = Delta(statistics.DecodedVideo, previous.DecodedVideo);
                var decodedAudio = Delta(statistics.DecodedAudio, previous.DecodedAudio);
                var displayed = Delta(statistics.DisplayedPictures, previous.DisplayedPictures);
                var lostPictures = Delta(statistics.LostPictures, previous.LostPictures);
                var playedAudio = Delta(statistics.PlayedAudioBuffers, previous.PlayedAudioBuffers);
                var lostAudio = Delta(statistics.LostAudioBuffers, previous.LostAudioBuffers);
                var discontinuities = Delta(statistics.DemuxDiscontinuity, previous.DemuxDiscontinuity);
                var corrupted = Delta(statistics.DemuxCorrupted, previous.DemuxCorrupted);

                if (readBytes < 0
                    || demuxBytes < 0
                    || decodedVideo < 0
                    || decodedAudio < 0
                    || displayed < 0
                    || lostPictures < 0
                    || playedAudio < 0
                    || lostAudio < 0
                    || discontinuities < 0
                    || corrupted < 0)
                {
                    counterResets++;
                    previous = statistics;
                    previousSampleTimestamp = sampledAt;
                    audioWithoutVideoSamples = 0;
                    _logger.LogInformation(
                        "Playback frame diagnostics counter baseline reset. Session={Session}; ResetCount={ResetCount}",
                        sessionId,
                        counterResets);
                    continue;
                }

                previous = statistics;
                var sampleElapsed = Stopwatch.GetElapsedTime(previousSampleTimestamp, sampledAt);
                previousSampleTimestamp = sampledAt;

                heartbeatReadBytes += readBytes;
                heartbeatDemuxBytes += demuxBytes;
                heartbeatDecodedVideo += decodedVideo;
                heartbeatDecodedAudio += decodedAudio;
                heartbeatDisplayed += displayed;
                heartbeatLostPictures += lostPictures;
                heartbeatPlayedAudio += playedAudio;
                heartbeatLostAudio += lostAudio;
                heartbeatDiscontinuities += discontinuities;
                heartbeatCorrupted += corrupted;

                totalReadBytes += readBytes;
                totalDemuxBytes += demuxBytes;
                totalDecodedVideo += decodedVideo;
                totalDecodedAudio += decodedAudio;
                totalDisplayed += displayed;
                totalLostPictures += lostPictures;
                totalPlayedAudio += playedAudio;
                totalLostAudio += lostAudio;
                totalDiscontinuities += discontinuities;
                totalCorrupted += corrupted;

                var playbackElapsed = Stopwatch.GetElapsedTime(started, sampledAt);
                var isPastStartupWindow = playbackElapsed >= StartupExclusionWindow;

                if (isPastStartupWindow && playedAudio > 0 && displayed == 0)
                {
                    audioWithoutVideoSamples++;
                    if (audioWithoutVideoSamples == 2)
                    {
                        audioWithoutVideoEpisodes++;
                        _logger.LogWarning(
                            "Playback frame diagnostics detected audio advancing without displayed video. Session={Session}; ElapsedMs={ElapsedMs:0}; ConsecutiveSamples={ConsecutiveSamples}; WindowMs={WindowMs:0}; ReadBytesDelta={ReadBytesDelta}; DemuxBytesDelta={DemuxBytesDelta}; DecodedVideoDelta={DecodedVideoDelta}; LostPicturesDelta={LostPicturesDelta}; PlayedAudioDelta={PlayedAudioDelta}; PlayerState={PlayerState}; MediaTimeMs={MediaTimeMs}; VoutCount={VoutCount}",
                            sessionId,
                            playbackElapsed.TotalMilliseconds,
                            audioWithoutVideoSamples,
                            sampleElapsed.TotalMilliseconds * audioWithoutVideoSamples,
                            readBytes,
                            demuxBytes,
                            decodedVideo,
                            lostPictures,
                            playedAudio,
                            mediaPlayer.State,
                            mediaPlayer.Time,
                            mediaPlayer.VoutCount);
                    }
                }
                else
                {
                    audioWithoutVideoSamples = 0;
                }

                if (isPastStartupWindow
                    && (lostPictures > 0 || lostAudio > 0 || discontinuities > 0 || corrupted > 0))
                {
                    _logger.LogWarning(
                        "Playback frame diagnostics counter anomaly. Session={Session}; ElapsedMs={ElapsedMs:0}; ReadBytesDelta={ReadBytesDelta}; DemuxBytesDelta={DemuxBytesDelta}; DecodedVideoDelta={DecodedVideoDelta}; DisplayedDelta={DisplayedDelta}; LostPicturesDelta={LostPicturesDelta}; PlayedAudioDelta={PlayedAudioDelta}; LostAudioDelta={LostAudioDelta}; DemuxDiscontinuityDelta={DemuxDiscontinuityDelta}; DemuxCorruptedDelta={DemuxCorruptedDelta}; PlayerState={PlayerState}; MediaTimeMs={MediaTimeMs}; VoutCount={VoutCount}",
                        sessionId,
                        playbackElapsed.TotalMilliseconds,
                        readBytes,
                        demuxBytes,
                        decodedVideo,
                        displayed,
                        lostPictures,
                        playedAudio,
                        lostAudio,
                        discontinuities,
                        corrupted,
                        mediaPlayer.State,
                        mediaPlayer.Time,
                        mediaPlayer.VoutCount);
                }

                var heartbeatElapsed = Stopwatch.GetElapsedTime(heartbeatStarted, sampledAt);
                if (heartbeatElapsed < HeartbeatInterval)
                {
                    continue;
                }

                var inputMegabitsPerSecond = ToMegabitsPerSecond(heartbeatReadBytes, heartbeatElapsed);
                var demuxMegabitsPerSecond = ToMegabitsPerSecond(heartbeatDemuxBytes, heartbeatElapsed);
                _logger.LogInformation(
                    "Playback frame diagnostics heartbeat. Session={Session}; ElapsedMs={ElapsedMs:0}; WindowMs={WindowMs:0}; InputMbps={InputMbps:0.000}; DemuxMbps={DemuxMbps:0.000}; DecodedVideo={DecodedVideo}; DecodedAudio={DecodedAudio}; Displayed={Displayed}; LostPictures={LostPictures}; PlayedAudio={PlayedAudio}; LostAudio={LostAudio}; DemuxDiscontinuity={DemuxDiscontinuity}; DemuxCorrupted={DemuxCorrupted}; AudioWithoutVideoEpisodes={AudioWithoutVideoEpisodes}; PlayerState={PlayerState}; MediaTimeMs={MediaTimeMs}; VoutCount={VoutCount}; Scheme={Scheme}",
                    sessionId,
                    playbackElapsed.TotalMilliseconds,
                    heartbeatElapsed.TotalMilliseconds,
                    inputMegabitsPerSecond,
                    demuxMegabitsPerSecond,
                    heartbeatDecodedVideo,
                    heartbeatDecodedAudio,
                    heartbeatDisplayed,
                    heartbeatLostPictures,
                    heartbeatPlayedAudio,
                    heartbeatLostAudio,
                    heartbeatDiscontinuities,
                    heartbeatCorrupted,
                    audioWithoutVideoEpisodes,
                    mediaPlayer.State,
                    mediaPlayer.Time,
                    mediaPlayer.VoutCount,
                    streamScheme);

                heartbeatStarted = sampledAt;
                heartbeatReadBytes = 0;
                heartbeatDemuxBytes = 0;
                heartbeatDecodedVideo = 0;
                heartbeatDecodedAudio = 0;
                heartbeatDisplayed = 0;
                heartbeatLostPictures = 0;
                heartbeatPlayedAudio = 0;
                heartbeatLostAudio = 0;
                heartbeatDiscontinuities = 0;
                heartbeatCorrupted = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Playback frame diagnostics monitor stopped unexpectedly. Session={Session}", sessionId);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            _logger.LogInformation(
                "Playback frame diagnostics summary. Session={Session}; DurationMs={DurationMs:0}; Samples={Samples}; InputBytes={InputBytes}; DemuxBytes={DemuxBytes}; DecodedVideo={DecodedVideo}; DecodedAudio={DecodedAudio}; Displayed={Displayed}; LostPictures={LostPictures}; PlayedAudio={PlayedAudio}; LostAudio={LostAudio}; DemuxDiscontinuity={DemuxDiscontinuity}; DemuxCorrupted={DemuxCorrupted}; AudioWithoutVideoEpisodes={AudioWithoutVideoEpisodes}; CounterResets={CounterResets}; Scheme={Scheme}",
                sessionId,
                elapsed.TotalMilliseconds,
                sampleCount,
                totalReadBytes,
                totalDemuxBytes,
                totalDecodedVideo,
                totalDecodedAudio,
                totalDisplayed,
                totalLostPictures,
                totalPlayedAudio,
                totalLostAudio,
                totalDiscontinuities,
                totalCorrupted,
                audioWithoutVideoEpisodes,
                counterResets,
                streamScheme);
        }
    }

    private void OnNativeLog(object? sender, LogEventArgs eventArgs)
    {
        var sessionId = Interlocked.Read(ref _activeSessionId);
        if (sessionId == 0)
        {
            return;
        }

        var message = eventArgs.Message;
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        if (TryReadModuleSelection(message, out var selectionEvent, out var selectedModule))
        {
            _logger.LogInformation(
                "Playback native diagnostics: video-output selection. Session={Session}; Event={Event}; Module={Module}",
                sessionId,
                selectionEvent,
                selectedModule);
            return;
        }

        if (TryReadMilliseconds(message, "waited ", " ms for the render fence", out var fenceWaitMs))
        {
            if (fenceWaitMs >= 5)
            {
                _logger.LogWarning(
                    "Playback native diagnostics: render fence wait. Session={Session}; WaitMs={WaitMs}",
                    sessionId,
                    fenceWaitMs);
            }

            return;
        }

        if (TryReadMilliseconds(message, "missing ", " ms", out var lateByMs)
            && message.Contains("picture", StringComparison.OrdinalIgnoreCase)
            && message.Contains("late", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Playback native diagnostics: late picture. Session={Session}; LateByMs={LateByMs}",
                sessionId,
                lateByMs);
            return;
        }

        if (message.Contains("late frames in a row", StringComparison.OrdinalIgnoreCase)
            || message.Contains("seconds of late video", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Playback native diagnostics: decoder late-frame escalation. Session={Session}", sessionId);
            return;
        }

        if (message.Contains("trying to reconnect", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Playback native diagnostics: reconnect attempt. Session={Session}", sessionId);
            return;
        }

        if (message.Contains("reconnection failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("HTTP connection failure", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Playback native diagnostics: connection failure. Session={Session}", sessionId);
            return;
        }

        if (message.Contains("SwapChain Present failed", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Playback native diagnostics: swap-chain present failure. Session={Session}", sessionId);
            return;
        }

        if ((string.Equals(eventArgs.Module, "direct3d11", StringComparison.OrdinalIgnoreCase)
             || string.Equals(eventArgs.Module, "direct3d9", StringComparison.OrdinalIgnoreCase)
             || string.Equals(eventArgs.Module, "gl", StringComparison.OrdinalIgnoreCase)
             || string.Equals(eventArgs.Module, "glwin32", StringComparison.OrdinalIgnoreCase))
            && (message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                "Playback native diagnostics: video-output module failure. Session={Session}; Module={Module}",
                sessionId,
                eventArgs.Module);
            return;
        }

        if (string.Equals(eventArgs.Module, "avcodec", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("hardware", StringComparison.OrdinalIgnoreCase)
                || message.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("error", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Playback native diagnostics: decoder warning. Session={Session}", sessionId);
            return;
        }

        if ((message.Contains("PCR", StringComparison.OrdinalIgnoreCase)
             || message.Contains("timestamp", StringComparison.OrdinalIgnoreCase)
             || message.Contains("clock", StringComparison.OrdinalIgnoreCase))
            && (message.Contains("late", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || message.Contains("reset", StringComparison.OrdinalIgnoreCase)
                || message.Contains("discontinu", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Playback native diagnostics: clock or timestamp discontinuity. Session={Session}", sessionId);
        }
    }

    private static long Delta(int current, int previous)
        => (long)current - previous;

    private static bool TryReadModuleSelection(string message, out string selectionEvent, out string module)
    {
        selectionEvent = string.Empty;
        module = string.Empty;

        if (message.Contains("looking for vout display module matching", StringComparison.OrdinalIgnoreCase))
        {
            selectionEvent = "requested-vout";
        }
        else if (message.Contains("using vout display module", StringComparison.OrdinalIgnoreCase))
        {
            selectionEvent = "selected-vout";
        }
        else if (message.Contains("no vout display modules matched", StringComparison.OrdinalIgnoreCase))
        {
            selectionEvent = "vout-not-found";
            module = "none";
            return true;
        }
        else if (message.Contains("using opengl module", StringComparison.OrdinalIgnoreCase))
        {
            selectionEvent = "selected-opengl-provider";
        }
        else
        {
            return false;
        }

        foreach (var knownModule in new[] { "direct3d11", "direct3d9", "glwin32", "wingdi", "directdraw", "wgl", "gl" })
        {
            if (message.Contains($"\"{knownModule}\"", StringComparison.OrdinalIgnoreCase))
            {
                module = knownModule;
                return true;
            }
        }

        module = "unrecognized";
        return true;
    }

    private static double ToMegabitsPerSecond(long bytes, TimeSpan elapsed)
        => elapsed.TotalSeconds <= 0d ? 0d : bytes * 8d / elapsed.TotalSeconds / 1_000_000d;

    private static bool TryReadMilliseconds(string message, string prefix, string suffix, out int milliseconds)
    {
        milliseconds = 0;
        var prefixIndex = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
        {
            return false;
        }

        var numberStart = prefixIndex + prefix.Length;
        var suffixIndex = message.IndexOf(suffix, numberStart, StringComparison.OrdinalIgnoreCase);
        if (suffixIndex <= numberStart)
        {
            return false;
        }

        return int.TryParse(
            message.AsSpan(numberStart, suffixIndex - numberStart),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out milliseconds);
    }
}
