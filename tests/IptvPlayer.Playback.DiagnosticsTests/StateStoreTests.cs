#if PLAYBACK_DIAGNOSTICS
using System.Collections;
using System.Collections.Concurrent;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static class StateStoreTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "IptvPlayer-state-test-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT");
        Environment.SetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT", root);
        try
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var collection = new ObservedCollection();
            var context = new ObservedContext();
            var userStore = new JsonUserStateStore(NullLogger<JsonUserStateStore>.Instance);
            var demandStore = new JsonOnDemandStateStore(NullLogger<JsonOnDemandStateStore>.Instance);
            Task[] tasks;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                // Queue writes without waiting, as rapid UI selections do.
                tasks = Enumerable.Range(0, 20).Select(i => userStore.SaveAsync(UserSessionState.Empty with
                { LastChannelId = "channel-" + i, FavoriteChannelIds = collection })).ToArray();
                tasks = tasks.Append(demandStore.SaveAsync(OnDemandState.Empty with
                { WatchlistMovieIds = collection })).ToArray();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            await Task.WhenAll(tasks);
            var state = await userStore.LoadAsync();
            var demand = await demandStore.LoadAsync();
            if (context.Posts != 0 || collection.Threads.Contains(uiThread))
                throw new InvalidOperationException("State persistence used the UI context");
            if (state.LastChannelId != "channel-19" || !state.FavoriteChannelIds.SequenceEqual(["fixture-item"])
                || !demand.WatchlistMovieIds.SequenceEqual(["fixture-item"]))
                throw new InvalidOperationException("State writes were reordered or failed to roundtrip");
            var demandPath = Path.Combine(root, "state", "on-demand-state.json");
            if (!ProtectedCatalogFile.IsProtected(demandPath))
                throw new InvalidOperationException("On-demand state lost Windows user protection");
            Console.WriteLine("PASS: state persistence stays off the UI context, preserves write order, and roundtrips protected state");
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT", previousRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ObservedContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class ObservedCollection : IReadOnlyCollection<string>
    {
        public readonly ConcurrentBag<int> Threads = [];
        public int Count { get { Threads.Add(Environment.CurrentManagedThreadId); return 1; } }
        public IEnumerator<string> GetEnumerator()
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            yield return "fixture-item";
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
#endif
