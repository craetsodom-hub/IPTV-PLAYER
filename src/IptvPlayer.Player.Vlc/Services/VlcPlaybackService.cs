using IptvPlayer.Contracts.Player;
using IptvPlayer.Contracts.Services;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Runtime;

namespace IptvPlayer.Player.Vlc.Services;

public sealed class VlcPlaybackService : IPlaybackService, INativePlayerBridge
{
    private const int DefaultNetworkCachingMs = 4000;
    private const int DefaultLiveCachingMs = 4000;
    private const int MinimumCachingMs = 500;
    private const int MaximumCachingMs = 30000;
    private const double ActivePlaybackHighCacheBufferingThreshold = 90d;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<VlcPlaybackService> _logger;
    private readonly int _networkCachingMs;
    private readonly int _liveCachingMs;
    private readonly bool _httpReconnect;
    private readonly bool _diagnosticsEnabled;
    private readonly PlaybackDiagnosticsProbe _diagnosticsProbe;
    private readonly object _gcLatencyGate = new();
    private readonly object _firstFrameMonitorGate = new();

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _activeMedia;
    private CancellationTokenSource? _firstFrameMonitorCts;
    private Task? _firstFrameMonitorTask;
    private long _activePlaybackSessionId;
    private long _presentationReadySessionId;
    private int _playingStatusEmitted;
    private long _playbackStartTimestamp;
    private long _lastPlayingTimestamp;
    private int _activeRebuffering;
    private DateTimeOffset _lastVlcBufferLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuppressedVlcBufferLogUtc = DateTimeOffset.MinValue;
    private GCLatencyMode _previousGcLatencyMode;
    private bool _playbackGcLatencyActive;
    private bool _initialized;

    public VlcPlaybackService(
        ILogger<VlcPlaybackService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _networkCachingMs = GetConfiguredCachingMs(configuration, "Playback:NetworkCachingMs", DefaultNetworkCachingMs);
        _liveCachingMs = GetConfiguredCachingMs(configuration, "Playback:LiveCachingMs", DefaultLiveCachingMs);
        _httpReconnect = GetConfiguredBool(configuration, "Playback:HttpReconnect", defaultValue: true);
#if DEBUG
        _diagnosticsEnabled = GetConfiguredBool(configuration, "PlaybackDiagnostics:Enabled", defaultValue: false);
#else
        _diagnosticsEnabled = false;
#endif
        _diagnosticsProbe = new PlaybackDiagnosticsProbe(logger, configuration);
    }

    public event EventHandler<PlayerStatus>? StatusChanged;

    public object? NativePlayer => _mediaPlayer;

    public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                var libVlcDirectory = ResolveLibVlcDirectory();
                if (string.IsNullOrWhiteSpace(libVlcDirectory))
                {
                    throw new DirectoryNotFoundException("VLC runtime directory was not found.");
                }

                Core.Initialize(libVlcDirectory);
                _libVlc = new LibVLC([.. _diagnosticsProbe.BuildLibVlcArguments(_networkCachingMs, _liveCachingMs)]);
                _diagnosticsProbe.Attach(_libVlc);

                _mediaPlayer = new MediaPlayer(_libVlc);
                HookPlayerEvents(_mediaPlayer);

                _initialized = true;
                Emit(new PlayerStatus(PlaybackState.Idle, "Player ready"));
                _logger.LogInformation(
                    "VLC playback engine initialized from {LibVlcDirectory} with network caching {NetworkCachingMs}ms and live caching {LiveCachingMs}ms",
                    libVlcDirectory,
                    _networkCachingMs,
                    _liveCachingMs);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "VLC initialization failed");
                Emit(new PlayerStatus(
                    PlaybackState.Failed,
                    "VLC runtime could not be loaded. Verify Windows antivirus is not blocking libvlc files.",
                    null,
                    "VLC_INIT_FAILED"));
                _initialized = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PlayAsync(Uri streamUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamUri);

        await InitializeAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_libVlc is null || _mediaPlayer is null)
            {
                Emit(new PlayerStatus(PlaybackState.Failed, "Player is not initialized", null, "PLAYER_NOT_INITIALIZED"));
                return;
            }

            var playbackSessionId = Interlocked.Increment(ref _activePlaybackSessionId);
            Interlocked.Exchange(ref _presentationReadySessionId, 0);
            Interlocked.Exchange(ref _playingStatusEmitted, 0);
            Interlocked.Exchange(ref _activeRebuffering, 0);
            await StopFirstFrameMonitorAsync();
            await _diagnosticsProbe.StopSessionAsync();
            _mediaPlayer.Stop();
            _activeMedia?.Dispose();
            _activeMedia = null;

            cancellationToken.ThrowIfCancellationRequested();

            _activeMedia = new Media(_libVlc, streamUri);
            _activeMedia.AddOption($":network-caching={_networkCachingMs}");
            _activeMedia.AddOption($":live-caching={_liveCachingMs}");
            _diagnosticsProbe.ApplyMediaOptions(_activeMedia);

            if (_httpReconnect && IsHttpStream(streamUri))
            {
                _activeMedia.AddOption(":http-reconnect");
            }

            var firstFrameBaseline = ReadFirstFrameBaseline(_activeMedia);

            Emit(new PlayerStatus(PlaybackState.Connecting, "Opening stream"));

            _playbackStartTimestamp = Stopwatch.GetTimestamp();
            _lastPlayingTimestamp = 0;
            if (_diagnosticsEnabled)
            {
                _logger.LogInformation(
                    "Playback diagnostics: starting VLC playback. Scheme={StreamScheme}; NetworkCachingMs={NetworkCachingMs}; LiveCachingMs={LiveCachingMs}; HttpReconnect={HttpReconnect}",
                    streamUri.Scheme,
                    _networkCachingMs,
                    _liveCachingMs,
                    _httpReconnect && IsHttpStream(streamUri));
            }

            EnterPlaybackGcLatencyMode();
            var started = _mediaPlayer.Play(_activeMedia);
            if (!started)
            {
                RestoreGcLatencyMode();
                Emit(new PlayerStatus(PlaybackState.Failed, "Failed to start playback", null, "PLAYBACK_START_FAILED"));
                return;
            }

            _diagnosticsProbe.StartSession(_activeMedia, _mediaPlayer, streamUri.Scheme);
            StartFirstFrameMonitor(
                _activeMedia,
                _mediaPlayer,
                playbackSessionId,
                firstFrameBaseline.DisplayedPictures,
                firstFrameBaseline.DemuxCorrupted);

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RestoreGcLatencyMode();
            _logger.LogError(exception, "VLC playback failed. Scheme={StreamScheme}", streamUri.Scheme);
            Emit(new PlayerStatus(PlaybackState.Failed, "Playback error occurred", null, "PLAYBACK_EXCEPTION"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_mediaPlayer is null)
            {
                Emit(new PlayerStatus(PlaybackState.Failed, "Player is not initialized", null, "PLAYER_NOT_INITIALIZED"));
                return;
            }

            if (_mediaPlayer.IsPlaying)
            {
                return;
            }

            EnterPlaybackGcLatencyMode();
            if (_mediaPlayer.Play())
            {
                TryEmitPresentationReadyPlaying(Interlocked.Read(ref _activePlaybackSessionId));
                return;
            }

            RestoreGcLatencyMode();
            Emit(new PlayerStatus(PlaybackState.Failed, "Failed to resume playback", null, "PLAYBACK_RESUME_FAILED"));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "VLC resume failed");
            Emit(new PlayerStatus(PlaybackState.Failed, "Playback error occurred", null, "PLAYBACK_EXCEPTION"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _activePlaybackSessionId);
            Interlocked.Exchange(ref _presentationReadySessionId, 0);
            Interlocked.Exchange(ref _playingStatusEmitted, 0);
            Interlocked.Exchange(ref _activeRebuffering, 0);
            await StopFirstFrameMonitorAsync();
            await _diagnosticsProbe.StopSessionAsync();
            _mediaPlayer?.Stop();
            _activeMedia?.Dispose();
            _activeMedia = null;
            RestoreGcLatencyMode();
            Emit(new PlayerStatus(PlaybackState.Stopped, "Stopped"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_mediaPlayer is null)
            {
                Emit(new PlayerStatus(PlaybackState.Failed, "Player is not initialized", null, "PLAYER_NOT_INITIALIZED"));
                return;
            }

            if (!_mediaPlayer.IsPlaying)
            {
                return;
            }

            _mediaPlayer.Pause();
            Interlocked.Exchange(ref _playingStatusEmitted, 0);
            RestoreGcLatencyMode();
            Emit(new PlayerStatus(PlaybackState.Paused, "Paused"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Mute = muted;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PlaybackProgress> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mediaPlayer = _mediaPlayer;
        if (mediaPlayer is null)
        {
            return Task.FromResult(new PlaybackProgress(TimeSpan.Zero, TimeSpan.Zero, false));
        }

        var positionMs = Math.Max(0L, mediaPlayer.Time);
        var durationMs = Math.Max(0L, mediaPlayer.Length);
        var canSeek = durationMs > 0 && mediaPlayer.IsSeekable;

        return Task.FromResult(new PlaybackProgress(
            TimeSpan.FromMilliseconds(positionMs),
            TimeSpan.FromMilliseconds(durationMs),
            canSeek));
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var mediaPlayer = _mediaPlayer;
            if (mediaPlayer is null || mediaPlayer.Length <= 0 || !mediaPlayer.IsSeekable)
            {
                return;
            }

            var targetMs = (long)Math.Clamp(
                position.TotalMilliseconds,
                0d,
                Math.Max(0L, mediaPlayer.Length));

            mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(targetMs));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SeekRelativeAsync(TimeSpan offset, CancellationToken cancellationToken = default)
    {
        var mediaPlayer = _mediaPlayer;
        var currentPosition = TimeSpan.FromMilliseconds(Math.Max(0L, mediaPlayer?.Time ?? 0L));
        await SeekAsync(currentPosition + offset, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();

        try
        {
            Interlocked.Increment(ref _activePlaybackSessionId);
            Interlocked.Exchange(ref _presentationReadySessionId, 0);
            Interlocked.Exchange(ref _playingStatusEmitted, 0);
            Interlocked.Exchange(ref _activeRebuffering, 0);
            await StopFirstFrameMonitorAsync();
            await _diagnosticsProbe.StopSessionAsync();
            _activeMedia?.Dispose();
            _activeMedia = null;

            _mediaPlayer?.Dispose();
            _mediaPlayer = null;

            await _diagnosticsProbe.DisposeAsync();
            _libVlc?.Dispose();
            _libVlc = null;
            RestoreGcLatencyMode();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void EnterPlaybackGcLatencyMode()
    {
        lock (_gcLatencyGate)
        {
            if (_playbackGcLatencyActive)
            {
                return;
            }

            var currentMode = GCSettings.LatencyMode;
            if (currentMode is GCLatencyMode.SustainedLowLatency or GCLatencyMode.LowLatency or GCLatencyMode.NoGCRegion)
            {
                return;
            }

            try
            {
                _previousGcLatencyMode = currentMode;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                _playbackGcLatencyActive = true;

                if (_diagnosticsEnabled)
                {
                    _logger.LogInformation(
                        "Playback diagnostics: GC latency mode changed for active playback. PreviousMode={PreviousMode}; ActiveMode={ActiveMode}",
                        currentMode,
                        GCSettings.LatencyMode);
                }
            }
            catch (InvalidOperationException exception)
            {
                _logger.LogDebug(exception, "GC latency mode was not changed for active playback");
            }
        }
    }

    private void RestoreGcLatencyMode()
    {
        lock (_gcLatencyGate)
        {
            if (!_playbackGcLatencyActive)
            {
                return;
            }

            try
            {
                GCSettings.LatencyMode = _previousGcLatencyMode;

                if (_diagnosticsEnabled)
                {
                    _logger.LogInformation(
                        "Playback diagnostics: GC latency mode restored after playback. RestoredMode={RestoredMode}",
                        _previousGcLatencyMode);
                }
            }
            catch (InvalidOperationException exception)
            {
                _logger.LogDebug(exception, "GC latency mode was not restored after playback");
            }
            finally
            {
                _playbackGcLatencyActive = false;
            }
        }
    }

    private bool ShouldSuppressTransientBuffering(double cachePercent)
    {
        // Only ignore a redundant high-cache notification while the player is
        // already playing. A low-cache event is real input starvation and must
        // keep the clean loading surface visible, including during startup.
        return _mediaPlayer?.IsPlaying == true
               && cachePercent >= ActivePlaybackHighCacheBufferingThreshold;
    }

    private bool ShouldLogSuppressedVlcBufferEvent()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSuppressedVlcBufferLogUtc < TimeSpan.FromSeconds(1))
        {
            return false;
        }

        _lastSuppressedVlcBufferLogUtc = now;
        return true;
    }

    private static (long DisplayedPictures, long DemuxCorrupted) ReadFirstFrameBaseline(Media media)
    {
        var statistics = media.Statistics;
        return (statistics.DisplayedPictures, statistics.DemuxCorrupted);
    }

    private void StartFirstFrameMonitor(
        Media media,
        MediaPlayer mediaPlayer,
        long playbackSessionId,
        long displayedPicturesBaseline,
        long demuxCorruptedBaseline)
    {
        var cts = new CancellationTokenSource();
        var monitorTask = MonitorFirstFrameAsync(
            media,
            mediaPlayer,
            playbackSessionId,
            displayedPicturesBaseline,
            demuxCorruptedBaseline,
            cts.Token);

        lock (_firstFrameMonitorGate)
        {
            _firstFrameMonitorCts = cts;
            _firstFrameMonitorTask = monitorTask;
        }
    }

    private async Task MonitorFirstFrameAsync(
        Media media,
        MediaPlayer mediaPlayer,
        long playbackSessionId,
        long displayedPicturesBaseline,
        long demuxCorruptedBaseline,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (playbackSessionId != Interlocked.Read(ref _activePlaybackSessionId))
                {
                    return;
                }

                var statistics = media.Statistics;
                if (statistics.DemuxCorrupted != demuxCorruptedBaseline)
                {
                    demuxCorruptedBaseline = statistics.DemuxCorrupted;
                    displayedPicturesBaseline = statistics.DisplayedPictures;
                    continue;
                }

                if (statistics.DisplayedPictures < displayedPicturesBaseline)
                {
                    displayedPicturesBaseline = statistics.DisplayedPictures;
                    continue;
                }

                if (statistics.DisplayedPictures == displayedPicturesBaseline)
                {
                    if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                if (playbackSessionId != Interlocked.Read(ref _activePlaybackSessionId))
                {
                    return;
                }

                // A displayed picture is not sufficient by itself: a source
                // can briefly expose a first frame while VLC is already
                // buffering. Keep the neutral surface until that buffering
                // episode has completed.
                if (Volatile.Read(ref _activeRebuffering) != 0)
                {
                    if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    continue;
                }

                Interlocked.Exchange(ref _presentationReadySessionId, playbackSessionId);

                if (_diagnosticsEnabled)
                {
                    var elapsed = _playbackStartTimestamp == 0
                        ? TimeSpan.Zero
                        : Stopwatch.GetElapsedTime(_playbackStartTimestamp);
                    _logger.LogInformation(
                        "Playback diagnostics: first displayed video frame is ready. Session={Session}; DisplayedPictures={DisplayedPictures}; StartupElapsedMs={StartupElapsedMs:0}",
                        playbackSessionId,
                        statistics.DisplayedPictures,
                        elapsed.TotalMilliseconds);
                }

                TryEmitPresentationReadyPlaying(playbackSessionId, mediaPlayer);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not verify the first displayed video frame");
        }
    }

    private async Task StopFirstFrameMonitorAsync()
    {
        CancellationTokenSource? cts;
        Task? monitorTask;

        lock (_firstFrameMonitorGate)
        {
            cts = _firstFrameMonitorCts;
            monitorTask = _firstFrameMonitorTask;
            _firstFrameMonitorCts = null;
            _firstFrameMonitorTask = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            if (monitorTask is not null)
            {
                await monitorTask.ConfigureAwait(false);
            }
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void TryEmitPresentationReadyPlaying(long playbackSessionId, MediaPlayer? mediaPlayer = null)
    {
        if (playbackSessionId == 0
            || playbackSessionId != Interlocked.Read(ref _activePlaybackSessionId)
            || playbackSessionId != Interlocked.Read(ref _presentationReadySessionId)
            || Volatile.Read(ref _activeRebuffering) != 0
            || (mediaPlayer ?? _mediaPlayer)?.IsPlaying != true
            || Interlocked.CompareExchange(ref _playingStatusEmitted, 1, 0) != 0)
        {
            return;
        }

        Emit(new PlayerStatus(PlaybackState.Playing, "Playing"));
    }

    private void HookPlayerEvents(MediaPlayer mediaPlayer)
    {
        mediaPlayer.Opening += (_, _) =>
        {
            if (_diagnosticsEnabled)
            {
                _logger.LogInformation("Playback diagnostics: VLC Opening event");
            }

            Emit(new PlayerStatus(PlaybackState.Connecting, "Opening stream"));
        };

        mediaPlayer.Buffering += (_, eventArgs) =>
        {
            if (_diagnosticsEnabled)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastVlcBufferLogUtc >= TimeSpan.FromSeconds(1)
                    || eventArgs.Cache >= 100d)
                {
                    _lastVlcBufferLogUtc = now;
                    _logger.LogInformation(
                        "Playback diagnostics: VLC Buffering event. Cache={CachePercent:0.0}%",
                        eventArgs.Cache);
                }
            }

            if (eventArgs.Cache >= 100d
                && Interlocked.Exchange(ref _activeRebuffering, 0) != 0)
            {
                _lastPlayingTimestamp = Stopwatch.GetTimestamp();
                TryEmitPresentationReadyPlaying(Interlocked.Read(ref _activePlaybackSessionId), mediaPlayer);
                return;
            }

            if (ShouldSuppressTransientBuffering(eventArgs.Cache))
            {
                if (_diagnosticsEnabled && ShouldLogSuppressedVlcBufferEvent())
                {
                    _logger.LogInformation(
                        "Playback diagnostics: transient VLC Buffering event suppressed after active playback. Cache={CachePercent:0.0}%",
                        eventArgs.Cache);
                }

                return;
            }

            if (Interlocked.Exchange(ref _activeRebuffering, 1) == 0)
            {
                Interlocked.Exchange(ref _playingStatusEmitted, 0);
            }

            Emit(new PlayerStatus(PlaybackState.Buffering, "Buffering stream", (float)eventArgs.Cache));
        };

        mediaPlayer.Playing += (_, _) =>
        {
            if (_diagnosticsEnabled)
            {
                var elapsed = _playbackStartTimestamp == 0
                    ? TimeSpan.Zero
                    : Stopwatch.GetElapsedTime(_playbackStartTimestamp);
                _logger.LogInformation(
                    "Playback diagnostics: VLC Playing event. StartupElapsedMs={StartupElapsedMs:0}",
                    elapsed.TotalMilliseconds);
            }

            _lastPlayingTimestamp = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _activeRebuffering, 0);
            TryEmitPresentationReadyPlaying(Interlocked.Read(ref _activePlaybackSessionId), mediaPlayer);
        };

        mediaPlayer.Paused += (_, _) =>
        {
            if (_diagnosticsEnabled)
            {
                _logger.LogInformation("Playback diagnostics: VLC Paused event");
            }

            Interlocked.Exchange(ref _playingStatusEmitted, 0);
            Interlocked.Exchange(ref _activeRebuffering, 0);
            Emit(new PlayerStatus(PlaybackState.Paused, "Paused"));
        };

        mediaPlayer.Stopped += (_, _) =>
        {
            if (_diagnosticsEnabled)
            {
                _logger.LogInformation("Playback diagnostics: VLC Stopped event");
            }

            RestoreGcLatencyMode();
            Interlocked.Exchange(ref _activeRebuffering, 0);
            Emit(new PlayerStatus(PlaybackState.Stopped, "Stopped"));
        };

        mediaPlayer.EndReached += (_, _) =>
        {
            if (_diagnosticsEnabled)
            {
                _logger.LogInformation("Playback diagnostics: VLC EndReached event");
            }

            RestoreGcLatencyMode();
            Interlocked.Exchange(ref _activeRebuffering, 0);
            Emit(new PlayerStatus(PlaybackState.Stopped, "Playback ended"));
        };

        mediaPlayer.EncounteredError += (_, _) =>
        {
            if (_diagnosticsEnabled)
            {
                _logger.LogWarning("Playback diagnostics: VLC EncounteredError event");
            }

            RestoreGcLatencyMode();
            Interlocked.Exchange(ref _activeRebuffering, 0);
            Emit(new PlayerStatus(PlaybackState.Failed, "Playback error", null, "VLC_ERROR"));
        };
    }

    private void Emit(PlayerStatus status)
        => StatusChanged?.Invoke(this, status);

    private static int GetConfiguredCachingMs(IConfiguration configuration, string key, int defaultValue)
    {
        if (!int.TryParse(configuration[key], out var configuredValue))
        {
            return defaultValue;
        }

        return Math.Clamp(configuredValue, MinimumCachingMs, MaximumCachingMs);
    }

    private static bool GetConfiguredBool(IConfiguration configuration, string key, bool defaultValue)
    {
        if (!bool.TryParse(configuration[key], out var configuredValue))
        {
            return defaultValue;
        }

        return configuredValue;
    }

    private static bool IsHttpStream(Uri streamUri)
        => string.Equals(streamUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
           || string.Equals(streamUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string ResolveLibVlcDirectory()
    {
        var architecture = Environment.Is64BitProcess ? "win-x64" : "win-x86";

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, AppContext.BaseDirectory);
        AddRoot(roots, Path.GetDirectoryName(Environment.ProcessPath));
        AddRoot(roots, Directory.GetCurrentDirectory());
        AddRoot(roots, Path.GetDirectoryName(typeof(VlcPlaybackService).Assembly.Location));

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearchDirectories
            && !string.IsNullOrWhiteSpace(nativeSearchDirectories))
        {
            foreach (var nativeDirectory in nativeSearchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddRoot(roots, nativeDirectory);
                AddRoot(roots, Path.GetDirectoryName(nativeDirectory));
            }
        }

        foreach (var root in roots.ToArray())
        {
            var parent = Directory.GetParent(root);
            var depth = 0;
            while (parent is not null && depth < 5)
            {
                AddRoot(roots, parent.FullName);
                parent = parent.Parent;
                depth++;
            }
        }

        var relativeCandidates = new[]
        {
            string.Empty,
            Path.Combine("libvlc", architecture),
            Path.Combine("runtimes", architecture, "native"),
            Path.Combine("dist", "IptvPlayer", "libvlc", architecture),
            Path.Combine("publish", "libvlc", architecture),
            Path.Combine("..", "libvlc", architecture),
            Path.Combine("..", "dist", "IptvPlayer", "libvlc", architecture),
            Path.Combine("..", "..", "dist", "IptvPlayer", "libvlc", architecture),
            Path.Combine("..", "..", "..", "dist", "IptvPlayer", "libvlc", architecture),
        };

        foreach (var root in roots)
        {
            foreach (var relativeCandidate in relativeCandidates)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, relativeCandidate));
                var libVlcDllPath = Path.Combine(candidate, "libvlc.dll");
                if (Directory.Exists(candidate) && File.Exists(libVlcDllPath))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static void AddRoot(ISet<string> roots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var normalized = Path.GetFullPath(candidate);
        if (Directory.Exists(normalized))
        {
            roots.Add(normalized);
        }
    }
}
