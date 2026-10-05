using System.Reflection;
using IptvPlayer.Application.Services;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Import;
using IptvPlayer.Contracts.Player;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class EventsNavigationTests
{
    public static async Task RunAsync(List<string> failures)
    {
        foreach (var test in new Func<Task>[] { LeavingEventsCancelsFeedAsync, PlaylistIndexIsLazyAndCanceledAsync, ChannelMatchingStopsWhenCanceledAsync, LiveSearchStillScansOtherCategoriesAsync, MatcherCacheSurvivesNavigationAndChangesSourceAsync, ChannelListRefreshPreservesPlaybackAsync, QualityLookupDoesNotRunOnCallerAsync, CanceledCategoryLoadKeepsNewLoadingStateAsync, LeavingOnDemandCancelsLoadAndAllowsReentryAsync, InterruptedOnDemandCategoryResumesAsync, SourceImportWorkStartsOffCallerAsync, ReopeningUnchangedEventsKeepsCardsAsync, CatalogLoadsDoNotRunOnCallerAsync })
        {
            try
            {
                await test();
                Console.WriteLine("PASS: " + test.Method.Name);
            }
            catch (Exception e) { failures.Add("Events navigation " + test.Method.Name + ": " + e.Message); }
        }
    }

    private static async Task CatalogLoadsDoNotRunOnCallerAsync()
    {
        foreach (var categories in new[] { true, false })
        {
            var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var categoryResult = new TaskCompletionSource<IReadOnlyList<CategoryModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var channelResult = new TaskCompletionSource<IReadOnlyList<ChannelModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tokenForwarded = false;
            var catalog = Proxy<ISourceCatalogService>((method, args) =>
            {
                if (method.Name != (categories ? "GetCategoriesAsync" : "GetChannelsAsync")) return null;
                tokenForwarded = args!.Last() is CancellationToken token && token.CanBeCanceled;
                entered.TrySetResult(Environment.CurrentManagedThreadId);
                return categories ? (object)categoryResult.Task : channelResult.Task;
            });
            var vm = Shell(catalog, Proxy<ISportsEventService>());
            var source = new SourceItemViewModel(Guid.NewGuid(), "Fixture", SourceKind.M3uFile,
                "fixture.m3u", "", "", "");
            Set(vm, "selectedSource", source);
            var caller = 0;
            var invoked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                try
                {
                    caller = Environment.CurrentManagedThreadId;
                    var arguments = categories ? new object[] { source }
                        : new object[] { source, new CategoryItemViewModel("chosen", "Chosen") };
                    invoked.TrySetResult((Task)typeof(MainShellViewModel)
                        .GetMethod(categories ? "LoadCategoriesAsync" : "LoadChannelsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(vm, arguments)!);
                }
                catch (Exception e) { invoked.TrySetException(e); }
            }) { IsBackground = true }.Start();
            var load = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            categoryResult.TrySetResult([]);
            channelResult.TrySetResult([]);
            await load;
            if (worker == caller || !tokenForwarded || vm.IsLoading)
                throw new InvalidOperationException("Cached " + (categories ? "categories" : "channels")
                    + " were processed on the interface caller, lost cancellation, or retained the loader");
        }
    }

    private static Task ReopeningUnchangedEventsKeepsCardsAsync()
    {
        var vm = Shell(Proxy<ISourceCatalogService>(), Proxy<ISportsEventService>());
        Set(vm, "_eventsLoaded", true);
        var model = new SportsEventModel("event", "Original match", "football", null,
            DateTimeOffset.UtcNow, SportsEventStatus.Confirmed, [], null, null, null);
        Set(vm, "_eventModels", new List<SportsEventModel> { model });
        vm.SelectEventsSectionCommand.Execute(null);
        var original = vm.TodayEvents.Single();
        vm.ReturnFromEventsCommand.Execute(null);
        var resets = 0;
        vm.TodayEvents.CollectionChanged += (_, _) => resets++;
        vm.SelectEventsSectionCommand.Execute(null);
        if (!ReferenceEquals(original, vm.TodayEvents.Single()) || resets != 0)
            throw new InvalidOperationException("Returning to unchanged Events rebuilt its cards");
        var changed = model with { Title = "Updated match" };
        typeof(MainShellViewModel).GetMethod("ApplySportsEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [new SportsEventModel[] { changed }]);
        if (ReferenceEquals(original, vm.TodayEvents.Single()) || vm.TodayEvents.Single().Title != "Updated match")
            throw new InvalidOperationException("A changed Events feed retained an outdated card");
        vm.ReturnFromEventsCommand.Execute(null);
        return Task.CompletedTask;
    }

    private static async Task SourceImportWorkStartsOffCallerAsync()
    {
        var methods = new[] { "ImportAsync", "UpdateSourceAsync", "RefreshSourceAsync", "DeleteSourceAsync" };
        var entered = methods.ToDictionary(name => name, _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
        var sourceResult = new TaskCompletionSource<SourceImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var tokensForwarded = 0;
        var service = Proxy<ISourceImportService>((method, args) =>
        {
            if (methods.Contains(method.Name))
            {
                if (args!.Last() is CancellationToken token && token == cancellation.Token) Interlocked.Increment(ref tokensForwarded);
                entered[method.Name].TrySetResult(Environment.CurrentManagedThreadId);
                return method.Name == "DeleteSourceAsync" ? (object)deleteResult.Task : sourceResult.Task;
            }
            return null;
        });
        var importer = new SourceImportOrchestrator(service, NullLogger<SourceImportOrchestrator>.Instance);
        var caller = 0;
        var invoked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            caller = Environment.CurrentManagedThreadId;
            var sourceId = Guid.NewGuid();
            invoked.TrySetResult(Task.WhenAll(
                importer.ImportAsync(new SourceImportRequest(SourceImportMode.M3uUrl, "https://example.invalid"), cancellationToken: cancellation.Token),
                importer.UpdateAsync(new SourceUpdateRequest(sourceId, "Fixture"), cancellationToken: cancellation.Token),
                importer.RefreshAsync(sourceId, cancellationToken: cancellation.Token),
                importer.DeleteAsync(sourceId, cancellation.Token)));
        }) { IsBackground = true }.Start();
        var work = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workers = await Task.WhenAll(entered.Values.Select(signal => signal.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        sourceResult.TrySetResult(SourceImportResult.Failed("Fixture"));
        deleteResult.TrySetResult(true);
        await work;
        if (workers.Any(thread => thread == caller) || tokensForwarded != 4)
            throw new InvalidOperationException("Playlist processing ran on its caller or lost cancellation");
    }

    private static async Task InterruptedOnDemandCategoryResumesAsync()
    {
        foreach (var movies in new[] { true, false })
        {
            var firstMovies = new TaskCompletionSource<IReadOnlyList<MovieModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextMovies = new TaskCompletionSource<IReadOnlyList<MovieModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSeries = new TaskCompletionSource<IReadOnlyList<SeriesModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextSeries = new TaskCompletionSource<IReadOnlyList<SeriesModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = Signal();
            var calls = 0;
            var oldToken = CancellationToken.None;
            string? resumedCategory = null;
            var catalog = Proxy<ISourceCatalogService>((method, args) =>
            {
                if (method.Name != (movies ? "GetMoviesAsync" : "GetSeriesAsync")) return null;
                if (++calls == 1)
                {
                    oldToken = (CancellationToken)args![2]!;
                    return movies ? (object)firstMovies.Task : firstSeries.Task;
                }
                resumedCategory = (string?)args![1];
                started.TrySetResult();
                return movies ? (object)nextMovies.Task : nextSeries.Task;
            });
            var vm = Shell(catalog, Proxy<ISportsEventService>());
            var source = new SourceItemViewModel(Guid.NewGuid(), "Fixture", SourceKind.XtreamCodes,
                "https://example.invalid", "", "", "");
            var category = new CategoryItemViewModel("chosen-category", "Chosen category");
            Set(vm, "selectedSource", source);
            Set(vm, "_liveLoadedSourceId", source.Id);
            Set(vm, movies ? "_moviesLoadedSourceId" : "_seriesLoadedSourceId", source.Id);
            Set(vm, movies ? "selectedMovieCategory" : "selectedSeriesCategory", category);
            Set(vm, "activeSection", movies ? ShellSection.Movies : ShellSection.Series);
            var oldLoad = (Task)typeof(MainShellViewModel).GetMethod(movies ? "ReloadMoviesForCategoryAsync" : "ReloadSeriesForCategoryAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [source, category])!;
            try
            {
                vm.SelectLiveTvSectionCommand.Execute(null);
                if (!oldToken.IsCancellationRequested) throw new InvalidOperationException("Hidden on-demand category retained its request");
                if (movies) vm.SelectMoviesSectionCommand.Execute(null); else vm.SelectSeriesSectionCommand.Execute(null);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                firstMovies.TrySetResult([]); firstSeries.TrySetResult([]);
                await oldLoad;
                if (resumedCategory != category.Id || !(movies ? vm.IsMovieCatalogLoading : vm.IsSeriesCatalogLoading))
                    throw new InvalidOperationException("Interrupted category was not resumed with its own loading indicator");
                vm.SelectLiveTvSectionCommand.Execute(null);
            }
            finally
            {
                firstMovies.TrySetResult([]); nextMovies.TrySetResult([]);
                firstSeries.TrySetResult([]); nextSeries.TrySetResult([]);
                await oldLoad;
            }
        }
    }

    private static async Task LeavingOnDemandCancelsLoadAndAllowsReentryAsync()
    {
        foreach (var movies in new[] { true, false })
        {
            var first = new TaskCompletionSource<IReadOnlyList<CategoryModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<IReadOnlyList<CategoryModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = Signal();
            var calls = 0;
            CancellationToken oldToken = default;
            var catalog = Proxy<ISourceCatalogService>((method, args) =>
            {
                if (method.Name != (movies ? "GetMovieCategoriesAsync" : "GetSeriesCategoriesAsync")) return null;
                if (++calls == 1) { oldToken = (CancellationToken)args![1]!; return first.Task; }
                secondStarted.TrySetResult();
                return second.Task;
            });
            var vm = Shell(catalog, Proxy<ISportsEventService>());
            var source = new SourceItemViewModel(Guid.NewGuid(), "Fixture", SourceKind.XtreamCodes,
                "https://example.invalid", "", "", "");
            Set(vm, "selectedSource", source);
            Set(vm, "_liveLoadedSourceId", source.Id);
            Set(vm, "activeSection", movies ? ShellSection.Movies : ShellSection.Series);
            var oldLoad = (Task)typeof(MainShellViewModel).GetMethod(movies ? "LoadMovieCatalogAsync" : "LoadSeriesCatalogAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [source])!;
            try
            {
                vm.SelectLiveTvSectionCommand.Execute(null);
                var canceled = oldToken.IsCancellationRequested;
                if (movies) vm.SelectMoviesSectionCommand.Execute(null); else vm.SelectSeriesSectionCommand.Execute(null);
                if (!canceled) throw new InvalidOperationException("Leaving " + (movies ? "Movies" : "Series") + " kept the catalog request running");
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                first.TrySetResult([]);
                await oldLoad;
                if (!(movies ? vm.IsMovieCatalogLoading : vm.IsSeriesCatalogLoading))
                    throw new InvalidOperationException("Old on-demand completion cleared the new request's loader");
                vm.SelectLiveTvSectionCommand.Execute(null);
            }
            finally { first.TrySetResult([]); second.TrySetResult([]); await oldLoad; }
        }
    }

    private static async Task CanceledCategoryLoadKeepsNewLoadingStateAsync()
    {
        var first = new TaskCompletionSource<IReadOnlyList<ChannelModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IReadOnlyList<ChannelModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = Proxy<ISourceCatalogService>((method, args) => method.Name == "GetChannelsAsync"
            ? (string)args![1]! == "first" ? first.Task : second.Task : null);
        var vm = Shell(catalog, Proxy<ISportsEventService>());
        var source = new SourceItemViewModel(Guid.NewGuid(), "Fixture", SourceKind.M3uUrl,
            "https://example.invalid", "", "", "");
        Set(vm, "selectedSource", source);
        Task Load(string id) => (Task)typeof(MainShellViewModel)
            .GetMethod("LoadChannelsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [source, new CategoryItemViewModel(id, id)])!;
        var oldLoad = Load("first");
        var newLoad = Load("second");
        first.TrySetResult([]); // Completion arrives after the category was changed.
        await oldLoad;
        var newerStillLoading = vm.IsLoading;
        second.TrySetResult([]);
        await newLoad;
        if (!newerStillLoading || vm.IsLoading)
            throw new InvalidOperationException("A canceled category load cleared the newer load's loading indicator");
    }

    private static async Task QualityLookupDoesNotRunOnCallerAsync()
    {
        var caller = 0;
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<ChannelModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = Proxy<ISourceCatalogService>((method, _) =>
        {
            if (method.Name != "GetChannelVariantsAsync") return null;
            // A warmed catalog may do all of its matching synchronously before
            // returning a Task. Record where that work starts, then hold it open.
            entered.TrySetResult(Environment.CurrentManagedThreadId);
            return release.Task;
        });
        var vm = Shell(catalog, Proxy<ISportsEventService>());
        var source = new SourceItemViewModel(Guid.NewGuid(), "Fixture", SourceKind.M3uUrl,
            "https://example.invalid", "", "", "");
        var channel = ChannelItemViewModel.FromModel(new ChannelModel("first", "sports", "FR | TF1 FHD",
            new Uri("https://example.invalid/first"), null, null, null, null, null, null, null, null, null, false), false);
        Set(vm, "selectedSource", source);
        Set(vm, "selectedChannel", channel);
        var invoked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A dedicated caller thread cannot be reused by Task.Run after an await.
        new Thread(() =>
        {
            try
            {
                caller = Environment.CurrentManagedThreadId;
                invoked.TrySetResult((Task)typeof(MainShellViewModel)
                    .GetMethod("EnsureQuickQualityVariantsLoadedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, [source, channel])!);
            }
            catch (Exception e) { invoked.TrySetException(e); }
        }) { IsBackground = true }.Start();
        var lookup = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var worker = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult([channel.ToModel()]);
        await lookup;
        if (worker == caller) throw new InvalidOperationException("The playlist quality scan ran on the channel-selection caller");
        if (vm.QuickQualityVariants.All(variant => variant.Id != channel.Id))
            throw new InvalidOperationException("Background quality lookup lost the selected channel option");
    }

    private static async Task ChannelListRefreshPreservesPlaybackAsync()
    {
        var plays = 0;
        var player = Proxy<IPlaybackService>((method, _) =>
        {
            if (method.Name == "PlayAsync") Interlocked.Increment(ref plays);
            return null;
        });
        var vm = Shell(Proxy<ISourceCatalogService>(), Proxy<ISportsEventService>(), player);
        var source = new SourceItemViewModel(Guid.NewGuid(), "Fixture", SourceKind.M3uUrl, "https://example.invalid", "", "", "");
        ChannelItemViewModel Channel(string id, string name) => ChannelItemViewModel.FromModel(new ChannelModel(
            id, "sports", name, new Uri("https://example.invalid/" + id), null, null, null, null, null, null, null, null, null, false), false);
        var original = Channel("first", "First channel");
        var clone = Channel("first", "First channel");
        var second = Channel("second", "Second channel");
        Set(vm, "selectedSource", source);
        Set(vm, "selectedChannel", original);
        Set(vm, "_eventsMatchedSourceId", source.Id);
        ((List<ChannelItemViewModel>)Get(vm, "_eventPlaylistChannels")!).AddRange([clone, second]);
        ((List<ChannelItemViewModel>)Get(vm, "_allChannels")!).AddRange([clone, second]);
        using var liveRequest = new CancellationTokenSource();
        Set(vm, "_livePlaybackCts", liveRequest);
        Set(vm, "_currentPlaybackState", PlaybackState.Playing);
        vm.IsNativeVideoSurfaceVisible = true;
        vm.VisibleChannels.Add(original);
        // WPF's two-way SelectedItem binding can clear selection on a Reset.
        // Reproduce that callback synchronously during the collection update.
        vm.VisibleChannels.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) vm.SelectedChannel = null;
        };
        async Task Filter(string name) => await (Task)typeof(MainShellViewModel)
            .GetMethod("ApplyChannelFilterAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [name, CancellationToken.None, false])!;
        await Filter("First channel");
        await Task.Delay(100);
        if (!ReferenceEquals(vm.SelectedChannel, original) || !ReferenceEquals(vm.VisibleChannels.Single(), original)
            || liveRequest.IsCancellationRequested || !vm.IsNativeVideoSurfaceVisible || plays != 0)
            throw new InvalidOperationException("Refreshing the same channel cleared its selection, hid video, or restarted playback");
        await Filter("Second channel");
        await Task.Delay(100);
        if (!ReferenceEquals(vm.SelectedChannel, original) || vm.VisibleChannels.Single().Id != "second"
            || liveRequest.IsCancellationRequested || !vm.IsNativeVideoSurfaceVisible || plays != 0)
            throw new InvalidOperationException("Searching for another channel changed the playing stream without a selection");
        await Filter("No matching channel");
        await Task.Delay(100);
        if (vm.VisibleChannels.Count != 0 || !ReferenceEquals(vm.SelectedChannel, original)
            || liveRequest.IsCancellationRequested || !vm.IsNativeVideoSurfaceVisible || plays != 0)
            throw new InvalidOperationException("An empty search interrupted the playing stream");
        await Filter(string.Empty);
        await Task.Delay(100);
        if (vm.VisibleChannels.Count != 2 || !ReferenceEquals(vm.SelectedChannel, original)
            || !ReferenceEquals(vm.VisibleChannels[0], original) || plays != 0)
            throw new InvalidOperationException("Clearing the search changed the playing channel");
        vm.SelectedChannel = second;
        await Task.Delay(100);
        if (vm.SelectedChannel?.Id != "second" || plays != 1)
            throw new InvalidOperationException("An explicit channel selection no longer starts playback exactly once");
        vm.SelectedChannel = null;
    }

    private static async Task MatcherCacheSurvivesNavigationAndChangesSourceAsync()
    {
        var firstSource = new SourceItemViewModel(Guid.NewGuid(), "First", SourceKind.M3uUrl,
            "https://example.invalid/first", "", "", "");
        var secondSource = new SourceItemViewModel(Guid.NewGuid(), "Second", SourceKind.M3uUrl,
            "https://example.invalid/second", "", "", "");
        ChannelModel Channel(string id) => new(id, "sports", "TF1", new Uri("https://example.invalid/" + id),
            null, null, null, null, null, null, null, null, null, false);
        var catalog = Proxy<ISourceCatalogService>((method, args) => method.Name switch
        {
            "GetCategoriesAsync" => (object)Task.FromResult<IReadOnlyList<CategoryModel>>([new("sports", "Sports", 0)]),
            "GetChannelsAsync" => Task.FromResult<IReadOnlyList<ChannelModel>>(
                [Channel((Guid)args![0]! == firstSource.Id ? "first-channel" : "second-channel")]),
            _ => null
        });
        var vm = Shell(catalog, Proxy<ISportsEventService>());
        Set(vm, "selectedSource", firstSource);
        Set(vm, "_liveLoadedSourceId", firstSource.Id);
        Set(vm, "_eventsLoaded", true);
        var model = new SportsEventModel("event", "Match", "football", null, DateTimeOffset.UtcNow,
            SportsEventStatus.Confirmed, [new EventBroadcastModel("TF1", [], "FR", true)], null, null, null);
        Set(vm, "_eventModels", new List<SportsEventModel> { model });
        ((List<CategoryItemViewModel>)Get(vm, "_allCategories")!).Add(new("sports", "Sports"));
        await SelectAndCheckAsync("first-channel");
        var firstIndex = Get(vm, "_eventPlaylistChannelIndex");
        vm.ReturnFromEventsCommand.Execute(null);
        vm.ReturnFromEventsCommand.Execute(null);
        await SelectAndCheckAsync("first-channel");
        if (firstIndex is null || !ReferenceEquals(firstIndex, Get(vm, "_eventPlaylistChannelIndex")))
            throw new InvalidOperationException("Navigating back rebuilt the unchanged channel-name index");
        vm.ReturnFromEventsCommand.Execute(null);
        vm.ReturnFromEventsCommand.Execute(null);
        vm.SelectedSource = secondSource;
        for (var i = 0; i < 50 && !vm.VisibleChannels.Any(c => c.Id == "second-channel"); i++) await Task.Delay(100);
        await SelectAndCheckAsync("second-channel");
        if (ReferenceEquals(firstIndex, Get(vm, "_eventPlaylistChannelIndex")))
            throw new InvalidOperationException("Changing playlists reused the old channel-name index");
        vm.ReturnFromEventsCommand.Execute(null);
        vm.ReturnFromEventsCommand.Execute(null);

        async Task SelectAndCheckAsync(string expectedChannel)
        {
            vm.SelectEventsSectionCommand.Execute(null);
            if (vm.TodayEvents.Count == 0) throw new InvalidOperationException("Fixture event missing");
            var selected = vm.TodayEvents[0];
            vm.SelectEventCommand.Execute(selected);
            for (var i = 0; i < 50 && !(selected.ChannelOptions.Count == 1
                && selected.ChannelOptions[0].Channel.Id == expectedChannel); i++) await Task.Delay(100);
            if (selected.ChannelOptions.Count != 1 || selected.ChannelOptions[0].Channel.Id != expectedChannel)
                throw new InvalidOperationException($"Event choices for {expectedChannel} contained {selected.ChannelOptions.Count} channels; notification: {vm.NotificationMessage}");
        }
    }

    private static async Task LiveSearchStillScansOtherCategoriesAsync()
    {
        var channel = new ChannelModel("other-category", "second", "Unique Sports", new Uri("https://example.invalid/stream"),
            null, null, null, null, null, null, null, null, null, false);
        var catalog = Proxy<ISourceCatalogService>((method, args) => method.Name == "GetChannelsAsync"
            ? Task.FromResult<IReadOnlyList<ChannelModel>>((string)args![1]! == "second" ? [channel] : []) : null);
        var vm = Shell(catalog, Proxy<ISportsEventService>());
        var source = new SourceItemViewModel(Guid.NewGuid(), "Test", SourceKind.M3uUrl,
            "https://example.invalid/playlist", "", "", "");
        Set(vm, "selectedSource", source);
        Set(vm, "_liveLoadedSourceId", source.Id);
        ((List<CategoryItemViewModel>)Get(vm, "_allCategories")!).AddRange([new("first", "First"), new("second", "Second")]);
        vm.ChannelSearchText = "Unique";
        for (var i = 0; i < 50 && !vm.VisibleChannels.Any(c => c.Id == channel.Id); i++) await Task.Delay(100);
        if (!vm.VisibleChannels.Any(c => c.Id == channel.Id) || vm.ActiveSection != ShellSection.LiveTv)
            throw new InvalidOperationException("Live TV search no longer finds channels in other categories");
    }

    private static Task ChannelMatchingStopsWhenCanceledAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var channels = new CancelingChannelList(cancellation);
        var model = new SportsEventModel("event", "Match", "football", null, DateTimeOffset.UtcNow,
            SportsEventStatus.Confirmed, [], null, null, null);
        try
        {
            EventChannelMatcher.MatchAll([model], channels, cancellation.Token);
            throw new InvalidOperationException("Canceled channel matching completed normally");
        }
        catch (OperationCanceledException)
        {
            if (channels.ReadCount > 21)
                throw new InvalidOperationException("Canceled matching kept scanning the playlist");
        }
        return Task.CompletedTask;
    }

    private sealed class CancelingChannelList(CancellationTokenSource cancellation) : IReadOnlyList<ChannelItemViewModel>
    {
        public int Count => 10000;
        public int ReadCount { get; private set; }
        public ChannelItemViewModel this[int index] => new(index.ToString(), "sports", "Sports " + index,
            new Uri("https://example.invalid/" + index), null, null, null, null, null, null, null, null, null, false);
        public IEnumerator<ChannelItemViewModel> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                ReadCount++;
                if (ReadCount == 20) cancellation.Cancel();
                yield return this[i];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task LeavingEventsCancelsFeedAsync()
    {
        var started = Signal();
        var canceled = Signal();
        var tokens = new List<CancellationToken>();
        var sports = Proxy<ISportsEventService>((method, args) => method.Name switch
        {
            "LoadCachedAsync" => (object)Task.FromResult<SportsEventFeedModel?>(null),
            "RefreshAsync" => BlockFeed((CancellationToken)args![0]!),
            _ => null
        });
        async Task<SportsEventFeedModel> BlockFeed(CancellationToken token)
        {
            tokens.Add(token);
            using var registration = token.Register(() => canceled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unexpected feed completion");
        }
        var vm = Shell(Proxy<ISourceCatalogService>(), sports);
        vm.SelectEventsSectionCommand.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.ReturnFromEventsCommand.Execute(null);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (vm.ActiveSection != ShellSection.LiveTv || vm.IsEventsLoading || vm.IsEventsRefreshing)
            throw new InvalidOperationException("Leaving Events retained its loading state");

        started = Signal(); canceled = Signal();
        vm.SelectEventsSectionCommand.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (tokens.Count != 2 || !tokens[0].IsCancellationRequested || tokens[1].IsCancellationRequested)
            throw new InvalidOperationException("Reopening Events reused canceled work");
        vm.ReturnFromEventsCommand.Execute(null);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task PlaylistIndexIsLazyAndCanceledAsync()
    {
        var indexStarted = Signal();
        var indexCanceled = Signal();
        var channelCalls = 0;
        var catalog = Proxy<ISourceCatalogService>((method, args) => method.Name == "GetChannelsAsync"
            ? BlockChannels((CancellationToken)args![2]!) : null);
        async Task<IReadOnlyList<ChannelModel>> BlockChannels(CancellationToken token)
        {
            Interlocked.Increment(ref channelCalls);
            using var registration = token.Register(() => indexCanceled.TrySetResult());
            indexStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Array.Empty<ChannelModel>();
        }
        var vm = Shell(catalog, Proxy<ISportsEventService>());
        var source = new SourceItemViewModel(Guid.NewGuid(), "Test", SourceKind.M3uUrl,
            "https://example.invalid/playlist", "", "", "");
        // Seed an already loaded playlist/feed; navigation uses the public commands.
        Set(vm, "selectedSource", source);
        Set(vm, "_liveLoadedSourceId", source.Id);
        Set(vm, "_eventsLoaded", true);
        var model = new SportsEventModel("event", "Match", "football", null, DateTimeOffset.UtcNow,
            SportsEventStatus.Confirmed, [], null, null, null);
        Set(vm, "_eventModels", new List<SportsEventModel> { model });
        ((List<CategoryItemViewModel>)Get(vm, "_allCategories")!).Add(new("sports", "Sports"));
        vm.SelectEventsSectionCommand.Execute(null);
        await Task.Delay(100);
        if (channelCalls != 0 || vm.TodayEvents.Count != 1)
            throw new InvalidOperationException("Opening the Events list eagerly scanned the playlist");
        vm.SelectEventCommand.Execute(vm.TodayEvents[0]);
        await indexStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Back first leaves the event details, then the Events section.
        vm.ReturnFromEventsCommand.Execute(null);
        vm.ReturnFromEventsCommand.Execute(null);
        await indexCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (vm.ActiveSection != ShellSection.LiveTv || vm.IsSelectedEventChannelsLoading)
            throw new InvalidOperationException("Hidden Events retained its channel loader");
    }

    private static MainShellViewModel Shell(ISourceCatalogService catalog, ISportsEventService sports, IPlaybackService? player = null)
        => new(new CatalogOrchestrator(catalog, NullLogger<CatalogOrchestrator>.Instance),
            new SourceImportOrchestrator(Proxy<ISourceImportService>(), NullLogger<SourceImportOrchestrator>.Instance),
            new SessionOrchestrator(Proxy<IUserStateStore>()), Proxy<IOnDemandStateStore>(), sports,
            new PlaybackOrchestrator(player ?? Proxy<IPlaybackService>(), NullLogger<PlaybackOrchestrator>.Instance),
            Proxy<IConfiguration>(), NullLogger<MainShellViewModel>.Instance);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static object? Get(object target, string name)
        => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var value = DispatchProxy.Create<T, NavigationStub>();
        ((NavigationStub)(object)value).Handler = handler;
        return value;
    }
}

public class NavigationStub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var result = Handler?.Invoke(method!, args);
        if (result is not null) return result;
        var type = method!.ReturnType;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var valueType = type.GetGenericArguments()[0];
            object? value = valueType == typeof(ChannelEpgModel) ? ChannelEpgModel.Empty
                : valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                ? Array.CreateInstance(valueType.GetGenericArguments()[0], 0)
                : valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(valueType).Invoke(null, [value]);
        }
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
