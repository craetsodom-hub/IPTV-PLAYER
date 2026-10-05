#if PLAYBACK_DIAGNOSTICS
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using IptvPlayer.Presentation.ViewModels;
using IptvPlayer.Application.Services;
using IptvPlayer.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using IptvPlayer.Player.Vlc.Services;
using IptvPlayer.Contracts.Import;

namespace IptvPlayer.App.Views;

public partial class MainWindow
{
    // Integration tests run the application's normal commands and layout methods.
    // This code is absent from normal Release, and does not create a video window.
    private async Task RunPlaybackStabilitySuiteAsync()
    {
        var phases = new List<object>();
        var coverage = new Dictionary<string, int>();
        var unavailable = new List<string>();
        var continuityFailures = new List<string>();
        var hiddenAnimations = new List<object>();
        var fullscreenRounds = new List<object>();
        var frameLossDetected = false;
        string? nativeVlcVersion = null;
        var eventsOnly = Environment.GetCommandLineArgs().Contains("--events-repro-only", StringComparer.Ordinal);
        var windowTransitionsOnly = Environment.GetCommandLineArgs().Contains("--window-transition-suite", StringComparer.Ordinal);
        var stressPlayback = Environment.GetCommandLineArgs().Contains("--stress-playback-suite", StringComparer.Ordinal);
        var arguments = Environment.GetCommandLineArgs();
        var rateArgument = Array.IndexOf(arguments, "--expected-picture-rate");
        var expectedPictureRate = rateArgument >= 0 && rateArgument + 1 < arguments.Length
            && double.TryParse(arguments[rateArgument + 1], out var requestedRate) && requestedRate is >= 1 and <= 120
                ? requestedRate : 50;
        var fixtureArgument = Array.IndexOf(arguments, "--playback-fixture");
        var fixture = fixtureArgument >= 0 && fixtureArgument + 1 < arguments.Length
            ? new Uri(Path.GetFullPath(arguments[fixtureArgument + 1])) : null;
        var suite = Stopwatch.StartNew();
        var folder = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        var output = Path.Combine(folder, "stability-suite.json");
        try
        {
            if (fixtureArgument >= 0)
                throw new InvalidOperationException("Playback validation requires live channels");
            await _diagnosticShellInitialized.Task.WaitAsync(TimeSpan.FromSeconds(120));
            if (!arguments.Contains("--startup-test-only", StringComparer.Ordinal))
                await CheckSkeletonLifecycleAsync();
            var importArgument = Array.IndexOf(arguments, "--import-test-playlist");
            var importFileArgument = Array.IndexOf(arguments, "--import-test-playlist-file");
            var importFile = importFileArgument >= 0 && importFileArgument + 1 < arguments.Length;
            if (importFile || (importArgument >= 0 && importArgument + 1 < arguments.Length))
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT")))
                    throw new InvalidOperationException("Playlist import tests require a separate data profile");
                _viewModel.ActiveImportMode = importFile ? SourceImportMode.M3uFile : SourceImportMode.M3uUrl;
                _viewModel.PlaylistInput = importFile
                    ? Path.GetFullPath(arguments[importFileArgument + 1])
                    : (await File.ReadAllTextAsync(arguments[importArgument + 1])).Trim();
                _viewModel.PlaylistDisplayName = "Playback test playlist";
                _diagnosticStartupPhase = "playlist-import";
                await _viewModel.SubmitImportCommand.ExecuteAsync(null);
                _diagnosticStartupPhase = "playlist-import-presentation";
                if (_viewModel.SelectedSource?.Name != "Playback test playlist")
                    throw new InvalidOperationException("Test playlist import failed");
                Count(importFile ? "local-playlist-file-imported" : "user-supplied-playlist-imported");
            }
            var startupWait = Stopwatch.StartNew();
            while ((_viewModel.VisibleChannels.Count == 0 || _viewModel.IsStartupLoading)
                   && startupWait.Elapsed < TimeSpan.FromSeconds(90))
                await Task.Delay(200);
            var categoryArgument = Array.IndexOf(arguments, "--test-category");
            if (categoryArgument >= 0 && categoryArgument + 1 < arguments.Length)
            {
                var categoryId = arguments[categoryArgument + 1];
                var category = _viewModel.VisibleCategories.FirstOrDefault(c => c.Id == categoryId)
                    ?? throw new InvalidOperationException("Requested test category is unavailable");
                _viewModel.SelectedCategory = category;
                var wait = Stopwatch.StartNew();
                while (!_viewModel.VisibleChannels.Any(c => c.CategoryId == categoryId) && wait.Elapsed < TimeSpan.FromSeconds(60))
                    await Task.Delay(200);
                await Task.Delay(600);
                Count("test-category-" + categoryId);
            }
            var channelArgument = Array.IndexOf(arguments, "--test-channel");
            var requestedChannel = channelArgument >= 0 && channelArgument + 1 < arguments.Length
                ? _viewModel.VisibleChannels.FirstOrDefault(c => c.Id == arguments[channelArgument + 1])
                    ?? throw new InvalidOperationException("Requested test channel is unavailable")
                : null;
            var initialChannel = requestedChannel ?? (fixture is not null ? null : windowTransitionsOnly
                ? await FindSavedTestChannelAsync("xt-4705")
                : _viewModel.VisibleChannels.FirstOrDefault(c => c.Id is "xt-274847" or "xt-32750"));
            if (fixture is not null)
            {
                // The diagnostic playback service supplies the file for normal
                // channel selections. Keep the actual item in the visible list.
                _viewModel.SelectedChannel = _viewModel.VisibleChannels.First();
                await WaitForPlaybackAsync(TimeSpan.FromSeconds(60));
                Count("local-recorded-fixture");
            }
            else if (initialChannel is not null && initialChannel != _viewModel.SelectedChannel)
            {
                await SelectTestChannelAsync(initialChannel);
            }
            await WaitForPlaybackAsync(TimeSpan.FromSeconds(90));
            Count("initial-channel-" + _viewModel.SelectedChannel?.Id);
            await Task.Delay(6000);
            if (arguments.Contains("--loading-ui-suite", StringComparer.Ordinal))
            {
                await RunLoadingUiSuiteAsync();
                return;
            }
            if (arguments.Contains("--minimal-playback-instrumentation", StringComparer.Ordinal))
            {
                // Startup is still measured. During playback, retain only the
                // phase-boundary counters and structural continuity checks.
                _diagnosticStartupTimer?.Stop();
                Count("playback-ui-sampling-disabled");
            }
            if (arguments.Contains("--input-replay-suite", StringComparer.Ordinal))
            {
                await MeasureAsync("input-baseline", () => Task.Delay(20000));
                await MeasureAsync("input-events-details-return-12", async () =>
                {
                    for (var i = 0; i < 12; i++)
                    {
                        _viewModel.SelectEventsSectionCommand.Execute(null);
                        await Task.Delay(500);
                        _viewModel.SelectEventCommand.Execute(_viewModel.TodayEvents.FirstOrDefault() ?? CreateLocalEventFixture());
                        await Task.Delay(150);
                        _viewModel.ReturnFromEventsCommand.Execute(null);
                        if (_viewModel.IsEventsSection) _viewModel.ReturnFromEventsCommand.Execute(null);
                        await Task.Delay(1500);
                    }
                });
                await MeasureAsync("input-after-events-soak", () => Task.Delay(30000));
                Save("completed", null);
                return;
            }
            if (arguments.Contains("--ui-stall-suite", StringComparer.Ordinal))
            {
                await MeasureAsync("dispatcher-stall-baseline", () => Task.Delay(10000));
                await MeasureAsync("dispatcher-stall-with-steady-geometry-5", async () =>
                {
                    for (var i = 0; i < 5; i++)
                    {
                        await Task.Delay(1000);
                        Thread.Sleep(1200); // Deliberate diagnostic-only UI stall.
                    }
                });
                await MeasureAsync("dispatcher-stall-after-fullscreen-layout-4", async () =>
                {
                    for (var i = 0; i < 4; i++)
                    {
                        ToggleFullscreen();
                        RootLayout.UpdateLayout();
                        Thread.Sleep(1200);
                        await Task.Delay(1000);
                    }
                });
                await MeasureAsync("dispatcher-stall-soak", () => Task.Delay(15000));
                Save("completed", null);
                return;
            }
            if (arguments.Contains("--channel-switch-suite", StringComparer.Ordinal))
            {
                var channelIdsArgument = Array.IndexOf(arguments, "--test-channel-ids");
                var channelIds = channelIdsArgument >= 0 && channelIdsArgument + 1 < arguments.Length
                    ? arguments[channelIdsArgument + 1].Split(',') : null;
                var switchChannels = channelIds is null ? _viewModel.VisibleChannels
                    .OrderBy(c => c == _viewModel.SelectedChannel ? 0 : 1).Take(6).ToArray()
                    : channelIds.Select(id => _viewModel.VisibleChannels.FirstOrDefault(c => c.Id == id)
                        ?? throw new InvalidOperationException("Requested switch channel is unavailable")).ToArray();
                if (switchChannels.Length < 2) throw new InvalidOperationException("Channel switch test needs distinct channels");
                var playableChannels = new List<ChannelItemViewModel>();
                await MeasureAsync("before-channel-switches", () => Task.Delay(20000));
                foreach (var channel in switchChannels)
                {
                    try { await SelectTestChannelAsync(channel); }
                    catch (TimeoutException) { unavailable.Add(channel.Id); continue; }
                    Count("playable-channel-switches");
                    await Task.Delay(6000);
                    if (!await MeasureAsync("switch-steady-" + channel.Id, () => Task.Delay(20000), continueOnInputStall: true))
                        continue;
                    var eventsPlaybackContinued = await MeasureAsync("switch-events-details-return-" + channel.Id, async () =>
                    {
                        for (var i = 0; i < 2; i++)
                        {
                            _viewModel.SelectEventsSectionCommand.Execute(null);
                            await Task.Delay(500);
                            _viewModel.SelectEventCommand.Execute(_viewModel.TodayEvents.FirstOrDefault() ?? CreateLocalEventFixture());
                            await Task.Delay(500);
                            _viewModel.ReturnFromEventsCommand.Execute(null);
                            if (_viewModel.IsEventsSection) _viewModel.ReturnFromEventsCommand.Execute(null);
                            await Task.Delay(1500);
                        }
                    }, continueOnInputStall: true);
                    if (eventsPlaybackContinued) playableChannels.Add(channel);
                }
                if (playableChannels.Count < 2) throw new InvalidOperationException("Too few playable channels for switch testing");
                for (var i = 0; i < 16; i++)
                {
                    _viewModel.SelectedChannel = playableChannels[i % playableChannels.Count];
                    await Task.Delay(75);
                }
                await WaitForPlaybackAsync(TimeSpan.FromSeconds(60));
                await Task.Delay(6000);
                Count("rapid-channel-selections-16");
                await MeasureAsync("after-channel-switches-soak", () => Task.Delay(180000));
                await CheckHiddenAnimationsAsync("after-channel-switches");
                Save("completed", null);
                return;
            }
            if (arguments.Contains("--startup-test-only", StringComparer.Ordinal))
            {
                await MeasureAsync("startup-playback", () => Task.Delay(30000));
                await CheckHiddenAnimationsAsync("after-startup");
                Save("completed", null);
                return;
            }
            if (arguments.Contains("--live-search-continuity-suite", StringComparer.Ordinal))
            {
                await MeasureAsync("search-baseline", () => Task.Delay(10000));
                await MeasureAsync("channel-search-without-selection-12", CheckSearchContinuityAsync);
                await MeasureAsync("search-after-clear-soak", () => Task.Delay(15000));
                Save("completed", null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--decoration-cost-suite", StringComparer.Ordinal))
            {
                var highRateChannel = await FindSavedTestChannelAsync("xt-4705")
                    ?? throw new InvalidOperationException("Missing high rate test channel");
                if (fixture is null) using (var previousMedia = _boundMediaPlayer?.Media)
                {
                    await SelectTestChannelAsync(highRateChannel);
                }
                await Task.Delay(6000);
                var decoration = Descendants(this).OfType<Button>()
                    .First(b => ReferenceEquals(b.Style, FindResource("EventsButtonStyle")));
                await MeasureAsync("decoration-visible-before", () => Task.Delay(30000));
                var originalVisibility = decoration.ReadLocalValue(VisibilityProperty);
                try
                {
                    // Hidden preserves the button's layout space and video geometry.
                    decoration.SetCurrentValue(VisibilityProperty, Visibility.Hidden);
                    await Task.Delay(1000);
                    await MeasureAsync("decoration-hidden", () => Task.Delay(30000));
                }
                finally
                {
                    if (originalVisibility == DependencyProperty.UnsetValue) decoration.ClearValue(VisibilityProperty);
                    else decoration.SetValue(VisibilityProperty, originalVisibility);
                }
                await Task.Delay(1000);
                await MeasureAsync("decoration-visible-after", () => Task.Delay(30000));
                Save("completed", null);
                return;
            }
            if (windowTransitionsOnly || stressPlayback || Environment.GetCommandLineArgs().Contains("--extended-playback-suite", StringComparer.Ordinal))
            {
                var originalChannel = _viewModel.SelectedChannel;
                var highRateChannel = fixture is null ? requestedChannel ?? await FindSavedTestChannelAsync("xt-4705") : _viewModel.SelectedChannel;
                if (highRateChannel is null) throw new InvalidOperationException("Missing high rate test channel");
                if (fixture is null && originalChannel?.Id == highRateChannel.Id)
                    originalChannel = _viewModel.VisibleChannels.FirstOrDefault(c => c.Id != highRateChannel.Id);
                if (fixture is not null)
                {
                    highRateChannel = _viewModel.SelectedChannel!;
                    originalChannel = _viewModel.VisibleChannels.First(c => c.Id != highRateChannel.Id);
                }
                if (fixture is null) using (var previousMedia = _boundMediaPlayer?.Media)
                {
                    await SelectTestChannelAsync(highRateChannel);
                }
                await Task.Delay(6000);
                await MeasureAsync("high-rate-baseline", () => Task.Delay(windowTransitionsOnly ? 10000 : 30000));
                if (!windowTransitionsOnly)
                {
                await MeasureAsync(stressPlayback ? "high-rate-events-details-return-60" : "high-rate-events-details-return-8", async () =>
                {
                    for (var i = 0; i < (stressPlayback ? 60 : 8); i++)
                    {
                        _viewModel.SelectEventsSectionCommand.Execute(null);
                        await Task.Delay(400);
                        var item = _viewModel.TodayEvents.FirstOrDefault() ?? CreateLocalEventFixture();
                        _viewModel.SelectEventCommand.Execute(item);
                        Count("high-rate-event-details");
                        await Task.Delay(i % 2 == 0 ? 100 : 1500);
                        // Exercise both the detail Back and section Back commands.
                        _viewModel.ReturnFromEventsCommand.Execute(null);
                        if (_viewModel.IsEventsSection) _viewModel.ReturnFromEventsCommand.Execute(null);
                        await Task.Delay(1500);
                    }
                });
                }
                await MeasureAsync(stressPlayback ? "high-rate-fullscreen-12" : "high-rate-fullscreen-4", async () =>
                {
                    for (var i = 0; i < (stressPlayback ? 12 : 4); i++)
                    {
                        await FullscreenRoundtripAsync(1500);
                    }
                });
                await MeasureAsync(stressPlayback ? "high-rate-resize-12" : "high-rate-resize-4", async () =>
                {
                    for (var i = 0; i < (stressPlayback ? 12 : 4); i++)
                    {
                        WindowState = WindowState.Normal; Width = 1150 + i % 4 * 50; Height = 780;
                        await Task.Delay(500); WindowState = WindowState.Maximized; await Task.Delay(1500);
                    }
                });
                if (windowTransitionsOnly)
                {
                    WindowState = WindowState.Normal;
                    await Task.Delay(3000);
                    await MeasureAsync("normal-window-fullscreen-3", async () =>
                    {
                        for (var i = 0; i < 3; i++) await FullscreenRoundtripAsync(1500);
                    });
                    WindowState = WindowState.Maximized;
                    await Task.Delay(3000);
                    await MeasureAsync("window-transition-soak", () => Task.Delay(30000));
                    Save("completed", null);
                    return;
                }
                await MeasureAsync("high-rate-playlist-search", async () =>
                {
                    _viewModel.ChannelSearchText = fixture is null ? "TVP" : _viewModel.SelectedChannel!.Name;
                    await Task.Delay(15000);
                    Count("global-search-results-" + _viewModel.VisibleChannels.Count);
                });
                if (originalChannel is not null)
                {
                    _viewModel.ChannelSearchText = string.Empty;
                    var listWait = Stopwatch.StartNew();
                    while ((!_viewModel.VisibleChannels.Any(c => c.Id == originalChannel.Id)
                        || !_viewModel.VisibleChannels.Any(c => c.Id == highRateChannel.Id))
                        && listWait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(200);
                    await Task.Delay(600);
                    originalChannel = _viewModel.VisibleChannels.First(c => c.Id == originalChannel.Id);
                    highRateChannel = _viewModel.VisibleChannels.First(c => c.Id == highRateChannel.Id);
                    using var previousMedia = _boundMediaPlayer?.Media;
                    for (var i = 0; i < 8; i++)
                    {
                        _viewModel.SelectedChannel = i % 2 == 0 ? originalChannel : highRateChannel;
                        await Task.Delay(75);
                    }
                    await WaitForPlaybackAsync(TimeSpan.FromSeconds(60), previousMedia?.NativeReference ?? IntPtr.Zero);
                    await Task.Delay(6000);
                    Count("rapid-channel-selections-8");
                }
                await MeasureAsync("high-rate-soak-after-rapid-switches", () => Task.Delay(stressPlayback ? 600000 : 120000));
                await CheckHiddenAnimationsAsync("after-high-rate-navigation");
                Save("completed", null);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--animation-probe-only", StringComparer.Ordinal))
            {
                await CheckHiddenAnimationsAsync("baseline");
                _viewModel.SelectEventsSectionCommand.Execute(null);
                await Task.Delay(1500);
                var item = _viewModel.TodayEvents.FirstOrDefault();
                if (item is not null) _viewModel.SelectEventCommand.Execute(item);
                await Task.Delay(1500);
                _viewModel.SelectedSportsEvent = null;
                _viewModel.ReturnFromEventsCommand.Execute(null);
                await Task.Delay(3000);
                await CheckHiddenAnimationsAsync("after-events");
                Save("completed", null);
                return;
            }
            if (eventsOnly)
            {
                var target = await FindPreviouslyFailingChannelAsync();
                if (target is not null && target != _viewModel.SelectedChannel)
                {
                    await SelectTestChannelAsync(target);
                    await Task.Delay(6000);
                }
            }
            await MeasureAsync("baseline", () => Task.Delay(30000));
            await CheckHiddenAnimationsAsync("baseline");
            await MeasureAsync("events-return-12", async () =>
            {
                for (var i = 0; i < 12; i++)
                {
                    _viewModel.SelectEventsSectionCommand.Execute(null);
                    await Task.Delay(i % 2 == 0 ? 150 : 1500);
                    _viewModel.SelectedSportsEvent = null;
                    _viewModel.ReturnFromEventsCommand.Execute(null);
                    await Task.Delay(2500);
                }
            });
            await MeasureAsync("event-details-return-4", async () =>
            {
                for (var i = 0; i < 4; i++)
                {
                    _viewModel.SelectEventsSectionCommand.Execute(null);
                    await Task.Delay(1500);
                    var sportsEvent = _viewModel.TodayEvents.FirstOrDefault();
                    if (sportsEvent is null)
                    {
                        // An explicit local fixture exercises the real details/index
                        // path when today's external feed has no matching event.
                        var model = new SportsEventModel("local-stability-event", "Playback test", "football", null,
                            DateTimeOffset.UtcNow, SportsEventStatus.Confirmed,
                            [new EventBroadcastModel("TF1", [], "FR", true)], null, null, null);
                        sportsEvent = new SportsEventItemViewModel(model, "Test", "Football", [], false);
                        Count("synthetic-event-details");
                    }
                    if (sportsEvent is not null)
                    {
                        _viewModel.SelectEventCommand.Execute(sportsEvent);
                        Count("event-details");
                    }
                    await Task.Delay(i % 2 == 0 ? 100 : 2000);
                    _viewModel.SelectedSportsEvent = null;
                    _viewModel.ReturnFromEventsCommand.Execute(null);
                    await Task.Delay(3000);
                }
            });
            await CheckHiddenAnimationsAsync("after-events");
            if (eventsOnly)
            {
                await MeasureAsync("soak-after-events", () => Task.Delay(60000));
                Save("completed", null);
                return;
            }
            await MeasureAsync("channel-search-without-selection-12", CheckSearchContinuityAsync);
            await MeasureAsync("movies-series-return-6", async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    _viewModel.SelectMoviesSectionCommand.Execute(null);
                    await Task.Delay(250);
                    _viewModel.SelectSeriesSectionCommand.Execute(null);
                    await Task.Delay(250);
                    _viewModel.SelectLiveTvSectionCommand.Execute(null);
                    await Task.Delay(3000);
                }
            });
            await MeasureAsync("fullscreen-roundtrip-6", async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    await FullscreenRoundtripAsync(2500);
                }
            });
            await MeasureAsync("window-resize-maximize-6", async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    WindowState = WindowState.Normal;
                    Width = i % 2 == 0 ? 1150 : 1300;
                    Height = i % 2 == 0 ? 760 : 850;
                    await Task.Delay(1500);
                    WindowState = WindowState.Maximized;
                    await Task.Delay(1500);
                }
            });
            await MeasureAsync("smooth-scroll-20", async () =>
            {
                var scrollViewer = Descendants(this).OfType<ScrollViewer>()
                    .FirstOrDefault(v => v.IsVisible && v.ScrollableHeight > 0);
                if (scrollViewer is null) return;
                Count("scrollable-list");
                for (var i = 0; i < 20; i++)
                {
                    QueueSmoothScroll(scrollViewer, i % 2 == 0 ? 128 : -128);
                    await Task.Delay(350);
                }
            });
            await MeasureAsync("controls-and-display-mode-6", async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    ShowWindowedPlayerControls();
                    TogglePlayerDisplayMode();
                    await Task.Delay(1000);
                    HidePlayerControlsOverlay();
                    await Task.Delay(1000);
                }
            });
            await MeasureAsync("pause-resume-3", async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    await _viewModel.TogglePlayPauseCommand.ExecuteAsync(null);
                    await Task.Delay(1000);
                    await _viewModel.TogglePlayPauseCommand.ExecuteAsync(null);
                    await Task.Delay(2500);
                }
            });
            await MeasureAsync("minimize-restore-3", async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    WindowState = WindowState.Minimized;
                    await Task.Delay(1500);
                    WindowState = WindowState.Maximized;
                    await Task.Delay(2500);
                }
            });
            await MeasureAsync("events-sport-filter-return-12", async () =>
            {
                for (var i = 0; i < 12; i++)
                {
                    _viewModel.SelectEventsSectionCommand.Execute(null);
                    var sport = _viewModel.EventSportCategories.ElementAtOrDefault(i % 6);
                    if (sport is not null) _viewModel.SelectEventSportCategoryCommand.Execute(sport);
                    await Task.Delay(300);
                    _viewModel.SelectedSportsEvent = null;
                    _viewModel.ReturnFromEventsCommand.Execute(null);
                    await Task.Delay(1500);
                }
            });
            // Include the exact channel with heavy frame loss in the original test.
            var channels = _viewModel.VisibleChannels.Where(c => c != _viewModel.SelectedChannel).Take(2).ToList();
            var previousFailureChannel = await FindPreviouslyFailingChannelAsync();
            if (previousFailureChannel is not null) channels.Add(previousFailureChannel);
            var lastGood = _viewModel.SelectedChannel;
            foreach (var channel in channels)
            {
                try
                {
                    await SelectTestChannelAsync(channel);
                }
                catch (TimeoutException)
                {
                    unavailable.Add(channel.Id);
                    Save("running", null);
                    continue;
                }
                lastGood = channel;
                Count("playable-channel-switches");
                await Task.Delay(6000);
                await MeasureAsync("channel-stable-" + channel.Id, () => Task.Delay(20000));
                await MeasureAsync("channel-events-return-" + channel.Id, async () =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        _viewModel.SelectEventsSectionCommand.Execute(null);
                        await Task.Delay(1500);
                        _viewModel.SelectedSportsEvent = null;
                        _viewModel.ReturnFromEventsCommand.Execute(null);
                        await Task.Delay(2500);
                    }
                });
            }
            if (_viewModel.SelectedChannel != lastGood && lastGood is not null)
            {
                await SelectTestChannelAsync(lastGood);
                await Task.Delay(6000);
            }
            await MeasureAsync("soak-after-navigation", () => Task.Delay(120000));
            await CheckHiddenAnimationsAsync("after-navigation");
            Save("completed", null);
            _logger.LogInformation("Playback test stability suite completed. Phases={Phases}; DurationSeconds={DurationSeconds:0}", phases.Count, suite.Elapsed.TotalSeconds);
        }
        catch (Exception e)
        {
            // Exception text can include provider credentials; persist only its type.
            Save("failed", e is InvalidOperationException && e.Message.StartsWith("Loading UI:", StringComparison.Ordinal)
                ? e.Message : e.GetType().Name);
            _logger.LogInformation("Playback test stability suite failed. ErrorType={ErrorType}", e.GetType().Name);
            if (e is InvalidOperationException)
                _logger.LogInformation("Playback test continuity failure. Fullscreen={Fullscreen}; WindowState={WindowState}; SelectedChannelId={SelectedChannelId}; MediaState={MediaState}; MediaTimeMs={MediaTimeMs}",
                    _isFullscreen, WindowState, _viewModel.SelectedChannel?.Id, _boundMediaPlayer?.State, _boundMediaPlayer?.Time);
        }

        async Task CheckSearchContinuityAsync()
        {
            var playing = _viewModel.SelectedChannel ?? throw new InvalidOperationException("Missing playing channel");
            var other = _viewModel.VisibleChannels.First(c => c.Id != playing.Id);
            var noMatch = "no-channel-" + Guid.NewGuid().ToString("N");
            for (var round = 0; round < 4; round++)
            {
                foreach (var query in new[] { other.Name, noMatch, string.Empty })
                {
                    _viewModel.ChannelSearchText = query;
                    var wait = Stopwatch.StartNew();
                    bool ResultsReady() => query == noMatch ? _viewModel.VisibleChannels.Count == 0
                        : query.Length == 0 ? _viewModel.VisibleChannels.Any(c => c.Id == playing.Id)
                        : _viewModel.VisibleChannels.Any(c => c.Id == other.Id)
                          && _viewModel.VisibleChannels.All(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || (c.CurrentProgram?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                              || (c.NextProgram?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
                    // Wait past the search debounce and WPF selection callbacks.
                    await Task.Delay(250);
                    while (!ResultsReady() && wait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
                    await Task.Delay(750);
                    if (!ResultsReady() || !ReferenceEquals(_viewModel.SelectedChannel, playing))
                        throw new InvalidOperationException("Channel search changed playback or did not settle");
                    Count("search-preserved-playing-channel");
                }
            }
        }

        async Task<bool> MeasureAsync(string name, Func<Task> operation, bool continueOnInputStall = false)
        {
            var player = _boundMediaPlayer ?? throw new InvalidOperationException("No active player");
            using var media = await Task.Run(() => player.Media)
                ?? throw new InvalidOperationException("No active media");
            var before = await Task.Run(() => (Time: player.Time, Statistics: media.Statistics));
            var initial = before.Statistics;
            var startedTick = Stopwatch.GetTimestamp();
            var watch = Stopwatch.StartNew();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var handle = PlayerView.Handle;
            var mediaTime = before.Time;
            var hidden = _diagnosticVideoHideCount;
            var zeroSized = _diagnosticVideoZeroSizeCount;
            var positionUpdates = PlayerView.AppliedPositionUpdates;
            var nativeSizeMessages = PlayerView.NativeSizeMessages;
            var nativePositionMessages = PlayerView.NativePositionMessages;
            var duplicateUpdates = PlayerView.DuplicatePositionUpdatesSkipped;
            var aspectUpdates = _diagnosticAspectRatioUpdates;
            _logger.LogInformation("Playback test stability phase started. Phase={Phase}", name);
            await operation();
            await Task.Delay(1000); // VLC publishes counters in batches.
            // Native input getters can wait for VLC locks. Keep diagnostic
            // snapshots off the dispatcher, just like the readiness check.
            var after = await Task.Run(() =>
            {
                using var current = player.Media;
                return (Time: player.Time, Statistics: media.Statistics,
                    Hwnd: player.Hwnd, MediaReference: current?.NativeReference ?? IntPtr.Zero,
                    State: player.State, IsPlaying: player.IsPlaying);
            });
            var end = after.Statistics;
            var renderIntervals = (_nativePlayerBridge as VlcPlaybackService)?.ReadNativeRenderIntervals(startedTick) ?? [];
            Array.Sort(renderIntervals);
            var videoParentIsAppWindow = handle != IntPtr.Zero
                && GetDiagnosticVideoRoot(handle, 2 /* GA_ROOT */) == new WindowInteropHelper(this).Handle;
            var embedded = after.Hwnd == handle && handle != IntPtr.Zero
                && PlayerView.Handle == handle && videoParentIsAppWindow
                && ReferenceEquals(player, _boundMediaPlayer);
            var expectedTopLeft = PlayerView.PointToScreen(new Point(0, 0));
            var expectedBottomRight = PlayerView.PointToScreen(new Point(PlayerView.ActualWidth, PlayerView.ActualHeight));
            var geometryMatches = GetWindowRect(handle, out var nativeBounds)
                && Math.Abs(nativeBounds.Left - expectedTopLeft.X) <= 2
                && Math.Abs(nativeBounds.Top - expectedTopLeft.Y) <= 2
                && Math.Abs(nativeBounds.Right - expectedBottomRight.X) <= 2
                && Math.Abs(nativeBounds.Bottom - expectedBottomRight.Y) <= 2;
            var overlayGeometry = ReadDiagnosticOverlayGeometry();
            var result = new
            {
                phase = name, elapsedSeconds = watch.Elapsed.TotalSeconds,
                inputBytes = end.ReadBytes - initial.ReadBytes,
                demuxBytes = end.DemuxReadBytes - initial.DemuxReadBytes,
                decodedVideo = end.DecodedVideo - initial.DecodedVideo,
                decodedAudio = end.DecodedAudio - initial.DecodedAudio,
                playedAudio = end.PlayedAudioBuffers - initial.PlayedAudioBuffers,
                displayed = end.DisplayedPictures - initial.DisplayedPictures,
                counterDisplayRate = (end.DisplayedPictures - initial.DisplayedPictures) / watch.Elapsed.TotalSeconds,
                lost = end.LostPictures - initial.LostPictures,
                initialLostPictures = initial.LostPictures, finalLostPictures = end.LostPictures,
                nativePrepareIntervals = renderIntervals.Length,
                nativePrepareGapMaxMs = renderIntervals.LastOrDefault(),
                nativePrepareGapP99Ms = renderIntervals.Length == 0 ? 0 : renderIntervals[(int)((renderIntervals.Length - 1) * 0.99)],
                nativePrepareGapsOver40Ms = renderIntervals.Count(interval => interval > 40),
                nativeTimingEvents = (_nativePlayerBridge as VlcPlaybackService)?.ReadNativeTimingEvents(startedTick) ?? [],
                fullscreenTimings = ReadDiagnosticFullscreenTimings(startedTick),
                lostAudio = end.LostAudioBuffers - initial.LostAudioBuffers,
                demuxCorrupted = end.DemuxCorrupted - initial.DemuxCorrupted,
                demuxDiscontinuity = end.DemuxDiscontinuity - initial.DemuxDiscontinuity,
                mediaTimeAdvanced = after.Time > mediaTime,
                mediaTimeProgressMs = after.Time - mediaTime,
                mediaUnchanged = after.MediaReference == media.NativeReference, embedded, videoParentIsAppWindow,
                videoHideEvents = _diagnosticVideoHideCount - hidden,
                videoZeroSizeEvents = _diagnosticVideoZeroSizeCount - zeroSized,
                nativePositionUpdates = PlayerView.AppliedPositionUpdates - positionUpdates,
                nativeSizeMessages = PlayerView.NativeSizeMessages - nativeSizeMessages,
                nativePositionMessages = PlayerView.NativePositionMessages - nativePositionMessages,
                postedPositionUpdatesApplied = PlayerView.PostedPositionUpdatesApplied,
                postedPositionGeneration = PlayerView.PostedPositionGeneration,
                postedPositionGenerationApplied = PlayerView.PostedPositionGenerationApplied,
                postedPositionError = PlayerView.PostedPositionError,
                duplicatePositionUpdatesSkipped = PlayerView.DuplicatePositionUpdatesSkipped - duplicateUpdates,
                aspectRatioUpdates = _diagnosticAspectRatioUpdates - aspectUpdates,
                nativeGeometryMatches = geometryMatches,
                overlayGeometry,
                desktopWindowTransitionsDisabled = _nativeWindowTransitionsDisabled,
                state = after.State.ToString(),
                isPlaying = after.IsPlaying,
                cpuPercent = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100
            };
            phases.Add(result);
            frameLossDetected |= result.lost != 0 || result.lostAudio != 0;
            var inputProgressed = result.mediaTimeAdvanced && result.displayed > 0;
            var streamContinued = inputProgressed && result.mediaUnchanged;
            if (!streamContinued) continuityFailures.Add(name);
            Save("running", null);
            _logger.LogInformation("Playback test stability phase finished. Phase={Phase}; Displayed={Displayed}; Lost={Lost}; Embedded={Embedded}; State={State}",
                name, result.displayed, result.lost, embedded, result.state);
            if (!embedded || !geometryMatches || overlayGeometry.Any(item => !item.LogicalBoundsMatch || !item.OwnerMatches)
                || (!streamContinued && !continueOnInputStall))
                throw new InvalidOperationException("Playback continuity failed");
            if (arguments.Contains("--native-host-worker-posted", StringComparer.Ordinal)
                && (result.postedPositionError != 0
                    || result.postedPositionGeneration != result.postedPositionGenerationApplied))
                throw new InvalidOperationException("Posted video geometry did not settle");
            if (name == "high-rate-baseline" && result.counterDisplayRate < expectedPictureRate * 0.9)
                throw new InvalidOperationException("High rate motion was reduced");
            if (name.Contains("fullscreen", StringComparison.Ordinal) && result.videoHideEvents != 0)
                throw new InvalidOperationException("Fullscreen hid the video host");
            return streamContinued;
        }
        void Save(string state, string? error)
        {
            nativeVlcVersion ??= (_nativePlayerBridge as VlcPlaybackService)?.ReadNativeVersion();
            File.WriteAllText(output, JsonSerializer.Serialize(new { state, error,
                passed = state == "completed" && error is null && !frameLossDetected && continuityFailures.Count == 0,
                frameLossDetected, nativeVlcVersion, expectedPictureRate,
                processPriority = Process.GetCurrentProcess().PriorityClass.ToString(),
                uiRendering = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString(),
                startupUi = new { samples = _diagnosticStartupUiTicks, maxGapMs = _diagnosticStartupMaxUiGapMs,
                    gapsOver75Ms = _diagnosticStartupUiGapsOver75Ms,
                    firstWpfRenderingCallbackAfterMarkerMs = _diagnosticFirstRenderTick == 0 ? 0 : Stopwatch.GetElapsedTime(_diagnosticStartupMarkerTick, _diagnosticFirstRenderTick).TotalMilliseconds,
                    maxUiGapAfterFirstRenderingCallbackMs = _diagnosticStartupMaxUiGapAfterFirstRenderMs },
                durationSeconds = suite.Elapsed.TotalSeconds, phases, coverage, unavailable, continuityFailures, hiddenAnimations, fullscreenRounds }, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Count(string name) => coverage[name] = coverage.GetValueOrDefault(name) + 1;
        SportsEventItemViewModel CreateLocalEventFixture()
        {
            Count("synthetic-event-details");
            var model = new SportsEventModel("local-stability-event", "Playback test", "football", null,
                DateTimeOffset.UtcNow, SportsEventStatus.Confirmed,
                [new EventBroadcastModel("TF1", [], "FR", true)], null, null, null);
            return new SportsEventItemViewModel(model, "Test", "Football", [], false);
        }
        async Task FullscreenRoundtripAsync(int delay)
        {
            var captureRound = arguments.Contains("--capture-fullscreen-rounds", StringComparer.Ordinal);
            // Longer settling keeps VLC's batched counters from being attributed
            // to the next direction. Native reads run off the UI dispatcher.
            if (captureRound) delay = Math.Max(delay, 3000);
            using var roundMedia = captureRound ? _boundMediaPlayer?.Media : null;
            var beforeCounters = roundMedia is null ? default : await Task.Run(() => roundMedia.Statistics);
            var enteredTick = Stopwatch.GetTimestamp();
            var window = new WindowInteropHelper(this).Handle;
            if (!GetWindowRect(window, out var before) || !GetWindowRect(PlayerView.Handle, out var videoBefore))
                throw new InvalidOperationException("Missing window bounds");
            var beforeState = WindowState;
            var layoutOnly = arguments.Contains("--fullscreen-layout-only", StringComparer.Ordinal);
            ToggleFullscreen();
            await Task.Delay(delay);
            var enteredCounters = roundMedia is null ? default : await Task.Run(() => roundMedia.Statistics);
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!_isFullscreen || !GetMonitorInfo(monitor, ref info)
                || !GetWindowRect(window, out var fullscreen)
                || !SameBounds(fullscreen, layoutOnly ? before : info.Monitor)
                || !GetWindowRect(PlayerView.Handle, out var fullscreenVideo)
                || (!layoutOnly && !SameBounds(fullscreenVideo, info.Monitor))
                || (layoutOnly && (fullscreenVideo.Left < before.Left || fullscreenVideo.Top < before.Top
                    || fullscreenVideo.Right > before.Right || fullscreenVideo.Bottom > before.Bottom
                    || fullscreenVideo.Right - fullscreenVideo.Left < videoBefore.Right - videoBefore.Left)))
            {
                LogGeometry("fill-monitor", before, videoBefore);
                throw new InvalidOperationException("Fullscreen does not fill the monitor");
            }
            var hud = _fullscreenHudWindow;
            var controlsEnabled = !arguments.Contains("--suppress-fullscreen-hud", StringComparer.Ordinal);
            if (controlsEnabled && hud?.IsVisible != true)
                throw new InvalidOperationException("Fullscreen controls were not shown");
            var enterTimings = captureRound ? ReadDiagnosticFullscreenTimings(enteredTick) : [];
            var exitedTick = Stopwatch.GetTimestamp();
            ToggleFullscreen();
            await Task.Delay(delay);
            if (roundMedia is not null)
            {
                var exitedCounters = await Task.Run(() => roundMedia.Statistics);
                fullscreenRounds.Add(new
                {
                    round = fullscreenRounds.Count + 1,
                    initialWindowState = beforeState.ToString(),
                    enterLost = enteredCounters.LostPictures - beforeCounters.LostPictures,
                    exitLost = exitedCounters.LostPictures - enteredCounters.LostPictures,
                    enterDisplayed = enteredCounters.DisplayedPictures - beforeCounters.DisplayedPictures,
                    exitDisplayed = exitedCounters.DisplayedPictures - enteredCounters.DisplayedPictures,
                    enterTimings,
                    exitTimings = ReadDiagnosticFullscreenTimings(exitedTick),
                });
            }
            if (_isFullscreen || WindowState != beforeState || !GetWindowRect(window, out var restored)
                || !SameBounds(before, restored) || !GetWindowRect(PlayerView.Handle, out var videoRestored)
                || !SameBounds(videoBefore, videoRestored))
            {
                LogGeometry("restore-window", before, videoBefore);
                throw new InvalidOperationException("Fullscreen did not restore window geometry");
            }
            if (controlsEnabled && !arguments.Contains("--recreate-fullscreen-hud-baseline", StringComparer.Ordinal))
            {
                if (!ReferenceEquals(hud, _fullscreenHudWindow) || hud?.IsVisible != false)
                    throw new InvalidOperationException("Fullscreen controls were recreated or left visible");
                Count("fullscreen-controls-reused-and-hidden");
            }
            Count(layoutOnly ? "fullscreen-layout-only-and-restore-geometry" : "fullscreen-monitor-and-restore-geometry");

            void LogGeometry(string reason, NativeRect expectedWindow, NativeRect expectedVideo)
            {
                GetWindowRect(window, out var actualWindow);
                GetWindowRect(PlayerView.Handle, out var actualVideo);
                _logger.LogInformation("Playback test geometry failure. Reason={Reason}; Fullscreen={Fullscreen}; WindowState={WindowState}; ExpectedWindow={ExpectedWindow}; ActualWindow={ActualWindow}; ExpectedVideo={ExpectedVideo}; ActualVideo={ActualVideo}",
                    reason, _isFullscreen, WindowState,
                    new[] { expectedWindow.Left, expectedWindow.Top, expectedWindow.Right, expectedWindow.Bottom },
                    new[] { actualWindow.Left, actualWindow.Top, actualWindow.Right, actualWindow.Bottom },
                    new[] { expectedVideo.Left, expectedVideo.Top, expectedVideo.Right, expectedVideo.Bottom },
                    new[] { actualVideo.Left, actualVideo.Top, actualVideo.Right, actualVideo.Bottom });
            }
        }
        static bool SameBounds(NativeRect left, NativeRect right)
            => Math.Abs(left.Left - right.Left) <= 2 && Math.Abs(left.Top - right.Top) <= 2
                && Math.Abs(left.Right - right.Right) <= 2 && Math.Abs(left.Bottom - right.Bottom) <= 2;
        async Task CheckSkeletonLifecycleAsync()
        {
            // Exercise the actual four XAML owner styles and their nested templates
            // in this app, before playback starts. Transparency does not stop clocks.
            var templateNames = new[] { "OnDemandCatalogSkeletonTemplate", "OnDemandFeaturedSkeletonTemplate" };
            var owners = Descendants(this).OfType<ContentControl>()
                .Where(control => templateNames.Any(key => ReferenceEquals(control.ContentTemplate, FindResource(key))))
                .ToArray();
            if (owners.Length != 4) throw new InvalidOperationException("Missing skeleton owner styles");
            var harness = new Canvas { Width = 360, Height = 318, ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false };
            RootLayout.Children.Add(harness);
            Grid.SetColumnSpan(harness, 3);
            Panel.SetZIndex(harness, 340);
            try
            {
                foreach (var owner in owners)
                {
                    var active = new { IsMovieSkeletonVisible = true, IsSeriesSkeletonVisible = true,
                        IsMovieFeaturedSkeletonVisible = true, IsSeriesFeaturedSkeletonVisible = true };
                    var idle = new { IsMovieSkeletonVisible = false, IsSeriesSkeletonVisible = false,
                        IsMovieFeaturedSkeletonVisible = false, IsSeriesFeaturedSkeletonVisible = false };
                    var sample = new ContentControl { Width = 360, Height = 318,
                        Content = active, DataContext = active, Style = owner.Style,
                        ContentTemplate = owner.ContentTemplate };
                    harness.Children.Add(sample);
                    sample.UpdateLayout();
                    await Task.Delay(300);
                    if (Descendants(sample).OfType<IptvPlayer.App.Controls.SmoothLoadingIndicator>().Any())
                        throw new InvalidOperationException("Loading UI: Movies/Series skeleton contains a spinner");
                    var transforms = Descendants(sample).OfType<Border>()
                        .Select(border => (border.Background as LinearGradientBrush)?.RelativeTransform)
                        .OfType<TranslateTransform>().ToArray();
                    if (!sample.IsVisible || transforms.Length == 0 || !transforms.Any(t => t.HasAnimatedProperties))
                        throw new InvalidOperationException("Loading UI: skeleton shimmer did not start");
                    var before = transforms.Select(t => t.X).ToArray();
                    await Task.Delay(230);
                    if (!transforms.Where((t, i) => Math.Abs(t.X - before[i]) > 0.01).Any())
                        throw new InvalidOperationException("Loading UI: skeleton shimmer did not move");
                    Count("visible-skeleton-shimmer-running-no-spinner");
                    sample.DataContext = idle;
                    sample.Content = idle;
                    await Task.Delay(230);
                    var inactive = Descendants(sample).OfType<Border>()
                        .Select(border => (border.Background as LinearGradientBrush)?.RelativeTransform)
                        .OfType<TranslateTransform>().ToArray();
                    if (sample.IsVisible || inactive.Any(t => t.HasAnimatedProperties))
                        throw new InvalidOperationException("Loading UI: idle skeleton animation kept running");
                    Count("idle-skeleton-clocks-stopped");
                    harness.Children.Remove(sample);
                }
            }
            finally { RootLayout.Children.Remove(harness); }
        }
        async Task CheckHiddenAnimationsAsync(string phase)
        {
            var candidates = Descendants(this).Concat(_fullscreenHudWindow is null
                    ? Array.Empty<DependencyObject>() : Descendants(_fullscreenHudWindow))
                .OfType<IptvPlayer.App.Controls.SmoothLoadingIndicator>().Where(e => !e.IsVisible).ToArray();
            await Task.Delay(230);
            var moving = 0;
            foreach (var candidate in candidates)
                if ((await candidate.ReadAnimationSnapshotAsync()).Active) moving++;
            hiddenAnimations.Add(new { phase, hiddenRotatingControls = candidates.Length, stillAnimating = moving });
            Save("running", null);
            if (moving != 0) throw new InvalidOperationException("Hidden loading animation kept running");
        }
        Task<ChannelItemViewModel?> FindPreviouslyFailingChannelAsync() => FindSavedTestChannelAsync("xt-32754");
        async Task<ChannelItemViewModel?> FindSavedTestChannelAsync(string channelId)
        {
            var source = _viewModel.SelectedSource;
            if (source is null) return null;
            var catalog = ((App)System.Windows.Application.Current).DiagnosticServices.GetRequiredService<CatalogOrchestrator>();
            foreach (var category in await catalog.GetCategoriesAsync(source.Id))
            {
                var raw = await catalog.GetChannelsAsync(source.Id, category.Id);
                var target = raw.FirstOrDefault(c => c.Id == channelId);
                if (target is null) continue;
                Count("test-channel-found-" + channelId);
                return ChannelItemViewModel.FromModel(target, target.IsFavorite);
            }
            return null;
        }
        async Task SelectTestChannelAsync(ChannelItemViewModel channel)
        {
            if (!_viewModel.VisibleChannels.Any(c => c.Id == channel.Id))
            {
                _viewModel.ChannelSearchText = channel.Name;
                var wait = Stopwatch.StartNew();
                while (!_viewModel.VisibleChannels.Any(c => c.Id == channel.Id) && wait.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(200);
                await Task.Delay(600); // Let filtering and list selection settle.
            }
            var visible = _viewModel.VisibleChannels.FirstOrDefault(c => c.Id == channel.Id)
                ?? throw new TimeoutException("Test channel was not found in the normal channel list");
            using var previous = _boundMediaPlayer?.Media;
            var changed = _viewModel.SelectedChannel?.Id != visible.Id;
            _viewModel.SelectedChannel = visible;
            await WaitForPlaybackAsync(TimeSpan.FromSeconds(60), changed ? previous?.NativeReference ?? IntPtr.Zero : IntPtr.Zero);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr GetDiagnosticVideoRoot(IntPtr window, uint flags);

    private sealed record DiagnosticOverlayGeometry(string Name, bool Visible, bool OwnerMatches,
        bool LogicalBoundsMatch, int Left, int Top, int Width, int Height);

    private DiagnosticOverlayGeometry[] ReadDiagnosticOverlayGeometry()
    {
        var results = new List<DiagnosticOverlayGeometry>();
        foreach (var (name, window) in new (string, Window?)[]
        {
            ("controls", _playerControlsOverlayWindow),
            ("fullscreen-controls", _fullscreenHudWindow),
            ("action-indicator", _playerActionIndicatorWindow),
        })
        {
            if (window is null) continue;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) continue;
            var dpi = VisualTreeHelper.GetDpi(window);
            var matches = GetWindowRect(handle, out var bounds)
                && double.IsFinite(window.Left) && double.IsFinite(window.Top)
                && double.IsFinite(window.Width) && double.IsFinite(window.Height)
                && Math.Abs(bounds.Left - window.Left * dpi.DpiScaleX) <= 2
                && Math.Abs(bounds.Top - window.Top * dpi.DpiScaleY) <= 2
                && Math.Abs(bounds.Right - bounds.Left - window.Width * dpi.DpiScaleX) <= 2
                && Math.Abs(bounds.Bottom - bounds.Top - window.Height * dpi.DpiScaleY) <= 2;
            results.Add(new(name, window.IsVisible, ReferenceEquals(window.Owner, this), matches,
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        }
        return results.ToArray();
    }

    private async Task WaitForPlaybackAsync(TimeSpan timeout, IntPtr previousMedia = default)
    {
        var arguments = Environment.GetCommandLineArgs();
        var fixtureArgument = Array.IndexOf(arguments, "--playback-fixture");
        var expectedInput = fixtureArgument >= 0 && fixtureArgument + 1 < arguments.Length
            ? new Uri(Path.GetFullPath(arguments[fixtureArgument + 1])) : _viewModel.SelectedChannel?.StreamUri;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var player = _boundMediaPlayer;
            if (player is not null && !_viewModel.IsStartupLoading)
            {
                // Native getters can wait for an input lock during startup. Keep
                // the recorder from adding its own pause to the measured UI.
                var ready = await Task.Run(() =>
                {
                    if (player.State != VLCState.Playing) return false;
                    using var media = player.Media;
                    return media is not null && media.NativeReference != previousMedia
                        && media.Statistics.DisplayedPictures > 0 && player.Time > 0
                        && expectedInput is not null && Uri.TryCreate(media.Mrl, UriKind.Absolute, out var actualInput)
                        && actualInput.Equals(expectedInput);
                });
                if (ready && ReferenceEquals(player, _boundMediaPlayer) && !_viewModel.IsStartupLoading) return;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("No playing video");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
#endif
