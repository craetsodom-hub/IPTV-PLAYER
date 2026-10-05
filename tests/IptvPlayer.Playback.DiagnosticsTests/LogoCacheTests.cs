#if PLAYBACK_DIAGNOSTICS
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IptvPlayer.App.Converters;
using IptvPlayer.App.Services;

internal static class LogoCacheTests
{
    public static Task<int> RunAsync()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await CheckAsync(); completion.TrySetResult(0); }
                catch (Exception exception) { Console.Error.WriteLine(exception); completion.TrySetResult(1); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task CheckAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "IptvPlayer-logo-test-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT");
        Environment.SetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT", root);
        var cachePath = Path.Combine(root, "cache", "logos");
        Directory.CreateDirectory(cachePath);
        var urls = Enumerable.Range(0, 64).Select(i => "https://localhost.invalid/logo-test/" + i + ".png").ToArray();
        try
        {
            foreach (var url in urls)
            {
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var file = Path.Combine(cachePath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img");
                using var stream = File.Create(file); encoder.Save(stream);
            }
            var uiThread = Environment.CurrentManagedThreadId;
            var loaded = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loadedOnUi = false;
            LogoCacheService.Instance.LogoLoaded += (url, bitmap) =>
            {
                if (url != urls[0]) return;
                loadedOnUi = Environment.CurrentManagedThreadId == uiThread;
                loaded.TrySetResult(bitmap);
            };
            if (LogoCacheService.Instance.GetCachedLogo(urls[0]) is not null)
                throw new InvalidOperationException("First disk lookup decoded synchronously");
            var decoded = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (loadedOnUi || !decoded.IsFrozen || !ReferenceEquals(decoded, LogoCacheService.Instance.GetCachedLogo(urls[0])))
                throw new InvalidOperationException("Logo was not decoded off UI and retained as a frozen memory entry");

            var images = urls.Skip(1).Select(_ => new Image()).ToArray();
            for (var i = 0; i < images.Length; i++) ImageAsyncHelper.SetSourceUri(images[i], urls[i + 1]);
            // Recycle a control before its original request can update it.
            ImageAsyncHelper.SetSourceUri(images[0], "  " + urls[0] + "  ");
            for (var retry = 0; retry < 100 && images.Any(image => image.Source is null); retry++) await Task.Delay(50);
            if (images.Any(image => image.Source is not BitmapSource { IsFrozen: true }) || !ReferenceEquals(images[0].Source, decoded))
                throw new InvalidOperationException("A completion was lost, or a recycled image received an old logo");
            Console.WriteLine("PASS: disk logos decode off UI; immediate completions and recycled controls display the correct frozen image");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT", previousRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
#endif
