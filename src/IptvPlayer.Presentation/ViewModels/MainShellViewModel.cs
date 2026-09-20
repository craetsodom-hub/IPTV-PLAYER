using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Application.Services;
using IptvPlayer.Contracts.Channels;
using IptvPlayer.Contracts.Import;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Player;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class MainShellViewModel : ObservableObject
{
    private readonly CatalogOrchestrator _catalog;
    private readonly SourceImportOrchestrator _sourceImport;
    private readonly SessionOrchestrator _session;
    private readonly IOnDemandStateStore _onDemandStateStore;
    private readonly ISportsEventService _sportsEvents;
    private readonly PlaybackOrchestrator _playback;
    private readonly ILogger<MainShellViewModel> _logger;
    private readonly SynchronizationContext _uiContext;
    private readonly bool _playbackDiagnosticsEnabled;

    private readonly List<CategoryItemViewModel> _allCategories = [];
    private readonly List<ChannelItemViewModel> _allChannels = [];
    private readonly Dictionary<string, IReadOnlyList<ChannelItemViewModel>> _quickQualityVariantsByIdentity = new(StringComparer.Ordinal);
    private readonly List<ChannelItemViewModel> _favoriteChannels = [];
    private readonly List<MovieItemViewModel> _allMovies = [];
    private readonly List<MovieItemViewModel> _playlistSearchMovies = [];
    private List<MovieItemViewModel> _filteredMovies = [];
    private readonly List<SeriesItemViewModel> _allSeries = [];
    private readonly List<SeriesItemViewModel> _playlistSearchSeries = [];
    private List<SeriesItemViewModel> _filteredSeries = [];
    private List<SportsEventModel> _eventModels = [];
    private readonly List<ChannelItemViewModel> _eventPlaylistChannels = [];
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _playlistChannelsLoadGate = new(1, 1);
    private readonly SemaphoreSlim _movieSearchCacheLoadGate = new(1, 1);
    private readonly SemaphoreSlim _seriesSearchCacheLoadGate = new(1, 1);
    private const int MaxConcurrentVisibleEpgRefreshes = 6;
    private const int MaxVisibleEpgRefreshChannels = 500;
    private const int OnDemandPageSize = 72;
    private const string AllMoviesCategoryId = "__all_movies";
    private const string AllSeriesCategoryId = "__all_series";
    private static readonly TimeSpan PlaybackProgressRefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LiveSessionTimerInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PlaybackProgressPersistenceInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlaybackSeekStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackSeekDebounce = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan ResumeSeekRetryInterval = TimeSpan.FromMilliseconds(250);
    private const int ResumeSeekMaxAttempts = 20;
    private static readonly TimeSpan VisibleEpgRefreshAfterPlaybackStableDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan VisibleEpgRefreshBetweenRequests = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan AllChannelsEpgSyncBetweenRequests = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan SuccessNotificationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ImportProgressFrameInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan ImportProgressFastCompletionDuration = TimeSpan.FromMilliseconds(500);
    private const double ImportProgressMaximumBeforeCompletion = 99.4d;
    private static readonly TimeSpan[] LivePlaybackRecoveryDelays =
    [
        TimeSpan.FromSeconds(1.5),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];
    private const int LivePlaybackNoSignalAttempt = 3;

    private CancellationTokenSource? _loadChannelsCts;
    private CancellationTokenSource? _loadCategoriesCts;
    private CancellationTokenSource? _loadFavoriteChannelsCts;
    private CancellationTokenSource? _loadMoviesCts;
    private CancellationTokenSource? _movieDetailsCts;
    private CancellationTokenSource? _loadSeriesCts;
    private CancellationTokenSource? _seriesDetailsCts;
    private CancellationTokenSource? _eventsCts;
    private CancellationTokenSource? _quickQualityVariantsCts;
    private CancellationTokenSource? _channelFilterCts;
    private CancellationTokenSource? _movieFilterCts;
    private CancellationTokenSource? _seriesFilterCts;
    private CancellationTokenSource? _movieSearchCacheCts;
    private CancellationTokenSource? _seriesSearchCacheCts;
    private Task? _movieSearchCacheWarmupTask;
    private Task? _seriesSearchCacheWarmupTask;
    private CancellationTokenSource? _selectedEpgRefreshCts;
    private CancellationTokenSource? _visibleEpgRefreshCts;
    private CancellationTokenSource? _deferredVisibleEpgRefreshCts;
    private CancellationTokenSource? _livePlaybackCts;
    private CancellationTokenSource? _liveSessionTimerCts;
    private CancellationTokenSource? _playbackProgressCts;
    private CancellationTokenSource? _playbackSeekCts;
    private CancellationTokenSource? _notificationClearCts;
    private CancellationTokenSource? _importCts;
    private readonly SemaphoreSlim _onDemandPlaybackGate = new(1, 1);
    private long _livePlaybackRequestVersion;
    private long _livePlaybackStartupVersion;
    private int _livePlaybackRecoveryInFlight;
    private int _livePlaybackRecoveryAttempt;
    private UserSessionState _sessionSnapshot = UserSessionState.Empty;
    private readonly List<RecentChannelHistoryEntry> _recentChannelHistory = [];
    private ChannelItemViewModel? _activeRecentPlaybackChannel;
    private DateTimeOffset _activeRecentPlaybackStartedUtc = DateTimeOffset.MinValue;
    private long _activeRecentPlaybackStartedTimestamp;
    private long _liveSessionTimerVersion;
    private ChannelItemViewModel? _pendingRecentPlaybackChannel;
    private long _pendingRecentPlaybackRequestVersion;
    private long _lastRecordedRecentPlaybackRequestVersion;
    private Guid? _restoredSourceId;
    private string? _restoredCategoryId;
    private string? _restoredChannelId;
    private OnDemandState _onDemandState = OnDemandState.Empty;
    private Guid? _liveLoadedSourceId;
    private Guid? _favoriteChannelsLoadedSourceId;
    private Guid? _recentChannelsLoadedSourceId;
    private Guid? _moviesLoadedSourceId;
    private Guid? _seriesLoadedSourceId;
    private Guid? _movieSearchCacheSourceId;
    private Guid? _seriesSearchCacheSourceId;
    private Guid? _eventsMatchedSourceId;
    private Guid? _eventsMatchingSourceId;
    private bool _isEventChannelPresentationLoading;
    private Guid? _quickQualityVariantsCacheSourceId;
    private int _selectedEventChannelMatchVersion;
    private Guid? _moviesLoadingSourceId;
    private Guid? _seriesLoadingSourceId;
    private bool _isInitializing;
    private bool _isImporting;
    private bool _isLoadingMovieCatalog;
    private bool _isLoadingSeriesCatalog;
    private bool _eventsLoaded;
    private bool _isStartingOnDemandPlayback;
    private bool _isApplyingExternalAudioState;
    private bool _suppressMovieCategoryReload;
    private bool _suppressSeriesCategoryReload;
    private bool _isUpdatingPlaybackProgress;
    private ContinueWatchingItemViewModel? _activeOnDemandHistoryItem;
    private bool _activeOnDemandHistoryIsSeries;
    private DateTimeOffset _lastPlaybackProgressPersistenceUtc = DateTimeOffset.MinValue;
    private int _onDemandProgressPersistenceInFlight;
    private string? _lastPlaybackFailureKey;
    private DateTimeOffset _lastPlaybackFailureUtc = DateTimeOffset.MinValue;
    private PlaybackState _currentPlaybackState = PlaybackState.Idle;
    private PlaybackState _lastLoggedPlaybackState = PlaybackState.Idle;
    private DateTimeOffset _lastPlaybackStateLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBufferingLogUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLivePlaybackPlayingUtc = DateTimeOffset.MinValue;
    private ShellSection _eventsReturnSection = ShellSection.LiveTv;

    [ObservableProperty]
    private double importProgressPercent;

    public bool IsImporting => _isImporting;

    public MainShellViewModel(
        CatalogOrchestrator catalog,
        SourceImportOrchestrator sourceImport,
        SessionOrchestrator session,
        IOnDemandStateStore onDemandStateStore,
        ISportsEventService sportsEvents,
        PlaybackOrchestrator playback,
        IConfiguration configuration,
        ILogger<MainShellViewModel> logger)
    {
        _catalog = catalog;
        _sourceImport = sourceImport;
        _session = session;
        _onDemandStateStore = onDemandStateStore;
        _sportsEvents = sportsEvents;
        _playback = playback;
        _logger = logger;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _playbackDiagnosticsEnabled = bool.TryParse(configuration["PlaybackDiagnostics:Enabled"], out var diagnosticsEnabled)
            && diagnosticsEnabled;

        _playback.StatusChanged += OnPlaybackStatusChanged;
        _playback.AudioStateChanged += OnAudioStateChanged;
        UiLocalization.Current.CultureChanged += UiLocalization_OnCultureChanged;
        StartupLoadingMessage = L("LoadingPlaylist");
        IsStartupLoading = false;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PlaySelectedChannelCommand = new AsyncRelayCommand(PlaySelectedChannelAsync, CanPlaySelectedChannel);
        ResumeRecentChannelCommand = new AsyncRelayCommand<RecentChannelItemViewModel?>(ResumeRecentChannelAsync);
        SelectQuickQualityVariantCommand = new RelayCommand<QuickQualityVariantViewModel?>(SelectQuickQualityVariant);
        PlaySelectedMovieCommand = new AsyncRelayCommand(PlaySelectedMovieAsync, CanPlaySelectedMovie);
        PlaySelectedSeriesCommand = new AsyncRelayCommand(PlaySelectedSeriesAsync, CanPlaySelectedSeries);
        ResumeSelectedMovieCommand = new AsyncRelayCommand(ResumeSelectedMovieAsync, CanResumeSelectedMovie);
        ResumeSelectedSeriesCommand = new AsyncRelayCommand(ResumeSelectedSeriesAsync, CanResumeSelectedSeries);
        PlayEpisodeCommand = new AsyncRelayCommand<SeriesEpisodeViewModel?>(PlayEpisodeAsync);
        PlayContinueWatchingCommand = new AsyncRelayCommand<ContinueWatchingItemViewModel?>(PlayContinueWatchingAsync);
        SeekBackwardCommand = new AsyncRelayCommand(SeekBackwardAsync, () => IsPlaybackSeekAvailable);
        SeekForwardCommand = new AsyncRelayCommand(SeekForwardAsync, () => IsPlaybackSeekAvailable);
        ToggleMovieContinueWatchingCommand = new RelayCommand(() => IsMovieContinueWatchingExpanded = !IsMovieContinueWatchingExpanded);
        ToggleSeriesContinueWatchingCommand = new RelayCommand(() => IsSeriesContinueWatchingExpanded = !IsSeriesContinueWatchingExpanded);
        SelectMovieWatchlistCommand = new AsyncRelayCommand(SelectMovieWatchlistAsync, () => SelectedSource is not null);
        SelectSeriesWatchlistCommand = new AsyncRelayCommand(SelectSeriesWatchlistAsync, () => SelectedSource is not null);
        LoadMoreMoviesCommand = new RelayCommand(LoadMoreMovies, () => HasMoreMovies);
        LoadMoreSeriesCommand = new RelayCommand(LoadMoreSeries, () => HasMoreSeries);
        ToggleMovieWatchlistCommand = new AsyncRelayCommand(ToggleSelectedMovieWatchlistAsync);
        ToggleSeriesWatchlistCommand = new AsyncRelayCommand(ToggleSelectedSeriesWatchlistAsync);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => IsOnDemandPlaybackActive);
        TogglePlayPauseCommand = new AsyncRelayCommand(TogglePlayPauseAsync);
        PauseCommand = new AsyncRelayCommand(PauseAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        PlayPreviousChannelCommand = new RelayCommand(PlayPreviousChannel);
        PlayNextChannelCommand = new RelayCommand(PlayNextChannel);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        ToggleFavoriteCommand = new RelayCommand<ChannelItemViewModel?>(ToggleFavorite);
        SelectFavoritesCategoryCommand = new AsyncRelayCommand(SelectFavoritesCategoryAsync, () => HasLoadedLiveTvPlaylist);
        SelectLiveTvSectionCommand = new RelayCommand(() => ActiveSection = ShellSection.LiveTv);
        SelectMoviesSectionCommand = new RelayCommand(() => ActiveSection = ShellSection.Movies);
        SelectSeriesSectionCommand = new RelayCommand(() => ActiveSection = ShellSection.Series);
        SelectEventsSectionCommand = new RelayCommand(SelectEventsSection);
        ReturnFromEventsCommand = new RelayCommand(ReturnFromEvents);
        RefreshEventsCommand = new AsyncRelayCommand(() => LoadEventsAsync(forceRefresh: true), () => !IsEventsLoading && !IsEventsRefreshing);
        SelectEventCommand = new RelayCommand<SportsEventItemViewModel?>(SelectEvent);
        BackToEventsCommand = new RelayCommand(() => SelectedSportsEvent = null);
        PlayEventChannelCommand = new AsyncRelayCommand<EventChannelOptionViewModel?>(PlayEventChannelAsync);
        SelectEventSportCategoryCommand = new RelayCommand<EventSportCategoryViewModel?>(SelectEventSportCategory);
        SelectEventSortModeCommand = new RelayCommand<EventFilterOptionViewModel?>(SelectEventSortMode);
        SelectEventTimeFilterCommand = new RelayCommand<EventFilterOptionViewModel?>(SelectEventTimeFilter);
        ToggleImportMenuCommand = new RelayCommand(() => IsImportMenuOpen = !IsImportMenuOpen);
        TogglePlaylistSidebarCommand = new RelayCommand(() => IsPlaylistSidebarOpen = !IsPlaylistSidebarOpen);
        ShowCategorySearchCommand = new RelayCommand(() => IsCategorySearchVisible = true);
        CloseCategorySearchCommand = new RelayCommand(CloseCategorySearch);
        ShowChannelSearchCommand = new RelayCommand(() => IsChannelSearchVisible = true);
        CloseChannelSearchCommand = new RelayCommand(CloseChannelSearch);
        ShowMovieSearchCommand = new RelayCommand(() => IsMovieSearchVisible = true);
        CloseMovieSearchCommand = new RelayCommand(CloseMovieSearch);
        ShowSeriesSearchCommand = new RelayCommand(() => IsSeriesSearchVisible = true);
        CloseSeriesSearchCommand = new RelayCommand(CloseSeriesSearch);

        AddXtreamSourceCommand = new RelayCommand(() => OpenImportPanel(SourceImportMode.XtreamCodes));
        AddM3uUrlSourceCommand = new RelayCommand(() => OpenImportPanel(SourceImportMode.M3uUrl));
        AddM3uFileSourceCommand = new RelayCommand(() => OpenImportPanel(SourceImportMode.M3uFile));
        AddM3u8SourceCommand = new RelayCommand(() => OpenImportPanel(SourceImportMode.M3u8Link));

        SubmitImportCommand = new AsyncRelayCommand(SubmitImportAsync, CanSubmitImport);
        EditSelectedSourceCommand = new RelayCommand(EditSelectedSource, CanEditSelectedSource);
        DeleteSelectedSourceCommand = new AsyncRelayCommand(DeleteSelectedSourceAsync, CanDeleteSelectedSource);
        RefreshSelectedSourceCommand = new AsyncRelayCommand(RefreshSelectedSourceAsync, CanRefreshSelectedSource);
        CancelImportCommand = new RelayCommand(CancelImport);

        InitializeEventSportCategories();
        InitializeEventFilters();
    }

    public ObservableCollection<SourceItemViewModel> Sources { get; } = new RangeObservableCollection<SourceItemViewModel>();

    public ObservableCollection<CategoryItemViewModel> VisibleCategories { get; } = new RangeObservableCollection<CategoryItemViewModel>();

    public ObservableCollection<ChannelItemViewModel> VisibleChannels { get; } = new RangeObservableCollection<ChannelItemViewModel>();

    public ObservableCollection<QuickQualityVariantViewModel> QuickQualityVariants { get; } = new RangeObservableCollection<QuickQualityVariantViewModel>();

    public ObservableCollection<ChannelItemViewModel> RecentChannels { get; } = new RangeObservableCollection<ChannelItemViewModel>();

    public ObservableCollection<RecentChannelItemViewModel> CompactRecentChannels { get; } = new RangeObservableCollection<RecentChannelItemViewModel>();

    [ObservableProperty]
    private RecentChannelItemViewModel? resumeLastChannel;

    public bool HasRecentChannels => ResumeLastChannel is not null;

    public bool HasCompactRecentChannels => CompactRecentChannels.Count > 0;

    public bool IsRecentChannelsEmptyStateVisible
        => SelectedSource is not null
           && _recentChannelsLoadedSourceId == SelectedSource.Id
           && !HasRecentChannels;

    public ObservableCollection<CategoryItemViewModel> MovieCategories { get; } = new RangeObservableCollection<CategoryItemViewModel>();

    public ObservableCollection<MovieItemViewModel> VisibleMovies { get; } = new RangeObservableCollection<MovieItemViewModel>();

    public ObservableCollection<ContinueWatchingItemViewModel> ContinueWatchingMovies { get; } = new RangeObservableCollection<ContinueWatchingItemViewModel>();

    public ObservableCollection<CategoryItemViewModel> SeriesCategories { get; } = new RangeObservableCollection<CategoryItemViewModel>();

    public ObservableCollection<SeriesItemViewModel> VisibleSeries { get; } = new RangeObservableCollection<SeriesItemViewModel>();

    public ObservableCollection<ContinueWatchingItemViewModel> ContinueWatchingSeries { get; } = new RangeObservableCollection<ContinueWatchingItemViewModel>();

    public ObservableCollection<SportsEventItemViewModel> TodayEvents { get; } = new RangeObservableCollection<SportsEventItemViewModel>();

    public ObservableCollection<EventSportCategoryViewModel> EventSportCategories { get; } = new RangeObservableCollection<EventSportCategoryViewModel>();
 
    public ObservableCollection<EventFilterOptionViewModel> EventSortModes { get; } = new RangeObservableCollection<EventFilterOptionViewModel>();

    public ObservableCollection<EventFilterOptionViewModel> EventTimeFilters { get; } = new RangeObservableCollection<EventFilterOptionViewModel>();

    [ObservableProperty]
    private ShellSection activeSection = ShellSection.LiveTv;

    [ObservableProperty]
    private SourceItemViewModel? selectedSource;

    [ObservableProperty]
    private CategoryItemViewModel? selectedCategory;

    [ObservableProperty]
    private ChannelItemViewModel? selectedChannel;

    [ObservableProperty]
    private CategoryItemViewModel? selectedMovieCategory;

    [ObservableProperty]
    private MovieItemViewModel? selectedMovie;

    [ObservableProperty]
    private MovieDetailsViewModel? selectedMovieDetails;

    [ObservableProperty]
    private CategoryItemViewModel? selectedSeriesCategory;

    [ObservableProperty]
    private SeriesItemViewModel? selectedSeries;

    [ObservableProperty]
    private SeriesDetailsViewModel? selectedSeriesDetails;

    [ObservableProperty]
    private string categorySearchText = string.Empty;

    [ObservableProperty]
    private string channelSearchText = string.Empty;

    [ObservableProperty]
    private string movieSearchText = string.Empty;

    [ObservableProperty]
    private string seriesSearchText = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool isStartupLoading;

    [ObservableProperty]
    private string startupLoadingMessage = "Loading your playlist...";

    private bool _hasSavedPlaylists;

    public void SetStartupLoadingInitialState(bool hasSavedPlaylists)
    {
        _hasSavedPlaylists = hasSavedPlaylists;
        IsStartupLoading = hasSavedPlaylists;
    }

    [ObservableProperty]
    private bool isMovieCatalogLoading;

    [ObservableProperty]
    private bool isMovieDetailsLoading;

    [ObservableProperty]
    private int visibleMovieLimit = OnDemandPageSize;

    [ObservableProperty]
    private bool isSeriesCatalogLoading;

    [ObservableProperty]
    private bool isSeriesDetailsLoading;

    [ObservableProperty]
    private int visibleSeriesLimit = OnDemandPageSize;

    [ObservableProperty]
    private bool isEventsLoading;

    [ObservableProperty]
    private bool isEventsRefreshing;

    [ObservableProperty]
    private string eventsErrorMessage = string.Empty;

    [ObservableProperty]
    private EventSportCategoryViewModel? selectedEventSportCategory;

    [ObservableProperty]
    private EventFilterOptionViewModel? selectedEventSortMode;

    [ObservableProperty]
    private EventFilterOptionViewModel? selectedEventTimeFilter;

    [ObservableProperty]
    private SportsEventItemViewModel? selectedSportsEvent;

    [ObservableProperty]
    private bool isPlaylistSidebarOpen;

    [ObservableProperty]
    private bool isMuted;

    [ObservableProperty]
    private double volume = 100d;

    [ObservableProperty]
    private string playbackStatusText = L("PlayerIdle");

    public bool HasPlaybackStatusText => !string.IsNullOrWhiteSpace(PlaybackStatusText);

    public bool IsNoSignalStatusVisible
        => string.Equals(PlaybackStatusText, L("NoSignal"), StringComparison.OrdinalIgnoreCase);

    public bool IsFullscreenNoSignalVisible
        => IsNoSignalStatusVisible
           && !IsFullscreenPlaybackLoadingVisible
           && !IsNativeVideoSurfaceVisible;

    public bool IsFullscreenHeaderTitleVisible
        => !IsNoSignalStatusVisible
            && !string.Equals(CurrentChannelTitle, L("NoSignal"), StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool isPlayerSurfaceOverlayVisible = true;

    [ObservableProperty]
    private bool isPlayerSurfaceLoadingVisible;

    [ObservableProperty]
    private bool isFullscreenPlaybackLoadingVisible;

    [ObservableProperty]
    private bool isPlayerSurfaceTextVisible = true;

    [ObservableProperty]
    private bool isNativeVideoSurfaceVisible;

    [ObservableProperty]
    private string playerSurfaceOverlayTitle = L("Ready");

    public bool IsPlayerSurfaceReady
        => string.Equals(PlayerSurfaceOverlayTitle, L("Ready"), StringComparison.Ordinal);

    [ObservableProperty]
    private string playerSurfaceOverlayMessage = L("SelectChannelToStart");

    [ObservableProperty]
    private bool isOnDemandPlaybackActive;

    [ObservableProperty]
    private bool isPlaybackSeekAvailable;

    [ObservableProperty]
    private double playbackPositionSeconds;

    [ObservableProperty]
    private double playbackDurationSeconds;

    [ObservableProperty]
    private string playbackPositionText = "00:00";

    [ObservableProperty]
    private string playbackDurationText = "00:00";

    [ObservableProperty]
    private string livePlaybackElapsedText = "00:00";

    [ObservableProperty]
    private double livePlaybackProgressPercent;

    public bool IsOnDemandPlaybackControlVisible => IsOnDemandPlaybackActive;

    public bool IsLivePlaybackActive => !IsOnDemandPlaybackActive && SelectedChannel is not null;

    [ObservableProperty]
    private string currentChannelTitle = L("NoChannelSelected");

    public string CurrentChannelDisplayTitle
        => (IsLiveTvSection || IsEventsSection)
            ? SelectedChannel?.CountryPrefixFreeDisplayName
              ?? ChannelItemViewModel.StripChannelDisplayPrefix(CurrentChannelTitle)
            : CurrentChannelTitle;

    public bool HasQuickQualityVariants
        => (IsLiveTvSection || IsEventsSection) && SelectedChannel is not null && QuickQualityVariants.Count > 0;

    public bool HasMultipleQuickQualityVariants => QuickQualityVariants.Count > 1;

    public string QuickQualityTitle
    {
        get
        {
            var verifiedQualifier = SelectedChannel is null
                ? null
                : StreamVariantIdentityNormalizer.Normalize(SelectedChannel.Name).VerifiedLocaleQualifier;
            return string.IsNullOrWhiteSpace(verifiedQualifier)
                ? L("QualityBestMatch")
                : LF("QualityBestMatchOnly", verifiedQualifier);
        }
    }

    [ObservableProperty]
    private string notificationMessage = string.Empty;

    [ObservableProperty]
    private string movieEmptyStateMessage = L("SelectMoviesToLoad");

    [ObservableProperty]
    private string movieErrorMessage = string.Empty;

    [ObservableProperty]
    private string movieDetailsErrorMessage = string.Empty;

    [ObservableProperty]
    private string seriesEmptyStateMessage = L("SelectSeriesToLoad");

    [ObservableProperty]
    private string seriesErrorMessage = string.Empty;

    [ObservableProperty]
    private string seriesDetailsErrorMessage = string.Empty;

    [ObservableProperty]
    private SourceImportMode activeImportMode;

    [ObservableProperty]
    private string xtreamServerUrl = string.Empty;

    [ObservableProperty]
    private string xtreamUsername = string.Empty;

    [ObservableProperty]
    private string xtreamPassword = string.Empty;

    [ObservableProperty]
    private string playlistInput = string.Empty;

    [ObservableProperty]
    private string playlistDisplayName = string.Empty;

    [ObservableProperty]
    private string importFeedback = string.Empty;

    [ObservableProperty]
    private bool isEditingSource;

    private Guid? _editingSourceId;

    [ObservableProperty]
    private bool isImportMenuOpen;

    [ObservableProperty]
    private bool isCategorySearchVisible;

    [ObservableProperty]
    private bool isChannelSearchVisible;

    [ObservableProperty]
    private bool isMovieSearchVisible;

    [ObservableProperty]
    private bool isSeriesSearchVisible;

    [ObservableProperty]
    private bool isFavoritesCategorySelected;

    [ObservableProperty]
    private bool isMovieContinueWatchingExpanded;

    [ObservableProperty]
    private bool isSeriesContinueWatchingExpanded;

    [ObservableProperty]
    private bool isMovieWatchlistSelected;

    [ObservableProperty]
    private bool isSeriesWatchlistSelected;

    public string MuteButtonText => IsMuted ? L("Unmute") : L("Mute");

    public bool IsPlaybackPlaying => _currentPlaybackState == PlaybackState.Playing;

    public bool IsPlayerPauseIndicatorVisible => _currentPlaybackState == PlaybackState.Paused;

    public bool IsPlayerStopIndicatorVisible => _currentPlaybackState == PlaybackState.Stopped;

    public bool IsPlayerActionIndicatorVisible
        => IsPlayerPauseIndicatorVisible || IsPlayerStopIndicatorVisible;

    public string PlayPauseIconGlyph => IsPlaybackPlaying ? "\uE769" : "\uE768";

    public string PlayPauseIconGeometry => IsPlaybackPlaying
        ? "M4.5,3 L8,3 L8,15 L4.5,15 Z M10,3 L13.5,3 L13.5,15 L10,15 Z"
        : "M5,3.5 L15,9 L5,14.5 Z";

    public string PlayPauseToolTip => IsPlaybackPlaying ? L("Pause") : L("Play");

    public string VolumeIconGlyph => IsMuted
        ? "\uE74F"
        : Volume switch
        {
            <= 0d => "\uE992",
            <= 33d => "\uE993",
            <= 66d => "\uE994",
            _ => "\uE995",
        };

    public string VolumeIconGeometry => IsMuted
        ? "M2.5,7 L5.5,7 L9.5,3.5 L9.5,14.5 L5.5,11 L2.5,11 Z M12,6 L16,12 M16,6 L12,12"
        : Volume switch
        {
            <= 0d => "M2.5,7 L5.5,7 L9.5,3.5 L9.5,14.5 L5.5,11 L2.5,11 Z",
            <= 33d => "M2.5,7 L5.5,7 L9.5,3.5 L9.5,14.5 L5.5,11 L2.5,11 Z M11.5,7.5 C12.6,8.3 12.6,9.7 11.5,10.5",
            <= 66d => "M2.5,7 L5.5,7 L9.5,3.5 L9.5,14.5 L5.5,11 L2.5,11 Z M12,7 C13.5,8.2 13.5,9.8 12,11",
            _ => "M2.5,7 L5.5,7 L9.5,3.5 L9.5,14.5 L5.5,11 L2.5,11 Z M12,7 C13.5,8.2 13.5,9.8 12,11 M14,5 C17,7 17,11 14,13",
        };

    public string VolumeToolTip => IsMuted ? L("Unmute") : L("Mute");

    public bool IsLiveTvSection => ActiveSection == ShellSection.LiveTv;

    public bool IsMoviesSection => ActiveSection == ShellSection.Movies;

    public bool IsSeriesSection => ActiveSection == ShellSection.Series;

    public bool IsEventsSection => ActiveSection == ShellSection.Events;

    public bool IsPlaylistCategoriesVisible => IsLiveTvSection || IsEventsSection;
    public bool IsLiveTvOrEventsSection => IsLiveTvSection || IsEventsSection;

    public bool IsOnDemandSection => ActiveSection is ShellSection.Movies or ShellSection.Series;

    public bool HasLoadedLiveTvPlaylist => SelectedSource is not null
        && _liveLoadedSourceId == SelectedSource.Id
        && _allCategories.Count > 0;

    public string FavoritesCategorySummary => GetFavoriteChannelIds().Count == 1
        ? L("OneSaved")
        : LF("SavedCount", GetFavoriteChannelIds().Count);

    public int FavoriteChannelsCount => GetFavoriteChannelIds().Count;

    public bool HasMovieError => !string.IsNullOrWhiteSpace(MovieErrorMessage);

    public bool HasMovieDetailsError => !string.IsNullOrWhiteSpace(MovieDetailsErrorMessage);

    public bool IsMovieEmpty => !IsMovieCatalogLoading && !HasMovieError && VisibleMovies.Count == 0;

    public string MovieEmptyStateTitle => IsMovieWatchlistSelected
        ? L("NoSavedMoviesOrSeries")
        : L("NoMoviesFound");

    public bool IsMovieSkeletonVisible => IsMovieCatalogLoading && VisibleMovies.Count == 0;

    public bool IsMovieFeaturedSkeletonVisible => (IsMovieCatalogLoading || IsMovieDetailsLoading)
        && SelectedMovieDetails is null;

    public bool HasMoreMovies => _filteredMovies.Count > VisibleMovies.Count;

    public string MovieResultSummary => _filteredMovies.Count == 1
        ? L("OneMovie")
        : LF("MovieCount", _filteredMovies.Count);

    public string MovieLoadMoreText => HasMoreMovies
        ? LF("ShowMoreRemaining", _filteredMovies.Count - VisibleMovies.Count)
        : L("AllMoviesLoaded");

    public bool HasContinueWatchingMovies => ContinueWatchingMovies.Count > 0;

    public bool HasSelectedMovieResumeProgress => GetSelectedMovieResumeItem() is not null;

    public int SavedMoviesCount => GetMovieWatchlistIds().Count;

    public int SavedSeriesCount => GetSeriesWatchlistIds().Count;

    public int WatchlistTotalCount => SavedMoviesCount + SavedSeriesCount;

    public string MovieWatchlistHeaderText => LF("MovieWatchlistHeader", SavedMoviesCount);

    public string MovieWatchlistCountSummary => LF("SavedMoviesCount", SavedMoviesCount);

    public string MovieWatchlistSummary => GetMovieWatchlistIds().Count == 1
        ? L("OneSaved")
        : LF("SavedCount", GetMovieWatchlistIds().Count);

    public bool IsSelectedMovieInWatchlist => SelectedMovie?.IsInWatchlist ?? false;

    public string MovieWatchlistButtonText => IsSelectedMovieInWatchlist
        ? L("RemoveFromWatchlist")
        : L("AddToWatchlist");

    public bool HasSeriesError => !string.IsNullOrWhiteSpace(SeriesErrorMessage);

    public bool HasSeriesDetailsError => !string.IsNullOrWhiteSpace(SeriesDetailsErrorMessage);

    public bool IsSeriesEmpty => !IsSeriesCatalogLoading && !HasSeriesError && VisibleSeries.Count == 0;

    public string SeriesEmptyStateTitle => IsSeriesWatchlistSelected
        ? L("NoSavedMoviesOrSeries")
        : L("NoSeriesFound");

    public bool IsSeriesSkeletonVisible => IsSeriesCatalogLoading && VisibleSeries.Count == 0;

    public bool IsSeriesFeaturedSkeletonVisible => (IsSeriesCatalogLoading || IsSeriesDetailsLoading)
        && SelectedSeriesDetails is null;

    public bool HasMoreSeries => _filteredSeries.Count > VisibleSeries.Count;

    public string SeriesResultSummary => _filteredSeries.Count == 1
        ? L("OneSeries")
        : LF("SeriesCount", _filteredSeries.Count);

    public string SeriesLoadMoreText => HasMoreSeries
        ? LF("ShowMoreRemaining", _filteredSeries.Count - VisibleSeries.Count)
        : L("AllSeriesLoaded");

    public bool HasContinueWatchingSeries => ContinueWatchingSeries.Count > 0;

    public bool HasSelectedSeriesResumeProgress => GetSelectedSeriesResumeItem() is not null;

    public string SeriesWatchlistHeaderText => LF("SeriesWatchlistHeader", SavedSeriesCount);

    public string SeriesWatchlistCountSummary => LF("SavedSeriesCount", SavedSeriesCount);

    public string SeriesWatchlistSummary => GetSeriesWatchlistIds().Count == 1
        ? L("OneSaved")
        : LF("SavedCount", GetSeriesWatchlistIds().Count);

    public bool IsSelectedSeriesInWatchlist => SelectedSeries?.IsInWatchlist ?? false;

    public string SeriesWatchlistButtonText => IsSelectedSeriesInWatchlist
        ? L("RemoveFromWatchlist")
        : L("AddToWatchlist");

    public bool HasEventsError => !string.IsNullOrWhiteSpace(EventsErrorMessage);

    public bool IsEventsEmpty => !IsEventsLoading && !HasEventsError && TodayEvents.Count == 0;

    public bool HasSelectedSportsEvent => SelectedSportsEvent is not null;

    public bool IsEventsListVisible => !HasSelectedSportsEvent;

    public string EventsEmptyTitle => L("NoEventsToday");

    public string EventsMatchScopeText => HasLoadedLiveTvPlaylist
        ? L("EventsMatchCurrentCategory")
        : L("EventsMatchLoadPlaylist");

    public bool IsSelectedEventChannelsLoading =>
        EventChannelPresentationState.IsLoading(
            SelectedSportsEvent != null,
            _isEventChannelPresentationLoading,
            _eventsMatchingSourceId,
            _eventsMatchedSourceId);

    private bool HasFinalEventChannelMatch =>
        SelectedSource != null && _eventsMatchedSourceId == SelectedSource.Id;

    public bool IsEventChannelContentVisible =>
        HasFinalEventChannelMatch && !IsSelectedEventChannelsLoading && (SelectedSportsEvent?.HasVisibleChannelOptions ?? false);

    public bool IsEventCountrySelectorVisible =>
        HasFinalEventChannelMatch && (SelectedSportsEvent?.HasVisibleCountrySelector ?? false);

    public bool IsNoMatchingEventChannelVisible =>
        EventChannelPresentationState.IsNoMatchVisible(
            SelectedSportsEvent != null,
            HasFinalEventChannelMatch,
            SelectedSportsEvent?.HasChannelOptions ?? false,
            _isEventChannelPresentationLoading,
            _eventsMatchingSourceId,
            _eventsMatchedSourceId);

    public bool IsSeriesDetailsEmpty => !IsSeriesDetailsLoading
        && SelectedSeriesDetails is not null
        && !SelectedSeriesDetails.HasEpisodes;

    public bool IsImportPanelVisible => ActiveImportMode != SourceImportMode.None;

    public bool IsXtreamImportMode => ActiveImportMode == SourceImportMode.XtreamCodes;

    public bool IsPlaylistImportMode => ActiveImportMode is SourceImportMode.M3uUrl or SourceImportMode.M3uFile or SourceImportMode.M3u8Link;

    public bool IsM3uFileImportMode => ActiveImportMode == SourceImportMode.M3uFile;

    public bool IsPlaylistTextInputVisible => IsPlaylistImportMode && !IsM3uFileImportMode;

    public string ImportTitle => IsEditingSource
        ? L("EditPlaylist")
        : ActiveImportMode switch
    {
        SourceImportMode.XtreamCodes => L("AddXtreamPlaylist"),
        SourceImportMode.M3uUrl => L("AddM3uUrl"),
        SourceImportMode.M3uFile => L("ImportM3uFile"),
        SourceImportMode.M3u8Link => L("ImportDirectM3u8"),
        _ => L("ImportSource"),
    };

    public string SubmitImportButtonText => IsEditingSource ? L("SaveChanges") : L("Import");

    public string PlaylistInputLabel => ActiveImportMode switch
    {
        SourceImportMode.M3uUrl => L("PlaylistUrl"),
        SourceImportMode.M3uFile => L("M3uFile"),
        SourceImportMode.M3u8Link => L("M3u8StreamUrl"),
        _ => L("Input"),
    };

    public string PlaylistInputHint => ActiveImportMode switch
    {
        SourceImportMode.M3uUrl => L("EnterPlaylistUrl"),
        SourceImportMode.M3uFile => L("SelectM3uFileDevice"),
        SourceImportMode.M3u8Link => L("EnterStreamUrl"),
        _ => string.Empty,
    };

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand PlaySelectedChannelCommand { get; }

    public IAsyncRelayCommand<RecentChannelItemViewModel?> ResumeRecentChannelCommand { get; }

    public IRelayCommand<QuickQualityVariantViewModel?> SelectQuickQualityVariantCommand { get; }

    public IAsyncRelayCommand PlaySelectedMovieCommand { get; }

    public IAsyncRelayCommand PlaySelectedSeriesCommand { get; }

    public IAsyncRelayCommand ResumeSelectedMovieCommand { get; }

    public IAsyncRelayCommand ResumeSelectedSeriesCommand { get; }

    public IAsyncRelayCommand<SeriesEpisodeViewModel?> PlayEpisodeCommand { get; }

    public IAsyncRelayCommand<ContinueWatchingItemViewModel?> PlayContinueWatchingCommand { get; }

    public IAsyncRelayCommand SeekBackwardCommand { get; }

    public IAsyncRelayCommand SeekForwardCommand { get; }

    public IRelayCommand ToggleMovieContinueWatchingCommand { get; }

    public IRelayCommand ToggleSeriesContinueWatchingCommand { get; }

    public IAsyncRelayCommand SelectMovieWatchlistCommand { get; }

    public IAsyncRelayCommand SelectSeriesWatchlistCommand { get; }

    public IRelayCommand LoadMoreMoviesCommand { get; }

    public IRelayCommand LoadMoreSeriesCommand { get; }

    public IAsyncRelayCommand ToggleMovieWatchlistCommand { get; }

    public IAsyncRelayCommand ToggleSeriesWatchlistCommand { get; }

    public IAsyncRelayCommand ResumeCommand { get; }

    public IAsyncRelayCommand TogglePlayPauseCommand { get; }

    public IAsyncRelayCommand PauseCommand { get; }

    public IAsyncRelayCommand StopCommand { get; }

    public IRelayCommand PlayPreviousChannelCommand { get; }

    public IRelayCommand PlayNextChannelCommand { get; }

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand<ChannelItemViewModel?> ToggleFavoriteCommand { get; }

    public IAsyncRelayCommand SelectFavoritesCategoryCommand { get; }

    public IRelayCommand SelectLiveTvSectionCommand { get; }

    public IRelayCommand SelectMoviesSectionCommand { get; }

    public IRelayCommand SelectSeriesSectionCommand { get; }

    public IRelayCommand SelectEventsSectionCommand { get; }

    public IRelayCommand ReturnFromEventsCommand { get; }

    public IAsyncRelayCommand RefreshEventsCommand { get; }

    public IRelayCommand<SportsEventItemViewModel?> SelectEventCommand { get; }

    public IRelayCommand BackToEventsCommand { get; }

    public IAsyncRelayCommand<EventChannelOptionViewModel?> PlayEventChannelCommand { get; }

    public IRelayCommand<EventSportCategoryViewModel?> SelectEventSportCategoryCommand { get; }
 
    public IRelayCommand<EventFilterOptionViewModel?> SelectEventSortModeCommand { get; }

    public IRelayCommand<EventFilterOptionViewModel?> SelectEventTimeFilterCommand { get; }

    public IRelayCommand ToggleImportMenuCommand { get; }

    public IRelayCommand TogglePlaylistSidebarCommand { get; }

    public IRelayCommand ShowCategorySearchCommand { get; }

    public IRelayCommand CloseCategorySearchCommand { get; }

    public IRelayCommand ShowChannelSearchCommand { get; }

    public IRelayCommand CloseChannelSearchCommand { get; }

    public IRelayCommand ShowMovieSearchCommand { get; }

    public IRelayCommand CloseMovieSearchCommand { get; }

    public IRelayCommand ShowSeriesSearchCommand { get; }

    public IRelayCommand CloseSeriesSearchCommand { get; }

    public IRelayCommand AddXtreamSourceCommand { get; }

    public IRelayCommand AddM3uUrlSourceCommand { get; }

    public IRelayCommand AddM3uFileSourceCommand { get; }

    public IRelayCommand AddM3u8SourceCommand { get; }

    public IAsyncRelayCommand SubmitImportCommand { get; }

    public IRelayCommand EditSelectedSourceCommand { get; }

    public IAsyncRelayCommand DeleteSelectedSourceCommand { get; }

    public IAsyncRelayCommand RefreshSelectedSourceCommand { get; }

    public IRelayCommand CancelImportCommand { get; }

    public async Task InitializeAsync()
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;

        try
        {
            IsLoading = true;
            NotificationMessage = string.Empty;
            if (_hasSavedPlaylists)
            {
                IsStartupLoading = true;
                StartupLoadingMessage = L("LoadingPlaylist");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    await _catalog.WarmOnDemandMetadataCacheAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "On-demand metadata cache warmup skipped");
                }
            });

            var playbackInitializeTask = Task.Run(() => _playback.InitializeAsync());
            var sessionLoadTask = _session.LoadAsync();
            var onDemandStateLoadTask = _onDemandStateStore.LoadAsync();

            await Task.WhenAll(playbackInitializeTask, sessionLoadTask, onDemandStateLoadTask);

            _sessionSnapshot = await sessionLoadTask;
            _onDemandState = await onDemandStateLoadTask;
            RestoreOnDemandState();

            _restoredSourceId = _sessionSnapshot.LastSourceId;
            _restoredCategoryId = _sessionSnapshot.LastCategoryId;
            _restoredChannelId = _sessionSnapshot.LastChannelId;

            if (_restoredSourceId.HasValue && _hasSavedPlaylists)
            {
                IsStartupLoading = true;
                StartupLoadingMessage = L("LoadingPlaylist");
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                if (IsStartupLoading)
                {
                    _uiContext.Post(_ => IsStartupLoading = false, null);
                }
            });

            IsMuted = _sessionSnapshot.IsMuted;
            await _playback.SetMutedAsync(IsMuted);

            await LoadSourcesAsync(_restoredSourceId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed during shell initialization");
            NotificationMessage = L("FailedInitializeShell");
            IsStartupLoading = false;
        }
        finally
        {
            IsLoading = false;
            _isInitializing = false;
            if (!_hasSavedPlaylists || Sources.Count == 0)
            {
                IsStartupLoading = false;
            }

            DeleteSelectedSourceCommand.NotifyCanExecuteChanged();
            EditSelectedSourceCommand.NotifyCanExecuteChanged();
            RefreshSelectedSourceCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnActiveSectionChanged(ShellSection value)
    {
        OnPropertyChanged(nameof(IsLiveTvSection));
        OnPropertyChanged(nameof(IsMoviesSection));
        OnPropertyChanged(nameof(IsSeriesSection));
        OnPropertyChanged(nameof(IsEventsSection));
        OnPropertyChanged(nameof(IsPlaylistCategoriesVisible));
        OnPropertyChanged(nameof(IsLiveTvOrEventsSection));
        OnPropertyChanged(nameof(IsOnDemandSection));
        OnPropertyChanged(nameof(CurrentChannelDisplayTitle));
        NotifyLiveTvFavoritesStateChanged();
        NotifyQuickQualityStateChanged();
        NotifyMovieStateChanged();
        NotifySeriesStateChanged();
        NotifyEventsStateChanged();

        if (value != ShellSection.LiveTv && value != ShellSection.Events)
        {
            CancelSelectedEpgRefresh();
            CancelVisibleEpgRefresh();
        }
        else if (value == ShellSection.Events)
        {
            CancelVisibleEpgRefresh();
            if (SelectedChannel is not null && SelectedChannel.CurrentProgram is null)
            {
                BeginEpgRefresh(SelectedChannel);
            }
        }

        _ = ExecuteAndReportAsync(async () =>
        {
            if (SelectedSource is null)
            {
                if (value == ShellSection.Events)
                {
                    await LoadEventsAsync();
                }

                return;
            }

            if (value is ShellSection.LiveTv or ShellSection.Events)
            {
                if (_liveLoadedSourceId != SelectedSource.Id)
                {
                    await LoadCategoriesAsync(SelectedSource);
                }

                if (value == ShellSection.LiveTv)
                {
                    return;
                }
            }

            if (value == ShellSection.Movies)
            {
                await EnsureMoviesLoadedAsync(SelectedSource);
                return;
            }

            if (value == ShellSection.Series)
            {
                await EnsureSeriesLoadedAsync(SelectedSource);
                return;
            }

            await LoadEventsAsync(loadPlaylistChannels: true);
        });
    }

    partial void OnSelectedSourceChanged(SourceItemViewModel? value)
    {
        CancelAllChannelsEpgSync();
        DeleteSelectedSourceCommand.NotifyCanExecuteChanged();
        EditSelectedSourceCommand.NotifyCanExecuteChanged();
        RefreshSelectedSourceCommand.NotifyCanExecuteChanged();
        SelectMovieWatchlistCommand.NotifyCanExecuteChanged();
        SelectSeriesWatchlistCommand.NotifyCanExecuteChanged();
        ClearLiveTvState();

        RefreshEventItems();
        ClearFavoriteChannelCache();
        ClearMovieState(L("MoviesAppearWhenIncluded"));
        ClearSeriesState(L("SeriesAppearWhenIncluded"));
        IsMovieContinueWatchingExpanded = false;
        IsSeriesContinueWatchingExpanded = false;
        RefreshContinueWatchingForSelectedSource();
        NotifyLiveTvFavoritesStateChanged();
        NotifyMovieStateChanged();
        NotifySeriesStateChanged();
        NotifyEventsStateChanged();

        _ = ExecuteAndReportAsync(async () =>
        {
            if (value is null)
            {
                return;
            }

            await LoadSelectedSourceContentAsync(value);
            await PersistStateAsync();
        });
    }

    partial void OnSelectedCategoryChanged(CategoryItemViewModel? value)
    {
        _ = ExecuteAndReportAsync(async () =>
        {
            if (value is null || SelectedSource is null)
            {
                return;
            }

            IsFavoritesCategorySelected = false;
            await LoadChannelsAsync(SelectedSource, value);
            await PersistStateAsync();
        });
    }

    partial void OnSelectedChannelChanged(ChannelItemViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentChannelDisplayTitle));
        OnPropertyChanged(nameof(IsLivePlaybackActive));
        PlaySelectedChannelCommand.NotifyCanExecuteChanged();
        CancelSelectedEpgRefresh();
        RefreshQuickQualityVariants(value);

        if (value is null)
        {
            CancelLivePlaybackRequest();
            ShowPlayerSurfaceOverlay(L("Ready"), L("SelectChannelToStart"));
            return;
        }

        if (SelectedSource is not null)
        {
            _ = EnsureQuickQualityVariantsLoadedAsync(SelectedSource, value);
        }

        var (requestVersion, cancellationToken) = StartLivePlaybackRequest();
        _ = ExecuteAndReportAsync(() => PlayChannelInternalAsync(value, requestVersion, cancellationToken));
        BeginEpgRefresh(value);
    }

    partial void OnCurrentChannelTitleChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentChannelDisplayTitle));
        OnPropertyChanged(nameof(IsFullscreenHeaderTitleVisible));
    }

    partial void OnSelectedMovieCategoryChanged(CategoryItemViewModel? value)
    {
        if (_suppressMovieCategoryReload)
        {
            return;
        }

        IsMovieWatchlistSelected = false;

        if (_isLoadingMovieCatalog || !IsMoviesSection || SelectedSource is null)
        {
            return;
        }

        _ = ExecuteAndReportAsync(() => ReloadMoviesForCategoryAsync(SelectedSource, value));
    }

    partial void OnSelectedMovieChanged(MovieItemViewModel? value)
    {
        PlaySelectedMovieCommand.NotifyCanExecuteChanged();
        CancelMovieDetailsLoad();

        if (value is null)
        {
            SelectedMovieDetails = null;
            NotifyMovieStateChanged();
            return;
        }

        ApplyMovieWatchlistState(value);
        SelectedMovieDetails = MovieDetailsViewModel.FromMovie(value);
        _ = ExecuteAndReportAsync(() => LoadMovieDetailsAsync(value));
    }

    partial void OnSelectedMovieDetailsChanged(MovieDetailsViewModel? value)
    {
        PlaySelectedMovieCommand.NotifyCanExecuteChanged();
        NotifyMovieStateChanged();
    }

    partial void OnSelectedSeriesCategoryChanged(CategoryItemViewModel? value)
    {
        if (_suppressSeriesCategoryReload)
        {
            return;
        }

        IsSeriesWatchlistSelected = false;

        if (_isLoadingSeriesCatalog || !IsSeriesSection || SelectedSource is null)
        {
            return;
        }

        _ = ExecuteAndReportAsync(() => ReloadSeriesForCategoryAsync(SelectedSource, value));
    }

    partial void OnSelectedSeriesChanged(SeriesItemViewModel? value)
    {
        CancelSeriesDetailsLoad();

        if (value is null)
        {
            SelectedSeriesDetails = null;
            NotifySeriesStateChanged();
            return;
        }

        ApplySeriesWatchlistState(value);
        SelectedSeriesDetails = SeriesDetailsViewModel.FromSeries(value);
        _ = ExecuteAndReportAsync(() => LoadSeriesDetailsAsync(value));
    }

    partial void OnSelectedSeriesDetailsChanged(SeriesDetailsViewModel? value)
        => NotifySeriesStateChanged();

    partial void OnCategorySearchTextChanged(string value)
        => ApplyCategoryFilter(value);

    partial void OnChannelSearchTextChanged(string value)
        => QueueChannelFilter(value);

    partial void OnMovieSearchTextChanged(string value)
    {
        VisibleMovieLimit = OnDemandPageSize;
        QueueMovieFilter(value);
    }

    partial void OnSeriesSearchTextChanged(string value)
    {
        VisibleSeriesLimit = OnDemandPageSize;
        QueueSeriesFilter(value);
    }

    partial void OnIsMovieCatalogLoadingChanged(bool value)
        => NotifyMovieStateChanged();

    partial void OnIsMovieDetailsLoadingChanged(bool value)
        => NotifyMovieStateChanged();

    partial void OnVisibleMovieLimitChanged(int value)
        => NotifyMovieStateChanged();

    partial void OnMovieErrorMessageChanged(string value)
        => NotifyMovieStateChanged();

    partial void OnMovieDetailsErrorMessageChanged(string value)
        => NotifyMovieStateChanged();

    partial void OnIsSeriesCatalogLoadingChanged(bool value)
        => NotifySeriesStateChanged();

    partial void OnIsSeriesDetailsLoadingChanged(bool value)
        => NotifySeriesStateChanged();

    partial void OnVisibleSeriesLimitChanged(int value)
        => NotifySeriesStateChanged();

    partial void OnSeriesErrorMessageChanged(string value)
        => NotifySeriesStateChanged();

    partial void OnSeriesDetailsErrorMessageChanged(string value)
        => NotifySeriesStateChanged();

    partial void OnIsEventsLoadingChanged(bool value)
    {
        NotifyEventsStateChanged();
        RefreshEventsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEventsRefreshingChanged(bool value)
    {
        NotifyEventsStateChanged();
        RefreshEventsCommand.NotifyCanExecuteChanged();
    }

    partial void OnEventsErrorMessageChanged(string value)
        => NotifyEventsStateChanged();

    partial void OnSelectedEventSportCategoryChanged(EventSportCategoryViewModel? value)
    {
        SelectedSportsEvent = null;
        UpdateEventCategorySelection();
        RefreshEventItems();
    }

    partial void OnSelectedEventSortModeChanged(EventFilterOptionViewModel? value)
    {
        UpdateEventFilterSelection();
        RefreshEventItems();
    }

    partial void OnSelectedEventTimeFilterChanged(EventFilterOptionViewModel? value)
    {
        UpdateEventFilterSelection();
        RefreshEventItems();
    }

    partial void OnSelectedSportsEventChanged(SportsEventItemViewModel? value)
    {
        _isEventChannelPresentationLoading = value is not null;
        OnPropertyChanged(nameof(HasSelectedSportsEvent));
        OnPropertyChanged(nameof(IsEventsListVisible));
        NotifyEventsStateChanged();
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MuteButtonText));
        OnPropertyChanged(nameof(VolumeIconGlyph));
        OnPropertyChanged(nameof(VolumeIconGeometry));
        OnPropertyChanged(nameof(VolumeToolTip));
        SubmitImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeIconGlyph));
        OnPropertyChanged(nameof(VolumeIconGeometry));
        if (_isApplyingExternalAudioState)
        {
            return;
        }

        var requestedVolume = (int)Math.Round(Math.Clamp(value, 0d, 100d));
        _ = ExecuteAndReportAsync(() => _playback.SetVolumeAsync(requestedVolume));
    }

    partial void OnActiveImportModeChanged(SourceImportMode value)
    {
        OnPropertyChanged(nameof(IsImportPanelVisible));
        OnPropertyChanged(nameof(IsXtreamImportMode));
        OnPropertyChanged(nameof(IsPlaylistImportMode));
        OnPropertyChanged(nameof(IsM3uFileImportMode));
        OnPropertyChanged(nameof(IsPlaylistTextInputVisible));
        OnPropertyChanged(nameof(ImportTitle));
        OnPropertyChanged(nameof(PlaylistInputLabel));
        OnPropertyChanged(nameof(PlaylistInputHint));
        OnPropertyChanged(nameof(SubmitImportButtonText));
        SubmitImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEditingSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(ImportTitle));
        OnPropertyChanged(nameof(SubmitImportButtonText));
        SubmitImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnXtreamServerUrlChanged(string value)
        => SubmitImportCommand.NotifyCanExecuteChanged();

    partial void OnXtreamUsernameChanged(string value)
        => SubmitImportCommand.NotifyCanExecuteChanged();

    partial void OnXtreamPasswordChanged(string value)
        => SubmitImportCommand.NotifyCanExecuteChanged();

    partial void OnPlaylistInputChanged(string value)
        => SubmitImportCommand.NotifyCanExecuteChanged();

    private bool CanPlaySelectedChannel()
        => SelectedChannel is not null;

    private bool CanPlaySelectedMovie()
        => !_isStartingOnDemandPlayback && (SelectedMovieDetails is not null || SelectedMovie is not null);

    private bool CanPlaySelectedSeries()
        => !_isStartingOnDemandPlayback
           && (SelectedSeriesDetails?.Seasons.SelectMany(season => season.Episodes).Any() ?? false);

    private bool CanResumeSelectedMovie()
        => !_isStartingOnDemandPlayback && HasSelectedMovieResumeProgress;

    private bool CanResumeSelectedSeries()
        => !_isStartingOnDemandPlayback && HasSelectedSeriesResumeProgress;

    private ContinueWatchingItemViewModel? GetSelectedMovieResumeItem()
    {
        var movieId = SelectedMovieDetails?.Id ?? SelectedMovie?.Id;
        return string.IsNullOrWhiteSpace(movieId)
            ? null
            : ContinueWatchingMovies.FirstOrDefault(item =>
                item.HasResumableProgress
                && string.Equals(item.MediaId, movieId, StringComparison.OrdinalIgnoreCase));
    }

    private ContinueWatchingItemViewModel? GetSelectedSeriesResumeItem()
    {
        var seriesId = SelectedSeriesDetails?.Id ?? SelectedSeries?.Id;
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        var episodeIds = SelectedSeriesDetails?
            .Seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => episode.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ContinueWatchingSeries.FirstOrDefault(item =>
            item.HasResumableProgress
            && (string.Equals(item.ParentId, seriesId, StringComparison.OrdinalIgnoreCase)
                || (episodeIds?.Contains(item.MediaId) ?? false)));
    }

    private bool CanDeleteSelectedSource()
        => SelectedSource is not null;

    private bool CanEditSelectedSource()
        => SelectedSource is not null;

    private bool CanRefreshSelectedSource()
        => SelectedSource is not null && !_isRefreshingSource;

    private bool CanSubmitImport()
    {
        if (_isImporting)
        {
            return false;
        }

        if (IsEditingSource && _editingSourceId.HasValue)
        {
            return ActiveImportMode is SourceImportMode.XtreamCodes
                or SourceImportMode.M3uUrl
                or SourceImportMode.M3uFile
                or SourceImportMode.M3u8Link;
        }

        return ActiveImportMode switch
        {
            SourceImportMode.XtreamCodes
                => !string.IsNullOrWhiteSpace(XtreamServerUrl)
                   && !string.IsNullOrWhiteSpace(XtreamUsername)
                   && !string.IsNullOrWhiteSpace(XtreamPassword),
            SourceImportMode.M3uUrl
                => !string.IsNullOrWhiteSpace(PlaylistInput),
            SourceImportMode.M3uFile or SourceImportMode.M3u8Link
                => !string.IsNullOrWhiteSpace(PlaylistInput),
            _ => false,
        };
    }

    private async Task RefreshAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        if (IsMoviesSection && IsMovieWatchlistSelected)
        {
            await LoadMovieWatchlistAsync(SelectedSource);
            return;
        }

        if (IsSeriesSection && IsSeriesWatchlistSelected)
        {
            await LoadSeriesWatchlistAsync(SelectedSource);
            return;
        }

        await LoadCategoriesAsync(SelectedSource);
    }

    private async Task PlaySelectedChannelAsync()
    {
        if (SelectedChannel is null)
        {
            _logger.LogInformation("Play command invoked but no channel is selected");
            return;
        }

        _logger.LogInformation("Play command invoked for channel {ChannelId} - {ChannelName}", SelectedChannel.Id, SelectedChannel.Name);
        var (requestVersion, cancellationToken) = StartLivePlaybackRequest();
        await PlayChannelInternalAsync(SelectedChannel, requestVersion, cancellationToken);
    }

    private async Task ResumeRecentChannelAsync(RecentChannelItemViewModel? recent)
    {
        if (recent is null || SelectedSource is null)
        {
            return;
        }

        var channel = VisibleChannels.FirstOrDefault(item =>
                          string.Equals(item.Id, recent.Id, StringComparison.OrdinalIgnoreCase))
                      ?? recent.Channel;

        _logger.LogInformation(
            "Resuming recent channel {ChannelId} - {ChannelName}",
            channel.Id,
            channel.Name);

        if (!ReferenceEquals(SelectedChannel, channel))
        {
            SelectedChannel = channel;
            return;
        }

        var (requestVersion, cancellationToken) = StartLivePlaybackRequest();
        await PlayChannelInternalAsync(channel, requestVersion, cancellationToken);
    }

    private void SelectQuickQualityVariant(QuickQualityVariantViewModel? variant)
    {
        if (variant is null
            || SelectedChannel is null
            || string.Equals(SelectedChannel.Id, variant.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.LogInformation(
            "Quick Quality switching directly to stream {ChannelId} - {ChannelName} ({QualityLabel})",
            variant.Channel.Id,
            variant.Channel.Name,
            variant.QualityLabel);
        SelectedChannel = VisibleChannels.FirstOrDefault(channel =>
                              string.Equals(channel.Id, variant.Channel.Id, StringComparison.OrdinalIgnoreCase))
                          ?? variant.Channel;
    }

    private async Task PlaySelectedMovieAsync()
    {
        var movie = SelectedMovieDetails ?? (SelectedMovie is null ? null : MovieDetailsViewModel.FromMovie(SelectedMovie));
        if (movie is null)
        {
            _logger.LogInformation("Movie play command invoked but no movie is selected");
            return;
        }

        await PlayOnDemandUriAsync(
            movie.PlaybackUri,
            movie.Title,
            async () =>
            {
                CurrentChannelTitle = movie.Title;
                await BeginContinueWatchingMovieTrackingAsync(movie);
            },
            $"movie {movie.Id} - {movie.Title}");
    }

    private async Task PlayEpisodeAsync(SeriesEpisodeViewModel? episode)
    {
        if (episode is null)
        {
            _logger.LogInformation("Episode play command invoked but no episode was supplied");
            return;
        }

        var title = SelectedSeriesDetails is null
            ? episode.Title
            : $"{SelectedSeriesDetails.Title}: {episode.Title}";

        await PlayOnDemandUriAsync(
            episode.PlaybackUri,
            title,
            async () =>
            {
                CurrentChannelTitle = title;
                await BeginContinueWatchingSeriesTrackingAsync(episode);
            },
            $"episode {episode.Id} - {title}");
    }

    private async Task PlaySelectedSeriesAsync()
    {
        var episode = SelectedSeriesDetails?
            .Seasons
            .SelectMany(season => season.Episodes)
            .OrderBy(episode => episode.EpisodeNumber)
            .FirstOrDefault();

        if (episode is null)
        {
            return;
        }

        await PlayEpisodeAsync(episode);
    }

    private async Task ResumeSelectedMovieAsync()
        => await ResumeContinueWatchingAsync(GetSelectedMovieResumeItem(), "movie");

    private async Task ResumeSelectedSeriesAsync()
        => await ResumeContinueWatchingAsync(GetSelectedSeriesResumeItem(), "series");

    private async Task ResumeContinueWatchingAsync(ContinueWatchingItemViewModel? item, string mediaKind)
    {
        if (item is null || !item.HasResumableProgress || item.PlaybackUri.Scheme == "about")
        {
            return;
        }

        await PlayOnDemandUriAsync(
            item.PlaybackUri,
            item.Title,
            () =>
            {
                CurrentChannelTitle = item.Title;
                BeginContinueWatchingItemTracking(item);
                return Task.CompletedTask;
            },
            $"resume {mediaKind} {item.MediaId} - {item.Title}",
            item.ProgressPercent);
    }

    private async Task PlayContinueWatchingAsync(ContinueWatchingItemViewModel? item)
    {
        if (item is null || item.PlaybackUri.Scheme == "about")
        {
            return;
        }

        await PlayOnDemandUriAsync(
            item.PlaybackUri,
            item.Title,
            () =>
            {
                CurrentChannelTitle = item.Title;
                BeginContinueWatchingItemTracking(item);
                return Task.CompletedTask;
            },
            $"continue watching item {item.MediaId} - {item.Title}");
    }

    private async Task PlayOnDemandUriAsync(
        Uri playbackUri,
        string title,
        Func<Task> afterPlaybackStarted,
        string logLabel,
        double? resumeProgressPercent = null)
    {
        if (TouchActiveRecentChannel(DateTimeOffset.UtcNow, endSession: true))
        {
            await PersistStateAsync();
        }

        CancelLivePlaybackRequest();

        if (!await _onDemandPlaybackGate.WaitAsync(0))
        {
            _logger.LogInformation("Ignored duplicate on-demand play request for {MediaLabel}", logLabel);
            return;
        }

        SetOnDemandPlaybackStarting(true);
        PlaybackStatusText = L("StartingPlayback");
        CurrentChannelTitle = title;
        IsNativeVideoSurfaceVisible = false;
        ShowPlayerSurfaceOverlay(L("OpeningStream"), title);

        try
        {
            _logger.LogInformation("Play command invoked for {MediaLabel}", logLabel);
            await Task.Run(async () => await _playback.PlayUriAsync(playbackUri, title).ConfigureAwait(false));
            SetOnDemandPlaybackActive(true);
            await afterPlaybackStarted();

            if (resumeProgressPercent is > 0d and < 100d)
            {
                await SeekToSavedProgressAsync(resumeProgressPercent.Value, logLabel);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to start on-demand playback for {MediaLabel}", logLabel);
            SetOnDemandPlaybackActive(false);
            PlaybackStatusText = L("PlaybackFailed");
            NotificationMessage = L("PlaybackCouldNotStart");
        }
        finally
        {
            SetOnDemandPlaybackStarting(false);
            _onDemandPlaybackGate.Release();
        }
    }

    private async Task SeekToSavedProgressAsync(double progressPercent, string logLabel)
    {
        for (var attempt = 0; attempt < ResumeSeekMaxAttempts; attempt++)
        {
            var progress = await _playback.GetProgressAsync().ConfigureAwait(false);
            if (progress.CanSeek && progress.Duration > TimeSpan.Zero)
            {
                var target = TimeSpan.FromTicks((long)(progress.Duration.Ticks * (progressPercent / 100d)));
                await _playback.SeekAsync(target).ConfigureAwait(false);
                await RefreshPlaybackProgressAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation(
                    "Resumed {MediaLabel} at {ProgressPercent:F1}% ({ResumePosition})",
                    logLabel,
                    progressPercent,
                    target);
                return;
            }

            await Task.Delay(ResumeSeekRetryInterval).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Could not seek {MediaLabel} to saved progress {ProgressPercent:F1}% because the stream was not seekable",
            logLabel,
            progressPercent);
    }

    private async Task SelectMovieWatchlistAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        IsMovieWatchlistSelected = true;
        _suppressMovieCategoryReload = true;
        try
        {
            SelectedMovieCategory = null;
        }
        finally
        {
            _suppressMovieCategoryReload = false;
        }

        await LoadMovieWatchlistAsync(SelectedSource);
    }

    private async Task SelectSeriesWatchlistAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        IsSeriesWatchlistSelected = true;
        _suppressSeriesCategoryReload = true;
        try
        {
            SelectedSeriesCategory = null;
        }
        finally
        {
            _suppressSeriesCategoryReload = false;
        }

        await LoadSeriesWatchlistAsync(SelectedSource);
    }

    private void SetOnDemandPlaybackStarting(bool value)
    {
        _isStartingOnDemandPlayback = value;
        PlaySelectedMovieCommand.NotifyCanExecuteChanged();
        PlaySelectedSeriesCommand.NotifyCanExecuteChanged();
        ResumeSelectedMovieCommand.NotifyCanExecuteChanged();
        ResumeSelectedSeriesCommand.NotifyCanExecuteChanged();
    }

    private async Task SeekBackwardAsync()
    {
        await _playback.SeekRelativeAsync(-PlaybackSeekStep);
        await RefreshPlaybackProgressAsync(CancellationToken.None);
    }

    private async Task SeekForwardAsync()
    {
        await _playback.SeekRelativeAsync(PlaybackSeekStep);
        await RefreshPlaybackProgressAsync(CancellationToken.None);
    }

    private void SetOnDemandPlaybackActive(bool active)
    {
        if (IsOnDemandPlaybackActive == active)
        {
            if (active && _playbackProgressCts is null)
            {
                StartPlaybackProgressRefresh();
            }

            return;
        }

        IsOnDemandPlaybackActive = active;

        if (active)
        {
            StartPlaybackProgressRefresh();
            return;
        }

        _activeOnDemandHistoryItem = null;
        _activeOnDemandHistoryIsSeries = false;
        _lastPlaybackProgressPersistenceUtc = DateTimeOffset.MinValue;
        StopPlaybackProgressRefresh(resetProgress: true);
    }

    private void StartPlaybackProgressRefresh()
    {
        StopPlaybackProgressRefresh(resetProgress: false);

        _playbackProgressCts = new CancellationTokenSource();
        var cancellationToken = _playbackProgressCts.Token;

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshPlaybackProgressAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(PlaybackProgressRefreshInterval, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    private void StopPlaybackProgressRefresh(bool resetProgress)
    {
        _playbackProgressCts?.Cancel();
        _playbackProgressCts?.Dispose();
        _playbackProgressCts = null;

        _playbackSeekCts?.Cancel();
        _playbackSeekCts?.Dispose();
        _playbackSeekCts = null;

        if (!resetProgress)
        {
            return;
        }

        _isUpdatingPlaybackProgress = true;
        try
        {
            IsPlaybackSeekAvailable = false;
            PlaybackPositionSeconds = 0d;
            PlaybackDurationSeconds = 0d;
            PlaybackPositionText = "00:00";
            PlaybackDurationText = "00:00";
        }
        finally
        {
            _isUpdatingPlaybackProgress = false;
        }
    }

    private async Task RefreshPlaybackProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            var progress = await _playback.GetProgressAsync(cancellationToken).ConfigureAwait(false);
            _uiContext.Post(_ => ApplyPlaybackProgress(progress), null);
        }
        catch (OperationCanceledException)
        {
            // Playback changed or window closed; the next active playback starts a new poller.
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Playback progress refresh skipped");
        }
    }

    private void ApplyPlaybackProgress(PlaybackProgress progress)
    {
        _isUpdatingPlaybackProgress = true;
        try
        {
            var durationSeconds = Math.Max(0d, progress.Duration.TotalSeconds);
            var positionSeconds = Math.Clamp(
                progress.Position.TotalSeconds,
                0d,
                durationSeconds > 0d ? durationSeconds : Math.Max(0d, progress.Position.TotalSeconds));

            IsPlaybackSeekAvailable = IsOnDemandPlaybackActive && progress.CanSeek && durationSeconds > 0d;
            PlaybackDurationSeconds = durationSeconds;
            PlaybackPositionSeconds = positionSeconds;
            PlaybackDurationText = FormatPlaybackTime(progress.Duration);
            PlaybackPositionText = FormatPlaybackTime(progress.Position);
            UpdateActiveOnDemandProgress(progress.CanSeek, positionSeconds, durationSeconds);
        }
        finally
        {
            _isUpdatingPlaybackProgress = false;
        }
    }

    private void QueuePlaybackSeek(double positionSeconds)
    {
        _playbackSeekCts?.Cancel();
        _playbackSeekCts?.Dispose();
        _playbackSeekCts = new CancellationTokenSource();
        var cancellationToken = _playbackSeekCts.Token;
        var target = TimeSpan.FromSeconds(Math.Clamp(positionSeconds, 0d, Math.Max(0d, PlaybackDurationSeconds)));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PlaybackSeekDebounce, cancellationToken).ConfigureAwait(false);
                await _playback.SeekAsync(target, cancellationToken).ConfigureAwait(false);
                await RefreshPlaybackProgressAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A newer drag/click target replaced this seek.
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Playback seek skipped");
            }
        }, cancellationToken);
    }

    private static string FormatPlaybackTime(TimeSpan value)
        => value.TotalHours >= 1d
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");

    private void AutoClearNotification(string message)
    {
        _notificationClearCts?.Cancel();
        _notificationClearCts?.Dispose();
        _notificationClearCts = new CancellationTokenSource();
        var cancellationToken = _notificationClearCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SuccessNotificationDuration, cancellationToken).ConfigureAwait(false);
                _uiContext.Post(_ =>
                {
                    if (string.Equals(NotificationMessage, message, StringComparison.Ordinal))
                    {
                        NotificationMessage = string.Empty;
                    }
                }, null);
            }
            catch (OperationCanceledException)
            {
                // A newer notification replaced this one.
            }
        }, cancellationToken);
    }

    private async Task ToggleSelectedMovieWatchlistAsync()
    {
        if (SelectedMovie is null)
        {
            return;
        }

        SelectedMovie.IsInWatchlist = !SelectedMovie.IsInWatchlist;
        UpdateMovieWatchlistCacheState(SelectedMovie.Id, SelectedMovie.IsInWatchlist);
        await PersistOnDemandStateAsync();
        if (IsMovieWatchlistSelected && SelectedSource is not null)
        {
            await LoadMovieWatchlistAsync(SelectedSource);
        }

        NotifyMovieStateChanged();
    }

    private async Task ToggleSelectedSeriesWatchlistAsync()
    {
        if (SelectedSeries is null)
        {
            return;
        }

        SelectedSeries.IsInWatchlist = !SelectedSeries.IsInWatchlist;
        UpdateSeriesWatchlistCacheState(SelectedSeries.Id, SelectedSeries.IsInWatchlist);
        await PersistOnDemandStateAsync();
        if (IsSeriesWatchlistSelected && SelectedSource is not null)
        {
            await LoadSeriesWatchlistAsync(SelectedSource);
        }

        NotifySeriesStateChanged();
    }

    private async Task PauseAsync()
    {
        _logger.LogInformation("Pause command invoked");
        await _playback.PauseAsync();
    }

    private async Task TogglePlayPauseAsync()
    {
        if (IsMoviesSection && !IsOnDemandPlaybackActive)
        {
            await PlaySelectedMovieAsync();
            return;
        }

        if (IsSeriesSection && !IsOnDemandPlaybackActive)
        {
            await PlaySelectedSeriesAsync();
            return;
        }

        if (_currentPlaybackState == PlaybackState.Playing)
        {
            await PauseAsync();
            return;
        }

        if (_currentPlaybackState == PlaybackState.Paused)
        {
            _logger.LogInformation("Resume command invoked from player control bar");
            await _playback.ResumeAsync();
            return;
        }

        if (IsLiveTvOrEventsSection)
        {
            await PlaySelectedChannelAsync();
            return;
        }

        await ResumeAsync();
    }

    private async Task ResumeAsync()
    {
        if (!IsOnDemandPlaybackActive)
        {
            return;
        }

        _logger.LogInformation("Resume command invoked");
        await _playback.ResumeAsync();
    }

    private void PlayPreviousChannel()
    {
        var channel = GetAdjacentVisibleChannel(-1);
        if (channel is not null)
        {
            SelectedChannel = channel;
        }
    }

    private void PlayNextChannel()
    {
        var channel = GetAdjacentVisibleChannel(1);
        if (channel is not null)
        {
            SelectedChannel = channel;
        }
    }

    private async Task StopAsync()
    {
        _logger.LogInformation("Stop command invoked");
        var recentHistoryChanged = TouchActiveRecentChannel(DateTimeOffset.UtcNow, endSession: true);
        CancelLivePlaybackRequest();
        await PersistActiveOnDemandProgressAsync();
        await _playback.StopAsync();
        if (recentHistoryChanged)
        {
            await PersistStateAsync();
        }

        SetOnDemandPlaybackActive(false);
        PlaybackStatusText = L("Stopped");
        IsNativeVideoSurfaceVisible = false;
        ShowPlayerSurfaceOverlay(string.Empty, string.Empty, showText: false);
    }

    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        _logger.LogInformation("Mute command invoked. New mute state: {IsMuted}", IsMuted);

        _ = ExecuteAndReportAsync(async () =>
        {
            await _playback.SetMutedAsync(IsMuted);
            await PersistStateAsync();
        });
    }

    private void ToggleFavorite(ChannelItemViewModel? channel)
    {
        if (channel is null)
        {
            return;
        }

        channel.IsFavorite = !channel.IsFavorite;
        UpdateFavoriteSessionSnapshot(channel);
        UpdateFavoriteChannelCache(channel);
        _ = ExecuteAndReportAsync(() => PersistStateAsync());
    }

    private async Task SelectFavoritesCategoryAsync()
    {
        if (SelectedSource is null || !HasLoadedLiveTvPlaylist)
        {
            return;
        }

        IsFavoritesCategorySelected = true;
        SelectedCategory = null;
        await EnsureFavoriteChannelsCachedAsync(SelectedSource);
        ShowFavoriteChannelsFromCache();
        await PersistStateAsync();
    }

    private void OpenImportPanel(SourceImportMode mode)
    {
        IsImportMenuOpen = false;
        IsEditingSource = false;
        _editingSourceId = null;
        ClearImportInputs();
        ActiveImportMode = mode;
        ImportFeedback = string.Empty;
        NotificationMessage = string.Empty;
    }

    private void EditSelectedSource()
    {
        if (SelectedSource is null)
        {
            return;
        }

        IsImportMenuOpen = false;
        IsEditingSource = true;
        _editingSourceId = SelectedSource.Id;
        ActiveImportMode = SelectedSource.Kind switch
        {
            IptvPlayer.Contracts.Models.SourceKind.XtreamCodes => SourceImportMode.XtreamCodes,
            IptvPlayer.Contracts.Models.SourceKind.M3uUrl => SourceImportMode.M3uUrl,
            IptvPlayer.Contracts.Models.SourceKind.M3uFile => SourceImportMode.M3uFile,
            IptvPlayer.Contracts.Models.SourceKind.M3u8Link => SourceImportMode.M3u8Link,
            _ => SourceImportMode.None,
        };

        PlaylistDisplayName = SelectedSource.Name;
        XtreamServerUrl = SelectedSource.Kind == IptvPlayer.Contracts.Models.SourceKind.XtreamCodes
            ? SelectedSource.Endpoint
            : string.Empty;
        XtreamUsername = string.Empty;
        XtreamPassword = string.Empty;
        PlaylistInput = SelectedSource.Kind == IptvPlayer.Contracts.Models.SourceKind.XtreamCodes
            ? string.Empty
            : SelectedSource.Endpoint;
        ImportFeedback = L("UpdatePlaylistThenSave");
        NotificationMessage = string.Empty;
        SubmitImportCommand.NotifyCanExecuteChanged();
    }

    private void CancelImport()
    {
        if (_isImporting)
        {
            _importCts?.Cancel();
            return;
        }

        IsImportMenuOpen = false;
        ActiveImportMode = SourceImportMode.None;
        IsEditingSource = false;
        _editingSourceId = null;
        ClearImportInputs();
        ImportFeedback = string.Empty;
    }

    private void CloseCategorySearch()
    {
        CategorySearchText = string.Empty;
        IsCategorySearchVisible = false;
    }

    private void CloseChannelSearch()
    {
        ChannelSearchText = string.Empty;
        IsChannelSearchVisible = false;
    }

    private void CloseMovieSearch()
    {
        MovieSearchText = string.Empty;
        IsMovieSearchVisible = false;
    }

    private void CloseSeriesSearch()
    {
        SeriesSearchText = string.Empty;
        IsSeriesSearchVisible = false;
    }

    private async Task SubmitImportAsync()
    {
        if (IsEditingSource)
        {
            await SaveSourceEditsAsync();
            return;
        }

        var request = BuildImportRequest();
        if (request is null)
        {
            ImportFeedback = L("CompleteRequiredFields");
            return;
        }

        _isImporting = true;
        OnPropertyChanged(nameof(IsImporting));
        ImportProgressPercent = 0;
        SubmitImportCommand.NotifyCanExecuteChanged();
        IsLoading = true;
        ImportFeedback = string.Empty;
        _importCts = new CancellationTokenSource();
        var progress = new ImportProgressTarget();
        using var progressAnimationCts = CancellationTokenSource.CreateLinkedTokenSource(_importCts.Token);
        var progressAnimationTask = AnimateImportProgressAsync(progress, progressAnimationCts.Token);

        try
        {
            var result = await _sourceImport.ImportAsync(request, progress, _importCts.Token);
            if (!result.Success || result.Source is null)
            {
                ImportFeedback = result.Message;
                NotificationMessage = result.Message;
                return;
            }

            await LoadSourcesAsync(result.Source.Id);
            progress.Report(new SourceImportProgress(100));
            await progressAnimationTask;
            ImportFeedback = L("PlaylistAdded");
            NotificationMessage = L("PlaylistAdded");
            ActiveImportMode = SourceImportMode.None;
            ClearImportInputs();
            StartAllChannelsEpgSync(result.Source.Id);
        }
        catch (OperationCanceledException) when (_importCts.IsCancellationRequested)
        {
            ImportFeedback = string.Empty;
        }
        finally
        {
            progressAnimationCts.Cancel();
            await IgnoreCancellationAsync(progressAnimationTask);
            _importCts.Dispose();
            _importCts = null;
            _isImporting = false;
            OnPropertyChanged(nameof(IsImporting));
            SubmitImportCommand.NotifyCanExecuteChanged();
            IsLoading = false;
        }
    }

    private async Task SaveSourceEditsAsync()
    {
        var request = BuildUpdateRequest();
        if (request is null)
        {
            ImportFeedback = L("CompletePlaylistDetails");
            return;
        }

        _isImporting = true;
        OnPropertyChanged(nameof(IsImporting));
        ImportProgressPercent = 0;
        SubmitImportCommand.NotifyCanExecuteChanged();
        IsLoading = true;
        ImportFeedback = string.Empty;
        _importCts = new CancellationTokenSource();
        var progress = new ImportProgressTarget();
        using var progressAnimationCts = CancellationTokenSource.CreateLinkedTokenSource(_importCts.Token);
        var progressAnimationTask = AnimateImportProgressAsync(progress, progressAnimationCts.Token);
        var resetSavedState = IsPlaylistConnectionChanging(request);

        try
        {
            var result = await _sourceImport.UpdateAsync(request, progress, _importCts.Token);
            if (!result.Success || result.Source is null)
            {
                ImportFeedback = result.Message;
                NotificationMessage = result.Message;
                return;
            }

            if (resetSavedState)
            {
                await RemoveSavedSourceStateAsync(result.Source.Id);
            }

            await LoadSourcesAsync(result.Source.Id);
            progress.Report(new SourceImportProgress(100));
            await progressAnimationTask;
            ImportFeedback = L("PlaylistSaved");
            NotificationMessage = L("PlaylistSaved");
            ActiveImportMode = SourceImportMode.None;
            IsEditingSource = false;
            _editingSourceId = null;
            ClearImportInputs();
            StartAllChannelsEpgSync(result.Source.Id);
        }
        catch (OperationCanceledException) when (_importCts.IsCancellationRequested)
        {
            ImportFeedback = string.Empty;
        }
        finally
        {
            progressAnimationCts.Cancel();
            await IgnoreCancellationAsync(progressAnimationTask);
            _importCts.Dispose();
            _importCts = null;
            _isImporting = false;
            OnPropertyChanged(nameof(IsImporting));
            SubmitImportCommand.NotifyCanExecuteChanged();
            IsLoading = false;
        }
    }

    private async Task AnimateImportProgressAsync(
        ImportProgressTarget progress,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var frameStopwatch = Stopwatch.StartNew();
        var displayedPercent = 0d;
        double? completionStartPercent = null;
        TimeSpan completionStartedAt = default;
        TimeSpan completionDuration = default;

        ImportProgressPercent = 0;

        while (true)
        {
            await Task.Delay(ImportProgressFrameInterval, cancellationToken);

            var frameSeconds = Math.Max(frameStopwatch.Elapsed.TotalSeconds, 0.001d);
            frameStopwatch.Restart();
            var targetPercent = progress.Percent;

            if (targetPercent >= 100d)
            {
                if (completionStartPercent is null)
                {
                    completionStartPercent = displayedPercent;
                    completionStartedAt = totalStopwatch.Elapsed;
                    var remainingFastWindow = ImportProgressFastCompletionDuration - completionStartedAt;
                    completionDuration = remainingFastWindow > TimeSpan.FromMilliseconds(100)
                        ? remainingFastWindow
                        : TimeSpan.FromMilliseconds(100);
                }

                var elapsed = totalStopwatch.Elapsed - completionStartedAt;
                var fraction = Math.Clamp(elapsed.TotalMilliseconds / completionDuration.TotalMilliseconds, 0d, 1d);
                var easedFraction = 1d - Math.Pow(1d - fraction, 2d);
                displayedPercent = completionStartPercent.Value
                    + ((100d - completionStartPercent.Value) * easedFraction);

                if (fraction >= 1d)
                {
                    ImportProgressPercent = 100d;
                    return;
                }
            }
            else
            {
                if (displayedPercent < targetPercent - 0.05d)
                {
                    var smoothingFactor = 1d - Math.Exp(-frameSeconds / 0.45d);
                    displayedPercent += (targetPercent - displayedPercent) * smoothingFactor;
                }
                else
                {
                    var remaining = ImportProgressMaximumBeforeCompletion - displayedPercent;
                    var continuousRate = Math.Clamp(remaining * 0.08d, 0.35d, 1.6d);
                    displayedPercent += continuousRate * frameSeconds;
                }

                displayedPercent = Math.Min(displayedPercent, ImportProgressMaximumBeforeCompletion);
            }

            ImportProgressPercent = Math.Clamp(displayedPercent, 0d, 100d);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ImportProgressTarget : IProgress<SourceImportProgress>
    {
        private readonly object _sync = new();
        private double _percent;

        public double Percent
        {
            get
            {
                lock (_sync)
                {
                    return _percent;
                }
            }
        }

        public void Report(SourceImportProgress value)
        {
            lock (_sync)
            {
                _percent = Math.Max(_percent, value.Percent);
            }
        }
    }

    private async Task DeleteSelectedSourceAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        CancelAllChannelsEpgSync();
        var sourceId = SelectedSource.Id;
        IsLoading = true;

        try
        {
            var deleted = await _sourceImport.DeleteAsync(sourceId);
            if (!deleted)
            {
            NotificationMessage = L("SourceNotDeleted");
                return;
            }

            await RemoveSavedSourceStateAsync(sourceId);
        NotificationMessage = L("SourceDeleted");
            await LoadSourcesAsync(null);
            await PersistStateAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool _isRefreshingSource;

    private async Task RefreshSelectedSourceAsync()
    {
        if (SelectedSource is null || _isRefreshingSource || _isImporting)
        {
            return;
        }

        _isRefreshingSource = true;
        RefreshSelectedSourceCommand.NotifyCanExecuteChanged();

        var sourceId = SelectedSource.Id;
        IsStartupLoading = true;
        StartupLoadingMessage = L("RefreshingPlaylist");

        try
        {
            CancelAllChannelsEpgSync();
            var result = await _sourceImport.RefreshAsync(sourceId);
            if (!result.Success || result.Source is null)
            {
                NotificationMessage = result.Message;
                return;
            }

            await LoadSourcesAsync(sourceId);
            if (SelectedSource is not null)
            {
                await LoadCategoriesAsync(SelectedSource);
                StartAllChannelsEpgSync(sourceId);
            }

            NotificationMessage = L("PlaylistRefreshed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh playlist {SourceId}", sourceId);
            NotificationMessage = L("FailedRefreshPlaylist");
        }
        finally
        {
            _isRefreshingSource = false;
            RefreshSelectedSourceCommand.NotifyCanExecuteChanged();
            IsStartupLoading = false;
        }
    }

    private SourceImportRequest? BuildImportRequest()
    {
        var displayName = string.IsNullOrWhiteSpace(PlaylistDisplayName)
            ? null
            : PlaylistDisplayName.Trim();

        return ActiveImportMode switch
        {
            SourceImportMode.XtreamCodes
                => new SourceImportRequest(
                    SourceImportMode.XtreamCodes,
                    XtreamServerUrl.Trim(),
                    XtreamUsername.Trim(),
                    XtreamPassword,
                    displayName),

            SourceImportMode.M3uUrl
                => new SourceImportRequest(SourceImportMode.M3uUrl, PlaylistInput.Trim(), DisplayName: displayName),

            SourceImportMode.M3uFile
                => new SourceImportRequest(SourceImportMode.M3uFile, PlaylistInput.Trim(), DisplayName: displayName),

            SourceImportMode.M3u8Link
                => new SourceImportRequest(SourceImportMode.M3u8Link, PlaylistInput.Trim(), DisplayName: displayName),

            _ => null,
        };
    }

    private SourceUpdateRequest? BuildUpdateRequest()
    {
        if (!_editingSourceId.HasValue)
        {
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(PlaylistDisplayName)
            ? null
            : PlaylistDisplayName.Trim();

        return ActiveImportMode switch
        {
            SourceImportMode.XtreamCodes
                => new SourceUpdateRequest(
                    _editingSourceId.Value,
                    displayName,
                    string.IsNullOrWhiteSpace(XtreamServerUrl) ? null : XtreamServerUrl.Trim(),
                    string.IsNullOrWhiteSpace(XtreamUsername) ? null : XtreamUsername.Trim(),
                    string.IsNullOrWhiteSpace(XtreamPassword) ? null : XtreamPassword),

            SourceImportMode.M3uUrl or SourceImportMode.M3uFile or SourceImportMode.M3u8Link
                => new SourceUpdateRequest(
                    _editingSourceId.Value,
                    displayName,
                    string.IsNullOrWhiteSpace(PlaylistInput) ? null : PlaylistInput.Trim()),

            _ => null,
        };
    }

    private bool IsPlaylistConnectionChanging(SourceUpdateRequest request)
    {
        if (SelectedSource?.Id != request.SourceId)
        {
            return true;
        }

        return (!string.IsNullOrWhiteSpace(request.PrimaryInput)
                && !string.Equals(request.PrimaryInput.Trim(), SelectedSource.Endpoint, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrWhiteSpace(request.Username)
            || !string.IsNullOrWhiteSpace(request.Password);
    }

    private void ClearImportInputs()
    {
        XtreamServerUrl = string.Empty;
        XtreamUsername = string.Empty;
        XtreamPassword = string.Empty;
        PlaylistInput = string.Empty;
        PlaylistDisplayName = string.Empty;
    }

    private static bool LooksLikeXtreamPlaylistUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var playlistUri))
        {
            return false;
        }

        return playlistUri.Query.Contains("username=", StringComparison.OrdinalIgnoreCase)
            && playlistUri.Query.Contains("password=", StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadSourcesAsync(Guid? preferredSourceId)
    {
        var sources = await _catalog.GetSourcesAsync();
        ReplaceCollection(Sources, sources.Select(SourceItemViewModel.FromModel));

        if (Sources.Count == 0)
        {
            SelectedSource = null;
            SelectedCategory = null;
            SelectedChannel = null;
            _liveLoadedSourceId = null;
            ReplaceCollection(VisibleCategories, Array.Empty<CategoryItemViewModel>());
            ReplaceCollection(VisibleChannels, Array.Empty<ChannelItemViewModel>());
            ReplaceCollection(RecentChannels, Array.Empty<ChannelItemViewModel>());
            _recentChannelHistory.Clear();
            _recentChannelsLoadedSourceId = null;
            RefreshRecentChannelPresentation();
            ClearFavoriteChannelCache();
            ClearMovieState(L("NoPlaylistMovies"));
            ClearSeriesState(L("NoPlaylistSeries"));
            NotificationMessage = L("NoPlaylistsAvailable");
            IsStartupLoading = false;
            return;
        }

        SelectedSource = Sources.FirstOrDefault(source => source.Id == preferredSourceId) ?? Sources[0];
    }

    private async Task LoadSelectedSourceContentAsync(SourceItemViewModel source)
    {
        if (IsLiveTvSection)
        {
            await LoadCategoriesAsync(source);
            return;
        }

        if (IsMoviesSection)
        {
            await EnsureMoviesLoadedAsync(source);
            return;
        }

        if (IsSeriesSection)
        {
            await EnsureSeriesLoadedAsync(source);
            return;
        }

        await LoadEventsAsync(loadPlaylistChannels: SelectedSportsEvent is not null);
    }

    private async Task LoadCategoriesAsync(SourceItemViewModel source)
    {
        _loadCategoriesCts?.Cancel();
        _loadCategoriesCts?.Dispose();
        _loadCategoriesCts = new CancellationTokenSource();
        var loadCts = _loadCategoriesCts;
        var cancellationToken = loadCts.Token;

        IsLoading = true;

        try
        {
            var categories = await _catalog.GetCategoriesAsync(source.Id, cancellationToken);
            if (cancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
            {
                return;
            }

            _liveLoadedSourceId = source.Id;

            lock (_syncRoot)
            {
                _allCategories.Clear();
                _allCategories.AddRange(categories
                    .OrderBy(category => category.SortOrder)
                    .Select(category => new CategoryItemViewModel(category.Id, category.Name)));
            }

            ApplyCategoryFilter(CategorySearchText);
            NotifyLiveTvFavoritesStateChanged();

            if (VisibleCategories.Count == 0)
            {
                SelectedCategory = null;
                ReplaceCollection(VisibleChannels, Array.Empty<ChannelItemViewModel>());
                ClearFavoriteChannelCache();
                CancelVisibleEpgRefresh();
                CurrentChannelTitle = L("NoChannelsAvailable");
                IsStartupLoading = false;
                return;
            }

            var restoreCategoryId = source.Id == _restoredSourceId ? _restoredCategoryId : null;
            SelectedCategory = VisibleCategories.FirstOrDefault(category => category.Id == restoreCategoryId) ?? VisibleCategories[0];
            StartAllChannelsEpgSync(source.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Live TV category loading canceled for source {SourceId}", source.Id);
        }
        finally
        {
            if (ReferenceEquals(_loadCategoriesCts, loadCts))
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadChannelsAsync(SourceItemViewModel source, CategoryItemViewModel category)
    {
        _loadChannelsCts?.Cancel();
        _loadChannelsCts?.Dispose();
        CancelVisibleEpgRefresh();
        CancelSelectedEpgRefresh();
        _loadChannelsCts = new CancellationTokenSource();

        IsLoading = true;

        try
        {
            var loadToken = _loadChannelsCts.Token;
            var channels = await _catalog.GetChannelsAsync(source.Id, category.Id, loadToken);
            if (loadToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
            {
                return;
            }

            var favorites = GetFavoriteChannelIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var channelItems = await Task.Run(() =>
            {
                loadToken.ThrowIfCancellationRequested();
                return channels
                    .Select(channel => ChannelItemViewModel.FromModel(channel, favorites.Contains(channel.Id)))
                    .ToList();
            }, loadToken);

            if (loadToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
            {
                return;
            }

            lock (_syncRoot)
            {
                _allChannels.Clear();
                _allChannels.AddRange(channelItems);
            }

            RefreshEventItems();
            await ApplyChannelFilterAsync(ChannelSearchText, loadToken);
            await RestoreRecentChannelsAsync(source, loadToken);

            if (VisibleChannels.Count == 0)
            {
                SelectedChannel = null;
                CurrentChannelTitle = L("NoChannelsInCategory");
                IsStartupLoading = false;
                return;
            }

            var restoreChannelId = source.Id == _restoredSourceId ? _restoredChannelId : null;
            SelectedChannel = VisibleChannels.FirstOrDefault(channel => channel.Id == restoreChannelId) ?? VisibleChannels[0];
            IsStartupLoading = false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Channel loading canceled for category {CategoryId}", category.Id);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RefreshQuickQualityVariants(
        ChannelItemViewModel? selectedChannel,
        IReadOnlyList<ChannelItemViewModel>? knownVariants = null)
    {
        if (selectedChannel is null)
        {
            ReplaceCollection(QuickQualityVariants, Array.Empty<QuickQualityVariantViewModel>());
            NotifyQuickQualityStateChanged();
            return;
        }

        var selectedIdentity = StreamVariantIdentityNormalizer.Normalize(selectedChannel.Name);
        List<ChannelItemViewModel> playlistChannels;
        if (knownVariants is not null)
        {
            playlistChannels = [.. knownVariants];
        }
        else
        {
            lock (_syncRoot)
            {
                playlistChannels = SelectedSource is not null
                                   && _quickQualityVariantsCacheSourceId == SelectedSource.Id
                                   && _quickQualityVariantsByIdentity.TryGetValue(selectedIdentity.Key, out var cachedVariants)
                    ? [.. cachedVariants]
                    : [.. _allChannels];
            }
        }

        if (playlistChannels.All(channel =>
                !string.Equals(channel.Id, selectedChannel.Id, StringComparison.OrdinalIgnoreCase)))
        {
            playlistChannels.Add(selectedChannel);
        }

        var variants = playlistChannels
            .DistinctBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase)
            .Select(channel => new
            {
                Channel = channel,
                Identity = StreamVariantIdentityNormalizer.Normalize(channel.Name),
            })
            .Where(candidate =>
                string.Equals(candidate.Channel.Id, selectedChannel.Id, StringComparison.OrdinalIgnoreCase)
                || StreamVariantIdentityNormalizer.CanGroup(selectedIdentity, candidate.Identity))
            .Select(candidate => new QuickQualityVariantViewModel(
                candidate.Channel,
                candidate.Identity,
                string.Equals(candidate.Channel.Id, selectedChannel.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(variant => variant.QualitySortRank)
            .ThenBy(variant => variant.QualityLabel, StringComparer.Ordinal)
            .ThenBy(variant => variant.DisplayName, StringComparer.Ordinal)
            .ThenBy(variant => variant.Id, StringComparer.Ordinal)
            .ToArray();

        ReplaceCollection(QuickQualityVariants, variants);
        NotifyQuickQualityStateChanged();
    }

    private async Task EnsureQuickQualityVariantsLoadedAsync(
        SourceItemViewModel source,
        ChannelItemViewModel selectedChannel)
    {
        var selectedIdentity = StreamVariantIdentityNormalizer.Normalize(selectedChannel.Name);
        if (!selectedIdentity.HasExplicitLocaleQualifier || selectedIdentity.Key.Length == 0)
        {
            return;
        }

        IReadOnlyList<ChannelItemViewModel>? cachedVariants = null;
        lock (_syncRoot)
        {
            if (_quickQualityVariantsCacheSourceId == source.Id)
            {
                _quickQualityVariantsByIdentity.TryGetValue(selectedIdentity.Key, out cachedVariants);
            }
        }

        if (cachedVariants is not null)
        {
            RefreshQuickQualityVariants(selectedChannel, cachedVariants);
            return;
        }

        _quickQualityVariantsCts?.Cancel();
        _quickQualityVariantsCts?.Dispose();
        _quickQualityVariantsCts = new CancellationTokenSource();
        var cancellationToken = _quickQualityVariantsCts.Token;

        try
        {
            var channelModels = await _catalog.GetChannelVariantsAsync(
                source.Id,
                selectedChannel.Name,
                cancellationToken);
            var favorites = GetFavoriteChannelIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ChannelItemViewModel> loadedChannelsById;
            lock (_syncRoot)
            {
                loadedChannelsById = _allChannels
                    .Concat(_favoriteChannels)
                    .DistinctBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(channel => channel.Id, StringComparer.OrdinalIgnoreCase);
            }

            var variants = await Task.Run(() => channelModels
                .Select(channel => loadedChannelsById.TryGetValue(channel.Id, out var loadedChannel)
                    ? loadedChannel
                    : ChannelItemViewModel.FromModel(channel, favorites.Contains(channel.Id)))
                .ToArray(), cancellationToken);

            if (cancellationToken.IsCancellationRequested
                || SelectedSource?.Id != source.Id
                || SelectedChannel is null
                || !string.Equals(
                    StreamVariantIdentityNormalizer.Normalize(SelectedChannel.Name).Key,
                    selectedIdentity.Key,
                    StringComparison.Ordinal))
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_quickQualityVariantsCacheSourceId != source.Id)
                {
                    _quickQualityVariantsByIdentity.Clear();
                    _quickQualityVariantsCacheSourceId = source.Id;
                }

                _quickQualityVariantsByIdentity[selectedIdentity.Key] = variants;
            }

            RefreshQuickQualityVariants(SelectedChannel, variants);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Quick Quality lookup canceled for source {SourceId}", source.Id);
        }
        catch (Exception exception)
        {
            // Variant discovery is optional presentation data. Playback must remain untouched
            // and the already selected exact stream stays as the sole safe option.
            _logger.LogWarning(exception, "Quick Quality lookup failed for source {SourceId}", source.Id);
        }
    }

    private async Task LoadEventsAsync(
        bool forceRefresh = false,
        bool suppressIndicators = false,
        bool loadPlaylistChannels = true)
    {
        if (_eventsLoaded && !forceRefresh)
        {
            RefreshEventItems();
            if (loadPlaylistChannels && SelectedSource is not null)
            {
                await EnsureEventPlaylistChannelsLoadedAsync(SelectedSource, CancellationToken.None);
            }

            return;
        }

        _eventsCts?.Cancel();
        _eventsCts?.Dispose();
        _eventsCts = new CancellationTokenSource();
        var loadCts = _eventsCts;
        var cancellationToken = loadCts.Token;

        var showFullLoader = !suppressIndicators && TodayEvents.Count == 0;
        IsEventsLoading = showFullLoader;
        IsEventsRefreshing = !suppressIndicators && !showFullLoader;
        EventsErrorMessage = string.Empty;

        try
        {
            if (!_eventsLoaded && !forceRefresh)
            {
                var cached = await _sportsEvents.LoadCachedAsync(cancellationToken);
                if (cached is not null && !cancellationToken.IsCancellationRequested)
                {
                    ApplySportsEvents(cached.Events);
                    _eventsLoaded = true;
                    IsEventsLoading = false;
                    IsEventsRefreshing = true;
                }
            }

            var feed = await _sportsEvents.RefreshAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ApplySportsEvents(feed.Events);
            _eventsLoaded = true;
            if (loadPlaylistChannels && SelectedSource is not null)
            {
                await EnsureEventPlaylistChannelsLoadedAsync(SelectedSource, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Sports events loading canceled");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Sports events could not be loaded");
            if (TodayEvents.Count == 0)
            {
                EventsErrorMessage = L("EventsUnavailable");
            }
            else
            {
                NotificationMessage = L("EventsRefreshFailed");
            }
        }
        finally
        {
            if (ReferenceEquals(_eventsCts, loadCts))
            {
                IsEventsLoading = false;
                IsEventsRefreshing = false;
            }
        }
    }

    private void ApplySportsEvents(IReadOnlyList<SportsEventModel> events)
    {
        var activeEvents = events
            .Where(sportsEvent => sportsEvent.Status is SportsEventStatus.Scheduled or SportsEventStatus.Confirmed)
            .ToList();

        lock (_syncRoot)
        {
            _eventModels = activeEvents;
        }

        RefreshEventItems();
    }

    private void RefreshEventItems()
    {
        List<SportsEventModel> events;
        var selectedSport = SelectedEventSportCategory?.Id;
        var selectedEventId = SelectedSportsEvent?.Id;
        var timeFilter = SelectedEventTimeFilter?.Id ?? "all";
        var sortMode = SelectedEventSortMode?.Id ?? "popularity";

        lock (_syncRoot)
        {
            IEnumerable<SportsEventModel> query = _eventModels;

            if (!string.IsNullOrWhiteSpace(selectedSport))
            {
                query = query.Where(sportsEvent => string.Equals(sportsEvent.Sport, selectedSport, StringComparison.OrdinalIgnoreCase));
            }

            query = timeFilter switch
            {
                "today" => query.Where(IsEventToday),
                "tomorrow" => query.Where(IsEventTomorrow),
                "live" => query.Where(IsEventLiveOrSoon),
                _ => query.Where(IsTodayOrTomorrow)
            };

            events = sortMode switch
            {
                "time" => query
                    .OrderBy(e => e.StartUtc.ToLocalTime().Date)
                    .ThenBy(e => e.StartUtc)
                    .ThenByDescending(EventPopularityRanker.Score)
                    .ToList(),
                _ => query
                    .OrderBy(e => e.StartUtc.ToLocalTime().Date)
                    .ThenByDescending(EventPopularityRanker.Score)
                    .ThenBy(e => e.StartUtc)
                    .ToList()
            };
        }

        var items = events
            .Select(sportsEvent => new SportsEventItemViewModel(
                sportsEvent,
                FormatEventTime(sportsEvent.StartUtc),
                SportLabel(sportsEvent.Sport),
                Array.Empty<EventChannelOptionViewModel>(),
                false))
            .ToArray();

        ReplaceCollection(TodayEvents, items);
        if (SelectedSportsEvent is not null)
        {
            SelectedSportsEvent = TodayEvents.FirstOrDefault(item =>
                string.Equals(item.Id, SelectedSportsEvent.Id, StringComparison.OrdinalIgnoreCase));
            if (SelectedSportsEvent is not null)
            {
                SelectedSportsEvent.IsChannelOptionsVisible = true;
                RefreshSelectedEventChannelOptions();
            }
        }

        NotifyEventsStateChanged();
    }

    private void RefreshSelectedEventChannelOptions()
    {
        var selectedEvent = SelectedSportsEvent;
        if (selectedEvent is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _selectedEventChannelMatchVersion);
        List<ChannelItemViewModel> channels;
        var selectedSource = SelectedSource;
        if (selectedSource is not null && _eventsMatchedSourceId != selectedSource.Id)
        {
            _ = ExecuteAndReportAsync(() => EnsureEventPlaylistChannelsLoadedAsync(selectedSource, CancellationToken.None));
            return;
        }

        lock (_syncRoot)
        {
            channels = selectedSource is not null
                ? [.. _eventPlaylistChannels]
                : [.. _allChannels];
        }

        if (channels.Count == 0)
        {
            selectedEvent.UpdateChannelOptions(Array.Empty<EventChannelOptionViewModel>());
            selectedEvent.IsChannelOptionsVisible = true;
            _isEventChannelPresentationLoading = false;
            NotifyEventsStateChanged();
            return;
        }

        _ = ExecuteAndReportAsync(async () =>
        {
            var eventModel = selectedEvent.Model;
            var channelOptions = await Task.Run(() =>
            {
                var matches = EventChannelMatcher.MatchAll([eventModel], channels);
                return matches.TryGetValue(selectedEvent.Id, out var options)
                    ? options
                    : Array.Empty<EventChannelOptionViewModel>();
            }).ConfigureAwait(false);

            if (version != Volatile.Read(ref _selectedEventChannelMatchVersion)
                || !ReferenceEquals(SelectedSportsEvent, selectedEvent))
            {
                return;
            }

            _uiContext.Post(_ =>
            {
                if (version != Volatile.Read(ref _selectedEventChannelMatchVersion)
                    || !ReferenceEquals(SelectedSportsEvent, selectedEvent))
                {
                    return;
                }

                selectedEvent.UpdateChannelOptions(channelOptions);
                selectedEvent.IsChannelOptionsVisible = true;
                _isEventChannelPresentationLoading = false;
                NotifyEventsStateChanged();
            }, null);
        });
    }

    private async Task EnsureEventPlaylistChannelsLoadedAsync(SourceItemViewModel source, CancellationToken cancellationToken)
    {
        await _playlistChannelsLoadGate.WaitAsync(cancellationToken);
        try
        {
            CategoryItemViewModel[] cachedCategories;
            lock (_syncRoot)
            {
                if (_eventsMatchedSourceId == source.Id)
                {
                    return;
                }

                _eventsMatchingSourceId = source.Id;
                cachedCategories = _allCategories
                    .Select(category => new CategoryItemViewModel(category.Id, category.Name))
                    .ToArray();
            }

            var categories = cachedCategories.Length > 0
                ? cachedCategories
                : (await _catalog.GetCategoriesAsync(source.Id, cancellationToken))
                    .OrderBy(category => category.SortOrder)
                    .Select(category => new CategoryItemViewModel(category.Id, category.Name))
                    .ToArray();
            var favorites = GetFavoriteChannelIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var channels = await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawChannels = new List<ChannelModel>();
                foreach (var category in categories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var categoryChannels = await _catalog.GetChannelsAsync(source.Id, category.Id, cancellationToken)
                        .ConfigureAwait(false);
                    rawChannels.AddRange(categoryChannels);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return rawChannels
                    .DistinctBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(channel => ChannelItemViewModel.FromModel(channel, favorites.Contains(channel.Id)))
                    .ToList();
            }, cancellationToken);

            if (SelectedSource?.Id != source.Id || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            lock (_syncRoot)
            {
                _eventPlaylistChannels.Clear();
                _eventPlaylistChannels.AddRange(channels);
                _eventsMatchedSourceId = source.Id;
            }

            RefreshSelectedEventChannelOptions();
        }
        finally
        {
            if (_eventsMatchingSourceId == source.Id)
            {
                _eventsMatchingSourceId = null;
            }

            _playlistChannelsLoadGate.Release();
        }
    }

    private void InitializeEventSportCategories()
    {
        var categories = CreateEventSportCategories();
        ReplaceCollection(EventSportCategories, categories);
        SelectedEventSportCategory = EventSportCategories.FirstOrDefault(category => category.Id == "football")
            ?? EventSportCategories.FirstOrDefault();
        UpdateEventCategorySelection();
    }

    private void RefreshEventSportCategoryLabels()
    {
        var labels = CreateEventSportCategories().ToDictionary(category => category.Id, category => category.Label, StringComparer.OrdinalIgnoreCase);
        foreach (var category in EventSportCategories)
        {
            if (labels.TryGetValue(category.Id, out var label))
            {
                category.UpdateLabel(label);
            }
        }
    }

    private IReadOnlyList<EventSportCategoryViewModel> CreateEventSportCategories()
        =>
        [
            new("football", L("SportFootball")),
            new("basketball", L("SportBasketball")),
            new("tennis", L("SportTennis")),
            new("formula1", L("SportFormulaOne")),
            new("cricket", L("SportCricket")),
            new("rugby", L("SportRugby")),
        ];

    private void SelectEventSportCategory(EventSportCategoryViewModel? category)
    {
        if (category is null || ReferenceEquals(SelectedEventSportCategory, category))
        {
            return;
        }

        SelectedEventSportCategory = category;
    }

    private void SelectEventsSection()
    {
        if (ActiveSection != ShellSection.Events)
        {
            _eventsReturnSection = ActiveSection;
        }

        ActiveSection = ShellSection.Events;
    }

    private void ReturnFromEvents()
    {
        if (ActiveSection != ShellSection.Events)
        {
            return;
        }

        if (SelectedSportsEvent is not null)
        {
            SelectedSportsEvent = null;
            return;
        }

        SelectedSportsEvent = null;
        ActiveSection = _eventsReturnSection is ShellSection.Movies or ShellSection.Series or ShellSection.LiveTv
            ? _eventsReturnSection
            : ShellSection.LiveTv;
    }

    private void UpdateEventCategorySelection()
    {
        foreach (var category in EventSportCategories)
        {
            category.IsSelected = ReferenceEquals(category, SelectedEventSportCategory);
        }
    }

    private void InitializeEventFilters()
    {
        var sortModes = CreateEventSortModes();
        ReplaceCollection(EventSortModes, sortModes);
        SelectedEventSortMode = EventSortModes.FirstOrDefault(mode => mode.Id == "popularity")
            ?? EventSortModes.FirstOrDefault();

        var timeFilters = CreateEventTimeFilters();
        ReplaceCollection(EventTimeFilters, timeFilters);
        SelectedEventTimeFilter = EventTimeFilters.FirstOrDefault(filter => filter.Id == "today")
            ?? EventTimeFilters.FirstOrDefault();

        UpdateEventFilterSelection();
    }

    private IReadOnlyList<EventFilterOptionViewModel> CreateEventSortModes()
        =>
        [
            new("popularity", L("FilterPopularity")),
            new("time", L("FilterTime")),
        ];

    private IReadOnlyList<EventFilterOptionViewModel> CreateEventTimeFilters()
        =>
        [
            new("today", L("Today")),
            new("tomorrow", L("Tomorrow")),
            new("live", L("FilterLive")),
            new("all", L("FilterAll")),
        ];

    private void SelectEventSortMode(EventFilterOptionViewModel? mode)
    {
        if (mode is null || ReferenceEquals(SelectedEventSortMode, mode))
        {
            return;
        }

        SelectedEventSortMode = mode;
    }

    private void SelectEventTimeFilter(EventFilterOptionViewModel? filter)
    {
        if (filter is null || ReferenceEquals(SelectedEventTimeFilter, filter))
        {
            return;
        }

        SelectedEventTimeFilter = filter;
    }

    private void UpdateEventFilterSelection()
    {
        foreach (var mode in EventSortModes)
        {
            mode.IsSelected = ReferenceEquals(mode, SelectedEventSortMode);
        }

        foreach (var filter in EventTimeFilters)
        {
            filter.IsSelected = ReferenceEquals(filter, SelectedEventTimeFilter);
        }
    }

    private void RefreshEventFilterLabels()
    {
        var sortLabels = CreateEventSortModes().ToDictionary(m => m.Id, m => m.Label, StringComparer.OrdinalIgnoreCase);
        foreach (var mode in EventSortModes)
        {
            if (sortLabels.TryGetValue(mode.Id, out var label))
            {
                mode.UpdateLabel(label);
            }
        }

        var timeLabels = CreateEventTimeFilters().ToDictionary(f => f.Id, f => f.Label, StringComparer.OrdinalIgnoreCase);
        foreach (var filter in EventTimeFilters)
        {
            if (timeLabels.TryGetValue(filter.Id, out var label))
            {
                filter.UpdateLabel(label);
            }
        }
    }

    private void SelectEvent(SportsEventItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _isEventChannelPresentationLoading = true;
        SelectedSportsEvent = item;
        NotifyEventsStateChanged();

        if (SelectedSource is not null && _eventsMatchedSourceId != SelectedSource.Id)
        {
            item.IsChannelOptionsVisible = false;
            _ = ExecuteAndReportAsync(() => EnsureEventPlaylistChannelsLoadedAsync(SelectedSource, CancellationToken.None));
            return;
        }

        item.IsChannelOptionsVisible = true;
        RefreshSelectedEventChannelOptions();
    }

    private async Task PlayEventChannelAsync(EventChannelOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }

        SelectedChannel = option.Channel;
        _logger.LogInformation(
            "Event channel play invoked for broadcaster {BroadcasterName} on channel {ChannelId} - {ChannelName}",
            option.BroadcasterName,
            option.Channel.Id,
            option.Channel.Name);
        var (requestVersion, cancellationToken) = StartLivePlaybackRequest();
        await PlayChannelInternalAsync(option.Channel, requestVersion, cancellationToken);
    }

    private async Task EnsureMoviesLoadedAsync(SourceItemViewModel source)
    {
        if (_moviesLoadedSourceId == source.Id || _moviesLoadingSourceId == source.Id)
        {
            return;
        }

        await LoadMovieCatalogAsync(source);
    }

    private async Task LoadMovieCatalogAsync(SourceItemViewModel source)
    {
        _loadMoviesCts?.Cancel();
        _loadMoviesCts?.Dispose();
        CancelMovieDetailsLoad();
        _loadMoviesCts = new CancellationTokenSource();
        var loadCts = _loadMoviesCts;
        var cancellationToken = loadCts.Token;

        _moviesLoadingSourceId = source.Id;
        _isLoadingMovieCatalog = true;
        IsMovieCatalogLoading = true;
        MovieErrorMessage = string.Empty;
        MovieDetailsErrorMessage = string.Empty;
        SelectedMovie = null;
        SelectedMovieDetails = null;
        ReplaceCollection(VisibleMovies, Array.Empty<MovieItemViewModel>());
        NotifyMovieStateChanged();

        try
        {
            if (source.Kind != IptvPlayer.Contracts.Models.SourceKind.XtreamCodes)
            {
                ReplaceCollection(MovieCategories, Array.Empty<CategoryItemViewModel>());
                ClearMovieCache();
        MovieEmptyStateMessage = L("MoviesAvailableWhenIncluded");
                _moviesLoadedSourceId = source.Id;
                return;
            }

            var categories = await _catalog.GetMovieCategoriesAsync(source.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectedSource?.Id != source.Id)
            {
                return;
            }

            var categoryItems = categories
                .OrderBy(category => category.SortOrder)
                .Select(category => new CategoryItemViewModel(category.Id, category.Name))
                .ToList();
            if (categoryItems.Count == 0)
            {
            categoryItems.Add(new CategoryItemViewModel(AllMoviesCategoryId, L("AllMovies")));
            }

            ReplaceCollection(MovieCategories, categoryItems);
            SelectedMovieCategory = MovieCategories.FirstOrDefault();

            await LoadMoviesForCategoryAsync(source, SelectedMovieCategory, cancellationToken);
            _moviesLoadedSourceId = source.Id;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Movie catalog loading canceled for source {SourceId}", source.Id);
            if (!cancellationToken.IsCancellationRequested)
            {
            MovieErrorMessage = L("MoviesLoadTimeout");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load movies for source {SourceId}", source.Id);
            ClearMovieCache();
            MovieErrorMessage = L("MoviesLoadFailed");
        }
        finally
        {
            if (ReferenceEquals(_loadMoviesCts, loadCts))
            {
                _moviesLoadingSourceId = null;
                _isLoadingMovieCatalog = false;
                IsMovieCatalogLoading = false;
                NotifyMovieStateChanged();
            }

            IsStartupLoading = false;
        }
    }

    private async Task ReloadMoviesForCategoryAsync(SourceItemViewModel source, CategoryItemViewModel? category)
    {
        _loadMoviesCts?.Cancel();
        _loadMoviesCts?.Dispose();
        CancelMovieDetailsLoad();
        _loadMoviesCts = new CancellationTokenSource();
        var cancellationToken = _loadMoviesCts.Token;

        IsMovieCatalogLoading = true;
        MovieErrorMessage = string.Empty;
        MovieDetailsErrorMessage = string.Empty;
        SelectedMovie = null;
        SelectedMovieDetails = null;

        try
        {
            await LoadMoviesForCategoryAsync(source, category, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Movie loading canceled for source {SourceId}", source.Id);
            if (!cancellationToken.IsCancellationRequested)
            {
            MovieErrorMessage = L("MoviesLoadTimeout");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load movies for source {SourceId}", source.Id);
            ClearMovieCache();
            MovieErrorMessage = L("MoviesCategoryLoadFailed");
        }
        finally
        {
            IsMovieCatalogLoading = false;
            NotifyMovieStateChanged();
        }
    }

    private async Task LoadMovieWatchlistAsync(SourceItemViewModel source)
    {
        _loadMoviesCts?.Cancel();
        _loadMoviesCts?.Dispose();
        CancelMovieDetailsLoad();
        _loadMoviesCts = new CancellationTokenSource();
        var cancellationToken = _loadMoviesCts.Token;

        MovieErrorMessage = string.Empty;
        MovieDetailsErrorMessage = string.Empty;
        SelectedMovie = null;
        SelectedMovieDetails = null;

        try
        {
            if (source.Kind != IptvPlayer.Contracts.Models.SourceKind.XtreamCodes)
            {
                ClearMovieCache();
                ReplaceCollection(VisibleMovies, Array.Empty<MovieItemViewModel>());
                MovieEmptyStateMessage = L("NoSavedMoviesOrSeries");
                return;
            }

            var savedItems = GetMovieWatchlistItems(source.Id).ToArray();
            if (savedItems.Length == 0)
            {
                var legacyIds = GetMovieWatchlistIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (legacyIds.Count > 0)
                {
                    var movies = await _catalog.GetMoviesAsync(source.Id, null, cancellationToken);
                    savedItems = movies
                        .Where(movie => legacyIds.Contains(movie.Id))
                        .Select(MovieItemViewModel.FromModel)
                        .Select(movie => movie.ToWatchlistItem())
                        .ToArray();
                }
            }

            if (savedItems.Length == 0)
            {
                ClearMovieCache();
                ReplaceCollection(VisibleMovies, Array.Empty<MovieItemViewModel>());
                MovieEmptyStateMessage = WatchlistTotalCount == 0
                    ? L("NoSavedMoviesOrSeries")
                    : L("NoSavedMovies");
                return;
            }

            var movieItems = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return savedItems
                    .Select(MovieItemViewModel.FromWatchlistItem)
                    .Select(movie =>
                    {
                        movie.IsInWatchlist = true;
                        return movie;
                    })
                    .ToList();
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
            {
                return;
            }

            lock (_syncRoot)
            {
                _allMovies.Clear();
                _allMovies.AddRange(movieItems);
            }

            await PersistOnDemandStateAsync();
            MovieEmptyStateMessage = L("MovieWatchlistEmpty");
            VisibleMovieLimit = OnDemandPageSize;
            await ApplyMovieFilterAsync(MovieSearchText, cancellationToken);
            SelectedMovie = VisibleMovies.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Movie watchlist loading canceled for source {SourceId}", source.Id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load movie watchlist for source {SourceId}", source.Id);
            ClearMovieCache();
            MovieErrorMessage = L("MovieWatchlistLoadFailed");
        }
        finally
        {
            IsMovieCatalogLoading = false;
            NotifyMovieStateChanged();
        }
    }

    private async Task LoadMoviesForCategoryAsync(
        SourceItemViewModel source,
        CategoryItemViewModel? category,
        CancellationToken cancellationToken)
    {
        var categoryId = category?.Id == AllMoviesCategoryId ? null : category?.Id;
        var movies = await _catalog.GetMoviesAsync(source.Id, categoryId, cancellationToken);
        await ApplyMovieModelsAsync(source, movies, cancellationToken);
    }

    private async Task ApplyMovieModelsAsync(
        SourceItemViewModel source,
        IReadOnlyList<IptvPlayer.Contracts.Models.MovieModel> movies,
        CancellationToken cancellationToken)
    {
        var watchlistIds = GetMovieWatchlistIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var movieItems = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return movies
                .Select(MovieItemViewModel.FromModel)
                .Select(movie =>
                {
                    movie.IsInWatchlist = watchlistIds.Contains(movie.Id);
                    return movie;
            })
            .ToList();
        }, cancellationToken);
        if (cancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
        {
            return;
        }

        lock (_syncRoot)
        {
            _allMovies.Clear();
            _allMovies.AddRange(movieItems);
        }

        MovieEmptyStateMessage = source.Kind == IptvPlayer.Contracts.Models.SourceKind.XtreamCodes
            ? L("NoMoviesReturned")
            : L("MoviesAvailableWhenIncluded");

        VisibleMovieLimit = OnDemandPageSize;
        await ApplyMovieFilterAsync(MovieSearchText, cancellationToken);
        SelectedMovie = VisibleMovies.FirstOrDefault();
        BeginMovieMetadataPreload(source.Id, movieItems, cancellationToken);
    }

    private async Task LoadMovieDetailsAsync(MovieItemViewModel movie)
    {
        if (SelectedSource is null)
        {
            return;
        }

        _movieDetailsCts?.Cancel();
        _movieDetailsCts?.Dispose();
        _movieDetailsCts = new CancellationTokenSource();
        var cancellationToken = _movieDetailsCts.Token;

        IsMovieDetailsLoading = true;
        MovieDetailsErrorMessage = string.Empty;

        try
        {
            var details = await _catalog.GetMovieDetailsAsync(SelectedSource.Id, movie.Id, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || SelectedMovie is null
                || !string.Equals(SelectedMovie.Id, movie.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedMovieDetails = details is null
                ? MovieDetailsViewModel.FromMovie(movie)
                : MovieDetailsViewModel.FromModel(details);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Movie details loading canceled for movie {MovieId}", movie.Id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load movie details for {MovieId}", movie.Id);
            SelectedMovieDetails = MovieDetailsViewModel.FromMovie(movie);
            MovieDetailsErrorMessage = L("MovieDetailsLoadFailed");
        }
        finally
        {
            IsMovieDetailsLoading = false;
            NotifyMovieStateChanged();
        }
    }

    private void BeginMovieMetadataPreload(
        Guid sourceId,
        IReadOnlyList<MovieItemViewModel> movies,
        CancellationToken cancellationToken)
    {
        var selectedMovieId = SelectedMovie?.Id;
        var movieIds = movies
            .Where(movie => !string.Equals(movie.Id, selectedMovieId, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .Select(movie => movie.Id)
            .ToArray();

        if (movieIds.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);

            foreach (var movieId in movieIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await _catalog.GetMovieDetailsAsync(sourceId, movieId, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Background movie metadata preload skipped for {MovieId}", movieId);
                }
            }
        }, cancellationToken);
    }

    private async Task EnsureSeriesLoadedAsync(SourceItemViewModel source)
    {
        if (_seriesLoadedSourceId == source.Id || _seriesLoadingSourceId == source.Id)
        {
            return;
        }

        await LoadSeriesCatalogAsync(source);
    }

    private async Task LoadSeriesCatalogAsync(SourceItemViewModel source)
    {
        _loadSeriesCts?.Cancel();
        _loadSeriesCts?.Dispose();
        CancelSeriesDetailsLoad();
        _loadSeriesCts = new CancellationTokenSource();
        var loadCts = _loadSeriesCts;
        var cancellationToken = loadCts.Token;

        _seriesLoadingSourceId = source.Id;
        _isLoadingSeriesCatalog = true;
        IsSeriesCatalogLoading = true;
        SeriesErrorMessage = string.Empty;
        SeriesDetailsErrorMessage = string.Empty;
        SelectedSeries = null;
        SelectedSeriesDetails = null;
        ReplaceCollection(VisibleSeries, Array.Empty<SeriesItemViewModel>());
        NotifySeriesStateChanged();

        try
        {
            if (source.Kind != IptvPlayer.Contracts.Models.SourceKind.XtreamCodes)
            {
                ReplaceCollection(SeriesCategories, Array.Empty<CategoryItemViewModel>());
                ClearSeriesCache();
        SeriesEmptyStateMessage = L("SeriesAvailableWhenIncluded");
                _seriesLoadedSourceId = source.Id;
                return;
            }

            var categories = await _catalog.GetSeriesCategoriesAsync(source.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectedSource?.Id != source.Id)
            {
                return;
            }

            var categoryItems = categories
                .OrderBy(category => category.SortOrder)
                .Select(category => new CategoryItemViewModel(category.Id, category.Name))
                .ToList();
            if (categoryItems.Count == 0)
            {
            categoryItems.Add(new CategoryItemViewModel(AllSeriesCategoryId, L("AllSeries")));
            }

            ReplaceCollection(SeriesCategories, categoryItems);
            SelectedSeriesCategory = SeriesCategories.FirstOrDefault();

            await LoadSeriesForCategoryAsync(source, SelectedSeriesCategory, cancellationToken);
            _seriesLoadedSourceId = source.Id;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Series catalog loading canceled for source {SourceId}", source.Id);
            if (!cancellationToken.IsCancellationRequested)
            {
            SeriesErrorMessage = L("SeriesLoadTimeout");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load series for source {SourceId}", source.Id);
            ClearSeriesCache();
            SeriesErrorMessage = L("SeriesLoadFailed");
        }
        finally
        {
            if (ReferenceEquals(_loadSeriesCts, loadCts))
            {
                _seriesLoadingSourceId = null;
                _isLoadingSeriesCatalog = false;
                IsSeriesCatalogLoading = false;
                NotifySeriesStateChanged();
            }

            IsStartupLoading = false;
        }
    }

    private async Task ReloadSeriesForCategoryAsync(SourceItemViewModel source, CategoryItemViewModel? category)
    {
        _loadSeriesCts?.Cancel();
        _loadSeriesCts?.Dispose();
        CancelSeriesDetailsLoad();
        _loadSeriesCts = new CancellationTokenSource();
        var cancellationToken = _loadSeriesCts.Token;

        IsSeriesCatalogLoading = true;
        SeriesErrorMessage = string.Empty;
        SeriesDetailsErrorMessage = string.Empty;
        SelectedSeries = null;
        SelectedSeriesDetails = null;

        try
        {
            await LoadSeriesForCategoryAsync(source, category, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Series loading canceled for source {SourceId}", source.Id);
            if (!cancellationToken.IsCancellationRequested)
            {
            SeriesErrorMessage = L("SeriesLoadTimeout");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load series for source {SourceId}", source.Id);
            ClearSeriesCache();
            SeriesErrorMessage = L("SeriesCategoryLoadFailed");
        }
        finally
        {
            IsSeriesCatalogLoading = false;
            NotifySeriesStateChanged();
        }
    }

    private async Task LoadSeriesWatchlistAsync(SourceItemViewModel source)
    {
        _loadSeriesCts?.Cancel();
        _loadSeriesCts?.Dispose();
        CancelSeriesDetailsLoad();
        _loadSeriesCts = new CancellationTokenSource();
        var cancellationToken = _loadSeriesCts.Token;

        SeriesErrorMessage = string.Empty;
        SeriesDetailsErrorMessage = string.Empty;
        SelectedSeries = null;
        SelectedSeriesDetails = null;

        try
        {
            if (source.Kind != IptvPlayer.Contracts.Models.SourceKind.XtreamCodes)
            {
                ClearSeriesCache();
                ReplaceCollection(VisibleSeries, Array.Empty<SeriesItemViewModel>());
                SeriesEmptyStateMessage = L("NoSavedMoviesOrSeries");
                return;
            }

            var savedItems = GetSeriesWatchlistItems(source.Id).ToArray();
            if (savedItems.Length == 0)
            {
                var legacyIds = GetSeriesWatchlistIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (legacyIds.Count > 0)
                {
                    var series = await _catalog.GetSeriesAsync(source.Id, null, cancellationToken);
                    savedItems = series
                        .Where(item => legacyIds.Contains(item.Id))
                        .Select(SeriesItemViewModel.FromModel)
                        .Select(item => item.ToWatchlistItem())
                        .ToArray();
                }
            }

            if (savedItems.Length == 0)
            {
                ClearSeriesCache();
                ReplaceCollection(VisibleSeries, Array.Empty<SeriesItemViewModel>());
                SeriesEmptyStateMessage = WatchlistTotalCount == 0
                    ? L("NoSavedMoviesOrSeries")
                    : L("NoSavedSeries");
                return;
            }

            var seriesItems = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return savedItems
                    .Select(SeriesItemViewModel.FromWatchlistItem)
                    .Select(item =>
                    {
                        item.IsInWatchlist = true;
                        return item;
                    })
                    .ToList();
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
            {
                return;
            }

            lock (_syncRoot)
            {
                _allSeries.Clear();
                _allSeries.AddRange(seriesItems);
            }

            await PersistOnDemandStateAsync();
            SeriesEmptyStateMessage = L("SeriesWatchlistEmpty");
            VisibleSeriesLimit = OnDemandPageSize;
            await ApplySeriesFilterAsync(SeriesSearchText, cancellationToken);
            SelectedSeries = VisibleSeries.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Series watchlist loading canceled for source {SourceId}", source.Id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load series watchlist for source {SourceId}", source.Id);
            ClearSeriesCache();
            SeriesErrorMessage = L("SeriesWatchlistLoadFailed");
        }
        finally
        {
            IsSeriesCatalogLoading = false;
            NotifySeriesStateChanged();
        }
    }

    private void BeginSeriesMetadataPreload(
        Guid sourceId,
        IReadOnlyList<SeriesItemViewModel> series,
        CancellationToken cancellationToken)
    {
        var selectedSeriesId = SelectedSeries?.Id;
        var seriesIds = series
            .Where(item => !string.Equals(item.Id, selectedSeriesId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .Select(item => item.Id)
            .ToArray();

        if (seriesIds.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);

            foreach (var seriesId in seriesIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await _catalog.GetSeriesDetailsAsync(sourceId, seriesId, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(160, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Background series metadata preload skipped for {SeriesId}", seriesId);
                }
            }
        }, cancellationToken);
    }

    private async Task LoadSeriesForCategoryAsync(
        SourceItemViewModel source,
        CategoryItemViewModel? category,
        CancellationToken cancellationToken)
    {
        var categoryId = category?.Id == AllSeriesCategoryId ? null : category?.Id;
        var series = await _catalog.GetSeriesAsync(source.Id, categoryId, cancellationToken);
        await ApplySeriesModelsAsync(source, series, cancellationToken);
    }

    private async Task ApplySeriesModelsAsync(
        SourceItemViewModel source,
        IReadOnlyList<IptvPlayer.Contracts.Models.SeriesModel> series,
        CancellationToken cancellationToken)
    {
        var watchlistIds = GetSeriesWatchlistIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seriesItems = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return series
                .Select(SeriesItemViewModel.FromModel)
                .Select(item =>
                {
                    item.IsInWatchlist = watchlistIds.Contains(item.Id);
                    return item;
            })
            .ToList();
        }, cancellationToken);
        if (cancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
        {
            return;
        }

        lock (_syncRoot)
        {
            _allSeries.Clear();
            _allSeries.AddRange(seriesItems);
        }

        SeriesEmptyStateMessage = source.Kind == IptvPlayer.Contracts.Models.SourceKind.XtreamCodes
            ? L("NoSeriesReturned")
            : L("SeriesAvailableWhenIncluded");

        VisibleSeriesLimit = OnDemandPageSize;
        await ApplySeriesFilterAsync(SeriesSearchText, cancellationToken);
        SelectedSeries = VisibleSeries.FirstOrDefault();
        BeginSeriesMetadataPreload(source.Id, seriesItems, cancellationToken);
    }

    private async Task LoadSeriesDetailsAsync(SeriesItemViewModel series)
    {
        if (SelectedSource is null)
        {
            return;
        }

        _seriesDetailsCts?.Cancel();
        _seriesDetailsCts?.Dispose();
        _seriesDetailsCts = new CancellationTokenSource();
        var cancellationToken = _seriesDetailsCts.Token;

        IsSeriesDetailsLoading = true;
        SeriesDetailsErrorMessage = string.Empty;

        try
        {
            var details = await _catalog.GetSeriesDetailsAsync(SelectedSource.Id, series.Id, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || SelectedSeries is null
                || !string.Equals(SelectedSeries.Id, series.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedSeriesDetails = details is null
                ? SeriesDetailsViewModel.FromSeries(series)
                : SeriesDetailsViewModel.FromModel(details);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Series details loading canceled for series {SeriesId}", series.Id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load series details for {SeriesId}", series.Id);
            SelectedSeriesDetails = SeriesDetailsViewModel.FromSeries(series);
            SeriesDetailsErrorMessage = L("SeriesDetailsLoadFailed");
        }
        finally
        {
            IsSeriesDetailsLoading = false;
            NotifySeriesStateChanged();
        }
    }

    private void RestoreOnDemandState()
        => RefreshContinueWatchingForSelectedSource();

    private IReadOnlyCollection<string> GetMovieWatchlistIds(Guid? sourceId = null)
        => GetMovieWatchlistItems(sourceId ?? SelectedSource?.Id)
            .Select(item => item.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyCollection<string> GetSeriesWatchlistIds(Guid? sourceId = null)
        => GetSeriesWatchlistItems(sourceId ?? SelectedSource?.Id)
            .Select(item => item.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyCollection<OnDemandWatchlistItem> GetMovieWatchlistItems(Guid? sourceId = null)
        => GetSourceCollection(_onDemandState.WatchlistMoviesBySource, sourceId ?? SelectedSource?.Id);

    private IReadOnlyCollection<OnDemandWatchlistItem> GetSeriesWatchlistItems(Guid? sourceId = null)
        => GetSourceCollection(_onDemandState.WatchlistSeriesBySource, sourceId ?? SelectedSource?.Id);

    private IReadOnlyCollection<string> GetFavoriteChannelIds(Guid? sourceId = null)
        => GetSourceCollection(_sessionSnapshot.FavoriteChannelIdsBySource, sourceId ?? SelectedSource?.Id);

    private IReadOnlyCollection<string> GetRecentChannelIds(Guid? sourceId = null)
        => GetSourceCollection(_sessionSnapshot.RecentChannelIdsBySource, sourceId ?? SelectedSource?.Id);

    private IReadOnlyCollection<RecentChannelHistoryEntry> GetRecentChannelHistory(Guid? sourceId = null)
        => GetSourceCollection(_sessionSnapshot.RecentChannelHistoryBySource, sourceId ?? SelectedSource?.Id);

    private static IReadOnlyCollection<string> GetSourceCollection(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? valuesBySource,
        Guid? sourceId)
        => GetSourceCollection<string>(valuesBySource, sourceId);

    private static IReadOnlyCollection<T> GetSourceCollection<T>(
        IReadOnlyDictionary<string, IReadOnlyCollection<T>>? valuesBySource,
        Guid? sourceId)
    {
        if (!sourceId.HasValue)
        {
            return Array.Empty<T>();
        }

        var sourceKey = sourceId.Value.ToString("D");
        return valuesBySource is not null && valuesBySource.TryGetValue(sourceKey, out var values)
            ? values
            : Array.Empty<T>();
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>> UpdateSourceCollection(
        IReadOnlyDictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>>? valuesBySource,
        Guid sourceId,
        IEnumerable<OnDemandWatchlistItem> values)
    {
        var updatedValues = valuesBySource is null
            ? new Dictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>>(valuesBySource, StringComparer.OrdinalIgnoreCase);

        updatedValues[sourceId.ToString("D")] = values
            .Where(value => !string.IsNullOrWhiteSpace(value.Id) && !string.IsNullOrWhiteSpace(value.Title))
            .DistinctBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return updatedValues;
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> UpdateSourceCollection(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? valuesBySource,
        Guid sourceId,
        IEnumerable<string> values)
    {
        var updatedValues = valuesBySource is null
            ? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyCollection<string>>(valuesBySource, StringComparer.OrdinalIgnoreCase);

        updatedValues[sourceId.ToString("D")] = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return updatedValues;
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>> UpdateSourceCollection(
        IReadOnlyDictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>>? valuesBySource,
        Guid sourceId,
        IEnumerable<RecentChannelHistoryEntry> values)
    {
        var updatedValues = valuesBySource is null
            ? new Dictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>>(valuesBySource, StringComparer.OrdinalIgnoreCase);

        updatedValues[sourceId.ToString("D")] = values
            .Where(value => !string.IsNullOrWhiteSpace(value.ChannelId))
            .DistinctBy(value => value.ChannelId, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        return updatedValues;
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> RemoveSourceCollection(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? valuesBySource,
        Guid sourceId)
        => RemoveSourceCollection<string>(valuesBySource, sourceId);

    private static IReadOnlyDictionary<string, IReadOnlyCollection<T>> RemoveSourceCollection<T>(
        IReadOnlyDictionary<string, IReadOnlyCollection<T>>? valuesBySource,
        Guid sourceId)
    {
        var updatedValues = valuesBySource is null
            ? new Dictionary<string, IReadOnlyCollection<T>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyCollection<T>>(valuesBySource, StringComparer.OrdinalIgnoreCase);

        updatedValues.Remove(sourceId.ToString("D"));
        return updatedValues;
    }

    private async Task RemoveSavedSourceStateAsync(Guid sourceId)
    {
        var sourceKey = sourceId.ToString("D");
        _onDemandState = _onDemandState with
        {
            WatchlistMovieIds = Array.Empty<string>(),
            WatchlistSeriesIds = Array.Empty<string>(),
            WatchlistMovieIdsBySource = RemoveSourceCollection(_onDemandState.WatchlistMovieIdsBySource, sourceId),
            WatchlistSeriesIdsBySource = RemoveSourceCollection(_onDemandState.WatchlistSeriesIdsBySource, sourceId),
            WatchlistMoviesBySource = RemoveSourceCollection(_onDemandState.WatchlistMoviesBySource, sourceId),
            WatchlistSeriesBySource = RemoveSourceCollection(_onDemandState.WatchlistSeriesBySource, sourceId),
            ContinueWatchingMovies = _onDemandState.ContinueWatchingMovies
                .Where(entry => !string.Equals(entry.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            ContinueWatchingSeries = _onDemandState.ContinueWatchingSeries
                .Where(entry => !string.Equals(entry.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };

        _sessionSnapshot = _sessionSnapshot with
        {
            FavoriteChannelIds = Array.Empty<string>(),
            RecentChannelIds = Array.Empty<string>(),
            FavoriteChannelIdsBySource = RemoveSourceCollection(_sessionSnapshot.FavoriteChannelIdsBySource, sourceId),
            RecentChannelIdsBySource = RemoveSourceCollection(_sessionSnapshot.RecentChannelIdsBySource, sourceId),
            RecentChannelHistoryBySource = RemoveSourceCollection(_sessionSnapshot.RecentChannelHistoryBySource, sourceId),
        };

        await Task.WhenAll(
            _onDemandStateStore.SaveAsync(_onDemandState),
            _session.SaveAsync(_sessionSnapshot));
    }

    private void RefreshContinueWatchingForSelectedSource()
    {
        var sourceKey = SelectedSource?.Id.ToString("D");
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            ReplaceCollection(ContinueWatchingMovies, Array.Empty<ContinueWatchingItemViewModel>());
            ReplaceCollection(ContinueWatchingSeries, Array.Empty<ContinueWatchingItemViewModel>());
            NotifyMovieStateChanged();
            NotifySeriesStateChanged();
            return;
        }

        ReplaceCollection(
            ContinueWatchingMovies,
            _onDemandState.ContinueWatchingMovies
                .Where(entry => string.Equals(entry.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.UpdatedUtc)
                .Take(12)
                .Select(ContinueWatchingItemViewModel.FromStateEntry));

        ReplaceCollection(
            ContinueWatchingSeries,
            _onDemandState.ContinueWatchingSeries
                .Where(entry => string.Equals(entry.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.UpdatedUtc)
                .Take(12)
                .Select(ContinueWatchingItemViewModel.FromStateEntry));

        NotifyMovieStateChanged();
        NotifySeriesStateChanged();
    }

    private Task BeginContinueWatchingMovieTrackingAsync(MovieDetailsViewModel movie)
    {
        if (SelectedSource is null)
        {
            return Task.CompletedTask;
        }

        BeginContinueWatchingTracking(
            ContinueWatchingItemViewModel.FromMovie(movie, SelectedSource.Id),
            isSeries: false);

        return Task.CompletedTask;
    }

    private Task BeginContinueWatchingSeriesTrackingAsync(SeriesEpisodeViewModel episode)
    {
        if (SelectedSource is null)
        {
            return Task.CompletedTask;
        }

        BeginContinueWatchingTracking(
            ContinueWatchingItemViewModel.FromSeriesEpisode(SelectedSeriesDetails, episode, SelectedSource.Id),
            isSeries: true);

        return Task.CompletedTask;
    }

    private void BeginContinueWatchingItemTracking(ContinueWatchingItemViewModel item)
        => BeginContinueWatchingTracking(item, isSeries: !string.IsNullOrWhiteSpace(item.ParentId));

    private void BeginContinueWatchingTracking(ContinueWatchingItemViewModel item, bool isSeries)
    {
        _activeOnDemandHistoryItem = item;
        _activeOnDemandHistoryIsSeries = isSeries;
        _lastPlaybackProgressPersistenceUtc = DateTimeOffset.MinValue;
    }

    private void UpdateActiveOnDemandProgress(bool canSeek, double positionSeconds, double durationSeconds)
    {
        var activeItem = _activeOnDemandHistoryItem;
        if (!IsOnDemandPlaybackActive
            || activeItem is null
            || !canSeek
            || positionSeconds < 1d
            || durationSeconds <= 0d)
        {
            return;
        }

        var progressPercent = Math.Clamp(positionSeconds / durationSeconds * 100d, 0d, 100d);
        _activeOnDemandHistoryItem = activeItem.WithProgress(progressPercent);

        var now = DateTimeOffset.UtcNow;
        if (_lastPlaybackProgressPersistenceUtc != DateTimeOffset.MinValue
            && now - _lastPlaybackProgressPersistenceUtc < PlaybackProgressPersistenceInterval)
        {
            return;
        }

        _lastPlaybackProgressPersistenceUtc = now;
        PublishActiveOnDemandProgress();
    }

    private void PublishActiveOnDemandProgress()
    {
        var activeItem = _activeOnDemandHistoryItem;
        if (activeItem is null || !activeItem.HasProgress)
        {
            return;
        }

        if (_activeOnDemandHistoryIsSeries)
        {
            UpsertContinueWatching(ContinueWatchingSeries, activeItem);
            NotifySeriesStateChanged();
        }
        else
        {
            UpsertContinueWatching(ContinueWatchingMovies, activeItem);
            NotifyMovieStateChanged();
        }

        if (Interlocked.CompareExchange(ref _onDemandProgressPersistenceInFlight, 1, 0) == 0)
        {
            _ = PersistPlaybackProgressAsync();
        }
    }

    private async Task PersistPlaybackProgressAsync()
    {
        try
        {
            await PersistOnDemandStateAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist on-demand playback progress");
        }
        finally
        {
            Interlocked.Exchange(ref _onDemandProgressPersistenceInFlight, 0);
        }
    }

    private async Task PersistActiveOnDemandProgressAsync()
    {
        var activeItem = _activeOnDemandHistoryItem;
        if (activeItem is null || !activeItem.HasProgress)
        {
            return;
        }

        if (_activeOnDemandHistoryIsSeries)
        {
            UpsertContinueWatching(ContinueWatchingSeries, activeItem);
            NotifySeriesStateChanged();
        }
        else
        {
            UpsertContinueWatching(ContinueWatchingMovies, activeItem);
            NotifyMovieStateChanged();
        }

        await PersistOnDemandStateAsync();
    }

    private static void UpsertContinueWatching(
        ObservableCollection<ContinueWatchingItemViewModel> target,
        ContinueWatchingItemViewModel item)
    {
        var existing = target.FirstOrDefault(entry =>
            string.Equals(entry.MediaId, item.MediaId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            target.Remove(existing);
        }

        target.Insert(0, item);

        while (target.Count > 12)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private void ApplyMovieWatchlistState(MovieItemViewModel movie)
    {
        var watchlist = GetMovieWatchlistIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        movie.IsInWatchlist = watchlist.Contains(movie.Id);
    }

    private void ApplySeriesWatchlistState(SeriesItemViewModel series)
    {
        var watchlist = GetSeriesWatchlistIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        series.IsInWatchlist = watchlist.Contains(series.Id);
    }

    private void UpdateMovieWatchlistCacheState(string movieId, bool isInWatchlist)
    {
        lock (_syncRoot)
        {
            foreach (var movie in _allMovies.Concat(_playlistSearchMovies)
                         .Where(item => string.Equals(item.Id, movieId, StringComparison.OrdinalIgnoreCase)))
            {
                movie.IsInWatchlist = isInWatchlist;
            }
        }
    }

    private void UpdateSeriesWatchlistCacheState(string seriesId, bool isInWatchlist)
    {
        lock (_syncRoot)
        {
            foreach (var series in _allSeries.Concat(_playlistSearchSeries)
                         .Where(item => string.Equals(item.Id, seriesId, StringComparison.OrdinalIgnoreCase)))
            {
                series.IsInWatchlist = isInWatchlist;
            }
        }
    }

    private async Task PersistOnDemandStateAsync()
    {
        if (SelectedSource is null)
        {
            return;
        }

        var sourceId = SelectedSource.Id;
        List<MovieItemViewModel> cachedMovies;
        List<SeriesItemViewModel> cachedSeries;
        lock (_syncRoot)
        {
            cachedMovies = _allMovies
                .Concat(_playlistSearchMovies)
                .DistinctBy(movie => movie.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            cachedSeries = _allSeries
                .Concat(_playlistSearchSeries)
                .DistinctBy(series => series.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var visibleMovieIds = cachedMovies
            .Select(movie => movie.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var movieWatchlistIds = cachedMovies
            .Where(movie => movie.IsInWatchlist)
            .Select(movie => movie.Id)
            .Concat(GetMovieWatchlistIds(sourceId).Where(id => !visibleMovieIds.Contains(id)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var movieWatchlistItems = cachedMovies
            .Where(movie => movie.IsInWatchlist)
            .Select(movie => movie.ToWatchlistItem())
            .Concat(GetMovieWatchlistItems(sourceId).Where(item => !visibleMovieIds.Contains(item.Id)))
            .Where(item => movieWatchlistIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var visibleSeriesIds = cachedSeries
            .Select(series => series.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seriesWatchlistIds = cachedSeries
            .Where(series => series.IsInWatchlist)
            .Select(series => series.Id)
            .Concat(GetSeriesWatchlistIds(sourceId).Where(id => !visibleSeriesIds.Contains(id)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var seriesWatchlistItems = cachedSeries
            .Where(series => series.IsInWatchlist)
            .Select(series => series.ToWatchlistItem())
            .Concat(GetSeriesWatchlistItems(sourceId).Where(item => !visibleSeriesIds.Contains(item.Id)))
            .Where(item => seriesWatchlistIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _onDemandState = new OnDemandState(
            movieWatchlistIds,
            seriesWatchlistIds,
            MergeContinueWatching(_onDemandState.ContinueWatchingMovies, ContinueWatchingMovies),
            MergeContinueWatching(_onDemandState.ContinueWatchingSeries, ContinueWatchingSeries))
        {
            WatchlistMovieIdsBySource = UpdateSourceCollection(
                _onDemandState.WatchlistMovieIdsBySource,
                sourceId,
                movieWatchlistIds),
            WatchlistSeriesIdsBySource = UpdateSourceCollection(
                _onDemandState.WatchlistSeriesIdsBySource,
                sourceId,
                seriesWatchlistIds),
            WatchlistMoviesBySource = UpdateSourceCollection(
                _onDemandState.WatchlistMoviesBySource,
                sourceId,
                movieWatchlistItems),
            WatchlistSeriesBySource = UpdateSourceCollection(
                _onDemandState.WatchlistSeriesBySource,
                sourceId,
                seriesWatchlistItems),
        };

        await _onDemandStateStore.SaveAsync(_onDemandState);
    }

    private IReadOnlyCollection<OnDemandHistoryEntry> MergeContinueWatching(
        IReadOnlyCollection<OnDemandHistoryEntry> savedItems,
        IEnumerable<ContinueWatchingItemViewModel> visibleItems)
    {
        var sourceKey = SelectedSource?.Id.ToString("D");
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return savedItems;
        }

        var currentSourceItems = visibleItems
            .Select(item => item.ToStateEntry())
            .ToArray();

        return currentSourceItems
            .Concat(savedItems.Where(item => !string.Equals(item.SourceId, sourceKey, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private async Task RestoreRecentChannelsAsync(
        SourceItemViewModel source,
        CancellationToken cancellationToken)
    {
        _recentChannelsLoadedSourceId = null;
        ReplaceCollection(RecentChannels, Array.Empty<ChannelItemViewModel>());
        _recentChannelHistory.Clear();
        RefreshRecentChannelPresentation();

        var recentChannelIds = GetRecentChannelIds(source.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        if (recentChannelIds.Length == 0)
        {
            _recentChannelsLoadedSourceId = source.Id;
            RefreshRecentChannelPresentation();
            return;
        }

        var favoriteIds = GetFavoriteChannelIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restoredModels = await _catalog.GetFavoriteChannelsAsync(source.Id, recentChannelIds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedSource?.Id != source.Id)
        {
            return;
        }

        var byId = restoredModels
            .GroupBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => ChannelItemViewModel.FromModel(group.First(), favoriteIds.Contains(group.Key)),
                StringComparer.OrdinalIgnoreCase);

        lock (_syncRoot)
        {
            foreach (var channel in _allChannels)
            {
                if (recentChannelIds.Contains(channel.Id, StringComparer.OrdinalIgnoreCase))
                {
                    byId[channel.Id] = channel;
                }
            }
        }

        var orderedRecents = recentChannelIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Take(12)
            .ToList();

        if (orderedRecents.Count > 0)
        {
            ReplaceCollection(RecentChannels, orderedRecents);
        }

        var savedHistory = GetRecentChannelHistory(source.Id)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ChannelId))
            .GroupBy(entry => entry.ChannelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var channel in orderedRecents)
        {
            _recentChannelHistory.Add(savedHistory.TryGetValue(channel.Id, out var history)
                ? history
                : new RecentChannelHistoryEntry(
                    channel.Id,
                    null,
                    null,
                    channel.CurrentProgramProgressPercent));
        }

        _recentChannelsLoadedSourceId = source.Id;
        RefreshRecentChannelPresentation();
    }

    private async Task EnsureFavoriteChannelsCachedAsync(SourceItemViewModel source)
    {
        if (_favoriteChannelsLoadedSourceId == source.Id)
        {
            return;
        }

        _loadFavoriteChannelsCts?.Cancel();
        _loadFavoriteChannelsCts?.Dispose();
        _loadFavoriteChannelsCts = new CancellationTokenSource();
        await RefreshFavoriteChannelsCacheAsync(source, _loadFavoriteChannelsCts.Token);
    }

    private async Task RefreshFavoriteChannelsCacheAsync(SourceItemViewModel source, CancellationToken cancellationToken)
    {
        var favoriteIds = GetFavoriteChannelIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (favoriteIds.Count == 0)
        {
            SetFavoriteChannelCache(source.Id, Array.Empty<ChannelItemViewModel>());
            return;
        }

        var favoriteChannels = await _catalog.GetFavoriteChannelsAsync(source.Id, favoriteIds, cancellationToken);
        var favoriteChannelItems = await Task.Run(
            () => favoriteChannels.Select(channel => ChannelItemViewModel.FromModel(channel, true)).ToArray(),
            cancellationToken);

        if (cancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
        {
            return;
        }

        SetFavoriteChannelCache(source.Id, favoriteChannelItems);
    }

    private void SetFavoriteChannelCache(Guid sourceId, IReadOnlyList<ChannelItemViewModel> channels)
    {
        var currentFavoriteIds = GetFavoriteChannelIds(sourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_syncRoot)
        {
            var mergedFavorites = new Dictionary<string, ChannelItemViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in channels)
            {
                if (currentFavoriteIds.Contains(channel.Id))
                {
                    mergedFavorites[channel.Id] = channel;
                }
            }

            foreach (var channel in _favoriteChannels)
            {
                if (currentFavoriteIds.Contains(channel.Id))
                {
                    mergedFavorites[channel.Id] = channel;
                }
            }

            _favoriteChannels.Clear();
            _favoriteChannels.AddRange(mergedFavorites.Values.OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase));
            _favoriteChannelsLoadedSourceId = sourceId;
        }

        NotifyLiveTvFavoritesStateChanged();

        if (IsFavoritesCategorySelected)
        {
            ShowFavoriteChannelsFromCache();
        }
    }

    private void ShowFavoriteChannelsFromCache()
    {
        List<ChannelItemViewModel> favorites;
        lock (_syncRoot)
        {
            favorites = [.. _favoriteChannels];
            _allChannels.Clear();
            _allChannels.AddRange(favorites);
        }

        ApplyChannelFilter(ChannelSearchText);

        if (VisibleChannels.Count == 0)
        {
            SelectedChannel = null;
        CurrentChannelTitle = L("NoFavoriteChannels");
            return;
        }

        if (SelectedChannel is null || VisibleChannels.All(channel => channel.Id != SelectedChannel.Id))
        {
            SelectedChannel = VisibleChannels[0];
        }
    }

    private void UpdateFavoriteSessionSnapshot(ChannelItemViewModel channel)
    {
        if (SelectedSource is null)
        {
            return;
        }

        var favoriteIds = GetFavoriteChannelIds(SelectedSource.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (channel.IsFavorite)
        {
            favoriteIds.Add(channel.Id);
        }
        else
        {
            favoriteIds.Remove(channel.Id);
        }

        _sessionSnapshot = _sessionSnapshot with
        {
            FavoriteChannelIds = favoriteIds.ToArray(),
            FavoriteChannelIdsBySource = UpdateSourceCollection(
                _sessionSnapshot.FavoriteChannelIdsBySource,
                SelectedSource.Id,
                favoriteIds),
        };
    }

    private void UpdateFavoriteChannelCache(ChannelItemViewModel channel)
    {
        lock (_syncRoot)
        {
            foreach (var cachedChannel in _allChannels.Concat(_eventPlaylistChannels)
                         .Where(item => string.Equals(item.Id, channel.Id, StringComparison.OrdinalIgnoreCase)))
            {
                cachedChannel.IsFavorite = channel.IsFavorite;
            }

            _favoriteChannels.RemoveAll(item => string.Equals(item.Id, channel.Id, StringComparison.OrdinalIgnoreCase));
            if (channel.IsFavorite)
            {
                _favoriteChannels.Add(channel);
                _favoriteChannels.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        NotifyLiveTvFavoritesStateChanged();

        if (IsFavoritesCategorySelected)
        {
            ShowFavoriteChannelsFromCache();
        }
    }

    private void ClearFavoriteChannelCache()
    {
        _loadFavoriteChannelsCts?.Cancel();
        _loadFavoriteChannelsCts?.Dispose();
        _loadFavoriteChannelsCts = null;
        _favoriteChannelsLoadedSourceId = null;
        IsFavoritesCategorySelected = false;

        lock (_syncRoot)
        {
            _favoriteChannels.Clear();
        }

        NotifyLiveTvFavoritesStateChanged();
    }

    private void NotifyLiveTvFavoritesStateChanged()
    {
        OnPropertyChanged(nameof(HasLoadedLiveTvPlaylist));
        OnPropertyChanged(nameof(FavoritesCategorySummary));
        OnPropertyChanged(nameof(FavoriteChannelsCount));
        SelectFavoritesCategoryCommand.NotifyCanExecuteChanged();
    }

    private async Task PlayChannelInternalAsync(
        ChannelItemViewModel channel,
        long requestVersion,
        CancellationToken cancellationToken)
    {
        await PersistActiveOnDemandProgressAsync();
        SetOnDemandPlaybackActive(false);

        if (!IsCurrentLivePlaybackRequest(channel, requestVersion, cancellationToken))
        {
            return;
        }

        _currentPlaybackState = PlaybackState.Connecting;
        NotifyPlayerControlStateChanged();
        _pendingRecentPlaybackChannel = channel;
        _pendingRecentPlaybackRequestVersion = requestVersion;
        CurrentChannelTitle = channel.Name;
        NotificationMessage = string.Empty;
        PlaybackStatusText = string.Empty;
        ShowCleanPlaybackLoadingSurface();
        Interlocked.Exchange(ref _livePlaybackStartupVersion, requestVersion);

        try
        {
            var channelModel = channel.ToModel();
            await Task.Run(() => _playback.PlayAsync(channelModel, cancellationToken), cancellationToken);

            if (!IsCurrentLivePlaybackRequest(channel, requestVersion, cancellationToken))
            {
                return;
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Live playback request was canceled for channel {ChannelId} - {ChannelName}",
                channel.Id,
                channel.Name);
        }
    }

    private (long RequestVersion, CancellationToken CancellationToken) StartLivePlaybackRequest()
    {
        var isContinuingSameChannel = _activeRecentPlaybackChannel is not null
            && SelectedChannel is not null
            && string.Equals(
                _activeRecentPlaybackChannel.Id,
                SelectedChannel.Id,
                StringComparison.OrdinalIgnoreCase)
            && _activeRecentPlaybackStartedTimestamp != 0L;

        TouchActiveRecentChannel(DateTimeOffset.UtcNow, endSession: !isContinuingSameChannel);
        if (!isContinuingSameChannel)
        {
            StopLiveSessionTimer();
        }

        _pendingRecentPlaybackChannel = null;
        _pendingRecentPlaybackRequestVersion = 0;
        CancelVisibleEpgRefresh();
        _livePlaybackCts?.Cancel();
        _livePlaybackCts?.Dispose();
        _livePlaybackCts = new CancellationTokenSource();
        _lastLivePlaybackPlayingUtc = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _livePlaybackRecoveryInFlight, 0);
        _livePlaybackRecoveryAttempt = 0;

        var requestVersion = Interlocked.Increment(ref _livePlaybackRequestVersion);
        Interlocked.Exchange(ref _livePlaybackStartupVersion, requestVersion);
        return (requestVersion, _livePlaybackCts.Token);
    }

    private bool IsCurrentLivePlaybackRequest(
        ChannelItemViewModel channel,
        long requestVersion,
        CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
           && requestVersion == Interlocked.Read(ref _livePlaybackRequestVersion)
           && SelectedChannel is not null
           && string.Equals(SelectedChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase);

    private void CancelLivePlaybackRequest()
    {
        TouchActiveRecentChannel(DateTimeOffset.UtcNow, endSession: true);
        StopLiveSessionTimer();
        CancelVisibleEpgRefresh();
        _livePlaybackCts?.Cancel();
        _livePlaybackCts?.Dispose();
        _livePlaybackCts = null;
        _pendingRecentPlaybackChannel = null;
        _pendingRecentPlaybackRequestVersion = 0;
        _lastLivePlaybackPlayingUtc = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _livePlaybackRecoveryInFlight, 0);
        _livePlaybackRecoveryAttempt = 0;
        Interlocked.Exchange(ref _livePlaybackStartupVersion, 0);
        Interlocked.Increment(ref _livePlaybackRequestVersion);
        HideFullscreenPlaybackLoading();
    }

    private void BeginEpgRefresh(ChannelItemViewModel channel)
    {
        if (SelectedSource is null)
        {
            return;
        }

        _selectedEpgRefreshCts = new CancellationTokenSource();
        var sourceId = SelectedSource.Id;
        var refreshToken = _selectedEpgRefreshCts.Token;

        if (ShouldDeferLivePlaybackBackgroundWork())
        {
            LogBackgroundWorkDuringPlayback("selected-channel-epg", channel.Id, "deferred");
            _ = ExecuteAndReportAsync(() => RefreshSelectedChannelEpgAfterPlaybackStableAsync(sourceId, channel, refreshToken));
            return;
        }

        _ = ExecuteAndReportAsync(() => RefreshChannelEpgAsync(sourceId, channel, refreshToken));
    }

    private async Task RefreshSelectedChannelEpgAfterPlaybackStableAsync(
        Guid sourceId,
        ChannelItemViewModel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            while (ShouldDeferLivePlaybackBackgroundWork())
            {
                await Task.Delay(GetLivePlaybackBackgroundWorkDelay(), cancellationToken).ConfigureAwait(false);
            }

            await RefreshChannelEpgAsync(sourceId, channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Selection/playback changed before startup became stable.
        }
    }

    private async Task RefreshChannelEpgAsync(
        Guid sourceId,
        ChannelItemViewModel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            LogBackgroundWorkDuringPlayback("selected-channel-epg", channel.Id, "start");
            var epg = await _catalog.GetChannelEpgAsync(sourceId, channel.Id, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || SelectedChannel is null
                || !string.Equals(SelectedChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyChannelEpg(channel, epg);
            LogBackgroundWorkDuringPlayback("selected-channel-epg", channel.Id, "applied");
        }
        catch (OperationCanceledException)
        {
            // Selection changed before the provider replied; ignore the stale EPG response.
        }
    }

    private void BeginVisibleChannelsEpgRefresh()
    {
        CancelVisibleEpgRefresh();

        if (!IsLiveTvSection || SelectedSource is null || VisibleChannels.Count == 0)
        {
            return;
        }

        // Visible-channel EPG refresh is nonessential background work. Keep
        // it completely off the UI/network path while live video is active so
        // provider metadata requests cannot compete with the media pipeline.
        if (IsLivePlaybackBackgroundWorkActive())
        {
            LogBackgroundWorkDuringPlayback("visible-epg-refresh", "live playback", "deferred-until-playback-stops");
            return;
        }

        if (ShouldDeferVisibleChannelsEpgRefresh())
        {
            ScheduleVisibleChannelsEpgRefreshAfterPlaybackStable();
            return;
        }

        var sourceId = SelectedSource.Id;
        var channels = VisibleChannels
            .OrderByDescending(channel => SelectedChannel is not null && string.Equals(channel.Id, SelectedChannel.Id, StringComparison.OrdinalIgnoreCase))
            .Take(MaxVisibleEpgRefreshChannels)
            .ToArray();
        channels = ExcludeSelectedChannelWhenSelectedEpgRefreshIsPending(channels);

        if (channels.Length == 0)
        {
            return;
        }

        _visibleEpgRefreshCts = new CancellationTokenSource();
        var refreshToken = _visibleEpgRefreshCts.Token;

        LogBackgroundWorkDuringPlayback("visible-epg-refresh", $"{channels.Length} channels", "scheduled");
        _ = Task.Run(() => RefreshVisibleChannelsEpgAsync(sourceId, channels, refreshToken), refreshToken);
    }

    private ChannelItemViewModel[] ExcludeSelectedChannelWhenSelectedEpgRefreshIsPending(ChannelItemViewModel[] channels)
    {
        if (_selectedEpgRefreshCts is null
            || _selectedEpgRefreshCts.IsCancellationRequested
            || SelectedChannel is null)
        {
            return channels;
        }

        return channels
            .Where(channel => !string.Equals(channel.Id, SelectedChannel.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private async Task RefreshVisibleChannelsEpgAsync(
        Guid sourceId,
        IReadOnlyList<ChannelItemViewModel> channels,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextIndex = -1;
            var workerCount = Math.Min(MaxConcurrentVisibleEpgRefreshes, channels.Count);
            var workers = Enumerable.Range(0, workerCount)
                .Select(_ => RefreshVisibleChannelsEpgWorkerAsync(sourceId, channels, () => Interlocked.Increment(ref nextIndex), cancellationToken))
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Category/search changed; the new visible list starts its own safe EPG refresh.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Background EPG refresh failed for visible channels");
        }
    }

    private async Task RefreshVisibleChannelsEpgWorkerAsync(
        Guid sourceId,
        IReadOnlyList<ChannelItemViewModel> channels,
        Func<int> getNextIndex,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var index = getNextIndex();
            if (index >= channels.Count)
            {
                return;
            }

            var channel = channels[index];
            if (!string.IsNullOrWhiteSpace(channel.CurrentProgram)
                && channel.HasCurrentProgramTiming
                && channel.CurrentProgramProgressPercent < 95)
            {
                continue;
            }

            LogBackgroundWorkDuringPlayback("visible-epg-refresh", channel.Id, "request");
            var epg = await _catalog.GetChannelEpgAsync(sourceId, channel.Id, cancellationToken).ConfigureAwait(false);
            if (epg != ChannelEpgModel.Empty)
            {
                ApplyChannelEpg(channel, epg);
            }
            LogBackgroundWorkDuringPlayback("visible-epg-refresh", channel.Id, "posted-ui-apply");

            await Task.Delay(VisibleEpgRefreshBetweenRequests, cancellationToken).ConfigureAwait(false);
        }
    }

    private void LogBackgroundWorkDuringPlayback(string workName, string itemId, string phase)
    {
        if (!_playbackDiagnosticsEnabled || !IsLivePlaybackActiveForDiagnostics())
        {
            return;
        }

        _logger.LogInformation(
            "Playback diagnostics: background work while live playback active. Work={WorkName}; Item={ItemId}; Phase={Phase}; PlaybackState={PlaybackState}; OverlayVisible={OverlayVisible}; LoadingOverlayVisible={LoadingOverlayVisible}",
            workName,
            itemId,
            phase,
            _currentPlaybackState,
            IsPlayerSurfaceOverlayVisible,
            IsPlayerSurfaceLoadingVisible || IsFullscreenPlaybackLoadingVisible);
    }

    private bool IsLivePlaybackActiveForDiagnostics()
        => _livePlaybackCts is not null
           && SelectedChannel is not null
           && _currentPlaybackState is PlaybackState.Connecting or PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Paused;

    private bool IsLivePlaybackBackgroundWorkActive()
        => _livePlaybackCts is not null
           && !IsOnDemandPlaybackActive
           && SelectedChannel is not null
           && _currentPlaybackState is PlaybackState.Connecting or PlaybackState.Buffering or PlaybackState.Playing;

    private bool ShouldDeferVisibleChannelsEpgRefresh()
    {
        return ShouldDeferLivePlaybackBackgroundWork();
    }

    private void ScheduleVisibleChannelsEpgRefreshAfterPlaybackStable()
    {
        CancelDeferredVisibleEpgRefresh();

        if (!IsLiveTvSection
            || SelectedSource is null
            || VisibleChannels.Count == 0
            || _livePlaybackCts is null
            || IsOnDemandPlaybackActive
            || SelectedChannel is null)
        {
            return;
        }

        _deferredVisibleEpgRefreshCts = new CancellationTokenSource();
        var refreshToken = _deferredVisibleEpgRefreshCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var delay = GetLivePlaybackBackgroundWorkDelay();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, refreshToken).ConfigureAwait(false);
                }

                refreshToken.ThrowIfCancellationRequested();
                _uiContext.Post(_ =>
                {
                    if (!refreshToken.IsCancellationRequested)
                    {
                        BeginVisibleChannelsEpgRefresh();
                    }
                }, null);
            }
            catch (OperationCanceledException)
            {
                // Playback/category/search changed; the next stable state schedules a fresh refresh.
            }
        }, refreshToken);
    }

    private bool ShouldDeferLivePlaybackBackgroundWork()
    {
        if (_livePlaybackCts is null || IsOnDemandPlaybackActive || SelectedChannel is null)
        {
            return false;
        }

        if (_currentPlaybackState is PlaybackState.Connecting or PlaybackState.Buffering)
        {
            return true;
        }

        if (_currentPlaybackState != PlaybackState.Playing
            || _lastLivePlaybackPlayingUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - _lastLivePlaybackPlayingUtc < VisibleEpgRefreshAfterPlaybackStableDelay;
    }

    private TimeSpan GetLivePlaybackBackgroundWorkDelay()
    {
        if (_currentPlaybackState is PlaybackState.Connecting or PlaybackState.Buffering
            || _lastLivePlaybackPlayingUtc == DateTimeOffset.MinValue)
        {
            return VisibleEpgRefreshAfterPlaybackStableDelay;
        }

        var elapsed = DateTimeOffset.UtcNow - _lastLivePlaybackPlayingUtc;
        return elapsed >= VisibleEpgRefreshAfterPlaybackStableDelay
            ? TimeSpan.Zero
            : VisibleEpgRefreshAfterPlaybackStableDelay - elapsed;
    }

    private void ApplyChannelEpg(ChannelItemViewModel channel, IptvPlayer.Contracts.Models.ChannelEpgModel epg)
    {
        _uiContext.Post(_ =>
        {
            channel.ApplyEpg(epg);
        }, null);
    }

    private void CancelSelectedEpgRefresh()
    {
        _selectedEpgRefreshCts?.Cancel();
        _selectedEpgRefreshCts?.Dispose();
        _selectedEpgRefreshCts = null;
    }

    private void CancelVisibleEpgRefresh()
    {
        CancelDeferredVisibleEpgRefresh();
        _visibleEpgRefreshCts?.Cancel();
        _visibleEpgRefreshCts?.Dispose();
        _visibleEpgRefreshCts = null;
    }

    private void CancelDeferredVisibleEpgRefresh()
    {
        _deferredVisibleEpgRefreshCts?.Cancel();
        _deferredVisibleEpgRefreshCts?.Dispose();
        _deferredVisibleEpgRefreshCts = null;
    }

    private CancellationTokenSource? _allChannelsEpgSyncCts;

    private void CancelAllChannelsEpgSync()
    {
        if (_allChannelsEpgSyncCts is null)
        {
            return;
        }

        try
        {
            _allChannelsEpgSyncCts.Cancel();
            _allChannelsEpgSyncCts.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to cancel Priority 2 background all-channels EPG sync");
        }
        finally
        {
            _allChannelsEpgSyncCts = null;
        }
    }

    private void StartAllChannelsEpgSync(Guid sourceId)
    {
        CancelAllChannelsEpgSync();

        if (SelectedSource is null || SelectedSource.Id != sourceId)
        {
            return;
        }

        _allChannelsEpgSyncCts = new CancellationTokenSource();
        var cancellationToken = _allChannelsEpgSyncCts.Token;

        _logger.LogInformation("Starting Priority 2 background all-channels EPG sync for source {SourceId}", sourceId);
        _ = Task.Run(() => SyncAllChannelsEpgAsync(sourceId, cancellationToken), cancellationToken);
    }

    private async Task SyncAllChannelsEpgAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            var categories = await _catalog.GetCategoriesAsync(sourceId, cancellationToken).ConfigureAwait(false);
            if (categories.Count == 0)
            {
                return;
            }

            var selectedCategoryId = SelectedCategory?.Id;
            var sortedCategories = categories
                .OrderByDescending(c => selectedCategoryId is not null && string.Equals(c.Id, selectedCategoryId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.SortOrder)
                .ToList();

            var allChannelsToSync = new List<ChannelModel>();
            foreach (var category in sortedCategories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var catChannels = await _catalog.GetChannelsAsync(sourceId, category.Id, cancellationToken).ConfigureAwait(false);
                allChannelsToSync.AddRange(catChannels);
            }

            var channels = allChannelsToSync
                .Where(c => string.IsNullOrWhiteSpace(c.CurrentProgram))
                .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (channels.Length == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Priority 2 background all-channels EPG sync queued {Count} channels for source {SourceId}",
                channels.Length,
                sourceId);

            var nextIndex = -1;
            const int WorkerCount = 1;
            var workers = Enumerable.Range(0, WorkerCount)
                .Select(_ => SyncAllChannelsEpgWorkerAsync(sourceId, channels, () => Interlocked.Increment(ref nextIndex), cancellationToken))
                .ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation on source switch or shutdown
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Priority 2 background all-channels EPG sync encountered an issue");
        }
    }

    private async Task SyncAllChannelsEpgWorkerAsync(
        Guid sourceId,
        IReadOnlyList<ChannelModel> channels,
        Func<int> getNextIndex,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var index = getNextIndex();
            if (index >= channels.Count)
            {
                return;
            }

            var channel = channels[index];

            while (IsLivePlaybackBackgroundWorkActive() || ShouldDeferLivePlaybackBackgroundWork())
            {
                await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var epg = await _catalog.GetChannelEpgAsync(sourceId, channel.Id, cancellationToken).ConfigureAwait(false);
            if (epg != ChannelEpgModel.Empty)
            {
                _uiContext.Post(_ =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var visibleChannel = VisibleChannels.FirstOrDefault(entry => string.Equals(entry.Id, channel.Id, StringComparison.OrdinalIgnoreCase));
                    if (visibleChannel is not null)
                    {
                        visibleChannel.ApplyEpg(epg);
                    }

                    lock (_syncRoot)
                    {
                        var allChannel = _allChannels.FirstOrDefault(entry => string.Equals(entry.Id, channel.Id, StringComparison.OrdinalIgnoreCase));
                        if (allChannel is not null && !ReferenceEquals(allChannel, visibleChannel))
                        {
                            allChannel.ApplyEpg(epg);
                        }
                    }
                }, null);
            }

            await Task.Delay(AllChannelsEpgSyncBetweenRequests, cancellationToken).ConfigureAwait(false);
        }
    }

    private void CancelMovieDetailsLoad()
    {
        _movieDetailsCts?.Cancel();
        _movieDetailsCts?.Dispose();
        _movieDetailsCts = null;
    }

    private void CancelSeriesDetailsLoad()
    {
        _seriesDetailsCts?.Cancel();
        _seriesDetailsCts?.Dispose();
        _seriesDetailsCts = null;
    }

    private void ClearLiveTvState()
    {
        CancelLivePlaybackRequest();
        _channelFilterCts?.Cancel();
        _channelFilterCts?.Dispose();
        _channelFilterCts = null;
        _quickQualityVariantsCts?.Cancel();
        _quickQualityVariantsCts?.Dispose();
        _quickQualityVariantsCts = null;
        _quickQualityVariantsCacheSourceId = null;
        _loadCategoriesCts?.Cancel();
        _loadCategoriesCts?.Dispose();
        _loadCategoriesCts = null;
        _loadChannelsCts?.Cancel();
        _loadChannelsCts?.Dispose();
        _loadChannelsCts = null;
        CancelVisibleEpgRefresh();
        CancelSelectedEpgRefresh();
        _liveLoadedSourceId = null;
        SelectedCategory = null;
        SelectedChannel = null;
        CurrentChannelTitle = L("SelectAChannel");
        IsNativeVideoSurfaceVisible = false;
        ShowPlayerSurfaceOverlay(L("Ready"), L("SelectChannelToStart"));

        lock (_syncRoot)
        {
            _allCategories.Clear();
            _allChannels.Clear();
            _eventPlaylistChannels.Clear();
            _eventsMatchedSourceId = null;
            _eventsMatchingSourceId = null;
            _quickQualityVariantsByIdentity.Clear();
        }

        ReplaceCollection(VisibleCategories, Array.Empty<CategoryItemViewModel>());
        ReplaceCollection(VisibleChannels, Array.Empty<ChannelItemViewModel>());
        ReplaceCollection(RecentChannels, Array.Empty<ChannelItemViewModel>());
        _recentChannelHistory.Clear();
        _recentChannelsLoadedSourceId = null;
        _activeRecentPlaybackChannel = null;
        _activeRecentPlaybackStartedUtc = DateTimeOffset.MinValue;
        RefreshRecentChannelPresentation();
        ReplaceCollection(QuickQualityVariants, Array.Empty<QuickQualityVariantViewModel>());
        NotifyQuickQualityStateChanged();
    }

    private void ClearMovieState(string emptyMessage)
    {
        _loadMoviesCts?.Cancel();
        _movieFilterCts?.Cancel();
        _movieFilterCts?.Dispose();
        _movieFilterCts = null;
        _movieSearchCacheCts?.Cancel();
        _movieSearchCacheCts?.Dispose();
        _movieSearchCacheCts = null;
        CancelMovieDetailsLoad();
        var wasLoading = _isLoadingMovieCatalog;
        _isLoadingMovieCatalog = true;
        _moviesLoadedSourceId = null;
        _moviesLoadingSourceId = null;
        MovieErrorMessage = string.Empty;
        MovieDetailsErrorMessage = string.Empty;
        MovieEmptyStateMessage = emptyMessage;
        IsMovieWatchlistSelected = false;
        SelectedMovieCategory = null;
        SelectedMovie = null;
        SelectedMovieDetails = null;
        ReplaceCollection(MovieCategories, Array.Empty<CategoryItemViewModel>());
        ReplaceCollection(VisibleMovies, Array.Empty<MovieItemViewModel>());
        ClearMovieCache();
        lock (_syncRoot)
        {
            _playlistSearchMovies.Clear();
            _movieSearchCacheSourceId = null;
            _movieSearchCacheWarmupTask = null;
        }
        _isLoadingMovieCatalog = wasLoading;
        NotifyMovieStateChanged();
    }

    private void ClearMovieCache()
    {
        lock (_syncRoot)
        {
            _allMovies.Clear();
            _filteredMovies.Clear();
        }
    }

    private void ClearSeriesState(string emptyMessage)
    {
        _loadSeriesCts?.Cancel();
        _seriesFilterCts?.Cancel();
        _seriesFilterCts?.Dispose();
        _seriesFilterCts = null;
        _seriesSearchCacheCts?.Cancel();
        _seriesSearchCacheCts?.Dispose();
        _seriesSearchCacheCts = null;
        CancelSeriesDetailsLoad();
        var wasLoading = _isLoadingSeriesCatalog;
        _isLoadingSeriesCatalog = true;
        _seriesLoadedSourceId = null;
        _seriesLoadingSourceId = null;
        SeriesErrorMessage = string.Empty;
        SeriesDetailsErrorMessage = string.Empty;
        SeriesEmptyStateMessage = emptyMessage;
        IsSeriesWatchlistSelected = false;
        SelectedSeriesCategory = null;
        SelectedSeries = null;
        SelectedSeriesDetails = null;
        ReplaceCollection(SeriesCategories, Array.Empty<CategoryItemViewModel>());
        ReplaceCollection(VisibleSeries, Array.Empty<SeriesItemViewModel>());
        ClearSeriesCache();
        lock (_syncRoot)
        {
            _playlistSearchSeries.Clear();
            _seriesSearchCacheSourceId = null;
            _seriesSearchCacheWarmupTask = null;
        }
        _isLoadingSeriesCatalog = wasLoading;
        NotifySeriesStateChanged();
    }

    private void ClearSeriesCache()
    {
        lock (_syncRoot)
        {
            _allSeries.Clear();
            _filteredSeries.Clear();
        }
    }

    private ChannelItemViewModel? GetAdjacentVisibleChannel(int offset)
    {
        if (VisibleChannels.Count == 0)
        {
            return null;
        }

        var currentIndex = SelectedChannel is null
            ? -1
            : VisibleChannels.IndexOf(SelectedChannel);

        if (currentIndex < 0)
        {
            return VisibleChannels[0];
        }

        var nextIndex = (currentIndex + offset + VisibleChannels.Count) % VisibleChannels.Count;
        return VisibleChannels[nextIndex];
    }

    private void UpdateRecents(ChannelItemViewModel channel, DateTimeOffset playedAtUtc)
    {
        var existing = RecentChannels.FirstOrDefault(item =>
            string.Equals(item.Id, channel.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentChannels.Remove(existing);
        }

        RecentChannels.Insert(0, channel);

        while (RecentChannels.Count > 12)
        {
            RecentChannels.RemoveAt(RecentChannels.Count - 1);
        }

        _recentChannelHistory.RemoveAll(entry =>
            string.Equals(entry.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase));
        _recentChannelHistory.Insert(0, new RecentChannelHistoryEntry(
            channel.Id,
            playedAtUtc,
            playedAtUtc,
            0d));

        while (_recentChannelHistory.Count > 12)
        {
            _recentChannelHistory.RemoveAt(_recentChannelHistory.Count - 1);
        }

        _activeRecentPlaybackChannel = channel;
        _activeRecentPlaybackStartedUtc = playedAtUtc;
        _activeRecentPlaybackStartedTimestamp = Stopwatch.GetTimestamp();
        RefreshRecentChannelPresentation();
    }

    private void RecordSuccessfulLivePlayback(DateTimeOffset playedAtUtc)
    {
        var channel = _pendingRecentPlaybackChannel;
        var requestVersion = Interlocked.Read(ref _livePlaybackRequestVersion);
        if (channel is null
            || _pendingRecentPlaybackRequestVersion != requestVersion
            || SelectedChannel is null
            || !string.Equals(SelectedChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase)
            || SelectedSource is null
            || IsOnDemandPlaybackActive
            || _livePlaybackCts is null)
        {
            return;
        }

        if (requestVersion == _lastRecordedRecentPlaybackRequestVersion)
        {
            return;
        }

        _lastRecordedRecentPlaybackRequestVersion = requestVersion;
        var isContinuingSameChannel = _activeRecentPlaybackChannel is not null
            && string.Equals(_activeRecentPlaybackChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase)
            && _activeRecentPlaybackStartedTimestamp != 0L;
        if (!isContinuingSameChannel)
        {
            UpdateRecents(channel, playedAtUtc);
        }

        StartLiveSessionTimer(channel);
        _ = ExecuteAndReportAsync(() => PersistStateAsync(channel.Id));
    }

    private void StartLiveSessionTimer(ChannelItemViewModel channel)
    {
        if (_activeRecentPlaybackChannel is null
            || !string.Equals(_activeRecentPlaybackChannel.Id, channel.Id, StringComparison.OrdinalIgnoreCase)
            || _activeRecentPlaybackStartedTimestamp == 0L)
        {
            return;
        }

        TouchActiveRecentChannel(DateTimeOffset.UtcNow, endSession: false);
        if (_liveSessionTimerCts is { IsCancellationRequested: false })
        {
            return;
        }

        StopLiveSessionTimer();
        _liveSessionTimerCts = new CancellationTokenSource();
        var cancellationToken = _liveSessionTimerCts.Token;
        var timerVersion = Interlocked.Increment(ref _liveSessionTimerVersion);
        var channelId = channel.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(LiveSessionTimerInterval);
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    _uiContext.Post(_ =>
                    {
                        if (cancellationToken.IsCancellationRequested
                            || timerVersion != Interlocked.Read(ref _liveSessionTimerVersion)
                            || _livePlaybackCts is null
                            || IsOnDemandPlaybackActive
                            || _activeRecentPlaybackChannel is null
                            || !string.Equals(_activeRecentPlaybackChannel.Id, channelId, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        TouchActiveRecentChannel(DateTimeOffset.UtcNow, endSession: false);
                    }, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }, cancellationToken);
    }

    private void StopLiveSessionTimer()
    {
        Interlocked.Increment(ref _liveSessionTimerVersion);
        _liveSessionTimerCts?.Cancel();
        _liveSessionTimerCts?.Dispose();
        _liveSessionTimerCts = null;
    }

    private bool TouchActiveRecentChannel(DateTimeOffset watchedToUtc, bool endSession)
    {
        var channel = _activeRecentPlaybackChannel;
        if (channel is null)
        {
            return false;
        }

        var historyIndex = _recentChannelHistory.FindIndex(entry =>
            string.Equals(entry.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase));
        if (historyIndex < 0)
        {
            if (endSession)
            {
                _activeRecentPlaybackChannel = null;
                _activeRecentPlaybackStartedUtc = DateTimeOffset.MinValue;
                _activeRecentPlaybackStartedTimestamp = 0L;
            }

            return false;
        }

        var current = _recentChannelHistory[historyIndex];
        var watchedFromUtc = current.WatchedFromUtc ?? _activeRecentPlaybackStartedUtc;
        if (watchedFromUtc == DateTimeOffset.MinValue || watchedToUtc < watchedFromUtc)
        {
            watchedFromUtc = watchedToUtc;
        }

        var watchedDuration = _activeRecentPlaybackStartedTimestamp == 0L
            ? watchedToUtc - watchedFromUtc
            : Stopwatch.GetElapsedTime(_activeRecentPlaybackStartedTimestamp);
        var watchedProgress = Math.Clamp(watchedDuration.TotalMinutes / 30d * 100d, 0d, 100d);
        var progressPercent = Math.Max(current.ProgressPercent, watchedProgress);

        _recentChannelHistory[historyIndex] = current with
        {
            WatchedFromUtc = watchedFromUtc,
            WatchedToUtc = watchedToUtc,
            ProgressPercent = Math.Clamp(progressPercent, 0d, 100d),
        };

        if (endSession)
        {
            _activeRecentPlaybackChannel = null;
            _activeRecentPlaybackStartedUtc = DateTimeOffset.MinValue;
            _activeRecentPlaybackStartedTimestamp = 0L;
        }

        RefreshRecentChannelPresentation();
        return true;
    }

    private void RefreshRecentChannelPresentation()
    {
        var historyById = _recentChannelHistory
            .GroupBy(entry => entry.ChannelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var existingItemsById = CompactRecentChannels
            .Prepend(ResumeLastChannel)
            .Where(item => item is not null)
            .Cast<RecentChannelItemViewModel>()
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var activeChannelId = _activeRecentPlaybackChannel?.Id;
        var activeSessionElapsed = _activeRecentPlaybackStartedTimestamp == 0L
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_activeRecentPlaybackStartedTimestamp);

        var recentItems = RecentChannels
            .Take(4)
            .Select(channel =>
            {
                var history = historyById.TryGetValue(channel.Id, out var savedHistory)
                    ? savedHistory
                    : null;

                RecentChannelItemViewModel item;
                if (existingItemsById.TryGetValue(channel.Id, out var existingItem)
                    && ReferenceEquals(existingItem.Channel, channel))
                {
                    existingItem.UpdateHistory(history);
                    item = existingItem;
                }
                else
                {
                    item = new RecentChannelItemViewModel(channel, history);
                }

                if (!string.IsNullOrWhiteSpace(activeChannelId)
                    && string.Equals(channel.Id, activeChannelId, StringComparison.OrdinalIgnoreCase))
                {
                    item.UpdateActiveSession(activeSessionElapsed);
                }

                return item;
            })
            .ToArray();

        if (!string.IsNullOrWhiteSpace(activeChannelId)
            && _activeRecentPlaybackStartedTimestamp != 0L)
        {
            LivePlaybackElapsedText = recentItems
                .FirstOrDefault(item => string.Equals(item.Id, activeChannelId, StringComparison.OrdinalIgnoreCase))
                ?.LastWatchedTimeRange
                ?? "00:00";
            LivePlaybackProgressPercent = Math.Clamp(activeSessionElapsed.TotalMinutes / 30d * 100d, 0d, 100d);
        }
        else
        {
            LivePlaybackElapsedText = "00:00";
            LivePlaybackProgressPercent = 0d;
        }

        ResumeLastChannel = recentItems.FirstOrDefault();
        var compactItems = recentItems.Skip(1).Take(3).ToArray();
        if (CompactRecentChannels.Count != compactItems.Length
            || !CompactRecentChannels.SequenceEqual(compactItems))
        {
            ReplaceCollection(CompactRecentChannels, compactItems);
        }

        OnPropertyChanged(nameof(HasRecentChannels));
        OnPropertyChanged(nameof(HasCompactRecentChannels));
        OnPropertyChanged(nameof(IsRecentChannelsEmptyStateVisible));
    }

    private async Task PersistStateAsync(string? forcedChannelId = null)
    {
        var sourceId = SelectedSource?.Id;
        var favoriteIds = GetFavoriteChannelIds(sourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sourceId.HasValue)
        {
            lock (_syncRoot)
            {
                foreach (var channel in _favoriteChannels)
                {
                    if (channel.IsFavorite)
                    {
                        favoriteIds.Add(channel.Id);
                    }
                    else
                    {
                        favoriteIds.Remove(channel.Id);
                    }
                }

                foreach (var channel in _allChannels)
                {
                    if (channel.IsFavorite)
                    {
                        favoriteIds.Add(channel.Id);
                    }
                    else
                    {
                        favoriteIds.Remove(channel.Id);
                    }
                }
            }
        }

        var recentStateIsLoaded = sourceId.HasValue && _recentChannelsLoadedSourceId == sourceId;
        var recentIds = recentStateIsLoaded
            ? RecentChannels
                .Select(channel => channel.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : GetRecentChannelIds(sourceId).ToArray();
        var recentHistory = recentStateIsLoaded
            ? OrderRecentHistory(recentIds, _recentChannelHistory)
            : GetRecentChannelHistory(sourceId).ToArray();

        _sessionSnapshot = new UserSessionState(
            sourceId,
            SelectedCategory?.Id,
            forcedChannelId ?? SelectedChannel?.Id,
            favoriteIds.ToArray(),
            recentIds,
            IsMuted)
        {
            FavoriteChannelIdsBySource = sourceId.HasValue
                ? UpdateSourceCollection(_sessionSnapshot.FavoriteChannelIdsBySource, sourceId.Value, favoriteIds)
                : _sessionSnapshot.FavoriteChannelIdsBySource,
            RecentChannelIdsBySource = sourceId.HasValue
                ? UpdateSourceCollection(_sessionSnapshot.RecentChannelIdsBySource, sourceId.Value, recentIds)
                : _sessionSnapshot.RecentChannelIdsBySource,
            RecentChannelHistoryBySource = sourceId.HasValue
                ? UpdateSourceCollection(_sessionSnapshot.RecentChannelHistoryBySource, sourceId.Value, recentHistory)
                : _sessionSnapshot.RecentChannelHistoryBySource,
        };

        await _session.SaveAsync(_sessionSnapshot);
    }

    private static RecentChannelHistoryEntry[] OrderRecentHistory(
        IEnumerable<string> recentIds,
        IEnumerable<RecentChannelHistoryEntry> history)
    {
        var historyById = history
            .GroupBy(entry => entry.ChannelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return recentIds
            .Where(historyById.ContainsKey)
            .Select(id => historyById[id])
            .ToArray();
    }

    private void ApplyCategoryFilter(string? filter)
    {
        List<CategoryItemViewModel> source;
        lock (_syncRoot)
        {
            source = [.. _allCategories];
        }

        var term = filter?.Trim();
        var results = string.IsNullOrWhiteSpace(term)
            ? source
            : source.Where(category => category.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        ReplaceCollection(VisibleCategories, results);
    }

    private void ApplyChannelFilter(string? filter)
    {
        var term = filter?.Trim();
        var searchSourceId = SelectedSource?.Id;
        List<ChannelItemViewModel> source;
        lock (_syncRoot)
        {
            source = !string.IsNullOrWhiteSpace(term)
                     && searchSourceId.HasValue
                     && _eventsMatchedSourceId == searchSourceId.Value
                ? [.. _eventPlaylistChannels]
                : [.. _allChannels];
        }

        var results = string.IsNullOrWhiteSpace(term)
            ? source
            : source.Where(channel =>
                    channel.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (channel.CurrentProgram?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (channel.NextProgram?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (channel.CurrentProgramTitle?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (channel.CurrentProgramDescription?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (channel.NextProgramTitle?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (channel.NextProgramDescription?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

        ReplaceCollection(VisibleChannels, results);
        BeginVisibleChannelsEpgRefresh();

        if (SelectedChannel is not null && VisibleChannels.All(channel => channel.Id != SelectedChannel.Id))
        {
            SelectedChannel = VisibleChannels.FirstOrDefault();
        }
    }

    private void QueueChannelFilter(string? filter)
    {
        _channelFilterCts?.Cancel();
        _channelFilterCts?.Dispose();
        _channelFilterCts = new CancellationTokenSource();
        var cancellationToken = _channelFilterCts.Token;

        _ = ExecuteAndReportAsync(() => ApplyChannelFilterAsync(filter, cancellationToken, debounce: true));
    }

    private async Task ApplyChannelFilterAsync(
        string? filter,
        CancellationToken cancellationToken,
        bool debounce = false)
    {
        if (debounce)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        var term = filter?.Trim();
        var searchSource = SelectedSource;
        if (!string.IsNullOrWhiteSpace(term) && searchSource is not null)
        {
            await EnsureEventPlaylistChannelsLoadedAsync(searchSource, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var searchSourceId = searchSource?.Id;
        var results = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ChannelItemViewModel> source;
            lock (_syncRoot)
            {
                source = !string.IsNullOrWhiteSpace(term)
                         && searchSourceId.HasValue
                         && _eventsMatchedSourceId == searchSourceId.Value
                    ? [.. _eventPlaylistChannels]
                    : [.. _allChannels];
            }

            return string.IsNullOrWhiteSpace(term)
                ? source
                : source.Where(channel =>
                        channel.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (channel.CurrentProgram?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (channel.NextProgram?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (channel.CurrentProgramTitle?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (channel.CurrentProgramDescription?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (channel.NextProgramTitle?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (channel.NextProgramDescription?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
        }, cancellationToken);

        if (cancellationToken.IsCancellationRequested
            || (searchSourceId.HasValue && SelectedSource?.Id != searchSourceId.Value))
        {
            return;
        }

        ReplaceCollection(VisibleChannels, results);
        BeginVisibleChannelsEpgRefresh();

        if (SelectedChannel is not null && VisibleChannels.All(channel => channel.Id != SelectedChannel.Id))
        {
            SelectedChannel = VisibleChannels.FirstOrDefault();
        }
    }

    private void QueueMovieFilter(string? filter)
    {
        _movieFilterCts?.Cancel();
        _movieFilterCts?.Dispose();
        _movieFilterCts = new CancellationTokenSource();
        var cancellationToken = _movieFilterCts.Token;

        _ = ExecuteAndReportAsync(() => ApplyMovieFilterAsync(filter, cancellationToken));
    }

    private void BeginOnDemandSearchCacheWarmup(SourceItemViewModel source)
    {
        BeginMovieSearchCacheWarmup(source);
        BeginSeriesSearchCacheWarmup(source);
    }

    private void BeginMovieSearchCacheWarmup(SourceItemViewModel source)
    {
        lock (_syncRoot)
        {
            if (_movieSearchCacheSourceId == source.Id
                || _movieSearchCacheWarmupTask is { IsCompleted: false })
            {
                return;
            }

            _movieSearchCacheWarmupTask = Task.Run(() => WarmMovieSearchCacheAsync(source));
        }
    }

    private async Task WarmMovieSearchCacheAsync(SourceItemViewModel source)
    {
        try
        {
            await EnsurePlaylistMovieSearchCacheAsync(source, CancellationToken.None);
            _uiContext.Post(_ =>
            {
                if (SelectedSource?.Id == source.Id && !string.IsNullOrWhiteSpace(MovieSearchText))
                {
                    QueueMovieFilter(MovieSearchText);
                }
            }, null);
        }
        catch (OperationCanceledException)
        {
            // The active playlist changed before its background index completed.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Background movie search index warmup failed for source {SourceId}", source.Id);
        }
    }

    private void BeginSeriesSearchCacheWarmup(SourceItemViewModel source)
    {
        lock (_syncRoot)
        {
            if (_seriesSearchCacheSourceId == source.Id
                || _seriesSearchCacheWarmupTask is { IsCompleted: false })
            {
                return;
            }

            _seriesSearchCacheWarmupTask = Task.Run(() => WarmSeriesSearchCacheAsync(source));
        }
    }

    private async Task WarmSeriesSearchCacheAsync(SourceItemViewModel source)
    {
        try
        {
            await EnsurePlaylistSeriesSearchCacheAsync(source, CancellationToken.None);
            _uiContext.Post(_ =>
            {
                if (SelectedSource?.Id == source.Id && !string.IsNullOrWhiteSpace(SeriesSearchText))
                {
                    QueueSeriesFilter(SeriesSearchText);
                }
            }, null);
        }
        catch (OperationCanceledException)
        {
            // The active playlist changed before its background index completed.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Background series search index warmup failed for source {SourceId}", source.Id);
        }
    }

    private async Task EnsurePlaylistMovieSearchCacheAsync(
        SourceItemViewModel source,
        CancellationToken cancellationToken)
    {
        await _movieSearchCacheLoadGate.WaitAsync(cancellationToken);
        try
        {
            lock (_syncRoot)
            {
                if (_movieSearchCacheSourceId == source.Id)
                {
                    return;
                }
            }

            _movieSearchCacheCts ??= new CancellationTokenSource();
            var cacheCancellationToken = _movieSearchCacheCts.Token;
            var movies = await _catalog.GetMoviesAsync(source.Id, null, cacheCancellationToken);
            var watchlistIds = GetMovieWatchlistIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var movieItems = await Task.Run(() => movies
                .Select(MovieItemViewModel.FromModel)
                .Select(movie =>
                {
                    movie.IsInWatchlist = watchlistIds.Contains(movie.Id);
                    return movie;
                })
                .ToList(), cacheCancellationToken);

            if (cacheCancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
            {
                return;
            }

            lock (_syncRoot)
            {
                _playlistSearchMovies.Clear();
                _playlistSearchMovies.AddRange(movieItems);
                _movieSearchCacheSourceId = source.Id;
            }
        }
        finally
        {
            _movieSearchCacheLoadGate.Release();
        }
    }

    private async Task ApplyMovieFilterAsync(
        string? filter,
        CancellationToken cancellationToken,
        bool debounce = false)
    {
        if (debounce)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        var term = filter?.Trim();
        var searchSource = SelectedSource;
        if (!string.IsNullOrWhiteSpace(term) && searchSource is not null)
        {
            BeginMovieSearchCacheWarmup(searchSource);
        }

        var searchSourceId = searchSource?.Id;
        var visibleLimit = VisibleMovieLimit;
        var (results, visible) = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<MovieItemViewModel> source;
            lock (_syncRoot)
            {
                source = !string.IsNullOrWhiteSpace(term)
                         && searchSourceId.HasValue
                         && _movieSearchCacheSourceId == searchSourceId.Value
                    ? [.. _playlistSearchMovies]
                    : [.. _allMovies];
            }

            var filtered = string.IsNullOrWhiteSpace(term)
                ? source
                : source.Where(movie =>
                        movie.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (movie.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();

            cancellationToken.ThrowIfCancellationRequested();
            return (filtered, filtered.Take(visibleLimit).ToArray());
        }, cancellationToken);

        if (cancellationToken.IsCancellationRequested
            || (searchSourceId.HasValue && SelectedSource?.Id != searchSourceId.Value))
        {
            return;
        }

        _filteredMovies = results;
        ReplaceCollection(VisibleMovies, visible);
        NotifyMovieStateChanged();

        if (SelectedMovie is not null && VisibleMovies.All(movie => movie.Id != SelectedMovie.Id))
        {
            SelectedMovie = VisibleMovies.FirstOrDefault();
        }
    }

    private void QueueSeriesFilter(string? filter)
    {
        _seriesFilterCts?.Cancel();
        _seriesFilterCts?.Dispose();
        _seriesFilterCts = new CancellationTokenSource();
        var cancellationToken = _seriesFilterCts.Token;

        _ = ExecuteAndReportAsync(() => ApplySeriesFilterAsync(filter, cancellationToken));
    }

    private async Task EnsurePlaylistSeriesSearchCacheAsync(
        SourceItemViewModel source,
        CancellationToken cancellationToken)
    {
        await _seriesSearchCacheLoadGate.WaitAsync(cancellationToken);
        try
        {
            lock (_syncRoot)
            {
                if (_seriesSearchCacheSourceId == source.Id)
                {
                    return;
                }
            }

            _seriesSearchCacheCts ??= new CancellationTokenSource();
            var cacheCancellationToken = _seriesSearchCacheCts.Token;
            var series = await _catalog.GetSeriesAsync(source.Id, null, cacheCancellationToken);
            var watchlistIds = GetSeriesWatchlistIds(source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seriesItems = await Task.Run(() => series
                .Select(SeriesItemViewModel.FromModel)
                .Select(item =>
                {
                    item.IsInWatchlist = watchlistIds.Contains(item.Id);
                    return item;
                })
                .ToList(), cacheCancellationToken);

            if (cacheCancellationToken.IsCancellationRequested || SelectedSource?.Id != source.Id)
            {
                return;
            }

            lock (_syncRoot)
            {
                _playlistSearchSeries.Clear();
                _playlistSearchSeries.AddRange(seriesItems);
                _seriesSearchCacheSourceId = source.Id;
            }
        }
        finally
        {
            _seriesSearchCacheLoadGate.Release();
        }
    }

    private async Task ApplySeriesFilterAsync(
        string? filter,
        CancellationToken cancellationToken,
        bool debounce = false)
    {
        if (debounce)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        var term = filter?.Trim();
        var searchSource = SelectedSource;
        if (!string.IsNullOrWhiteSpace(term) && searchSource is not null)
        {
            BeginSeriesSearchCacheWarmup(searchSource);
        }

        var searchSourceId = searchSource?.Id;
        var visibleLimit = VisibleSeriesLimit;
        var (results, visible) = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<SeriesItemViewModel> source;
            lock (_syncRoot)
            {
                source = !string.IsNullOrWhiteSpace(term)
                         && searchSourceId.HasValue
                         && _seriesSearchCacheSourceId == searchSourceId.Value
                    ? [.. _playlistSearchSeries]
                    : [.. _allSeries];
            }

            var filtered = string.IsNullOrWhiteSpace(term)
                ? source
                : source.Where(series =>
                        series.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (series.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();

            cancellationToken.ThrowIfCancellationRequested();
            return (filtered, filtered.Take(visibleLimit).ToArray());
        }, cancellationToken);

        if (cancellationToken.IsCancellationRequested
            || (searchSourceId.HasValue && SelectedSource?.Id != searchSourceId.Value))
        {
            return;
        }

        _filteredSeries = results;
        ReplaceCollection(VisibleSeries, visible);
        NotifySeriesStateChanged();

        if (SelectedSeries is not null && VisibleSeries.All(series => series.Id != SelectedSeries.Id))
        {
            SelectedSeries = VisibleSeries.FirstOrDefault();
        }
    }

    private void LoadMoreMovies()
    {
        if (!HasMoreMovies)
        {
            return;
        }

        VisibleMovieLimit += OnDemandPageSize;
        ReplaceCollection(VisibleMovies, _filteredMovies.Take(VisibleMovieLimit));
        NotifyMovieStateChanged();
        LoadMoreMoviesCommand.NotifyCanExecuteChanged();
    }

    private void LoadMoreSeries()
    {
        if (!HasMoreSeries)
        {
            return;
        }

        VisibleSeriesLimit += OnDemandPageSize;
        ReplaceCollection(VisibleSeries, _filteredSeries.Take(VisibleSeriesLimit));
        NotifySeriesStateChanged();
        LoadMoreSeriesCommand.NotifyCanExecuteChanged();
    }

    private void NotifyMovieStateChanged()
    {
        OnPropertyChanged(nameof(HasMovieError));
        OnPropertyChanged(nameof(HasMovieDetailsError));
        OnPropertyChanged(nameof(IsMovieEmpty));
        OnPropertyChanged(nameof(MovieEmptyStateTitle));
        OnPropertyChanged(nameof(IsMovieSkeletonVisible));
        OnPropertyChanged(nameof(IsMovieFeaturedSkeletonVisible));
        OnPropertyChanged(nameof(HasMoreMovies));
        OnPropertyChanged(nameof(MovieResultSummary));
        OnPropertyChanged(nameof(MovieLoadMoreText));
        OnPropertyChanged(nameof(HasContinueWatchingMovies));
        OnPropertyChanged(nameof(HasSelectedMovieResumeProgress));
        OnPropertyChanged(nameof(SavedMoviesCount));
        OnPropertyChanged(nameof(SavedSeriesCount));
        OnPropertyChanged(nameof(WatchlistTotalCount));
        OnPropertyChanged(nameof(MovieWatchlistHeaderText));
        OnPropertyChanged(nameof(MovieWatchlistCountSummary));
        OnPropertyChanged(nameof(SeriesWatchlistHeaderText));
        OnPropertyChanged(nameof(SeriesWatchlistCountSummary));
        OnPropertyChanged(nameof(MovieWatchlistSummary));
        OnPropertyChanged(nameof(IsSelectedMovieInWatchlist));
        OnPropertyChanged(nameof(MovieWatchlistButtonText));
        PlaySelectedMovieCommand.NotifyCanExecuteChanged();
        ResumeSelectedMovieCommand.NotifyCanExecuteChanged();
        LoadMoreMoviesCommand.NotifyCanExecuteChanged();
        ToggleMovieWatchlistCommand.NotifyCanExecuteChanged();
    }

    private void NotifySeriesStateChanged()
    {
        OnPropertyChanged(nameof(HasSeriesError));
        OnPropertyChanged(nameof(HasSeriesDetailsError));
        OnPropertyChanged(nameof(IsSeriesEmpty));
        OnPropertyChanged(nameof(SeriesEmptyStateTitle));
        OnPropertyChanged(nameof(IsSeriesSkeletonVisible));
        OnPropertyChanged(nameof(IsSeriesFeaturedSkeletonVisible));
        OnPropertyChanged(nameof(IsSeriesDetailsEmpty));
        OnPropertyChanged(nameof(HasMoreSeries));
        OnPropertyChanged(nameof(SeriesResultSummary));
        OnPropertyChanged(nameof(SeriesLoadMoreText));
        OnPropertyChanged(nameof(HasContinueWatchingSeries));
        OnPropertyChanged(nameof(HasSelectedSeriesResumeProgress));
        OnPropertyChanged(nameof(SavedMoviesCount));
        OnPropertyChanged(nameof(SavedSeriesCount));
        OnPropertyChanged(nameof(WatchlistTotalCount));
        OnPropertyChanged(nameof(MovieWatchlistHeaderText));
        OnPropertyChanged(nameof(MovieWatchlistCountSummary));
        OnPropertyChanged(nameof(SeriesWatchlistHeaderText));
        OnPropertyChanged(nameof(SeriesWatchlistCountSummary));
        OnPropertyChanged(nameof(SeriesWatchlistSummary));
        OnPropertyChanged(nameof(IsSelectedSeriesInWatchlist));
        OnPropertyChanged(nameof(SeriesWatchlistButtonText));
        PlaySelectedSeriesCommand.NotifyCanExecuteChanged();
        ResumeSelectedSeriesCommand.NotifyCanExecuteChanged();
        LoadMoreSeriesCommand.NotifyCanExecuteChanged();
        ToggleSeriesWatchlistCommand.NotifyCanExecuteChanged();
    }

    private void NotifyEventsStateChanged()
    {
        OnPropertyChanged(nameof(HasEventsError));
        OnPropertyChanged(nameof(IsEventsEmpty));
        OnPropertyChanged(nameof(HasSelectedSportsEvent));
        OnPropertyChanged(nameof(IsEventsListVisible));
        OnPropertyChanged(nameof(EventsEmptyTitle));
        OnPropertyChanged(nameof(EventsMatchScopeText));
        OnPropertyChanged(nameof(IsSelectedEventChannelsLoading));
        OnPropertyChanged(nameof(IsEventChannelContentVisible));
        OnPropertyChanged(nameof(IsEventCountrySelectorVisible));
        OnPropertyChanged(nameof(IsNoMatchingEventChannelVisible));
        RefreshEventsCommand.NotifyCanExecuteChanged();
    }

    private void NotifyQuickQualityStateChanged()
    {
        OnPropertyChanged(nameof(HasQuickQualityVariants));
        OnPropertyChanged(nameof(HasMultipleQuickQualityVariants));
        OnPropertyChanged(nameof(QuickQualityTitle));
    }

    private void OnPlaybackStatusChanged(object? sender, PlayerStatus status)
    {
        _uiContext.Post(_ =>
        {
            if (status.State == PlaybackState.Stopped
                && ((_livePlaybackCts is not null
                     && Interlocked.Read(ref _livePlaybackStartupVersion) != 0
                     && _currentPlaybackState is PlaybackState.Connecting or PlaybackState.Buffering)
                    || (string.Equals(status.Message, "Stopped", StringComparison.OrdinalIgnoreCase)
                        && (_currentPlaybackState == PlaybackState.Failed
                            || string.Equals(PlaybackStatusText, "Stream did not start", StringComparison.OrdinalIgnoreCase)))))
            {
                return;
            }

            var previousPlaybackState = _currentPlaybackState;
            var now = DateTimeOffset.UtcNow;
            LogPlaybackStatus(status, previousPlaybackState);

            var isUnexpectedLiveTermination = IsUnexpectedLivePlaybackTermination(status);
            if (isUnexpectedLiveTermination
                && TryRecoverUnexpectedLivePlayback(status))
            {
                return;
            }

            if (status.State == PlaybackState.Buffering
                && previousPlaybackState is PlaybackState.Playing or PlaybackState.Paused)
            {
                CancelVisibleEpgRefresh();
                _lastLivePlaybackPlayingUtc = now;
                ScheduleVisibleChannelsEpgRefreshAfterPlaybackStable();
                IsLoading = true;
                ShowPlayerSurfaceOverlay(string.Empty, string.Empty, showLoader: true, showText: false);
                return;
            }

            _currentPlaybackState = status.State;
            NotifyPlayerControlStateChanged();

            if (status.State == PlaybackState.Playing)
            {
                RecordSuccessfulLivePlayback(now);
            }
            else if (status.State == PlaybackState.Paused
                     && TouchActiveRecentChannel(now, endSession: false))
            {
                _ = ExecuteAndReportAsync(() => PersistStateAsync());
            }

            if (status.State == PlaybackState.Playing
                && _livePlaybackCts is not null
                && !IsOnDemandPlaybackActive)
            {
                _livePlaybackRecoveryAttempt = 0;
                _lastLivePlaybackPlayingUtc = now;
                ScheduleVisibleChannelsEpgRefreshAfterPlaybackStable();
            }
            else if (status.State is PlaybackState.Stopped or PlaybackState.Failed)
            {
                _lastLivePlaybackPlayingUtc = DateTimeOffset.MinValue;
                CancelVisibleEpgRefresh();
            }

            if (status.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Stopped or PlaybackState.Failed)
            {
                Interlocked.Exchange(ref _livePlaybackStartupVersion, 0);
                HideFullscreenPlaybackLoading();
            }

            PlaybackStatusText = isUnexpectedLiveTermination
                ? L("NoSignal")
                : status.State is PlaybackState.Connecting or PlaybackState.Buffering
                    ? string.Empty
                    : LocalizePlayerStatus(status);

            IsLoading = status.State is PlaybackState.Connecting or PlaybackState.Buffering;
            if (isUnexpectedLiveTermination)
            {
                IsNativeVideoSurfaceVisible = false;
                ShowPlayerSurfaceOverlay(L("NoSignal"), string.Empty);
            }
            else
            {
                ApplyPlayerSurfaceStatus(status);
            }

            if (status.State is PlaybackState.Stopped or PlaybackState.Failed)
            {
                SetOnDemandPlaybackActive(false);
            }

            if (status.State == PlaybackState.Failed)
            {
                var failureKey = string.IsNullOrWhiteSpace(status.ErrorCode)
                    ? status.Message
                    : status.ErrorCode;
                if (string.Equals(_lastPlaybackFailureKey, failureKey, StringComparison.OrdinalIgnoreCase)
                    && now - _lastPlaybackFailureUtc < TimeSpan.FromSeconds(4))
                {
                    return;
                }

                _lastPlaybackFailureKey = failureKey;
                _lastPlaybackFailureUtc = now;
                NotificationMessage = L("PlaybackFailedTryAnother");
            }
        }, null);
    }

    private void OnAudioStateChanged(object? sender, PlayerAudioState state)
    {
        _uiContext.Post(_ =>
        {
            _isApplyingExternalAudioState = true;
            try
            {
                Volume = Math.Clamp(state.Volume, 0, 100);
                IsMuted = state.IsMuted;
            }
            finally
            {
                _isApplyingExternalAudioState = false;
            }
        }, null);
    }

    private void NotifyPlayerControlStateChanged()
    {
        OnPropertyChanged(nameof(IsPlaybackPlaying));
        OnPropertyChanged(nameof(IsPlayerPauseIndicatorVisible));
        OnPropertyChanged(nameof(IsPlayerStopIndicatorVisible));
        OnPropertyChanged(nameof(IsPlayerActionIndicatorVisible));
        OnPropertyChanged(nameof(PlayPauseIconGlyph));
        OnPropertyChanged(nameof(PlayPauseIconGeometry));
        OnPropertyChanged(nameof(PlayPauseToolTip));
    }

    private bool IsUnexpectedLivePlaybackTermination(PlayerStatus status)
        => _livePlaybackCts is not null
           && !_livePlaybackCts.IsCancellationRequested
           && SelectedChannel is not null
           && !IsOnDemandPlaybackActive
           && _lastLivePlaybackPlayingUtc != DateTimeOffset.MinValue
           && status.State is PlaybackState.Stopped or PlaybackState.Failed;

    private bool TryRecoverUnexpectedLivePlayback(PlayerStatus status)
    {
        if (Interlocked.CompareExchange(ref _livePlaybackRecoveryInFlight, 1, 0) != 0)
        {
            return true;
        }

        if (SelectedChannel is null
            || _livePlaybackCts is null)
        {
            Interlocked.Exchange(ref _livePlaybackRecoveryInFlight, 0);
            return false;
        }

        _livePlaybackRecoveryAttempt = Math.Min(
            _livePlaybackRecoveryAttempt + 1,
            LivePlaybackRecoveryDelays.Length);
        var recoveryDelay = LivePlaybackRecoveryDelays[_livePlaybackRecoveryAttempt - 1];
        var channel = SelectedChannel;
        var requestVersion = Interlocked.Read(ref _livePlaybackRequestVersion);
        var cancellationToken = _livePlaybackCts.Token;

        _logger.LogWarning(
            "Unexpected live playback termination; reconnecting the selected channel until the user stops or changes it. Attempt={RecoveryAttempt}; RetryDelayMs={RetryDelayMs:0}; State={State}; Message={Message}; ErrorCode={ErrorCode}",
            _livePlaybackRecoveryAttempt,
            recoveryDelay.TotalMilliseconds,
            status.State,
            status.Message,
            status.ErrorCode ?? "none");

        _currentPlaybackState = PlaybackState.Connecting;
        NotifyPlayerControlStateChanged();
        if (_livePlaybackRecoveryAttempt >= LivePlaybackNoSignalAttempt)
        {
            PlaybackStatusText = L("NoSignal");
            IsLoading = false;
            IsNativeVideoSurfaceVisible = false;
            ShowPlayerSurfaceOverlay(L("NoSignal"), string.Empty);
        }
        else
        {
            PlaybackStatusText = string.Empty;
            IsLoading = true;
            ShowCleanPlaybackLoadingSurface();
        }

        Interlocked.Exchange(ref _livePlaybackStartupVersion, requestVersion);
        _ = RecoverSelectedLiveChannelAsync(channel, requestVersion, recoveryDelay, cancellationToken);
        return true;
    }

    private async Task RecoverSelectedLiveChannelAsync(
        ChannelItemViewModel channel,
        long requestVersion,
        TimeSpan recoveryDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentLivePlaybackRequest(channel, requestVersion, cancellationToken))
            {
                return;
            }

            if (recoveryDelay > TimeSpan.Zero)
            {
                await Task.Delay(recoveryDelay, cancellationToken).ConfigureAwait(false);
            }

            if (!IsCurrentLivePlaybackRequest(channel, requestVersion, cancellationToken))
            {
                return;
            }

            Interlocked.Exchange(ref _livePlaybackRecoveryInFlight, 0);
            await Task.Run(
                    () => _playback.PlayAsync(channel.ToModel(), cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Live playback recovery failed before VLC accepted the stream");
            Interlocked.Exchange(ref _livePlaybackRecoveryInFlight, 0);
            _uiContext.Post(_ =>
            {
                if (IsCurrentLivePlaybackRequest(channel, requestVersion, cancellationToken))
                {
                    TryRecoverUnexpectedLivePlayback(
                        new PlayerStatus(PlaybackState.Failed, "Live playback recovery exception", null, "LIVE_RECOVERY_EXCEPTION"));
                }
            }, null);
        }
    }

    private void ShowFullscreenPlaybackLoading()
    {
        IsFullscreenPlaybackLoadingVisible = true;
    }

    private void HideFullscreenPlaybackLoading()
    {
        IsFullscreenPlaybackLoadingVisible = false;
    }

    private void LogPlaybackStatus(PlayerStatus status, PlaybackState previousPlaybackState)
    {
        var now = DateTimeOffset.UtcNow;
        var currentChannelId = SelectedChannel?.Id ?? "none";
        var currentChannelName = SelectedChannel?.Name ?? CurrentChannelTitle;

        if (status.State == PlaybackState.Buffering)
        {
            if (previousPlaybackState is PlaybackState.Playing or PlaybackState.Paused)
            {
                if (now - _lastBufferingLogUtc < TimeSpan.FromSeconds(2))
                {
                    return;
                }

                _lastBufferingLogUtc = now;
                var activePlaybackCache = status.BufferingPercent?.ToString("0.0") ?? "unknown";
                _logger.LogWarning(
                    "Playback rebuffering during active stream for channel {ChannelId} - {ChannelName}. Cache={CachePercent}%",
                    currentChannelId,
                    currentChannelName,
                    activePlaybackCache);
                return;
            }

            var shouldLogBuffering = previousPlaybackState != PlaybackState.Buffering
                || now - _lastBufferingLogUtc >= TimeSpan.FromSeconds(2);

            if (!shouldLogBuffering)
            {
                return;
            }

            _lastBufferingLogUtc = now;
            var cache = status.BufferingPercent?.ToString("0.0") ?? "unknown";

            _logger.LogInformation(
                "Playback buffering for channel {ChannelId} - {ChannelName}. PreviousState={PreviousState}; Cache={CachePercent}%",
                currentChannelId,
                currentChannelName,
                previousPlaybackState,
                cache);
            return;
        }

        if (status.State == _lastLoggedPlaybackState
            && now - _lastPlaybackStateLogUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastLoggedPlaybackState = status.State;
        _lastPlaybackStateLogUtc = now;

        _logger.LogInformation(
            "Playback state changed for channel {ChannelId} - {ChannelName}. {PreviousState} -> {State}; Message={Message}; ErrorCode={ErrorCode}",
            currentChannelId,
            currentChannelName,
            previousPlaybackState,
            status.State,
            status.Message,
            status.ErrorCode ?? "none");
    }

    private void ApplyPlayerSurfaceStatus(PlayerStatus status)
    {
        switch (status.State)
        {
            case PlaybackState.Idle:
                IsNativeVideoSurfaceVisible = false;
        ShowPlayerSurfaceOverlay(L("Ready"), L("SelectChannelToStart"));
                break;
            case PlaybackState.Connecting:
                ShowCleanPlaybackLoadingSurface();
                break;
            case PlaybackState.Buffering:
                if (IsNativeVideoSurfaceVisible)
                {
                    ShowFullscreenPlaybackLoading();
                    ShowPlayerSurfaceOverlay(string.Empty, string.Empty, showLoader: true, showText: false);
                }
                else
                {
                    ShowCleanPlaybackLoadingSurface();
                }
                break;
            case PlaybackState.Playing:
            case PlaybackState.Paused:
                IsNativeVideoSurfaceVisible = true;
                IsPlayerSurfaceLoadingVisible = false;
                IsPlayerSurfaceTextVisible = false;
                IsPlayerSurfaceOverlayVisible = false;
                break;
            case PlaybackState.Stopped:
                IsNativeVideoSurfaceVisible = false;
                ShowPlayerSurfaceOverlay(string.Empty, string.Empty, showText: false);
                break;
            case PlaybackState.Failed:
                IsNativeVideoSurfaceVisible = false;
                if (IsNoSignalFailure(status))
                {
                    ShowPlayerSurfaceOverlay(L("NoSignal"), string.Empty);
                }
                else
                {
                    ShowPlayerSurfaceOverlay(
                        L("PlaybackFailed"),
                        L("TryAnotherStream"));
                }
                break;
        }
    }

    private void ShowCleanPlaybackLoadingSurface()
    {
        IsNativeVideoSurfaceVisible = false;
        ShowFullscreenPlaybackLoading();
        ShowPlayerSurfaceOverlay(string.Empty, string.Empty, showLoader: true, showText: false);
    }

    private void ShowPlayerSurfaceOverlay(
        string title,
        string message,
        bool showLoader = false,
        bool showText = true)
    {
        PlayerSurfaceOverlayTitle = title;
        OnPropertyChanged(nameof(IsPlayerSurfaceReady));
        PlayerSurfaceOverlayMessage = message;
        IsPlayerSurfaceLoadingVisible = showLoader;
        IsPlayerSurfaceTextVisible = showText;
        IsPlayerSurfaceOverlayVisible = true;
    }

    partial void OnPlaybackStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPlaybackStatusText));
        OnPropertyChanged(nameof(IsNoSignalStatusVisible));
        OnPropertyChanged(nameof(IsFullscreenNoSignalVisible));
        OnPropertyChanged(nameof(IsFullscreenHeaderTitleVisible));
    }

    partial void OnIsFullscreenPlaybackLoadingVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFullscreenNoSignalVisible));
    }

    partial void OnIsNativeVideoSurfaceVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFullscreenNoSignalVisible));
    }

    partial void OnNotificationMessageChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _notificationClearCts?.Cancel();
            _notificationClearCts?.Dispose();
            _notificationClearCts = null;
            return;
        }

        AutoClearNotification(value);
    }

    partial void OnIsOnDemandPlaybackActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOnDemandPlaybackControlVisible));
        OnPropertyChanged(nameof(IsLivePlaybackActive));
        ResumeCommand.NotifyCanExecuteChanged();
        SeekBackwardCommand.NotifyCanExecuteChanged();
        SeekForwardCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPlaybackSeekAvailableChanged(bool value)
    {
        SeekBackwardCommand.NotifyCanExecuteChanged();
        SeekForwardCommand.NotifyCanExecuteChanged();
    }

    partial void OnPlaybackPositionSecondsChanged(double value)
    {
        if (_isUpdatingPlaybackProgress || !IsOnDemandPlaybackActive || !IsPlaybackSeekAvailable)
        {
            return;
        }

        QueuePlaybackSeek(value);
        PlaybackPositionText = FormatPlaybackTime(TimeSpan.FromSeconds(Math.Max(0d, value)));
    }

    private async Task ExecuteAndReportAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Action in main shell canceled");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Action in main shell failed");
            NotificationMessage = L("UnexpectedErrorDiagnostics");
            IsLoading = false;
        }
    }

    private void UiLocalization_OnCultureChanged(object? sender, EventArgs e)
    {
        _uiContext.Post(_ =>
        {
            var localization = UiLocalization.Current;
            PlaybackStatusText = localization.Relocalize(PlaybackStatusText);
            PlayerSurfaceOverlayTitle = localization.Relocalize(PlayerSurfaceOverlayTitle);
            PlayerSurfaceOverlayMessage = localization.Relocalize(PlayerSurfaceOverlayMessage);
            CurrentChannelTitle = localization.Relocalize(CurrentChannelTitle);
            NotificationMessage = localization.Relocalize(NotificationMessage);
            MovieEmptyStateMessage = localization.Relocalize(MovieEmptyStateMessage);
            MovieErrorMessage = localization.Relocalize(MovieErrorMessage);
            MovieDetailsErrorMessage = localization.Relocalize(MovieDetailsErrorMessage);
            SeriesEmptyStateMessage = localization.Relocalize(SeriesEmptyStateMessage);
            SeriesErrorMessage = localization.Relocalize(SeriesErrorMessage);
            SeriesDetailsErrorMessage = localization.Relocalize(SeriesDetailsErrorMessage);
            ImportFeedback = localization.Relocalize(ImportFeedback);
            StartupLoadingMessage = localization.Relocalize(StartupLoadingMessage);

            OnPropertyChanged(nameof(MuteButtonText));
            OnPropertyChanged(nameof(PlayPauseToolTip));
            OnPropertyChanged(nameof(VolumeToolTip));
            OnPropertyChanged(nameof(FavoritesCategorySummary));
            OnPropertyChanged(nameof(FavoriteChannelsCount));
            OnPropertyChanged(nameof(ImportTitle));
            OnPropertyChanged(nameof(SubmitImportButtonText));
            OnPropertyChanged(nameof(PlaylistInputLabel));
            OnPropertyChanged(nameof(PlaylistInputHint));
            OnPropertyChanged(nameof(QuickQualityTitle));

            foreach (var source in Sources)
            {
                source.RefreshLocalizedText();
            }

            MovieCategories.FirstOrDefault(category => category.Id == AllMoviesCategoryId)
                ?.RefreshLocalizedName("AllMovies");
            SeriesCategories.FirstOrDefault(category => category.Id == AllSeriesCategoryId)
                ?.RefreshLocalizedName("AllSeries");

            foreach (var channel in VisibleChannels
                         .Concat(RecentChannels)
                         .Append(SelectedChannel)
                         .Where(channel => channel is not null)
                         .Cast<ChannelItemViewModel>()
                         .DistinctBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase))
            {
                channel.RefreshLocalizedText();
            }

            foreach (var variant in QuickQualityVariants)
            {
                variant.RefreshLocalizedText();
            }

            foreach (var recent in CompactRecentChannels.Append(ResumeLastChannel)
                         .Where(recent => recent is not null)
                         .Cast<RecentChannelItemViewModel>()
                         .DistinctBy(recent => recent.Id, StringComparer.OrdinalIgnoreCase))
            {
                recent.RefreshLocalizedText();
            }

            List<MovieItemViewModel> movies;
            List<SeriesItemViewModel> series;
            lock (_syncRoot)
            {
                movies = _allMovies.Concat(_playlistSearchMovies).Concat(VisibleMovies)
                    .DistinctBy(movie => movie.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                series = _allSeries.Concat(_playlistSearchSeries).Concat(VisibleSeries)
                    .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            foreach (var movie in movies)
            {
                movie.RefreshLocalizedText();
            }

            foreach (var item in series)
            {
                item.RefreshLocalizedText();
            }

            SelectedMovieDetails?.RefreshLocalizedText();
            SelectedSeriesDetails?.RefreshLocalizedText();
            foreach (var item in ContinueWatchingMovies.Concat(ContinueWatchingSeries))
            {
                item.RefreshLocalizedText();
            }

            NotifyMovieStateChanged();
            NotifySeriesStateChanged();
            RefreshEventSportCategoryLabels();
            RefreshEventFilterLabels();
            RefreshEventItems();
            NotifyEventsStateChanged();
        }, null);
    }

    private static bool IsTodayOrTomorrow(SportsEventModel sportsEvent)
    {
        var localDate = sportsEvent.StartUtc.ToLocalTime().Date;
        var today = DateTimeOffset.Now.Date;
        return localDate == today || localDate == today.AddDays(1);
    }

    private static bool IsEventToday(SportsEventModel sportsEvent)
    {
        var localDate = sportsEvent.StartUtc.ToLocalTime().Date;
        var today = DateTimeOffset.Now.Date;
        return localDate == today;
    }

    private static bool IsEventTomorrow(SportsEventModel sportsEvent)
    {
        var localDate = sportsEvent.StartUtc.ToLocalTime().Date;
        var today = DateTimeOffset.Now.Date;
        return localDate == today.AddDays(1);
    }

    private static bool IsEventLiveOrSoon(SportsEventModel sportsEvent)
    {
        var now = DateTimeOffset.UtcNow;
        var start = sportsEvent.StartUtc;
        return now >= start.AddHours(-2) && now <= start.AddHours(3);
    }

    private static string FormatEventTime(DateTimeOffset startUtc)
    {
        var local = startUtc.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var day = local.Date == today
            ? L("Today")
            : local.Date == today.AddDays(1)
                ? L("Tomorrow")
                : local.ToString("d", CultureInfo.CurrentUICulture);
        return $"{day} - {local.ToString("t", CultureInfo.CurrentUICulture)}";
    }

    private static string SportLabel(string sport)
        => sport.ToLowerInvariant() switch
        {
            "football" => L("SportFootball"),
            "basketball" => L("SportBasketball"),
            "tennis" => L("SportTennis"),
            "formula1" => L("SportFormulaOne"),
            "cricket" => L("SportCricket"),
            "rugby" => L("SportRugby"),
            _ => sport,
        };

    private static string L(string key)
        => UiLocalization.Current.GetString(key);

    private static string LF(string key, params object?[] arguments)
        => UiLocalization.Current.Format(key, arguments);

    private static string LocalizePlayerStatus(PlayerStatus status)
        => status.State switch
        {
            PlaybackState.Idle => L("Ready"),
            PlaybackState.Playing => L("Play"),
            PlaybackState.Paused => L("Pause"),
            PlaybackState.Stopped => L("Stopped"),
            PlaybackState.Failed when IsNoSignalFailure(status) => L("NoSignal"),
            PlaybackState.Failed => L("PlaybackFailed"),
            _ => status.Message,
        };

    private static bool IsNoSignalFailure(PlayerStatus status)
        => status.State == PlaybackState.Failed
           && string.Equals(status.ErrorCode, "VLC_ERROR", StringComparison.OrdinalIgnoreCase);

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        if (target is RangeObservableCollection<T> rangeCollection)
        {
            rangeCollection.ReplaceRange(values);
            return;
        }

        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
