using IptvPlayer.Contracts.Player;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.ViewModels;
using IptvPlayer.Presentation.Localization;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IptvPlayer.App.Views;

public partial class MainWindow : Window
{
    private const double LogicalWheelStep = 1d;
    private const double PhysicalWheelStep = 64d;
    private const double SmoothScrollCompletionThreshold = 0.08d;
    private const double SmoothScrollEase = 0.34d;
    private const double DefaultVideoAspectRatio = 16d / 9d;
    private const int NativeArrowCursorId = 32512;
    private const int NativeHandCursorId = 32649;
    private const int ClassLongCursor = -12;
    private const string VlcVideoWindowClassPrefix = "VLC video ";
    private const int WindowMessageSetCursor = 0x0020;
    private const int WindowMessageCreate = 0x0001;
    private const int WindowMessageLeftButtonDown = 0x0201;
    private const int WindowMessageLeftButtonUp = 0x0202;
    private const int WindowMessageParentNotify = 0x0210;
    private static readonly IntPtr ArrowCursorHandle = LoadCursor(IntPtr.Zero, new IntPtr(NativeArrowCursorId));
    private static readonly IntPtr HandCursorHandle = LoadCursor(IntPtr.Zero, new IntPtr(NativeHandCursorId));

    private readonly MainShellViewModel _viewModel;
    private readonly UiLocalization _localization = UiLocalization.Current;
    private readonly INativePlayerBridge _nativePlayerBridge;
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
    private HorizontalAlignment _playerSurfaceHostHorizontalAlignment;
    private VerticalAlignment _playerSurfaceHostVerticalAlignment;
    private double _playerSurfaceHostWidth;
    private double _playerSurfaceHostHeight;
    private readonly Dictionary<UIElement, Visibility> _fullscreenHiddenElementVisibility = [];
    private IntPtr _fullscreenWindowHandle;
    private IntPtr _previousNativeWindowStyle;
    private NativeRect _previousNativeWindowBounds;
    private bool _previousNativeTopmost;
    private bool _hasNativeFullscreenSnapshot;
    private readonly DispatcherTimer _fullscreenExitHintTimer;
    private readonly DispatcherTimer _miniPlayerControlsTimer;
    private CancellationTokenSource? _playbackDiagnosticsCts;
    private int _lastGen0Collections;
    private int _lastGen1Collections;
    private int _lastGen2Collections;
    private int _pendingGen0Collections;
    private int _pendingGen1Collections;
    private int _pendingGen2Collections;
    private DateTimeOffset _lastUiStallLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGcLogUtc = DateTimeOffset.MinValue;
    private FullscreenHudWindow? _fullscreenHudWindow;
    private HwndHost? _playerVideoHost;

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
        _logger = logger;
#if DEBUG
        _playbackDiagnosticsEnabled = configuration.GetValue("PlaybackDiagnostics:Enabled", defaultValue: false);
#else
        _playbackDiagnosticsEnabled = false;
#endif

        InitializeComponent();
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
            Interval = TimeSpan.FromSeconds(3),
        };
        _fullscreenExitHintTimer.Tick += FullscreenExitHintTimer_OnTick;
        _miniPlayerControlsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _miniPlayerControlsTimer.Tick += MiniPlayerControlsTimer_OnTick;
        DataContext = _viewModel;

        SourceInitialized += MainWindow_OnSourceInitialized;
        Loaded += MainWindow_OnLoaded;
        LocationChanged += MainWindow_OnFullscreenBoundsChanged;
        SizeChanged += MainWindow_OnFullscreenBoundsChanged;
        Closed += MainWindow_OnClosed;
    }

    private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
        => ApplyPremiumTitleBar();

    private void Localization_OnCultureChanged(object? sender, EventArgs e)
        => ApplyUiFlowDirection();

    private void ApplyUiFlowDirection()
        => RootLayout.FlowDirection = System.Windows.FlowDirection.LeftToRight;

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Main window loaded. Fullscreen overlay visibility: {OverlayVisibility}", FullscreenOverlay.Visibility);

        await _viewModel.InitializeAsync();

        if (_nativePlayerBridge.NativePlayer is MediaPlayer mediaPlayer)
        {
            mediaPlayer.EnableMouseInput = false;
            PlayerView.MediaPlayer = mediaPlayer;
            AttachPlayerVideoInputHook();
            _logger.LogInformation("Player view bound to native media player instance: {PlayerType}", mediaPlayer.GetType().FullName);
        }
        else
        {
            _logger.LogWarning("Native player bridge did not provide a media player instance on load");
        }

        UpdateMiniPlayerLayout();
        StartPlaybackDiagnostics();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_isFullscreen)
        {
            ExitFullscreen(animate: false);
        }

        _fullscreenExitHintTimer.Stop();
        _fullscreenExitHintTimer.Tick -= FullscreenExitHintTimer_OnTick;
        _miniPlayerControlsTimer.Stop();
        _miniPlayerControlsTimer.Tick -= MiniPlayerControlsTimer_OnTick;
        StopPlaybackDiagnostics();
        SourceInitialized -= MainWindow_OnSourceInitialized;
        LocationChanged -= MainWindow_OnFullscreenBoundsChanged;
        SizeChanged -= MainWindow_OnFullscreenBoundsChanged;
        _localization.CultureChanged -= Localization_OnCultureChanged;
        DetachPlayerVideoInputHook();
        PlayerView.MediaPlayer = null;

        foreach (var state in _smoothScrollStates.Values)
        {
            state.Timer.Stop();
        }

        _smoothScrollStates.Clear();
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
        => UpdateMiniPlayerLayout();

    private void MainWindow_OnFullscreenBoundsChanged(object? sender, EventArgs e)
    {
        if (_isFullscreen)
        {
            UpdateFullscreenHudBounds();
        }
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

    private void UpdateMiniPlayerLayout()
    {
        if (!_isFullscreen)
        {
            var availableWidth = PlayerRowHost.ActualWidth;
            var availableHeight = PlayerRowHost.ActualHeight;
            if (availableWidth > 0d && availableHeight > 0d)
            {
                var targetHeight = Math.Min(availableHeight, availableWidth / DefaultVideoAspectRatio);
                var targetWidth = targetHeight * DefaultVideoAspectRatio;

                if (double.IsNaN(PlayerSurfaceHost.Width)
                    || Math.Abs(PlayerSurfaceHost.Width - targetWidth) > 0.5d)
                {
                    PlayerSurfaceHost.Width = targetWidth;
                }

                if (double.IsNaN(PlayerSurfaceHost.Height)
                    || Math.Abs(PlayerSurfaceHost.Height - targetHeight) > 0.5d)
                {
                    PlayerSurfaceHost.Height = targetHeight;
                }
            }
        }
    }

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
        _playerSurfaceHostHorizontalAlignment = PlayerSurfaceHost.HorizontalAlignment;
        _playerSurfaceHostVerticalAlignment = PlayerSurfaceHost.VerticalAlignment;
        _playerSurfaceHostWidth = PlayerSurfaceHost.Width;
        _playerSurfaceHostHeight = PlayerSurfaceHost.Height;
        _isFullscreenTransitioning = true;
        CompleteEnterFullscreen();
    }

    private void CompleteEnterFullscreen()
    {
        if (!TryEnterNativeFullscreen())
        {
            _isFullscreenTransitioning = false;
            return;
        }

        RootLayout.Margin = new Thickness(0);
        Background = System.Windows.Media.Brushes.Black;
        RootLayout.Background = System.Windows.Media.Brushes.Black;
        SourceColumn.Width = new GridLength(0);
        ChannelColumn.Width = new GridLength(0);
        ContentColumn.Width = new GridLength(1, GridUnitType.Star);
        SourcePanel.Visibility = Visibility.Collapsed;
        ChannelPanel.Visibility = Visibility.Collapsed;
        PlaybackPanel.Margin = new Thickness(0);
        PlaybackPanel.Padding = new Thickness(0);
        PlaybackPanel.BorderThickness = new Thickness(0);
        PlaybackPanel.Background = System.Windows.Media.Brushes.Black;
        PlaybackPanel.BorderBrush = System.Windows.Media.Brushes.Black;

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

        PlayerSurfaceHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlayerSurfaceHost.VerticalAlignment = VerticalAlignment.Stretch;
        PlayerSurfaceHost.ClearValue(WidthProperty);
        PlayerSurfaceHost.ClearValue(HeightProperty);
        PlayerSurface.CornerRadius = new CornerRadius(0);
        PlayerSurface.BorderThickness = new Thickness(0);
        PlayerSurface.Background = System.Windows.Media.Brushes.Black;
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
        UpdatePlayerVideoCursor();
        HideMiniVodControls(immediate: true);

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
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

        if (!TryExitNativeFullscreen())
        {
            _isFullscreenTransitioning = false;
            return;
        }

        FullscreenHost.Children.Clear();
        FullscreenOverlay.BeginAnimation(OpacityProperty, null);
        FullscreenOverlay.Opacity = 1d;
        FullscreenOverlay.Visibility = Visibility.Collapsed;
        RootLayout.Margin = _rootLayoutMargin;
        Background = _windowBackground;
        RootLayout.Background = _rootLayoutBackground;
        SourceColumn.Width = _sourceColumnWidth;
        ChannelColumn.Width = _channelColumnWidth;
        ContentColumn.Width = _contentColumnWidth;
        SourcePanel.Visibility = _sourcePanelVisibility;
        ChannelPanel.Visibility = _channelPanelVisibility;
        PlaybackPanel.Margin = _playbackPanelMargin;
        PlaybackPanel.Padding = _playbackPanelPadding;
        PlaybackPanel.BorderThickness = _playbackPanelBorderThickness;
        PlaybackPanel.Background = _playbackPanelBackground;
        PlaybackPanel.BorderBrush = _playbackPanelBorderBrush;
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
        PlayerSurfaceHost.HorizontalAlignment = _playerSurfaceHostHorizontalAlignment;
        PlayerSurfaceHost.VerticalAlignment = _playerSurfaceHostVerticalAlignment;
        PlayerSurfaceHost.Width = _playerSurfaceHostWidth;
        PlayerSurfaceHost.Height = _playerSurfaceHostHeight;
        PlayerSurface.CornerRadius = _playerCornerRadius;
        PlayerSurface.BorderThickness = _playerBorderThickness;
        PlayerSurface.Background = _playerBackground;
        PlayerSurface.Effect = _playerEffect;
        _fullscreenExitHintTimer.Stop();

        _isFullscreen = false;
        UpdatePlayerVideoCursor();

        UpdateMiniPlayerLayout();
        _isFullscreenTransitioning = false;

        _logger.LogInformation("Exited fullscreen. Overlay visibility={OverlayVisibility}", FullscreenOverlay.Visibility);
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
            _logger.LogError("Fullscreen native window transaction failed. Error={NativeError}", nativeError);
            return false;
        }

        _fullscreenWindowHandle = handle;
        _previousNativeWindowStyle = previousStyle;
        _previousNativeWindowBounds = previousBounds;
        _previousNativeTopmost = (previousExtendedStyle.ToInt64() & WindowExtendedStyleTopmost) != 0;
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

        _fullscreenWindowHandle = IntPtr.Zero;
        _previousNativeWindowStyle = IntPtr.Zero;
        _previousNativeWindowBounds = default;
        _previousNativeTopmost = false;
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
        PlayerView.ApplyTemplate();
        if (PlayerView.Template.FindName("PART_PlayerHost", PlayerView) is not HwndHost playerVideoHost)
        {
            _logger.LogWarning("Native video host was not available for mini-player input");
            return;
        }

        if (ReferenceEquals(_playerVideoHost, playerVideoHost))
        {
            return;
        }

        DetachPlayerVideoInputHook();
        _playerVideoHost = playerVideoHost;
        _playerVideoHost.MessageHook += PlayerVideoHost_OnMessage;
        UpdatePlayerVideoCursor();
        _ = AttachNativeVideoCursorAfterWindowCreatedAsync();
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
        if (sender is not ListBox listBox)
        {
            return;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer is null)
        {
            return;
        }

        var notches = Math.Max(1d, Math.Abs(e.Delta) / 120d);
        var direction = e.Delta > 0 ? -1d : 1d;
        var step = GetWheelStep(listBox, scrollViewer);
        QueueSmoothScroll(scrollViewer, direction * step * notches);
        e.Handled = true;
    }

    private static double GetWheelStep(ListBox listBox, ScrollViewer scrollViewer)
    {
        var itemCount = Math.Max(1, listBox.Items.Count);
        var averageExtentPerItem = scrollViewer.ExtentHeight / itemCount;
        return averageExtentPerItem > 3d ? PhysicalWheelStep : LogicalWheelStep;
    }

    private void QueueSmoothScroll(ScrollViewer scrollViewer, double delta)
    {
        var maxOffset = Math.Max(0d, scrollViewer.ScrollableHeight);
        if (maxOffset <= 0d)
        {
            return;
        }

        var state = GetSmoothScrollState(scrollViewer);
        if (!state.Timer.IsEnabled)
        {
            state.TargetOffset = scrollViewer.VerticalOffset;
        }

        state.TargetOffset = Math.Clamp(state.TargetOffset + delta, 0d, maxOffset);
        state.Timer.Start();
    }

    private SmoothScrollState GetSmoothScrollState(ScrollViewer scrollViewer)
    {
        if (_smoothScrollStates.TryGetValue(scrollViewer, out var state))
        {
            return state;
        }

        state = new SmoothScrollState
        {
            TargetOffset = scrollViewer.VerticalOffset,
            Timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            },
        };

        state.Timer.Tick += (_, _) => SmoothScrollTimer_OnTick(scrollViewer, state);
        _smoothScrollStates[scrollViewer] = state;
        return state;
    }

    private static void SmoothScrollTimer_OnTick(ScrollViewer scrollViewer, SmoothScrollState state)
    {
        if (!scrollViewer.IsVisible)
        {
            state.Timer.Stop();
            return;
        }

        var maxOffset = Math.Max(0d, scrollViewer.ScrollableHeight);
        state.TargetOffset = Math.Clamp(state.TargetOffset, 0d, maxOffset);

        var currentOffset = scrollViewer.VerticalOffset;
        var remaining = state.TargetOffset - currentOffset;
        if (Math.Abs(remaining) <= SmoothScrollCompletionThreshold)
        {
            scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
            state.Timer.Stop();
            return;
        }

        var nextOffset = currentOffset + (remaining * SmoothScrollEase);
        if (Math.Abs(nextOffset - currentOffset) < 0.02d)
        {
            nextOffset = state.TargetOffset;
        }

        scrollViewer.ScrollToVerticalOffset(nextOffset);

        if (Math.Abs(scrollViewer.VerticalOffset - currentOffset) < 0.001d)
        {
            scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
            state.Timer.Stop();
        }
    }

    private sealed class SmoothScrollState
    {
        public required DispatcherTimer Timer { get; init; }

        public double TargetOffset { get; set; }
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

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T typedParent)
            {
                return typedParent;
            }

            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
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
        _fullscreenExitHintTimer.Start();
    }

    private void FullscreenExitHintTimer_OnTick(object? sender, EventArgs e)
    {
        _fullscreenExitHintTimer.Stop();

        if (_isFullscreen)
        {
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
            hudWindow.Closed += FullscreenHudWindow_OnClosed;
            _fullscreenHudWindow = hudWindow;
        }

        if (!_fullscreenHudWindow.IsVisible)
        {
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

        _ = SetWindowPos(
            hudHandle,
            IntPtr.Zero,
            ownerBounds.Left,
            ownerBounds.Top,
            ownerBounds.Right - ownerBounds.Left,
            ownerBounds.Bottom - ownerBounds.Top,
            SetWindowPosNoZOrder | SetWindowPosNoActivate | SetWindowPosNoOwnerZOrder);
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

    private void FullscreenHudWindow_OnClosed(object? sender, EventArgs e)
        => _fullscreenHudWindow = null;

    private void PlayerSurface_OnMouseActivity(object sender, MouseEventArgs e)
    {
        if (_isFullscreen || !_viewModel.IsOnDemandPlaybackControlVisible)
        {
            return;
        }

        ShowMiniVodControls();
    }

    private void PlayerSurface_OnMouseLeave(object sender, MouseEventArgs e)
        => HideMiniVodControls();

    private void VodTimelineSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled || slider.ActualWidth <= 0d)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindVisualParent<Thumb>(source) is not null)
        {
            return;
        }

        var ratio = GetTimelineSeekRatio(slider, e);
        slider.Value = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
        e.Handled = true;

        if (!_isFullscreen)
        {
            ShowMiniVodControls();
        }
    }

    private static double GetTimelineSeekRatio(Slider slider, MouseButtonEventArgs e)
    {
        slider.ApplyTemplate();

        if (slider.Template.FindName("PART_Track", slider) is Track track && track.ActualWidth > 0d)
        {
            return Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0d, 1d);
        }

        return Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0d, 1d);
    }

    private void MiniPlayerControlsTimer_OnTick(object? sender, EventArgs e)
    {
        _miniPlayerControlsTimer.Stop();
        HideMiniVodControls();
    }

    private void ShowMiniVodControls()
    {
        MiniVodControlsOverlay.IsHitTestVisible = true;
        AnimateMiniVodControlsOpacity(1d, TimeSpan.FromMilliseconds(160));
        _miniPlayerControlsTimer.Stop();
        _miniPlayerControlsTimer.Start();
    }

    private void HideMiniVodControls(bool immediate = false)
    {
        _miniPlayerControlsTimer.Stop();
        MiniVodControlsOverlay.IsHitTestVisible = false;

        if (immediate)
        {
            MiniVodControlsOverlay.BeginAnimation(OpacityProperty, null);
            MiniVodControlsOverlay.Opacity = 0d;
            return;
        }

        AnimateMiniVodControlsOpacity(0d, TimeSpan.FromMilliseconds(360));
    }

    private void AnimateMiniVodControlsOpacity(double opacity, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = duration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        MiniVodControlsOverlay.BeginAnimation(OpacityProperty, animation);
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

        SetDwmColor(windowHandle, DwmWindowAttribute.CaptionColor, 0x00160C07);
        SetDwmColor(windowHandle, DwmWindowAttribute.TextColor, 0x00FFF7F4);
        SetDwmColor(windowHandle, DwmWindowAttribute.BorderColor, 0x00543827);
    }

    private static void SetDwmColor(IntPtr windowHandle, DwmWindowAttribute attribute, int color)
        => _ = DwmSetWindowAttribute(windowHandle, attribute, ref color, sizeof(int));

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
}
