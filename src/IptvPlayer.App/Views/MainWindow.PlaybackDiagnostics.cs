#if PLAYBACK_DIAGNOSTICS
using System.Diagnostics;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Windows.Threading;

namespace IptvPlayer.App.Views;

public partial class MainWindow
{
    private int _diagnosticMarkerCount;
    private int _diagnosticVideoHideCount;
    private int _diagnosticVideoZeroSizeCount;
    private readonly TaskCompletionSource _diagnosticShellInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _diagnosticPreviousUiTick;
    private long _diagnosticStartupMarkerTick;
    private long _diagnosticFirstRenderTick;
    private double _diagnosticStartupMaxUiGapAfterFirstRenderMs;
    private double _diagnosticStartupMaxUiGapMs;
    private int _diagnosticStartupUiTicks;
    private int _diagnosticStartupUiGapsOver75Ms;
    private DispatcherTimer? _diagnosticStartupTimer;
    private string _diagnosticStartupPhase = "first-layout";

    private void InitializeDiagnosticMarkers(IConfiguration configuration)
    {
        Title = "Whose IPTV — PLAYBACK TEST";
        if (Environment.GetCommandLineArgs().Contains("--stability-suite", StringComparer.Ordinal))
        {
            // An unattended comparison must not accept unrelated clicks or keys
            // as its scripted navigation. Manual test builds remain interactive.
            IsHitTestVisible = false;
            PreviewKeyDown += (_, e) => e.Handled = true;
        }
        _diagnosticPreviousUiTick = Stopwatch.GetTimestamp();
        _diagnosticStartupMarkerTick = _diagnosticPreviousUiTick;
        System.Windows.Media.CompositionTarget.Rendering += OnDiagnosticFirstRender;
        Closed += (_, _) => System.Windows.Media.CompositionTarget.Rendering -= OnDiagnosticFirstRender;
        _diagnosticStartupTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _diagnosticStartupTimer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var gap = Stopwatch.GetElapsedTime(_diagnosticPreviousUiTick, now).TotalMilliseconds;
            _diagnosticPreviousUiTick = now;
            if (_viewModel.IsStartupLoading || _viewModel.IsLoading || _viewModel.IsImporting || _diagnosticStartupUiTicks == 0)
            {
                _diagnosticStartupUiTicks++;
                _diagnosticStartupMaxUiGapMs = Math.Max(_diagnosticStartupMaxUiGapMs, gap);
                if (_diagnosticFirstRenderTick != 0 && now - (long)(gap * Stopwatch.Frequency / 1000) >= _diagnosticFirstRenderTick)
                    _diagnosticStartupMaxUiGapAfterFirstRenderMs = Math.Max(_diagnosticStartupMaxUiGapAfterFirstRenderMs, gap);
                if (gap > 75)
                {
                    _diagnosticStartupUiGapsOver75Ms++;
                    _logger.LogInformation("Playback test startup UI gap. Phase={Phase}; GapMs={GapMs}; Loaded={Loaded}; LoadingOverlayVisible={LoadingOverlayVisible}",
                        _diagnosticStartupPhase, gap, IsLoaded, StartupLoadingOverlay.IsVisible);
                }
            }
        };
        _diagnosticStartupTimer.Start();
        Closed += (_, _) => _diagnosticStartupTimer.Stop();
        // Thread preprocessing also sees keys when the embedded native video has focus.
        ComponentDispatcher.ThreadPreprocessMessage += OnDiagnosticKeyMessage;
        PlayerView.IsVisibleChanged += (_, _) =>
        {
            if (!PlayerView.IsVisible && _boundMediaPlayer is { State: LibVLCSharp.Shared.VLCState.Playing })
                _diagnosticVideoHideCount++;
        };
        PlayerView.SizeChanged += (_, _) =>
        {
            if ((PlayerView.ActualWidth <= 0 || PlayerView.ActualHeight <= 0)
                && _boundMediaPlayer is { State: LibVLCSharp.Shared.VLCState.Playing })
                _diagnosticVideoZeroSizeCount++;
        };
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= OnDiagnosticKeyMessage;
        if (configuration.GetValue("PlaybackDiagnostics:RunStabilitySuite", false)
            || Environment.GetCommandLineArgs().Contains("--stability-suite", StringComparer.Ordinal))
        {
            Loaded += async (_, _) => await RunPlaybackStabilitySuiteAsync();
        }
        _logger.LogInformation(
            "Playback test started. ProcessId={ProcessId}; TimestampFrequency={TimestampFrequency}; EmbeddedPlayer=true; PlaybackSettings=unchanged; SampleCountersAreNotFramePresentationTimestamps=true",
            Environment.ProcessId, Stopwatch.Frequency);
    }

    private void OnDiagnosticFirstRender(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        _diagnosticFirstRenderTick = Stopwatch.GetTimestamp();
        System.Windows.Media.CompositionTarget.Rendering -= OnDiagnosticFirstRender;
        _logger.LogInformation("Playback test first WPF rendering callback. AfterMarkerMs={AfterMarkerMs}; PhysicalPresentationTimestamp=false",
            Stopwatch.GetElapsedTime(_diagnosticStartupMarkerTick, _diagnosticFirstRenderTick).TotalMilliseconds);
    }

    private void OnDiagnosticKeyMessage(ref MSG message, ref bool handled)
    {
        if (!IsActive || message.message != 0x0100 || message.wParam.ToInt32() != 0x77
            || (message.lParam.ToInt64() & (1L << 30)) != 0) return;
        _diagnosticMarkerCount++;
        _logger.LogInformation(
            "Playback test user marker. Marker={Marker}; CapturedUtc={CapturedUtc:O}; TimestampTicks={TimestampTicks}; MediaTimeMs={MediaTimeMs}; Fullscreen={Fullscreen}",
            _diagnosticMarkerCount, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
            _boundMediaPlayer?.Time, _isFullscreen);
        Title = $"Whose IPTV — TEST VERSION — Freeze marker {_diagnosticMarkerCount} saved — F8 to mark again";
        handled = true;
    }
}
#endif
