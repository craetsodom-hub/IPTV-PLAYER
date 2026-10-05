using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading.Channels;

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
    private readonly Channel<NativePlaybackDiagnostic> _nativeEvents = Channel.CreateBounded<NativePlaybackDiagnostic>(
        new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long _droppedNativeEvents;
    private readonly bool _logSamples;
#if PLAYBACK_DIAGNOSTICS
    private readonly int? _experimentDecoderThreads;
    private readonly string? _experimentDeinterlaceMode;
    private readonly bool _experimentExcludeDirect3D11;
    private readonly bool _experimentPreserveLatePictures;
    private readonly bool _captureNativeRenderIntervals;
    private readonly string? _experimentVideoOutput;
    private NativeRenderTimingProbe? _nativeRenderTiming;
#endif

    public PlaybackDiagnosticsProbe(ILogger logger, IConfiguration configuration)
    {
        _logger = logger;
        var probeEnabledSetting = configuration["PlaybackDiagnostics:ProbeEnabled"];
#if DEBUG || PLAYBACK_DIAGNOSTICS
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
        _logSamples = bool.TryParse(configuration["PlaybackDiagnostics:LogSamples"], out var logSamples) && logSamples;
#if PLAYBACK_DIAGNOSTICS
        if (int.TryParse(configuration["PlaybackDiagnostics:ExperimentDecoderThreads"], out var decoderThreads)
            && decoderThreads is >= 1 and <= 8) _experimentDecoderThreads = decoderThreads;
        var deinterlaceMode = configuration["PlaybackDiagnostics:ExperimentDeinterlaceMode"];
        if (deinterlaceMode is "yadif2x" or "bob") _experimentDeinterlaceMode = deinterlaceMode;
        _experimentExcludeDirect3D11 = bool.TryParse(configuration["PlaybackDiagnostics:ExperimentExcludeDirect3D11"], out var excludeDirect3D11)
            && excludeDirect3D11;
        _experimentPreserveLatePictures = bool.TryParse(configuration["PlaybackDiagnostics:ExperimentPreserveLatePictures"], out var preserveLatePictures)
            && preserveLatePictures;
        _captureNativeRenderIntervals = bool.TryParse(configuration["PlaybackDiagnostics:CaptureNativeRenderIntervals"], out var capture)
            && capture;
        var videoOutput = configuration["PlaybackDiagnostics:ExperimentVideoOutput"];
        if (videoOutput is "direct3d9" or "direct3d11" or "glwin32") _experimentVideoOutput = videoOutput;
#endif
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
        var deinterlaceMode = "auto";
#if PLAYBACK_DIAGNOSTICS
        if (Enabled && _experimentDeinterlaceMode is { } mode) deinterlaceMode = mode;
#endif
        var arguments = new List<string>
        {
            Enabled && NativeVerbosity > 0 ? $"--verbose={NativeVerbosity}" : "--quiet",
            $"--network-caching={networkCachingMs}",
            $"--live-caching={liveCachingMs}",
            $"--file-caching={networkCachingMs}",
            "--avcodec-hw=d3d11va",
            "--direct3d11-hw-blending",
            "--deinterlace=-1",
            $"--deinterlace-mode={deinterlaceMode}",
        };
#if PLAYBACK_DIAGNOSTICS
        if (Enabled && _experimentVideoOutput is { } videoOutput) arguments.Add("--vout=" + videoOutput);
        if (Enabled && _experimentExcludeDirect3D11) arguments.Remove("--direct3d11-hw-blending");
        if (Enabled && _experimentPreserveLatePictures)
        {
            arguments.Add("--no-drop-late-frames");
        }
#endif

        return arguments;
    }

    public void Attach(LibVLC libVlc)
    {
        if (!Enabled || NativeVerbosity <= 0)
        {
            return;
        }

        _attachedLibVlc = libVlc;
#if PLAYBACK_DIAGNOSTICS
        if (_captureNativeRenderIntervals && NativeVerbosity == 4)
        {
            _nativeRenderTiming = new NativeRenderTimingProbe(libVlc);
            return;
        }
#endif
        libVlc.Log += _nativeLogHandler;
    }

    public void ApplyMediaOptions(Media media)
    {
#if PLAYBACK_DIAGNOSTICS
        if (Enabled && _experimentDecoderThreads is { } threads) media.AddOption($":avcodec-threads={threads}");
        if (Enabled && _experimentDeinterlaceMode is { } mode) media.AddOption($":deinterlace-mode={mode}");
#endif
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
            HardwareDecoding ?? "d3d11va (baseline)");
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
#if PLAYBACK_DIAGNOSTICS
            _nativeRenderTiming?.Dispose();
            _nativeRenderTiming = null;
#endif
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
        var profileLogged = false;

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
                DrainNativeEvents();
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

                if (_logSamples)
                {
                    _logger.LogInformation(
                        "Playback frame diagnostics sample. Session={Session}; TimestampTicks={TimestampTicks}; ElapsedMs={ElapsedMs:0.0}; WindowMs={WindowMs:0.0}; InputBytes={InputBytes}; DemuxBytes={DemuxBytes}; DecodedVideo={DecodedVideo}; Displayed={Displayed}; LostPictures={LostPictures}; PlayedAudio={PlayedAudio}; LostAudio={LostAudio}; Discontinuities={Discontinuities}; Corrupted={Corrupted}; State={State}",
                        sessionId, sampledAt, playbackElapsed.TotalMilliseconds, sampleElapsed.TotalMilliseconds,
                        readBytes, demuxBytes, decodedVideo, displayed, lostPictures, playedAudio, lostAudio,
                        discontinuities, corrupted, mediaPlayer.State);
                }

                if (!profileLogged && isPastStartupWindow)
                {
                    profileLogged = LogTechnicalProfile(media, sessionId);
                }

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
            DrainNativeEvents(2048);
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
        if (sessionId == 0) return;
        var nativeMessage = eventArgs.Message;
        var diagnostic = NativePlaybackDiagnosticClassifier.Classify(nativeMessage, eventArgs.Module, sessionId);
        // LibVLCSharp relays log events through Task.Run. This timestamp is the
        // managed receipt time, not a native emission or picture presentation time.
        // Keep handling bounded and drain file logging on the monitor task.
        if (diagnostic.HasValue && !_nativeEvents.Writer.TryWrite(diagnostic.Value))
        {
            Interlocked.Increment(ref _droppedNativeEvents);
        }
    }

    private void DrainNativeEvents(int maximum = 256)
    {
        for (var count = 0; count < maximum && _nativeEvents.Reader.TryRead(out var item); count++)
        {
            _logger.LogInformation(
                "Playback native diagnostics event. ReceivedUtc={ReceivedUtc:O}; ReceivedTimestampTicks={ReceivedTimestampTicks}; Session={Session}; Kind={Kind}; Module={Module}; Milliseconds={Milliseconds}; DecoderThreads={DecoderThreads}",
                item.CapturedUtc, item.TimestampTicks, item.SessionId, item.Kind, item.Module, item.Milliseconds,
                item.DecoderThreads);
        }

        var dropped = Interlocked.Exchange(ref _droppedNativeEvents, 0);
        if (dropped > 0)
        {
            _logger.LogWarning("Playback native diagnostics overflow. DroppedEvents={DroppedEvents}", dropped);
        }
    }

    private bool LogTechnicalProfile(Media media, long sessionId)
    {
        foreach (var track in media.Tracks)
        {
            if (track.TrackType != TrackType.Video) continue;
            var video = track.Data.Video;
            _logger.LogInformation(
                "Playback frame diagnostics technical profile. Session={Session}; CodecFourCC={CodecFourCC}; Width={Width}; Height={Height}; FrameRateNum={FrameRateNum}; FrameRateDen={FrameRateDen}",
                sessionId, track.Codec, video.Width, video.Height, video.FrameRateNum, video.FrameRateDen);
            return true;
        }
        return false;
    }
    private static long Delta(int current, int previous)
        => (long)current - previous;

#if PLAYBACK_DIAGNOSTICS
    internal double[] ReadNativeRenderIntervals(long sinceTick) => _nativeRenderTiming?.ReadIntervals(sinceTick) ?? [];
    internal NativeRenderTimingProbe.NativeTimingEvent[] ReadNativeTimingEvents(long sinceTick) => _nativeRenderTiming?.ReadEvents(sinceTick) ?? [];
#endif

    private static double ToMegabitsPerSecond(long bytes, TimeSpan elapsed)
        => elapsed.TotalSeconds <= 0d ? 0d : bytes * 8d / elapsed.TotalSeconds / 1_000_000d;

}
