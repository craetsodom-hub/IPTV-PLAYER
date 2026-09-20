using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IptvPlayer.App.Services;

/// <summary>
/// High-performance channel logo cache providing persistent local disk caching,
/// in-memory frozen bitmap caching, bounded decode dimensions, and throttled background preloading.
/// </summary>
public sealed class LogoCacheService
{
    private static readonly Lazy<LogoCacheService> _instance = new(() => new LogoCacheService());
    public static LogoCacheService Instance => _instance.Value;

    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, BitmapSource?> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloadThrottle = new(6, 6);
    private readonly HttpClient _httpClient;

    public event Action<string, BitmapSource>? LogoLoaded;

    public LogoCacheService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhoseIptv",
            "Cache",
            "Logos");

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch
        {
            // Fallback to temp if LocalApplicationData has permission restrictions
            _cacheDirectory = Path.Combine(Path.GetTempPath(), "WhoseIptv", "Cache", "Logos");
            Directory.CreateDirectory(_cacheDirectory);
        }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 10,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// Attempts to retrieve a cached logo from memory or disk instantly.
    /// If not cached, queues an asynchronous background download and returns null.
    /// </summary>
    public BitmapSource? GetCachedLogo(string? uriString, int decodeWidth = 80)
    {
        if (string.IsNullOrWhiteSpace(uriString) || !Uri.TryCreate(uriString.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var normalizedUrl = uri.AbsoluteUri;

        // 1. Check in-memory cache (0 ms)
        if (_memoryCache.TryGetValue(normalizedUrl, out var cachedBitmap))
        {
            return cachedBitmap;
        }

        // 2. Check local disk cache (< 0.1 ms)
        var cacheFilePath = GetDiskCachePath(normalizedUrl);
        if (File.Exists(cacheFilePath))
        {
            var diskBitmap = LoadBitmapFromFile(cacheFilePath, decodeWidth);
            if (diskBitmap is not null)
            {
                _memoryCache[normalizedUrl] = diskBitmap;
                return diskBitmap;
            }

            // If file was corrupt/0 bytes, delete it so it can be re-fetched
            try { File.Delete(cacheFilePath); } catch { }
        }

        // 3. Queue asynchronous background download
        QueueDownload(normalizedUrl, decodeWidth);
        return null;
    }

    /// <summary>
    /// Preloads a collection of channel logo URLs in the background so they are ready on disk and in memory.
    /// </summary>
    public void PreloadLogos(IEnumerable<string?> uris, int decodeWidth = 80)
    {
        Task.Run(() =>
        {
            foreach (var rawUri in uris)
            {
                if (string.IsNullOrWhiteSpace(rawUri) || !Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out var uri))
                {
                    continue;
                }

                var normalizedUrl = uri.AbsoluteUri;
                if (_memoryCache.ContainsKey(normalizedUrl))
                {
                    continue;
                }

                var diskPath = GetDiskCachePath(normalizedUrl);
                if (File.Exists(diskPath))
                {
                    var bitmap = LoadBitmapFromFile(diskPath, decodeWidth);
                    if (bitmap is not null)
                    {
                        _memoryCache[normalizedUrl] = bitmap;
                        continue;
                    }
                }

                QueueDownload(normalizedUrl, decodeWidth);
            }
        });
    }

    private void QueueDownload(string normalizedUrl, int decodeWidth)
    {
        if (!_inFlightDownloads.TryAdd(normalizedUrl, 0))
        {
            return; // Already downloading
        }

        Task.Run(async () =>
        {
            try
            {
                await _downloadThrottle.WaitAsync().ConfigureAwait(false);
                try
                {
                    var cacheFilePath = GetDiskCachePath(normalizedUrl);
                    if (!File.Exists(cacheFilePath))
                    {
                        using var response = await _httpClient.GetAsync(normalizedUrl, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            return;
                        }

                        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        if (bytes.Length == 0)
                        {
                            return;
                        }

                        var tempPath = cacheFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
                        File.Move(tempPath, cacheFilePath, overwrite: true);
                    }

                    var bitmap = LoadBitmapFromFile(cacheFilePath, decodeWidth);
                    if (bitmap is not null)
                    {
                        _memoryCache[normalizedUrl] = bitmap;
                        LogoLoaded?.Invoke(normalizedUrl, bitmap);
                    }
                }
                finally
                {
                    _downloadThrottle.Release();
                }
            }
            catch
            {
                // Network error, 404, or invalid image; ignored silently
            }
            finally
            {
                _inFlightDownloads.TryRemove(normalizedUrl, out _);
            }
        });
    }

    private string GetDiskCachePath(string normalizedUrl)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedUrl));
        var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".img";
        return Path.Combine(_cacheDirectory, fileName);
    }

    private static BitmapSource? LoadBitmapFromFile(string filePath, int decodeWidth)
    {
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fileStream.Length == 0)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.StreamSource = fileStream;
            bitmap.EndInit();

            var processed = RemoveWhiteBackground(bitmap);
            if (processed.CanFreeze)
            {
                processed.Freeze();
            }

            return processed;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Detects if an image has an opaque white or near-white exterior background,
    /// and makes the exterior white region transparent using perimeter flood fill,
    /// preserving any white details inside the team crest/logo boundaries.
    /// </summary>
    public static BitmapSource RemoveWhiteBackground(BitmapSource source)
    {
        try
        {
            BitmapSource src = source;
            if (src.Format != PixelFormats.Bgra32)
            {
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            }

            int width = src.PixelWidth;
            int height = src.PixelHeight;
            if (width < 3 || height < 3)
            {
                return source;
            }

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            src.CopyPixels(pixels, stride, 0);

            bool IsWhitePixel(int offset)
            {
                byte a = pixels[offset + 3];
                if (a < 180) return false; // already transparent
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                return r >= 235 && g >= 235 && b >= 235;
            }

            // Sample corners: (0,0), (width-1,0), (0,height-1), (width-1,height-1)
            int c00 = 0;
            int cTR = (width - 1) * 4;
            int cBL = (height - 1) * stride;
            int cBR = (height - 1) * stride + (width - 1) * 4;

            int whiteCorners = 0;
            if (IsWhitePixel(c00)) whiteCorners++;
            if (IsWhitePixel(cTR)) whiteCorners++;
            if (IsWhitePixel(cBL)) whiteCorners++;
            if (IsWhitePixel(cBR)) whiteCorners++;

            // If corners are not white (e.g. already transparent PNG or dark background), return immediately
            if (whiteCorners < 2)
            {
                return source;
            }

            bool[] visited = new bool[width * height];
            var queue = new Queue<int>(width * 2 + height * 2);

            void TryEnqueue(int x, int y)
            {
                int idx = y * width + x;
                if (!visited[idx])
                {
                    visited[idx] = true;
                    int off = y * stride + x * 4;
                    if (IsWhitePixel(off))
                    {
                        queue.Enqueue(idx);
                    }
                }
            }

            // Seed from outer borders
            for (int x = 0; x < width; x++)
            {
                TryEnqueue(x, 0);
                TryEnqueue(x, height - 1);
            }
            for (int y = 0; y < height; y++)
            {
                TryEnqueue(0, y);
                TryEnqueue(width - 1, y);
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % width;
                int y = idx / width;
                int off = y * stride + x * 4;

                // Erase outer white background pixel to transparent
                pixels[off + 3] = 0;

                // Propagate flood fill
                if (x > 0) TryEnqueue(x - 1, y);
                if (x < width - 1) TryEnqueue(x + 1, y);
                if (y > 0) TryEnqueue(x, y - 1);
                if (y < height - 1) TryEnqueue(x, y + 1);
            }

            var result = BitmapSource.Create(
                width, height,
                src.DpiX, src.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);

            return result;
        }
        catch
        {
            return source;
        }
    }
}
