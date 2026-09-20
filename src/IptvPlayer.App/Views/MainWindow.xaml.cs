using IptvPlayer.App.Services;
using IptvPlayer.App.Themes;
using IptvPlayer.Contracts.Player;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.ViewModels;
using IptvPlayer.Presentation.Localization;
using System.Collections.Specialized;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IptvPlayer.App.Views;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsFullscreenPresentationActiveProperty = DependencyProperty.Register(
        nameof(IsFullscreenPresentationActive),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    private const double LogicalWheelStep = 0.72d;
    private const double PhysicalWheelStep = 52d;
    private const double SmoothScrollCompletionThreshold = 0.06d;
    private const double SmoothScrollEase = 0.22d;
    private const int NativeArrowCursorId = 32512;
    private const int NativeHandCursorId = 32649;
    private const int ClassLongCursor = -12;
    private const string VlcVideoWindowClassPrefix = "VLC video ";
    private const int WindowMessageSetCursor = 0x0020;
    private const int WindowMessageCreate = 0x0001;
    private const int WindowMessageLeftButtonDown = 0x0201;
    private const int WindowMessageLeftButtonUp = 0x0202;
    private const int WindowMessageMouseMove = 0x0200;
    private const int WindowMessageParentNotify = 0x0210;
    private const double PlayerControlsOverlayHeight = 82d;
    private const double PlayerActionIndicatorSize = 92d;
    private static readonly TimeSpan PlayerControlsAutoHideDelay = TimeSpan.FromSeconds(2.5);
    private static readonly HttpClient RecordingHttpClient = CreateRecordingHttpClient();
    private static readonly Regex InvalidFileNameCharactersPattern = new(
        $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IntPtr ArrowCursorHandle = LoadCursor(IntPtr.Zero, new IntPtr(NativeArrowCursorId));
    private static readonly IntPtr HandCursorHandle = LoadCursor(IntPtr.Zero, new IntPtr(NativeHandCursorId));

    private readonly MainShellViewModel _viewModel;
    private readonly UiLocalization _localization = UiLocalization.Current;
    private readonly INativePlayerBridge _nativePlayerBridge;
    private readonly IPlaybackService _playbackService;
    private readonly ILogger<MainWindow> _logger;
    private readonly bool _playbackDiagnosticsEnabled;
    private readonly Dictionary<ScrollViewer, SmoothScrollState> _smoothScrollStates = [];

    private bool _isFullscreen;
    private bool _isFullscreenTransitioning;
    private Thickness _rootLayoutMargin;
    private CornerRadius _playerCornerRadius;
    private Thickness _playerBorderThickness;
    private System.Windows.Media.Brush? _playerBackground;
    private System.Windows.Media.Effects.Effect? _playerEffect;
    private System.Windows.Media.Brush? _windowBackground;
    private System.Windows.Media.Brush? _rootLayoutBackground;
    private System.Windows.Media.Brush? _playbackPanelBackground;
    private System.Windows.Media.Brush? _playbackPanelBorderBrush;
    private Thickness _playbackPanelMargin;
    private Thickness _playbackPanelPadding;
    private Thickness _playbackPanelBorderThickness;
    private GridLength _sourceColumnWidth;
    private GridLength _channelColumnWidth;
    private GridLength _contentColumnWidth;
    private Visibility _sourcePanelVisibility;
    private Visibility _channelPanelVisibility;
    private int _playerRowHostGridRow;
    private int _playerRowHostGridColumn;
    private int _playerRowHostGridRowSpan = 1;
    private int _playerRowHostGridColumnSpan = 1;
    private GridLength _playerRowHostVideoRowHeight;
    private GridLength _playerRowHostControlsRowHeight;
    private HorizontalAlignment _playerSurfaceHostHorizontalAlignment;
    private VerticalAlignment _playerSurfaceHostVerticalAlignment;
    private double _playerSurfaceHostWidth;
    private double _playerSurfaceHostHeight;
    private readonly Dictionary<UIElement, Visibility> _fullscreenHiddenElementVisibility = [];
    private IntPtr _fullscreenWindowHandle;
    private IntPtr _previousNativeWindowStyle;
    private NativeRect _previousNativeWindowBounds;
    private bool _previousNativeTopmost;
    private WindowState _previousManagedWindowState = WindowState.Normal;
    private bool _hasNativeFullscreenSnapshot;
    private readonly DispatcherTimer _fullscreenExitHintTimer;
    private readonly DispatcherTimer _playerControlsAutoHideTimer;
    private readonly DispatcherTimer _playerPointerPollTimer;
    private readonly DispatcherTimer _playerTimelineTimer;
    private readonly DispatcherTimer _mediaSavedToastTimer;
    private CancellationTokenSource? _playbackDiagnosticsCts;
    private int _lastGen0Collections;
    private int _lastGen1Collections;
    private int _lastGen2Collections;
    private int _pendingGen0Collections;
    private int _pendingGen1Collections;
    private int _pendingGen2Collections;
    private DateTimeOffset _lastUiStallLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGcLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPlayerControlsActivityUtc = DateTimeOffset.MinValue;
    private FullscreenHudWindow? _fullscreenHudWindow;
    private PlayerControlsOverlayWindow? _playerControlsOverlayWindow;
    private PlayerActionIndicatorWindow? _playerActionIndicatorWindow;
    private HwndHost? _playerVideoHost;
    private MediaPlayer? _boundMediaPlayer;
    private CancellationTokenSource? _recordingCts;
    private Task? _recordingTask;
    private static readonly TimeSpan MinimumStartupLoadingDuration = TimeSpan.FromSeconds(1);
    private DateTimeOffset _startupLoadingShownUtc = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _startupLoadingDismissCts;
    private string? _recordingOutputPath;
    private int _recordingSessionId;
    private bool _isRecording;
    private bool _isStretchDisplayMode;
    private bool _suppressPlayerControlsUntilPointerMoves;
    private NativePoint? _lastPointerPosition;

    public bool IsFullscreenPresentationActive
    {
        get => (bool)GetValue(IsFullscreenPresentationActiveProperty);
        private set => SetValue(IsFullscreenPresentationActiveProperty, value);
    }

    public MainWindow(
        MainShellViewModel viewModel,
        IPlaybackService playbackService,
        INativePlayerBridge nativePlayerBridge,
        IConfiguration configuration,
        ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(playbackService);

        _viewModel = viewModel;
        _nativePlayerBridge = nativePlayerBridge;
        _playbackService = playbackService;
        _logger = logger;
#if DEBUG
        _playbackDiagnosticsEnabled = configuration.GetValue("PlaybackDiagnostics:Enabled", defaultValue: false);
#else
        _playbackDiagnosticsEnabled = false;
#endif

        InitializeComponent();
        WindowState = WindowState.Maximized;
        if (_viewModel.IsStartupLoading)
        {
            StartupLoadingOverlay.Visibility = Visibility.Visible;
            StartupLoadingOverlay.Opacity = 1.0;
            PlayerView.Visibility = Visibility.Hidden;
            ApplyStartupBlur(true);
        }
        else
        {
            StartupLoadingOverlay.Visibility = Visibility.Collapsed;
            StartupLoadingOverlay.Opacity = 0;
            PlayerView.ClearValue(VisibilityProperty);
            ApplyStartupBlur(false);
        }
        ApplyUiFlowDirection();
        _localization.CultureChanged += Localization_OnCultureChanged;

        _rootLayoutMargin = RootLayout.Margin;
        _windowBackground = Background;
        _rootLayoutBackground = RootLayout.Background;
        _playerCornerRadius = PlayerSurface.CornerRadius;
        _playerBorderThickness = PlayerSurface.BorderThickness;
        _playerBackground = PlayerSurface.Background;
        _playerEffect = PlayerSurface.Effect;
        _fullscreenExitHintTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _fullscreenExitHintTimer.Tick += FullscreenExitHintTimer_OnTick;
        _playerControlsAutoHideTimer = new DispatcherTimer
        {
            Interval = PlayerControlsAutoHideDelay,
        };
        _playerControlsAutoHideTimer.Tick += PlayerControlsAutoHideTimer_OnTick;
        _playerPointerPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _playerPointerPollTimer.Tick += PlayerPointerPollTimer_OnTick;
        _playerTimelineTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _playerTimelineTimer.Tick += PlayerTimelineTimer_OnTick;
        _mediaSavedToastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _mediaSavedToastTimer.Tick += MediaSavedToastTimer_OnTick;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        _viewModel.VisibleChannels.CollectionChanged += VisibleChannels_OnCollectionChanged;
        PlayerSurface.LayoutUpdated += PlayerSurface_OnLayoutUpdated;

        ThemeManager.Current.ThemeChanged += ThemeManager_OnThemeChanged;
        SourceInitialized += MainWindow_OnSourceInitialized;
        Loaded += MainWindow_OnLoaded;
        LocationChanged += MainWindow_OnFullscreenBoundsChanged;
        SizeChanged += MainWindow_OnFullscreenBoundsChanged;
        StateChanged += MainWindow_OnFullscreenBoundsChanged;
        Closed += MainWindow_OnClosed;
    }

    private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowState = WindowState.Maximized;
        ApplyPremiumTitleBar();
    }

    private void ThemeManager_OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyPremiumTitleBar();
        RefreshThemeSensitiveVisuals();
    }

    private void Localization_OnCultureChanged(object? sender, EventArgs e)
        => ApplyUiFlowDirection();

    public void SetStartupLoadingState(bool hasSavedPlaylists)
    {
        _viewModel.SetStartupLoadingInitialState(hasSavedPlaylists);
        if (hasSavedPlaylists)
        {
            StartupLoadingOverlay.Visibility = Visibility.Visible;
            StartupLoadingOverlay.Opacity = 1.0;
            PlayerView.Visibility = Visibility.Hidden;
            ApplyStartupBlur(true);
        }
        else
        {
            StartupLoadingOverlay.Visibility = Visibility.Collapsed;
            StartupLoadingOverlay.Opacity = 0;
            PlayerView.ClearValue(VisibilityProperty);
            ApplyStartupBlur(false);
        }
    }

    private void ApplyUiFlowDirection()
        => RootLayout.FlowDirection = System.Windows.FlowDirection.LeftToRight;

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_OnLoaded;
        if (WindowState != WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        Activate();
        _startupLoadingShownUtc = DateTimeOffset.UtcNow;
        if (_viewModel.IsStartupLoading)
        {
            HandleStartupLoadingChanged(true);
        }
        else
        {
            StartupLoadingOverlay.Visibility = Visibility.Collapsed;
            StartupLoadingOverlay.Opacity = 0;
            PlayerView.ClearValue(VisibilityProperty);
            ApplyStartupBlur(false);
        }
        ResetPlayerControlsOverlayForContextChange();
        RefreshPlayerActionIndicatorOverlayVisibility();
        _playerPointerPollTimer.Start();
        _playerTimelineTimer.Start();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await InitializeAfterFirstRenderAsync();
    }

    private async Task InitializeAfterFirstRenderAsync()
    {
        _logger.LogInformation("Main window loaded. Fullscreen overlay visibility: {OverlayVisibility}", FullscreenOverlay.Visibility);

        try
        {
            var handleAttempts = 0;
            while (PlayerView.Handle == IntPtr.Zero && handleAttempts < 50)
            {
                await Task.Delay(20);
                handleAttempts++;
            }

            if (PlayerView.Handle != IntPtr.Zero)
            {
                _nativePlayerBridge.SetVideoHostHandle(PlayerView.Handle);
            }

            await _playbackService.InitializeAsync();

            if (_nativePlayerBridge.NativePlayer is MediaPlayer mediaPlayer)
            {
                mediaPlayer.EnableMouseInput = false;
                BindNativePlayerHost(mediaPlayer);
                AttachPlayerVideoInputHook();
                _logger.LogInformation("Player view bound to native media player instance: {PlayerType}", mediaPlayer.GetType().FullName);
            }
            else
            {
                _logger.LogWarning("Native player bridge did not provide a media player instance on load");
            }

            await _viewModel.InitializeAsync();

            if (_boundMediaPlayer is null && _nativePlayerBridge.NativePlayer is MediaPlayer fallbackPlayer)
            {
                fallbackPlayer.EnableMouseInput = false;
                BindNativePlayerHost(fallbackPlayer);
                AttachPlayerVideoInputHook();
                _logger.LogInformation("Player view fallback bound to native media player instance: {PlayerType}", fallbackPlayer.GetType().FullName);
            }

            StartPlaybackDiagnostics();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Main window initialization failed after first render");
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _startupLoadingDismissCts?.Cancel();
        _startupLoadingDismissCts?.Dispose();
        _startupLoadingDismissCts = null;

        if (_isFullscreen)
        {
            ExitFullscreen(animate: false);
        }

        _fullscreenExitHintTimer.Stop();
        _fullscreenExitHintTimer.Tick -= FullscreenExitHintTimer_OnTick;
        _playerControlsAutoHideTimer.Stop();
        _playerControlsAutoHideTimer.Tick -= PlayerControlsAutoHideTimer_OnTick;
        _playerPointerPollTimer.Stop();
        _playerPointerPollTimer.Tick -= PlayerPointerPollTimer_OnTick;
        _playerTimelineTimer.Stop();
        _playerTimelineTimer.Tick -= PlayerTimelineTimer_OnTick;
        _mediaSavedToastTimer.Stop();
        _mediaSavedToastTimer.Tick -= MediaSavedToastTimer_OnTick;
        StopRecordingOnClose();
        StopPlaybackDiagnostics();
        ThemeManager.Current.ThemeChanged -= ThemeManager_OnThemeChanged;
        SourceInitialized -= MainWindow_OnSourceInitialized;
        LocationChanged -= MainWindow_OnFullscreenBoundsChanged;
        SizeChanged -= MainWindow_OnFullscreenBoundsChanged;
        StateChanged -= MainWindow_OnFullscreenBoundsChanged;
        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        _viewModel.VisibleChannels.CollectionChanged -= VisibleChannels_OnCollectionChanged;
        PlayerSurface.LayoutUpdated -= PlayerSurface_OnLayoutUpdated;
        _localization.CultureChanged -= Localization_OnCultureChanged;
        DetachPlayerVideoInputHook();
        UnbindNativePlayerHost();
        ClosePlayerControlsOverlay();
        ClosePlayerActionIndicatorOverlay();

        foreach (var state in _smoothScrollStates.Values)
        {
            state.Stop();
        }

        _smoothScrollStates.Clear();
    }

    private async void ScreenshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_nativePlayerBridge.NativePlayer is not MediaPlayer mediaPlayer || !_viewModel.IsNativeVideoSurfaceVisible)
        {
            MessageBox.Show(this, _localization.GetString("ScreenshotNeedsLive"), _localization.GetString("Screenshot"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = _localization.GetString("SaveScreenshot"),
            Filter = _localization.GetString("PngImageFilter"),
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildDefaultMediaFileName("screenshot", ".png"),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var path = Path.GetFullPath(dialog.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);

            if (!mediaPlayer.TakeSnapshot(0, path, 0, 0))
            {
                throw new InvalidOperationException("VLC could not capture the current frame.");
            }

            await WaitForSnapshotFileAsync(path);
            ShowSavedToast(_localization.GetString("ScreenshotSaved"));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Screenshot capture failed");
            MessageBox.Show(this, _localization.GetString("ScreenshotSaveFailed"), _localization.GetString("Screenshot"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RecordLiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            await StopRecordingFromUserAsync();
            return;
        }

        var channel = _viewModel.SelectedChannel;
        if (channel is null)
        {
            MessageBox.Show(this, _localization.GetString("RecordingNeedsLive"), _localization.GetString("Recording"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = _localization.GetString("SaveRecording"),
            Filter = _localization.GetString("Mp4VideoFilter"),
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildDefaultMediaFileName("recording", ".mp4"),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StartRecording(channel.StreamUri, Path.GetFullPath(dialog.FileName));
    }

    private void StartRecording(Uri streamUri, string outputPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
            var cts = new CancellationTokenSource();
            var sessionId = ++_recordingSessionId;

            _recordingCts = cts;
            _recordingOutputPath = outputPath;
            _isRecording = true;
            UpdateRecordingButton();

            _recordingTask = Task.Run(() => RecordCurrentStreamAsync(streamUri, outputPath, cts.Token), cts.Token);
            _ = ObserveRecordingTaskAsync(_recordingTask, cts, sessionId, outputPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Recording could not be started");
            ResetRecordingState();
            MessageBox.Show(this, _localization.GetString("RecordingStartFailed"), _localization.GetString("Recording"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopRecordingFromUserAsync()
    {
        var cts = _recordingCts;
        var task = _recordingTask;
        var outputPath = _recordingOutputPath;
        ++_recordingSessionId;
        ResetRecordingState();

        if (cts is null || task is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Recording failed while stopping");
            MessageBox.Show(this, _localization.GetString("RecordingStopFailed"), _localization.GetString("Recording"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            cts.Dispose();
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            ShowSavedToast(_localization.GetString("RecordingSaved"));
        }
    }

    private async Task ObserveRecordingTaskAsync(Task recordingTask, CancellationTokenSource cts, int sessionId, string outputPath)
    {
        Exception? failure = null;
        try
        {
            await recordingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            cts.Dispose();
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_recordingSessionId != sessionId)
            {
                return;
            }

            ResetRecordingState();
            if (failure is not null)
            {
                _logger.LogWarning(failure, "Recording failed");
                MessageBox.Show(this, _localization.GetString("RecordingStopFailed"), _localization.GetString("Recording"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (File.Exists(outputPath))
            {
                ShowSavedToast(_localization.GetString("RecordingSaved"));
            }
        });
    }

    private void StopRecordingOnClose()
    {
        ++_recordingSessionId;
        _recordingCts?.Cancel();
        ResetRecordingState();
    }

    private void ResetRecordingState()
    {
        _recordingCts = null;
        _recordingTask = null;
        _recordingOutputPath = null;
        _isRecording = false;
        UpdateRecordingButton();
    }

    private void UpdateRecordingButton()
    {
        _playerControlsOverlayWindow?.SetRecordingState(_isRecording);
        _fullscreenHudWindow?.SetRecordingState(_isRecording);

        if (_isFullscreen)
        {
            ShowFullscreenExitHint();
        }
        else if (IsPlayerControlsSection)
        {
            ShowWindowedPlayerControls();
        }
    }

    private void TogglePlayerDisplayMode()
    {
        _isStretchDisplayMode = !_isStretchDisplayMode;
        ApplyPlayerDisplayMode();
        _playerControlsOverlayWindow?.SetDisplayModeState(_isStretchDisplayMode);
        _fullscreenHudWindow?.SetDisplayModeState(_isStretchDisplayMode);

        if (_isFullscreen)
        {
            ShowFullscreenExitHint();
        }
        else if (IsPlayerControlsSection)
        {
            ShowWindowedPlayerControls();
        }
    }

    private void ApplyPlayerDisplayMode()
    {
        var mediaPlayer = _boundMediaPlayer;
        if (mediaPlayer is null)
        {
            return;
        }

        try
        {
            if (!_isStretchDisplayMode)
            {
                mediaPlayer.AspectRatio = null;
                return;
            }

            var width = Math.Max(1, (int)Math.Round(PlayerSurface.ActualWidth));
            var height = Math.Max(1, (int)Math.Round(PlayerSurface.ActualHeight));
            mediaPlayer.AspectRatio = $"{width}:{height}";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not apply player display mode");
        }
    }

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _logger.LogDebug("PreviewKeyDown received: {Key}. IsFullscreen={IsFullscreen}", e.Key, _isFullscreen);

        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    private void PlayerSurfaceHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            UpdatePlayerOverlayBounds();
            ApplyPlayerDisplayMode();
        }));

    private void PlayerSurface_OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_playerActionIndicatorWindow?.IsVisible == true && !_isFullscreen && !_isFullscreenTransitioning)
        {
            UpdatePlayerActionIndicatorOverlayBounds();
        }
    }

    private void MainWindow_OnFullscreenBoundsChanged(object? sender, EventArgs e)
    {
        if (_isFullscreen)
        {
            UpdateFullscreenHudBounds();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePlayerOverlayBounds));
    }

    private void StartPlaybackDiagnostics()
    {
        if (!_playbackDiagnosticsEnabled || _playbackDiagnosticsCts is not null)
        {
            return;
        }

        _lastGen0Collections = GC.CollectionCount(0);
        _lastGen1Collections = GC.CollectionCount(1);
        _lastGen2Collections = GC.CollectionCount(2);
        _pendingGen0Collections = 0;
        _pendingGen1Collections = 0;
        _pendingGen2Collections = 0;
        _playbackDiagnosticsCts = new CancellationTokenSource();
        _logger.LogInformation("Playback diagnostics: UI/GC monitor started");
        _ = MonitorUiAndGcDuringPlaybackAsync(_playbackDiagnosticsCts.Token);
    }

    private void StopPlaybackDiagnostics()
    {
        _playbackDiagnosticsCts?.Cancel();
        _playbackDiagnosticsCts?.Dispose();
        _playbackDiagnosticsCts = null;
    }

    private async Task MonitorUiAndGcDuringPlaybackAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);

                var postedAt = Stopwatch.GetTimestamp();
                await Dispatcher.InvokeAsync(
                    () => LogUiQueueLatencyIfNeeded(postedAt),
                    DispatcherPriority.Background,
                    cancellationToken);

                LogGcCollectionsIfNeeded();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Playback diagnostics monitor skipped one sample");
            }
        }
    }

    private void LogUiQueueLatencyIfNeeded(long postedAt)
    {
        if (!IsLivePlaybackActiveForDiagnostics())
        {
            return;
        }

        var latencyMs = Stopwatch.GetElapsedTime(postedAt).TotalMilliseconds;
        var thresholdMs = latencyMs switch
        {
            >= 100d => 100,
            >= 50d => 50,
            >= 33d => 33,
            >= 16d => 16,
            _ => 0,
        };

        if (thresholdMs == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastUiStallLogUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastUiStallLogUtc = now;
        _logger.LogWarning(
            "Playback diagnostics: UI thread queue stall while live playback active. LatencyMs={LatencyMs:0.0}; ThresholdMs={ThresholdMs}; OverlayVisible={OverlayVisible}; LoadingOverlayVisible={LoadingOverlayVisible}; Fullscreen={IsFullscreen}",
            latencyMs,
            thresholdMs,
            _viewModel.IsPlayerSurfaceOverlayVisible,
            _viewModel.IsPlayerSurfaceLoadingVisible || _viewModel.IsFullscreenPlaybackLoadingVisible,
            _isFullscreen);
    }

    private void LogGcCollectionsIfNeeded()
    {
        if (!IsLivePlaybackActiveForDiagnostics())
        {
            _lastGen0Collections = GC.CollectionCount(0);
            _lastGen1Collections = GC.CollectionCount(1);
            _lastGen2Collections = GC.CollectionCount(2);
            _pendingGen0Collections = 0;
            _pendingGen1Collections = 0;
            _pendingGen2Collections = 0;
            return;
        }

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var gen0Delta = gen0 - _lastGen0Collections;
        var gen1Delta = gen1 - _lastGen1Collections;
        var gen2Delta = gen2 - _lastGen2Collections;

        _lastGen0Collections = gen0;
        _lastGen1Collections = gen1;
        _lastGen2Collections = gen2;

        _pendingGen0Collections += gen0Delta;
        _pendingGen1Collections += gen1Delta;
        _pendingGen2Collections += gen2Delta;

        var now = DateTimeOffset.UtcNow;
        if (_pendingGen0Collections == 0
            && _pendingGen1Collections == 0
            && _pendingGen2Collections == 0)
        {
            return;
        }

        if (_pendingGen2Collections == 0
            && _pendingGen1Collections == 0
            && now - _lastGcLogUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _logger.LogWarning(
            "Playback diagnostics: GC collections while live playback active. Gen0Delta={Gen0Delta}; Gen1Delta={Gen1Delta}; Gen2Delta={Gen2Delta}",
            _pendingGen0Collections,
            _pendingGen1Collections,
            _pendingGen2Collections);

        _pendingGen0Collections = 0;
        _pendingGen1Collections = 0;
        _pendingGen2Collections = 0;
        _lastGcLogUtc = now;
    }

    private bool IsLivePlaybackActiveForDiagnostics()
        => _viewModel.IsNativeVideoSurfaceVisible && !_viewModel.IsOnDemandPlaybackActive;

    private void ToggleFullscreen()
    {
        if (_isFullscreenTransitioning)
        {
            return;
        }

        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen || _isFullscreenTransitioning)
        {
            return;
        }

        _windowBackground = Background;
        _rootLayoutBackground = RootLayout.Background;
        _playerBackground = PlayerSurface.Background;
        _playerEffect = PlayerSurface.Effect;
        _sourceColumnWidth = SourceColumn.Width;
        _channelColumnWidth = ChannelColumn.Width;
        _contentColumnWidth = ContentColumn.Width;
        _sourcePanelVisibility = SourcePanel.Visibility;
        _channelPanelVisibility = ChannelPanel.Visibility;
        _playbackPanelMargin = PlaybackPanel.Margin;
        _playbackPanelPadding = PlaybackPanel.Padding;
        _playbackPanelBorderThickness = PlaybackPanel.BorderThickness;
        _playbackPanelBackground = PlaybackPanel.Background;
        _playbackPanelBorderBrush = PlaybackPanel.BorderBrush;
        _playerRowHostGridRow = Grid.GetRow(PlayerRowHost);
        _playerRowHostGridColumn = Grid.GetColumn(PlayerRowHost);
        _playerRowHostGridRowSpan = Grid.GetRowSpan(PlayerRowHost);
        _playerRowHostGridColumnSpan = Grid.GetColumnSpan(PlayerRowHost);
        _playerRowHostVideoRowHeight = PlayerRowHost.RowDefinitions[0].Height;
        _playerRowHostControlsRowHeight = PlayerRowHost.RowDefinitions[1].Height;
        _playerSurfaceHostHorizontalAlignment = PlayerSurfaceHost.HorizontalAlignment;
        _playerSurfaceHostVerticalAlignment = PlayerSurfaceHost.VerticalAlignment;
        _playerSurfaceHostWidth = PlayerSurfaceHost.Width;
        _playerSurfaceHostHeight = PlayerSurfaceHost.Height;
        _isFullscreenTransitioning = true;
        HidePlayerControlsOverlay();
        HidePlayerActionIndicatorOverlay();
        CompleteEnterFullscreen();
    }

    private void CompleteEnterFullscreen()
    {
        SuspendPlayerViewForFullscreenTransition();

        if (!TryEnterNativeFullscreen())
        {
            RestorePlayerViewAfterFullscreenTransition();
            _isFullscreenTransitioning = false;
            return;
        }

        RootLayout.Margin = new Thickness(0);
        SetResourceReference(BackgroundProperty, "Brush.WindowBackground");
        RootLayout.SetResourceReference(Panel.BackgroundProperty, "Brush.WindowBackground");
        SourceColumn.Width = new GridLength(0);
        ChannelColumn.Width = new GridLength(0);
        ContentColumn.Width = new GridLength(1, GridUnitType.Star);
        SourcePanel.Visibility = Visibility.Collapsed;
        ChannelPanel.Visibility = Visibility.Collapsed;
        PlaybackPanel.Margin = new Thickness(0);
        PlaybackPanel.Padding = new Thickness(0);
        PlaybackPanel.BorderThickness = new Thickness(0);
        PlaybackPanel.SetResourceReference(Border.BackgroundProperty, "Brush.WindowBackground");
        PlaybackPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.WindowBackground");

        _fullscreenHiddenElementVisibility.Clear();
        foreach (UIElement child in PlaybackLayout.Children)
        {
            if (ReferenceEquals(child, PlayerRowHost))
            {
                continue;
            }

            _fullscreenHiddenElementVisibility[child] = child.Visibility;
            child.Visibility = Visibility.Collapsed;
        }

        Grid.SetRow(PlayerRowHost, 0);
        Grid.SetColumn(PlayerRowHost, 0);
        Grid.SetRowSpan(PlayerRowHost, Math.Max(1, PlaybackLayout.RowDefinitions.Count));
        Grid.SetColumnSpan(PlayerRowHost, Math.Max(1, PlaybackLayout.ColumnDefinitions.Count));
        Panel.SetZIndex(PlayerRowHost, 250);
        PlayerRowHost.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        PlayerRowHost.RowDefinitions[1].Height = new GridLength(0);

        PlayerSurfaceHost.IsAspectRatioEnabled = false;
        PlayerSurfaceHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlayerSurfaceHost.VerticalAlignment = VerticalAlignment.Stretch;
        PlayerSurfaceHost.ClearValue(WidthProperty);
        PlayerSurfaceHost.ClearValue(HeightProperty);
        PlayerSurface.CornerRadius = new CornerRadius(0);
        PlayerSurface.BorderThickness = new Thickness(0);
        PlayerSurface.SetResourceReference(Border.BackgroundProperty, "Theme.Brush.000000");
        PlayerSurface.Effect = null;
        PlayerSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlayerSurface.VerticalAlignment = VerticalAlignment.Stretch;
        PlayerSurface.ClearValue(WidthProperty);
        PlayerSurface.ClearValue(HeightProperty);

        FullscreenOverlay.BeginAnimation(OpacityProperty, null);
        FullscreenOverlay.Opacity = 1d;
        FullscreenOverlay.Visibility = Visibility.Collapsed;
        _fullscreenExitHintTimer.Stop();
        Activate();
        Focus();
        Keyboard.Focus(this);
        _isFullscreen = true;
        IsFullscreenPresentationActive = true;
        UpdatePlayerVideoCursor();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            RestorePlayerViewAfterFullscreenTransition();
            _isFullscreenTransitioning = false;

            _logger.LogInformation(
                "Entered fullscreen. Overlay visibility={OverlayVisibility}; playerHost={HostWidth}x{HostHeight}; player={PlayerWidth}x{PlayerHeight}",
                FullscreenOverlay.Visibility,
                PlayerSurfaceHost.ActualWidth,
                PlayerSurfaceHost.ActualHeight,
                PlayerSurface.ActualWidth,
                PlayerSurface.ActualHeight);

            ShowFullscreenExitHint();
        });
    }

    private void ExitFullscreen(bool animate = true)
    {
        if (!_isFullscreen)
        {
            return;
        }

        if (_isFullscreenTransitioning && animate)
        {
            return;
        }

        _isFullscreenTransitioning = true;
        CompleteExitFullscreen();
    }

    private void CompleteExitFullscreen()
    {
        CloseFullscreenHud();
        SuspendPlayerViewForFullscreenTransition();

        if (!TryExitNativeFullscreen())
        {
            RestorePlayerViewAfterFullscreenTransition();
            _isFullscreenTransitioning = false;
            return;
        }

        FullscreenHost.Children.Clear();
        FullscreenOverlay.BeginAnimation(OpacityProperty, null);
        FullscreenOverlay.Opacity = 1d;
        FullscreenOverlay.Visibility = Visibility.Collapsed;
        RootLayout.Margin = _rootLayoutMargin;
        RestoreWindowedThemeResourceReferences();
        SourceColumn.Width = _sourceColumnWidth;
        ChannelColumn.Width = _channelColumnWidth;
        ContentColumn.Width = _contentColumnWidth;
        SourcePanel.Visibility = _sourcePanelVisibility;
        ChannelPanel.Visibility = _channelPanelVisibility;
        PlaybackPanel.Margin = _playbackPanelMargin;
        PlaybackPanel.Padding = _playbackPanelPadding;
        PlaybackPanel.BorderThickness = _playbackPanelBorderThickness;
        foreach (var (element, visibility) in _fullscreenHiddenElementVisibility)
        {
            element.Visibility = visibility;
        }

        _fullscreenHiddenElementVisibility.Clear();
        Grid.SetRow(PlayerRowHost, _playerRowHostGridRow);
        Grid.SetColumn(PlayerRowHost, _playerRowHostGridColumn);
        Grid.SetRowSpan(PlayerRowHost, _playerRowHostGridRowSpan);
        Grid.SetColumnSpan(PlayerRowHost, _playerRowHostGridColumnSpan);
        Panel.SetZIndex(PlayerRowHost, 0);
        PlayerRowHost.RowDefinitions[0].Height = _playerRowHostVideoRowHeight;
        PlayerRowHost.RowDefinitions[1].Height = _playerRowHostControlsRowHeight;
        PlayerSurfaceHost.IsAspectRatioEnabled = true;
        PlayerSurfaceHost.HorizontalAlignment = _playerSurfaceHostHorizontalAlignment;
        PlayerSurfaceHost.VerticalAlignment = _playerSurfaceHostVerticalAlignment;
        PlayerSurfaceHost.Width = _playerSurfaceHostWidth;
        PlayerSurfaceHost.Height = _playerSurfaceHostHeight;
        PlayerSurface.CornerRadius = _playerCornerRadius;
        PlayerSurface.BorderThickness = _playerBorderThickness;
        PlayerSurface.Effect = _playerEffect;
        _fullscreenExitHintTimer.Stop();

        _isFullscreen = false;
        IsFullscreenPresentationActive = false;
        UpdatePlayerVideoCursor();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            RestorePlayerViewAfterFullscreenTransition();
            _isFullscreenTransitioning = false;
            RefreshPlayerControlsOverlayVisibility(showControls: true);
            RefreshPlayerActionIndicatorOverlayVisibility();
            _logger.LogInformation("Exited fullscreen. Overlay visibility={OverlayVisibility}", FullscreenOverlay.Visibility);
        });
    }

    private void SuspendPlayerViewForFullscreenTransition()
    {
        // LibVLCSharp's transparent foreground window follows this surface during layout.
        // Collapse it for the native frame resize so it never receives transient invalid bounds.
        PlayerView.Visibility = Visibility.Collapsed;
        RootLayout.UpdateLayout();
    }

    private void RestorePlayerViewAfterFullscreenTransition()
    {
        PlayerView.ClearValue(VisibilityProperty);
        PlayerView.InvalidateMeasure();
        PlayerView.InvalidateVisual();
    }

    private bool TryEnterNativeFullscreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero
            || !GetWindowRect(handle, out var previousBounds))
        {
            _logger.LogError("Fullscreen could not capture the current native window bounds. Error={NativeError}", Marshal.GetLastWin32Error());
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            _logger.LogError("Fullscreen could not resolve the active monitor bounds. Error={NativeError}", Marshal.GetLastWin32Error());
            return false;
        }

        var previousStyle = GetWindowLongPtr(handle, WindowLongStyle);
        var previousExtendedStyle = GetWindowLongPtr(handle, WindowLongExtendedStyle);
        var fullscreenStyle = new IntPtr(previousStyle.ToInt64() & ~FullscreenRemovedStyleMask);
        var previousWindowState = WindowState;

        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
            UpdateLayout();
        }

        _ = SetWindowLongPtr(handle, WindowLongStyle, fullscreenStyle);
        var monitorBounds = monitorInfo.Monitor;
        var applied = SetWindowPos(
            handle,
            WindowTopmost,
            monitorBounds.Left,
            monitorBounds.Top,
            monitorBounds.Right - monitorBounds.Left,
            monitorBounds.Bottom - monitorBounds.Top,
            SetWindowPosFrameChanged | SetWindowPosShowWindow | SetWindowPosNoOwnerZOrder);

        if (!applied)
        {
            var nativeError = Marshal.GetLastWin32Error();
            _ = SetWindowLongPtr(handle, WindowLongStyle, previousStyle);
            _ = SetWindowPos(
                handle,
                (previousExtendedStyle.ToInt64() & WindowExtendedStyleTopmost) != 0 ? WindowTopmost : WindowNotTopmost,
                previousBounds.Left,
                previousBounds.Top,
                previousBounds.Right - previousBounds.Left,
                previousBounds.Bottom - previousBounds.Top,
                SetWindowPosFrameChanged | SetWindowPosShowWindow | SetWindowPosNoOwnerZOrder);
            WindowState = previousWindowState;
            _logger.LogError("Fullscreen native window transaction failed. Error={NativeError}", nativeError);
            return false;
        }

        _fullscreenWindowHandle = handle;
        _previousNativeWindowStyle = previousStyle;
        _previousNativeWindowBounds = previousBounds;
        _previousNativeTopmost = (previousExtendedStyle.ToInt64() & WindowExtendedStyleTopmost) != 0;
        _previousManagedWindowState = previousWindowState;
        _hasNativeFullscreenSnapshot = true;
        return true;
    }

    private bool TryExitNativeFullscreen()
    {
        if (!_hasNativeFullscreenSnapshot || _fullscreenWindowHandle == IntPtr.Zero)
        {
            _logger.LogError("Fullscreen exit could not restore the native window because no valid snapshot exists");
            return false;
        }

        _ = SetWindowLongPtr(_fullscreenWindowHandle, WindowLongStyle, _previousNativeWindowStyle);
        var bounds = _previousNativeWindowBounds;
        var restored = SetWindowPos(
            _fullscreenWindowHandle,
            _previousNativeTopmost ? WindowTopmost : WindowNotTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            SetWindowPosFrameChanged | SetWindowPosShowWindow | SetWindowPosNoOwnerZOrder);

        if (!restored)
        {
            _logger.LogError("Fullscreen exit native window transaction failed. Error={NativeError}", Marshal.GetLastWin32Error());
            return false;
        }

        WindowState = _previousManagedWindowState;

        _fullscreenWindowHandle = IntPtr.Zero;
        _previousNativeWindowStyle = IntPtr.Zero;
        _previousNativeWindowBounds = default;
        _previousNativeTopmost = false;
        _previousManagedWindowState = WindowState.Normal;
        _hasNativeFullscreenSnapshot = false;
        return true;
    }

    private const int WindowLongStyle = -16;
    private const int WindowLongExtendedStyle = -20;
    private const long WindowStyleCaption = 0x00C00000L;
    private const long WindowStyleThickFrame = 0x00040000L;
    private const long WindowStyleSystemMenu = 0x00080000L;
    private const long WindowStyleMinimizeBox = 0x00020000L;
    private const long WindowStyleMaximizeBox = 0x00010000L;
    private const long FullscreenRemovedStyleMask = WindowStyleCaption
                                                    | WindowStyleThickFrame
                                                    | WindowStyleSystemMenu
                                                    | WindowStyleMinimizeBox
                                                    | WindowStyleMaximizeBox;
    private const long WindowExtendedStyleTopmost = 0x00000008L;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosNoOwnerZOrder = 0x0200;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const uint SetWindowPosShowWindow = 0x0040;
    private static readonly IntPtr WindowTopmost = new(-1);
    private static readonly IntPtr WindowNotTopmost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    private delegate bool EnumChildWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parentWindow,
        EnumChildWindowCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr64(IntPtr window, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
    private static extern uint SetClassLong32(IntPtr window, int index, uint newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, newValue)
            : new IntPtr(SetWindowLong32(window, index, newValue.ToInt32()));

    private static IntPtr SetClassLongPtr(IntPtr window, int index, IntPtr newValue)
        => IntPtr.Size == 8
            ? SetClassLongPtr64(window, index, newValue)
            : new IntPtr(unchecked((int)SetClassLong32(window, index, unchecked((uint)newValue.ToInt32()))));

    private void FullscreenToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Fullscreen toggle button clicked. IsFullscreen={IsFullscreen}", _isFullscreen);
        ToggleFullscreen();
        e.Handled = true;
    }

    private void PlayerSurface_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        TryEnterFullscreenFromPlayerSurface();
        e.Handled = true;
    }

    private void AttachPlayerVideoInputHook()
    {
        if (PlayerView.Handle == IntPtr.Zero)
        {
            _logger.LogWarning("Native video host was not available for mini-player input");
            return;
        }

        if (ReferenceEquals(_playerVideoHost, PlayerView))
        {
            return;
        }

        DetachPlayerVideoInputHook();
        _playerVideoHost = PlayerView;
        _playerVideoHost.MessageHook += PlayerVideoHost_OnMessage;
        UpdatePlayerVideoCursor();
        _ = AttachNativeVideoCursorAfterWindowCreatedAsync();
    }

    private void BindNativePlayerHost(MediaPlayer mediaPlayer)
    {
        if (PlayerView.Handle == IntPtr.Zero)
        {
            _logger.LogWarning("Native video host handle was not available for media player binding");
            return;
        }

        mediaPlayer.Hwnd = PlayerView.Handle;
        _nativePlayerBridge.SetVideoHostHandle(PlayerView.Handle);
        _boundMediaPlayer = mediaPlayer;
        ApplyPlayerDisplayMode();
    }

    private void UnbindNativePlayerHost()
    {
        if (_boundMediaPlayer is null)
        {
            return;
        }

        _boundMediaPlayer.Hwnd = IntPtr.Zero;
        _boundMediaPlayer = null;
    }

    private async Task AttachNativeVideoCursorAfterWindowCreatedAsync()
    {
        const int maximumAttempts = 16;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            if (_playerVideoHost is null || !IsLoaded)
            {
                return;
            }

            var attached = await Dispatcher.InvokeAsync(
                () => SetNativeVideoWindowCursor(_isFullscreen ? ArrowCursorHandle : HandCursorHandle),
                DispatcherPriority.Loaded);
            if (attached)
            {
                return;
            }
        }
    }

    private void UpdatePlayerVideoCursor()
    {
        if (_playerVideoHost is null)
        {
            return;
        }

        _playerVideoHost.Cursor = _isFullscreen ? Cursors.Arrow : Cursors.Hand;
        _playerVideoHost.ForceCursor = true;
        SetNativeVideoWindowCursor(_isFullscreen ? ArrowCursorHandle : HandCursorHandle);
    }

    private bool SetNativeVideoWindowCursor(IntPtr cursorHandle)
    {
        if (_playerVideoHost is null || cursorHandle == IntPtr.Zero)
        {
            return false;
        }

        var cursorAssigned = false;
        EnumChildWindows(
            _playerVideoHost.Handle,
            (window, _) =>
            {
                var className = new StringBuilder(128);
                if (GetClassName(window, className, className.Capacity) > 0
                    && className.ToString().StartsWith(VlcVideoWindowClassPrefix, StringComparison.Ordinal))
                {
                    SetClassLongPtr(window, ClassLongCursor, cursorHandle);
                    cursorAssigned = true;
                }

                return true;
            },
            IntPtr.Zero);
        return cursorAssigned;
    }

    private void DetachPlayerVideoInputHook()
    {
        if (_playerVideoHost is null)
        {
            return;
        }

        _playerVideoHost.MessageHook -= PlayerVideoHost_OnMessage;
        SetNativeVideoWindowCursor(ArrowCursorHandle);
        _playerVideoHost.ForceCursor = false;
        _playerVideoHost.ClearValue(FrameworkElement.CursorProperty);
        _playerVideoHost = null;
    }

    private IntPtr PlayerVideoHost_OnMessage(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WindowMessageSetCursor
            && !_isFullscreen
            && HandCursorHandle != IntPtr.Zero)
        {
            SetCursor(HandCursorHandle);
            handled = true;
            return new IntPtr(1);
        }

        if (message == WindowMessageMouseMove && !_isFullscreen && IsPlayerControlsSection)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (AcceptPlayerControlsPointerActivity())
                {
                    ShowWindowedPlayerControls();
                }
            }));
        }

        var isDirectClick = message == WindowMessageLeftButtonUp;
        var parentNotification = message == WindowMessageParentNotify
            ? (int)((long)wParam & 0xFFFF)
            : 0;
        if (parentNotification == WindowMessageCreate)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePlayerVideoCursor));
        }

        var isChildClick = parentNotification == WindowMessageLeftButtonDown;

        if (isDirectClick || isChildClick)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(TryEnterFullscreenFromPlayerSurface));
        }

        return IntPtr.Zero;
    }

    private void TryEnterFullscreenFromPlayerSurface()
    {
        if (_isFullscreen
            || _isFullscreenTransitioning
            || !_viewModel.IsNativeVideoSurfaceVisible)
        {
            return;
        }

        _logger.LogInformation("Mini player clicked; entering fullscreen");
        ToggleFullscreen();
    }

    private void ChooseM3uFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _localization.GetString("SelectM3uFile"),
            Filter = _localization.GetString("M3uFileFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _viewModel.PlaylistInput = dialog.FileName;
        if (string.IsNullOrWhiteSpace(_viewModel.PlaylistDisplayName))
        {
            _viewModel.PlaylistDisplayName = Path.GetFileNameWithoutExtension(dialog.FileName);
        }

        e.Handled = true;
    }

    private void SearchTextBox_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox searchTextBox || !searchTextBox.IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!searchTextBox.IsVisible)
            {
                return;
            }

            searchTextBox.Focus();
            Keyboard.Focus(searchTextBox);
            searchTextBox.SelectAll();
        });
    }

    private void SmoothList_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = sender as ScrollViewer
            ?? (sender is DependencyObject dependencyObject ? FindVisualChild<ScrollViewer>(dependencyObject) : null);
        if (scrollViewer is null)
        {
            return;
        }

        var wheelStep = GetWheelStep(sender, scrollViewer);
        QueueSmoothScroll(scrollViewer, (-((double)e.Delta / 120.0)) * wheelStep);
        e.Handled = true;
    }

    private static double GetWheelStep(object sender, ScrollViewer scrollViewer)
    {
        // 92px to 128px step provides fluid, effortless scrolling aligned with 60fps physics
        return Math.Clamp(scrollViewer.ViewportHeight * 0.22d, 92.0d, 128.0d);
    }

    private void QueueSmoothScroll(ScrollViewer scrollViewer, double delta)
    {
        var maxOffset = Math.Max(0d, scrollViewer.ScrollableHeight);
        if (maxOffset <= 0d)
        {
            return;
        }

        var state = GetSmoothScrollState(scrollViewer);
        if (!state.IsActive)
        {
            state.TargetOffset = scrollViewer.VerticalOffset;
        }

        state.TargetOffset = Math.Clamp(state.TargetOffset + delta, 0d, maxOffset);
        state.Start();
    }

    private SmoothScrollState GetSmoothScrollState(ScrollViewer scrollViewer)
    {
        if (_smoothScrollStates.TryGetValue(scrollViewer, out var state))
        {
            return state;
        }

        state = new SmoothScrollState(scrollViewer);
        _smoothScrollStates[scrollViewer] = state;
        return state;
    }

    private sealed class SmoothScrollState
    {
        private readonly ScrollViewer _scrollViewer;
        private EventHandler? _renderingHandler;

        public double TargetOffset { get; set; }
        public bool IsActive { get; private set; }

        public SmoothScrollState(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
        }

        public void Start()
        {
            if (IsActive)
            {
                return;
            }

            IsActive = true;
            _renderingHandler = OnRendering;
            System.Windows.Media.CompositionTarget.Rendering += _renderingHandler;
        }

        public void Stop()
        {
            if (!IsActive)
            {
                return;
            }

            IsActive = false;
            if (_renderingHandler is not null)
            {
                System.Windows.Media.CompositionTarget.Rendering -= _renderingHandler;
                _renderingHandler = null;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_scrollViewer.IsVisible || _scrollViewer.ScrollableHeight <= 0d)
            {
                Stop();
                return;
            }

            var maxOffset = Math.Max(0d, _scrollViewer.ScrollableHeight);
            TargetOffset = Math.Clamp(TargetOffset, 0d, maxOffset);

            var currentOffset = _scrollViewer.VerticalOffset;
            var remaining = TargetOffset - currentOffset;

            if (Math.Abs(remaining) <= 0.35d)
            {
                _scrollViewer.ScrollToVerticalOffset(TargetOffset);
                Stop();
                return;
            }

            // Smooth 60fps exponential easing curve (0.165 factor gives fluid, non-jarring glide)
            var nextOffset = currentOffset + (remaining * 0.165d);
            _scrollViewer.ScrollToVerticalOffset(nextOffset);

            if (Math.Abs(_scrollViewer.VerticalOffset - currentOffset) < 0.001d && Math.Abs(remaining) < 0.8d)
            {
                _scrollViewer.ScrollToVerticalOffset(TargetOffset);
                Stop();
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void FullscreenOverlay_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen)
        {
            return;
        }

        ShowFullscreenExitHint();
    }

    private void ShowFullscreenExitHint()
    {
        ShowFullscreenHud();
        _fullscreenExitHintTimer.Stop();
        if (_viewModel.IsPlaybackPlaying && !_isRecording)
        {
            _fullscreenExitHintTimer.Start();
        }
    }

    private void FullscreenExitHintTimer_OnTick(object? sender, EventArgs e)
    {
        _fullscreenExitHintTimer.Stop();

        if (_isFullscreen)
        {
            if (!_viewModel.IsPlaybackPlaying
                || _isRecording
                || _fullscreenHudWindow?.IsPointerOverControls == true)
            {
                _fullscreenHudWindow?.SetChromeVisible(true);
                if (_viewModel.IsPlaybackPlaying && !_isRecording)
                {
                    _fullscreenExitHintTimer.Start();
                }

                return;
            }

            _fullscreenHudWindow?.SetChromeVisible(false);
        }
    }

    private void ShowFullscreenHud()
    {
        if (_fullscreenHudWindow is null)
        {
            var hudWindow = new FullscreenHudWindow
            {
                Owner = this,
                DataContext = _viewModel,
            };

            hudWindow.ActivityDetected += FullscreenHudWindow_OnActivityDetected;
            hudWindow.ExitRequested += FullscreenHudWindow_OnExitRequested;
            hudWindow.ScreenshotRequested += FullscreenHudWindow_OnScreenshotRequested;
            hudWindow.RecordRequested += FullscreenHudWindow_OnRecordRequested;
            hudWindow.DisplayModeRequested += FullscreenHudWindow_OnDisplayModeRequested;
            hudWindow.Closed += FullscreenHudWindow_OnClosed;
            _fullscreenHudWindow = hudWindow;
            hudWindow.SetRecordingState(_isRecording);
            hudWindow.SetDisplayModeState(_isStretchDisplayMode);
        }

        if (!_fullscreenHudWindow.IsVisible)
        {
            if (!TryApplyFullscreenHudLogicalBounds(_fullscreenHudWindow))
            {
                _logger.LogWarning("Fullscreen HUD was not shown because finite owner bounds were unavailable");
                return;
            }

            _fullscreenHudWindow.Show();
        }

        UpdateFullscreenHudBounds();
        _fullscreenHudWindow.SetChromeVisible(true);
    }

    private void UpdateFullscreenHudBounds()
    {
        if (_fullscreenHudWindow is null)
        {
            return;
        }

        var ownerHandle = new WindowInteropHelper(this).Handle;
        var hudHandle = new WindowInteropHelper(_fullscreenHudWindow).Handle;
        if (ownerHandle == IntPtr.Zero
            || hudHandle == IntPtr.Zero
            || !GetWindowRect(ownerHandle, out var ownerBounds))
        {
            return;
        }

        ApplyFullscreenHudLogicalBounds(_fullscreenHudWindow, ownerBounds);

        _ = SetWindowPos(
            hudHandle,
            IntPtr.Zero,
            ownerBounds.Left,
            ownerBounds.Top,
            ownerBounds.Right - ownerBounds.Left,
            ownerBounds.Bottom - ownerBounds.Top,
            SetWindowPosNoZOrder | SetWindowPosNoActivate | SetWindowPosNoOwnerZOrder);
    }

    private bool TryApplyFullscreenHudLogicalBounds(FullscreenHudWindow hudWindow)
    {
        var ownerHandle = new WindowInteropHelper(this).Handle;
        if (ownerHandle == IntPtr.Zero || !GetWindowRect(ownerHandle, out var ownerBounds))
        {
            return false;
        }

        return ApplyFullscreenHudLogicalBounds(hudWindow, ownerBounds);
    }

    private bool ApplyFullscreenHudLogicalBounds(FullscreenHudWindow hudWindow, NativeRect ownerBounds)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY;
        var width = (ownerBounds.Right - ownerBounds.Left) / scaleX;
        var height = (ownerBounds.Bottom - ownerBounds.Top) / scaleY;
        var left = ownerBounds.Left / scaleX;
        var top = ownerBounds.Top / scaleY;

        if (!double.IsFinite(width)
            || !double.IsFinite(height)
            || !double.IsFinite(left)
            || !double.IsFinite(top)
            || width <= 0d
            || height <= 0d)
        {
            _logger.LogWarning(
                "Rejected invalid fullscreen HUD bounds. Left={Left}; Top={Top}; Width={Width}; Height={Height}; ScaleX={ScaleX}; ScaleY={ScaleY}",
                left,
                top,
                width,
                height,
                scaleX,
                scaleY);
            return false;
        }

        hudWindow.Left = left;
        hudWindow.Top = top;
        hudWindow.Width = width;
        hudWindow.Height = height;
        return true;
    }

    private void CloseFullscreenHud()
    {
        if (_fullscreenHudWindow is null)
        {
            return;
        }

        var hudWindow = _fullscreenHudWindow;
        _fullscreenHudWindow = null;

        hudWindow.ActivityDetected -= FullscreenHudWindow_OnActivityDetected;
        hudWindow.ExitRequested -= FullscreenHudWindow_OnExitRequested;
        hudWindow.ScreenshotRequested -= FullscreenHudWindow_OnScreenshotRequested;
        hudWindow.RecordRequested -= FullscreenHudWindow_OnRecordRequested;
        hudWindow.DisplayModeRequested -= FullscreenHudWindow_OnDisplayModeRequested;
        hudWindow.Closed -= FullscreenHudWindow_OnClosed;
        hudWindow.Close();
    }

    private void FullscreenHudWindow_OnActivityDetected(object? sender, EventArgs e)
    {
        if (_isFullscreen)
        {
            ShowFullscreenExitHint();
        }
    }

    private void FullscreenHudWindow_OnExitRequested(object? sender, EventArgs e)
        => ExitFullscreen();

    private void FullscreenHudWindow_OnScreenshotRequested(object? sender, EventArgs e)
        => ScreenshotButton_OnClick(this, new RoutedEventArgs());

    private void FullscreenHudWindow_OnRecordRequested(object? sender, EventArgs e)
        => RecordLiveButton_OnClick(this, new RoutedEventArgs());

    private void FullscreenHudWindow_OnDisplayModeRequested(object? sender, EventArgs e)
        => TogglePlayerDisplayMode();

    private void FullscreenHudWindow_OnClosed(object? sender, EventArgs e)
        => _fullscreenHudWindow = null;

    private void EnsurePlayerControlsOverlay()
    {
        if (_playerControlsOverlayWindow is not null)
        {
            return;
        }

        var overlayWindow = new PlayerControlsOverlayWindow
        {
            Owner = this,
            DataContext = _viewModel,
        };
        overlayWindow.ActivityDetected += PlayerControlsOverlayWindow_OnActivityDetected;
        overlayWindow.ScreenshotRequested += PlayerControlsOverlayWindow_OnScreenshotRequested;
        overlayWindow.RecordRequested += PlayerControlsOverlayWindow_OnRecordRequested;
        overlayWindow.DisplayModeRequested += PlayerControlsOverlayWindow_OnDisplayModeRequested;
        overlayWindow.FullscreenRequested += PlayerControlsOverlayWindow_OnFullscreenRequested;
        overlayWindow.Closed += PlayerControlsOverlayWindow_OnClosed;
        overlayWindow.SetRecordingState(_isRecording);
        overlayWindow.SetDisplayModeState(_isStretchDisplayMode);
        _playerControlsOverlayWindow = overlayWindow;
    }

    private void RefreshPlayerControlsOverlayVisibility(bool showControls, bool immediate = false)
    {
        var shouldShow = IsLoaded
                         && IsVisible
                         && WindowState != WindowState.Minimized
                         && !_isFullscreen
                         && !_isFullscreenTransitioning
                         && IsPlayerControlsSection;
        if (!shouldShow)
        {
            HidePlayerControlsOverlay();
            return;
        }

        EnsurePlayerControlsOverlay();
        UpdatePlayerControlsOverlayBounds();
        if (_playerControlsOverlayWindow is null)
        {
            return;
        }

        if (!_playerControlsOverlayWindow.IsVisible)
        {
            _playerControlsOverlayWindow.Show();
            UpdatePlayerControlsOverlayBounds();
        }

        if (showControls)
        {
            _lastPlayerControlsActivityUtc = DateTimeOffset.UtcNow;
            _playerControlsOverlayWindow.SetControlsVisible(true, immediate);
            SchedulePlayerControlsAutoHide();
        }
    }

    private void ShowWindowedPlayerControls()
    {
        _lastPlayerControlsActivityUtc = DateTimeOffset.UtcNow;
        RefreshPlayerControlsOverlayVisibility(showControls: false);
        if (_playerControlsOverlayWindow is null || !_playerControlsOverlayWindow.IsVisible)
        {
            return;
        }

        _playerControlsOverlayWindow.SetControlsVisible(true);
        SchedulePlayerControlsAutoHide();
    }

    private void SchedulePlayerControlsAutoHide()
    {
        _playerControlsAutoHideTimer.Stop();
        if (_viewModel.IsPlaybackPlaying && !_isRecording)
        {
            var idleTime = DateTimeOffset.UtcNow - _lastPlayerControlsActivityUtc;
            var remainingDelay = PlayerControlsAutoHideDelay - idleTime;
            _playerControlsAutoHideTimer.Interval = remainingDelay > TimeSpan.FromMilliseconds(50)
                ? remainingDelay
                : TimeSpan.FromMilliseconds(50);
            _playerControlsAutoHideTimer.Start();
        }
    }

    private void PlayerControlsAutoHideTimer_OnTick(object? sender, EventArgs e)
    {
        _playerControlsAutoHideTimer.Stop();
        var overlayWindow = _playerControlsOverlayWindow;
        if (overlayWindow is null || !overlayWindow.IsVisible)
        {
            return;
        }

        if (!_viewModel.IsPlaybackPlaying || _isRecording || overlayWindow.IsPointerOverControls)
        {
            _lastPlayerControlsActivityUtc = DateTimeOffset.UtcNow;
            overlayWindow.SetControlsVisible(true);
            SchedulePlayerControlsAutoHide();
            return;
        }

        var idleTime = DateTimeOffset.UtcNow - _lastPlayerControlsActivityUtc;
        if (idleTime < PlayerControlsAutoHideDelay)
        {
            SchedulePlayerControlsAutoHide();
            return;
        }

        overlayWindow.SetControlsVisible(false);
    }

    private void PlayerPointerPollTimer_OnTick(object? sender, EventArgs e)
    {
        if (!IsLoaded || !GetCursorPos(out var pointerPosition))
        {
            return;
        }

        var pointerMoved = !_lastPointerPosition.HasValue
                           || _lastPointerPosition.Value.X != pointerPosition.X
                           || _lastPointerPosition.Value.Y != pointerPosition.Y;
        _lastPointerPosition = pointerPosition;
        if (!pointerMoved)
        {
            return;
        }

        _suppressPlayerControlsUntilPointerMoves = false;

        if (_isFullscreen)
        {
            ShowFullscreenExitHint();
            return;
        }

        if (IsPlayerControlsSection && IsPointerInsidePlayerSurface(pointerPosition))
        {
            ShowWindowedPlayerControls();
        }
    }

    private bool IsPointerInsidePlayerSurface(NativePoint pointerPosition)
    {
        if (!PlayerSurface.IsVisible || PlayerSurface.ActualWidth <= 0d || PlayerSurface.ActualHeight <= 0d)
        {
            return false;
        }

        try
        {
            var topLeft = PlayerSurface.PointToScreen(new Point(0d, 0d));
            var bottomRight = PlayerSurface.PointToScreen(new Point(PlayerSurface.ActualWidth, PlayerSurface.ActualHeight));
            return pointerPosition.X >= topLeft.X
                   && pointerPosition.X <= bottomRight.X
                   && pointerPosition.Y >= topLeft.Y
                   && pointerPosition.Y <= bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void UpdatePlayerOverlayBounds()
    {
        UpdatePlayerControlsOverlayBounds();
        UpdatePlayerActionIndicatorOverlayBounds();
    }

    private void UpdatePlayerControlsOverlayBounds()
    {
        var overlayWindow = _playerControlsOverlayWindow;
        if (overlayWindow is null
            || _isFullscreen
            || !PlayerSurface.IsVisible
            || PlayerSurface.ActualWidth <= 0d
            || PlayerSurface.ActualHeight <= 0d)
        {
            return;
        }

        try
        {
            var overlayHeight = Math.Min(PlayerControlsOverlayHeight, PlayerSurface.ActualHeight);
            var screenPoint = PlayerSurface.PointToScreen(new Point(0d, PlayerSurface.ActualHeight - overlayHeight));
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(PlayerSurface);
            var left = screenPoint.X / dpi.DpiScaleX;
            var top = screenPoint.Y / dpi.DpiScaleY;
            if (!double.IsFinite(left) || !double.IsFinite(top))
            {
                return;
            }

            overlayWindow.Left = left;
            overlayWindow.Top = top;
            overlayWindow.Width = PlayerSurface.ActualWidth;
            overlayWindow.Height = overlayHeight;
        }
        catch (InvalidOperationException)
        {
            // The visual can be temporarily disconnected during fullscreen and DPI transitions.
        }
    }

    private void EnsurePlayerActionIndicatorOverlay()
    {
        if (_playerActionIndicatorWindow is not null)
        {
            return;
        }

        var indicatorWindow = new PlayerActionIndicatorWindow
        {
            Owner = this,
            DataContext = _viewModel,
        };
        indicatorWindow.Closed += PlayerActionIndicatorWindow_OnClosed;
        _playerActionIndicatorWindow = indicatorWindow;
    }

    private void RefreshPlayerActionIndicatorOverlayVisibility()
    {
        var shouldShow = IsLoaded
                         && IsVisible
                         && WindowState != WindowState.Minimized
                         && !_isFullscreen
                         && !_isFullscreenTransitioning
                         && _viewModel.IsPlayerActionIndicatorVisible;
        if (!shouldShow)
        {
            HidePlayerActionIndicatorOverlay();
            return;
        }

        EnsurePlayerActionIndicatorOverlay();
        UpdatePlayerActionIndicatorOverlayBounds();
        if (_playerActionIndicatorWindow is null || _playerActionIndicatorWindow.IsVisible)
        {
            return;
        }

        _playerActionIndicatorWindow.Show();
        UpdatePlayerActionIndicatorOverlayBounds();
    }

    private void UpdatePlayerActionIndicatorOverlayBounds()
    {
        var indicatorWindow = _playerActionIndicatorWindow;
        if (indicatorWindow is null
            || _isFullscreen
            || !PlayerSurface.IsVisible
            || PlayerSurface.ActualWidth <= 0d
            || PlayerSurface.ActualHeight <= 0d)
        {
            return;
        }

        try
        {
            if (!TryGetPlayerVideoScreenBounds(out var playerBounds))
            {
                return;
            }

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(PlayerSurface);
            var playerWidth = playerBounds.Right - playerBounds.Left;
            var playerHeight = playerBounds.Bottom - playerBounds.Top;
            var indicatorWidth = Math.Min(playerWidth, Math.Max(1, (int)Math.Round(PlayerActionIndicatorSize * dpi.DpiScaleX)));
            var indicatorHeight = Math.Min(playerHeight, Math.Max(1, (int)Math.Round(PlayerActionIndicatorSize * dpi.DpiScaleY)));
            var leftPixels = playerBounds.Left + ((playerWidth - indicatorWidth) / 2);
            var topPixels = playerBounds.Top + ((playerHeight - indicatorHeight) / 2);
            var left = leftPixels / dpi.DpiScaleX;
            var top = topPixels / dpi.DpiScaleY;
            if (!double.IsFinite(left) || !double.IsFinite(top))
            {
                return;
            }

            indicatorWindow.Left = left;
            indicatorWindow.Top = top;
            indicatorWindow.Width = indicatorWidth / dpi.DpiScaleX;
            indicatorWindow.Height = indicatorHeight / dpi.DpiScaleY;

            var indicatorHandle = new WindowInteropHelper(indicatorWindow).Handle;
            if (indicatorHandle != IntPtr.Zero
                && (!GetWindowRect(indicatorHandle, out var currentBounds)
                    || currentBounds.Left != leftPixels
                    || currentBounds.Top != topPixels
                    || currentBounds.Right - currentBounds.Left != indicatorWidth
                    || currentBounds.Bottom - currentBounds.Top != indicatorHeight))
            {
                _ = SetWindowPos(
                    indicatorHandle,
                    IntPtr.Zero,
                    leftPixels,
                    topPixels,
                    indicatorWidth,
                    indicatorHeight,
                    SetWindowPosNoZOrder | SetWindowPosNoActivate | SetWindowPosNoOwnerZOrder);
            }
        }
        catch (InvalidOperationException)
        {
            // The player can be briefly disconnected while switching fullscreen or DPI contexts.
        }
    }

    private bool TryGetPlayerVideoScreenBounds(out NativeRect bounds)
    {
        var playerHandle = PlayerView.Handle;
        if (PlayerView.IsVisible
            && playerHandle != IntPtr.Zero
            && GetWindowRect(playerHandle, out bounds)
            && bounds.Right > bounds.Left
            && bounds.Bottom > bounds.Top)
        {
            return true;
        }

        var topLeft = PlayerSurface.PointToScreen(new Point(0d, 0d));
        var bottomRight = PlayerSurface.PointToScreen(
            new Point(PlayerSurface.ActualWidth, PlayerSurface.ActualHeight));
        bounds = new NativeRect
        {
            Left = (int)Math.Round(topLeft.X),
            Top = (int)Math.Round(topLeft.Y),
            Right = (int)Math.Round(bottomRight.X),
            Bottom = (int)Math.Round(bottomRight.Y),
        };
        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    private void HidePlayerActionIndicatorOverlay()
        => _playerActionIndicatorWindow?.Hide();

    private void ClosePlayerActionIndicatorOverlay()
    {
        if (_playerActionIndicatorWindow is null)
        {
            return;
        }

        var indicatorWindow = _playerActionIndicatorWindow;
        _playerActionIndicatorWindow = null;
        indicatorWindow.Closed -= PlayerActionIndicatorWindow_OnClosed;
        indicatorWindow.Close();
    }

    private void PlayerActionIndicatorWindow_OnClosed(object? sender, EventArgs e)
        => _playerActionIndicatorWindow = null;

    private void HidePlayerControlsOverlay()
    {
        _playerControlsAutoHideTimer.Stop();
        _playerControlsOverlayWindow?.SetControlsVisible(false, immediate: true);
        _playerControlsOverlayWindow?.Hide();
    }

    private bool IsPlayerControlsSection
        => _viewModel.ActiveSection is ShellSection.LiveTv
            or ShellSection.Movies
            or ShellSection.Series
            or ShellSection.Events;

    private void ResetPlayerControlsOverlayForContextChange()
    {
        _playerControlsAutoHideTimer.Stop();
        _lastPlayerControlsActivityUtc = DateTimeOffset.MinValue;
        _suppressPlayerControlsUntilPointerMoves = true;
        _lastPointerPosition = GetCursorPos(out var pointerPosition)
            ? pointerPosition
            : null;
        HidePlayerControlsOverlay();

        if (_isFullscreen)
        {
            _fullscreenExitHintTimer.Stop();
            _fullscreenHudWindow?.SetChromeVisible(false);
        }
    }

    private bool AcceptPlayerControlsPointerActivity()
    {
        if (!_suppressPlayerControlsUntilPointerMoves)
        {
            return true;
        }

        if (!GetCursorPos(out var pointerPosition))
        {
            return false;
        }

        var pointerMoved = !_lastPointerPosition.HasValue
                           || _lastPointerPosition.Value.X != pointerPosition.X
                           || _lastPointerPosition.Value.Y != pointerPosition.Y;
        _lastPointerPosition = pointerPosition;
        if (!pointerMoved)
        {
            return false;
        }

        _suppressPlayerControlsUntilPointerMoves = false;
        return true;
    }

    private void ClosePlayerControlsOverlay()
    {
        if (_playerControlsOverlayWindow is null)
        {
            return;
        }

        var overlayWindow = _playerControlsOverlayWindow;
        _playerControlsOverlayWindow = null;
        overlayWindow.ActivityDetected -= PlayerControlsOverlayWindow_OnActivityDetected;
        overlayWindow.ScreenshotRequested -= PlayerControlsOverlayWindow_OnScreenshotRequested;
        overlayWindow.RecordRequested -= PlayerControlsOverlayWindow_OnRecordRequested;
        overlayWindow.DisplayModeRequested -= PlayerControlsOverlayWindow_OnDisplayModeRequested;
        overlayWindow.FullscreenRequested -= PlayerControlsOverlayWindow_OnFullscreenRequested;
        overlayWindow.Closed -= PlayerControlsOverlayWindow_OnClosed;
        overlayWindow.Close();
    }

    private void PlayerControlsOverlayWindow_OnActivityDetected(object? sender, EventArgs e)
        => ShowWindowedPlayerControls();

    private void PlayerControlsOverlayWindow_OnScreenshotRequested(object? sender, EventArgs e)
        => ScreenshotButton_OnClick(this, new RoutedEventArgs());

    private void PlayerControlsOverlayWindow_OnRecordRequested(object? sender, EventArgs e)
        => RecordLiveButton_OnClick(this, new RoutedEventArgs());

    private void PlayerControlsOverlayWindow_OnDisplayModeRequested(object? sender, EventArgs e)
        => TogglePlayerDisplayMode();

    private void PlayerControlsOverlayWindow_OnFullscreenRequested(object? sender, EventArgs e)
        => ToggleFullscreen();

    private void PlayerControlsOverlayWindow_OnClosed(object? sender, EventArgs e)
        => _playerControlsOverlayWindow = null;

    private void VisibleChannels_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.VisibleChannels.Count > 0)
        {
            LogoCacheService.Instance.PreloadLogos(_viewModel.VisibleChannels.Select(channel => channel.LogoUri));
        }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainShellViewModel.IsStartupLoading))
        {
            HandleStartupLoadingChanged(_viewModel.IsStartupLoading);
            return;
        }

        if (e.PropertyName is nameof(MainShellViewModel.ActiveSection)
            or nameof(MainShellViewModel.SelectedChannel)
            or nameof(MainShellViewModel.SelectedMovie)
            or nameof(MainShellViewModel.SelectedSeries)
            or nameof(MainShellViewModel.CurrentChannelTitle))
        {
            ResetPlayerControlsOverlayForContextChange();
            return;
        }

        if (e.PropertyName == nameof(MainShellViewModel.IsPlayerActionIndicatorVisible))
        {
            if (_isFullscreen && !_suppressPlayerControlsUntilPointerMoves)
            {
                ShowFullscreenExitHint();
            }
            else
            {
                RefreshPlayerActionIndicatorOverlayVisibility();
            }

            return;
        }

        if (e.PropertyName != nameof(MainShellViewModel.IsPlaybackPlaying))
        {
            return;
        }

        if (_suppressPlayerControlsUntilPointerMoves)
        {
            return;
        }

        if (_isFullscreen)
        {
            ShowFullscreenExitHint();
            return;
        }

        if (!IsPlayerControlsSection)
        {
            HidePlayerControlsOverlay();
            return;
        }

        if (_viewModel.IsPlaybackPlaying)
        {
            SchedulePlayerControlsAutoHide();
            return;
        }

        ShowWindowedPlayerControls();
    }


    private void ApplyStartupBlur(bool enable)
    {
        // Panel-level BlurEffects force offscreen bitmap allocation and multi-pass shaders across
        // large virtualized item lists, dropping animation framerates below 60fps.
        // StartupLoadingOverlay's dark translucent scrim achieves the modern focused backdrop at 60fps.
        if (!enable && SourcePanel.Effect is not null)
        {
            SourcePanel.Effect = null;
            ChannelPanel.Effect = null;
            PlaybackPanel.Effect = null;
        }
    }

    private async void HandleStartupLoadingChanged(bool isLoading)
    {
        _startupLoadingDismissCts?.Cancel();
        _startupLoadingDismissCts?.Dispose();
        _startupLoadingDismissCts = null;

        if (isLoading)
        {
            _startupLoadingShownUtc = DateTimeOffset.UtcNow;
            PlayerView.Visibility = Visibility.Hidden;
            ApplyStartupBlur(true);

            if (StartupLoadingOverlay.Visibility == Visibility.Visible && Math.Abs(StartupLoadingOverlay.Opacity - 1.0) < 0.01)
            {
                StartupLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                StartupLoadingOverlay.Opacity = 1.0;
                return;
            }

            StartupLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            StartupLoadingOverlay.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation
            {
                From = StartupLoadingOverlay.Opacity,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (_, _) =>
            {
                StartupLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                StartupLoadingOverlay.Opacity = 1.0;
            };
            StartupLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
        else
        {
            if (StartupLoadingOverlay.Visibility != Visibility.Visible)
            {
                StartupLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                StartupLoadingOverlay.Opacity = 0;
                StartupLoadingOverlay.Visibility = Visibility.Collapsed;
                ApplyStartupBlur(false);
                PlayerView.ClearValue(VisibilityProperty);
                PlayerView.InvalidateMeasure();
                PlayerView.InvalidateVisual();
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - _startupLoadingShownUtc;
            var remaining = MinimumStartupLoadingDuration - elapsed;

            if (remaining > TimeSpan.Zero)
            {
                var cts = new CancellationTokenSource();
                _startupLoadingDismissCts = cts;

                try
                {
                    await Task.Delay(remaining, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_viewModel.IsStartupLoading || cts.IsCancellationRequested)
                {
                    return;
                }
            }

            if (StartupLoadingOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            var fadeOut = new DoubleAnimation
            {
                From = StartupLoadingOverlay.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };

            fadeOut.Completed += (_, _) =>
            {
                StartupLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                StartupLoadingOverlay.Opacity = 0;
                StartupLoadingOverlay.Visibility = Visibility.Collapsed;
                ApplyStartupBlur(false);
                PlayerView.ClearValue(VisibilityProperty);
                PlayerView.InvalidateMeasure();
                PlayerView.InvalidateVisual();
            };

            StartupLoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    private void PlayerSurface_OnMouseActivity(object sender, MouseEventArgs e)
    {
        if (_isFullscreen)
        {
            return;
        }

        if (IsPlayerControlsSection && AcceptPlayerControlsPointerActivity())
        {
            ShowWindowedPlayerControls();
        }
    }

    private void PlayerTimelineTimer_OnTick(object? sender, EventArgs e)
    {
        if (_viewModel.IsLivePlaybackActive)
        {
            _viewModel.SelectedChannel?.RefreshEpgClock();
        }
    }

    private void MediaSavedToastTimer_OnTick(object? sender, EventArgs e)
    {
        _mediaSavedToastTimer.Stop();
        AnimateSavedToast(0d, TimeSpan.FromMilliseconds(180));
    }

    private void ShowSavedToast(string message)
    {
        _mediaSavedToastTimer.Stop();
        MediaSavedToastText.Text = message;
        MediaSavedToast.BeginAnimation(OpacityProperty, null);
        AnimateSavedToast(1d, TimeSpan.FromMilliseconds(120));
        _mediaSavedToastTimer.Start();
    }

    private void AnimateSavedToast(double opacity, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = duration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        MediaSavedToast.BeginAnimation(OpacityProperty, animation);
    }

    private static HttpClient CreateRecordingHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0 WhoseIPTV/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }

    private async Task WaitForSnapshotFileAsync(string path)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new IOException("The screenshot file was not created.");
    }

    private string BuildDefaultMediaFileName(string kind, string extension)
    {
        var title = _viewModel.SelectedChannel?.DisplayName ?? _viewModel.CurrentChannelDisplayTitle;
        var safeTitle = InvalidFileNameCharactersPattern.Replace(title, " ").Trim();
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            safeTitle = "channel";
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return $"{safeTitle}-{kind}-{timestamp}{extension}";
    }

    private static async Task RecordCurrentStreamAsync(Uri streamUri, string outputPath, CancellationToken cancellationToken)
    {
        if (streamUri.Scheme is not ("http" or "https"))
        {
            throw new NotSupportedException("Only HTTP and HTTPS streams can be recorded by the built-in recorder.");
        }

        using var response = await SendRecordingRequestAsync(streamUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var isPlaylist = streamUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                         || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                         || contentType.Contains("vnd.apple", StringComparison.OrdinalIgnoreCase);

        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        if (!isPlaylist)
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await input.CopyToAsync(output, cancellationToken);
            return;
        }

        var playlistText = await response.Content.ReadAsStringAsync(cancellationToken);
        await RecordHlsPlaylistAsync(streamUri, playlistText, output, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendRecordingRequestAsync(
        Uri uri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("VLC/3.0 WhoseIPTV/1.0");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        return await RecordingHttpClient.SendAsync(request, completionOption, cancellationToken);
    }

    private static async Task RecordHlsPlaylistAsync(
        Uri originalPlaylistUri,
        string initialPlaylistText,
        FileStream output,
        CancellationToken cancellationToken)
    {
        var playlistUri = ResolveVariantPlaylist(originalPlaylistUri, initialPlaylistText) ?? originalPlaylistUri;
        var downloadedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstRead = playlistUri == originalPlaylistUri;
        var targetDuration = TimeSpan.FromSeconds(2);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var playlistText = firstRead
                ? initialPlaylistText
                : await FetchPlaylistTextAsync(playlistUri, cancellationToken);
            firstRead = false;

            var variantPlaylist = ResolveVariantPlaylist(playlistUri, playlistText);
            if (variantPlaylist is not null && variantPlaylist != playlistUri)
            {
                playlistUri = variantPlaylist;
                firstRead = false;
                continue;
            }

            var playlist = ParseMediaPlaylist(playlistUri, playlistText);
            targetDuration = playlist.TargetDuration;

            foreach (var segmentUri in playlist.Segments)
            {
                if (!downloadedSegments.Add(segmentUri.AbsoluteUri))
                {
                    continue;
                }

                await DownloadSegmentAsync(segmentUri, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            if (playlist.IsEndList)
            {
                return;
            }

            var delay = TimeSpan.FromMilliseconds(Math.Clamp(targetDuration.TotalMilliseconds / 2d, 1000d, 5000d));
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static async Task<string> FetchPlaylistTextAsync(Uri playlistUri, CancellationToken cancellationToken)
    {
        using var response = await SendRecordingRequestAsync(playlistUri, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task DownloadSegmentAsync(Uri segmentUri, Stream output, CancellationToken cancellationToken)
    {
        using var response = await SendRecordingRequestAsync(segmentUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static Uri? ResolveVariantPlaylist(Uri playlistUri, string playlistText)
    {
        Uri? bestUri = null;
        var bestBandwidth = -1L;
        long pendingBandwidth = -1L;

        foreach (var rawLine in EnumeratePlaylistLines(playlistText))
        {
            if (rawLine.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                pendingBandwidth = ParseLongAttribute(rawLine, "BANDWIDTH") ?? 0L;
                continue;
            }

            if (pendingBandwidth < 0 || rawLine.StartsWith('#'))
            {
                continue;
            }

            if (pendingBandwidth >= bestBandwidth)
            {
                bestBandwidth = pendingBandwidth;
                bestUri = new Uri(playlistUri, rawLine);
            }

            pendingBandwidth = -1L;
        }

        return bestUri;
    }

    private static HlsMediaPlaylist ParseMediaPlaylist(Uri playlistUri, string playlistText)
    {
        var segments = new List<Uri>();
        var targetDuration = TimeSpan.FromSeconds(2);
        var isEndList = false;

        foreach (var line in EnumeratePlaylistLines(playlistText))
        {
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], out var seconds)
                && seconds > 0)
            {
                targetDuration = TimeSpan.FromSeconds(seconds);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Encrypted HLS streams cannot be recorded by the built-in recorder.");
            }

            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                isEndList = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            segments.Add(new Uri(playlistUri, line));
        }

        return new HlsMediaPlaylist(segments, targetDuration, isEndList);
    }

    private static IEnumerable<string> EnumeratePlaylistLines(string playlistText)
    {
        using var reader = new StringReader(playlistText);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static long? ParseLongAttribute(string line, string attributeName)
    {
        var pattern = attributeName + "=";
        var start = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += pattern.Length;
        var end = line.IndexOf(',', start);
        var value = end < 0 ? line[start..] : line[start..end];
        return long.TryParse(value.Trim('"'), out var parsed) ? parsed : null;
    }

    private void RefreshThemeSensitiveVisuals()
    {
        if (_isFullscreen)
        {
            SetResourceReference(BackgroundProperty, "Brush.WindowBackground");
            RootLayout.SetResourceReference(Panel.BackgroundProperty, "Brush.WindowBackground");
            PlaybackPanel.SetResourceReference(Border.BackgroundProperty, "Brush.WindowBackground");
            PlaybackPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.WindowBackground");
            PlayerSurface.SetResourceReference(Border.BackgroundProperty, "Theme.Brush.000000");
        }
        else
        {
            RestoreWindowedThemeResourceReferences();
        }

        PlayerSurface.InvalidateVisual();
        PlaybackPanel.InvalidateVisual();
        _fullscreenHudWindow?.InvalidateVisual();
    }

    private void RestoreWindowedThemeResourceReferences()
    {
        SetResourceReference(BackgroundProperty, "Brush.WindowBackground");
        RootLayout.ClearValue(Panel.BackgroundProperty);
        PlaybackPanel.SetResourceReference(Border.BackgroundProperty, "Brush.CardBackground");
        PlaybackPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        PlayerSurface.SetResourceReference(Border.BackgroundProperty, "Theme.Brush.000000");
        PlayerSurface.SetResourceReference(Border.BorderBrushProperty, "Theme.Brush.604EA2FF");
    }

    private void ApplyPremiumTitleBar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var darkMode = 1;
        if (DwmSetWindowAttribute(windowHandle, DwmWindowAttribute.UseImmersiveDarkMode, ref darkMode, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(windowHandle, DwmWindowAttribute.UseImmersiveDarkModeBefore20H1, ref darkMode, sizeof(int));
        }

        SetDwmColor(windowHandle, DwmWindowAttribute.CaptionColor, GetDwmColor("Brush.WindowBackground", "#070C16"));
        SetDwmColor(windowHandle, DwmWindowAttribute.TextColor, GetDwmColor("Brush.TextPrimary", "#F4F7FF"));
        SetDwmColor(windowHandle, DwmWindowAttribute.BorderColor, GetDwmColor("Brush.CardBorder", "#273854"));
    }

    private static void SetDwmColor(IntPtr windowHandle, DwmWindowAttribute attribute, int color)
        => _ = DwmSetWindowAttribute(windowHandle, attribute, ref color, sizeof(int));

    private int GetDwmColor(string resourceKey, string fallbackHex)
    {
        var color = TryFindResource(resourceKey) switch
        {
            System.Windows.Media.SolidColorBrush brush => brush.Color,
            System.Windows.Media.Color resourceColor => resourceColor,
            _ => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallbackHex),
        };

        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        DwmWindowAttribute attribute,
        ref int pvAttribute,
        int cbAttribute);

    private enum DwmWindowAttribute
    {
        UseImmersiveDarkModeBefore20H1 = 19,
        UseImmersiveDarkMode = 20,
        BorderColor = 34,
        CaptionColor = 35,
        TextColor = 36,
    }

    private sealed record HlsMediaPlaylist(
        IReadOnlyList<Uri> Segments,
        TimeSpan TargetDuration,
        bool IsEndList);
}
