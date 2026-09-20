using System.Globalization;
using System.Text;
using System.Text.Json;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Infrastructure.Services;

public sealed class SportsEventService : ISportsEventService
{
    private const string FeedUrl = "https://raw.githubusercontent.com/craetsodom-hub/whoseiptv/main/feed/events/v1/events.json";
    private const int SchemaVersion = 1;
    private const int MaxFeedBytes = 8 * 1024 * 1024;
    private const int MaxBroadcastsPerEvent = 300;
    private const int MaxAliasesPerBroadcast = 20;
    private const string TheSportsDbEventsDayUrl = "https://www.thesportsdb.com/api/v1/json/3/eventsday.php";
    private const string TheSportsDbEventsNextUrl = "https://www.thesportsdb.com/api/v1/json/3/eventsnext.php";

    private static readonly TimeSpan MaxPastWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxFutureWindow = TimeSpan.FromDays(8);
    private static readonly string[] SupplementalFriendlyLeagueIds =
    [
        "4569",
        "4562",
    ];
    private static readonly string[] SupplementalBigClubTeamIds =
    [
        "133612", // Manchester United
        "133613", // Manchester City
        "133602", // Liverpool
        "133604", // Arsenal
        "133610", // Chelsea
        "133616", // Tottenham Hotspur
        "133738", // Real Madrid
        "133739", // Barcelona
        "133664", // Bayern Munich
        "133650", // Borussia Dortmund
        "133676", // Juventus
        "133681", // Inter Milan
        "133667", // AC Milan
        "133670", // Napoli
        "133729", // Atletico Madrid
        "133772", // Ajax
        "134108", // Benfica
        "134114", // Porto
        "133647", // Celtic
        "133642", // Rangers
        "133768", // PSV Eindhoven
        "133758", // Feyenoord
        "133682", // Roma
        "133668", // Lazio
        "133666", // Bayer Leverkusen
        "134695", // RB Leipzig
    ];
    private static readonly HashSet<string> SupportedSports = new(StringComparer.OrdinalIgnoreCase)
    {
        "football",
        "basketball",
        "tennis",
        "formula1",
        "cricket",
        "rugby",
    };

    private static readonly HashSet<string> TrustedArtworkHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "r2.thesportsdb.com",
        "www.thesportsdb.com",
        "images.thesportsdb.com",
        "cdn.nba.com",
        "media.formula1.com",
        "crests.football-data.org",
        "media.api-sports.io",
        "a.espncdn.com",
        "static.livescore.com",
        "upload.wikimedia.org",
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<SportsEventService> _logger;
    private readonly string _cacheFilePath;

    public SportsEventService(ILogger<SportsEventService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "cache");
        _cacheFilePath = Path.Combine(cacheRoot, "sports-events-v1.json");
    }

    public async Task<SportsEventFeedModel?> LoadCachedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(_cacheFilePath);
            if (!file.Exists || file.Length > MaxFeedBytes)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(_cacheFilePath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Cached sports events feed could not be loaded");
            return null;
        }
    }

    public async Task<SportsEventFeedModel> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.CacheControl = new()
        {
            NoCache = true,
        };

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxFeedBytes)
        {
            throw new InvalidOperationException("Events feed is too large.");
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var json = await ReadUtf8WithLimitAsync(stream, MaxFeedBytes, cancellationToken)
            .ConfigureAwait(false);
        var feed = Parse(json);
        await SaveCacheAsync(json, cancellationToken).ConfigureAwait(false);
        var supplementalEvents = await LoadSupplementalFriendliesAsync(cancellationToken).ConfigureAwait(false);
        return supplementalEvents.Count == 0
            ? feed
            : MergeEvents(feed, supplementalEvents);
    }

    private async Task<IReadOnlyList<SportsEventModel>> LoadSupplementalFriendliesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now.Date;
        var dates = new[] { now, now.AddDays(1) };
        var events = new List<SportsEventModel>();

        foreach (var date in dates)
        {
            foreach (var leagueId in SupplementalFriendlyLeagueIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var url = $"{TheSportsDbEventsDayUrl}?d={date:yyyy-MM-dd}&l={leagueId}";
                    using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!document.RootElement.TryGetProperty("events", out var eventsElement)
                        || eventsElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var eventElement in eventsElement.EnumerateArray())
                    {
                        if (TryParseTheSportsDbFriendly(eventElement) is { } sportsEvent
                            && events.All(existing => !string.Equals(existing.Id, sportsEvent.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            events.Add(sportsEvent);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogDebug(exception, "Supplemental friendly events could not be loaded for league {LeagueId}", leagueId);
                }
            }
        }

        foreach (var teamId in SupplementalBigClubTeamIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var url = $"{TheSportsDbEventsNextUrl}?id={teamId}";
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("events", out var eventsElement)
                    || eventsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var eventElement in eventsElement.EnumerateArray())
                {
                    if (TryParseTheSportsDbFriendly(eventElement) is { } sportsEvent
                        && IsTodayOrTomorrowLocal(sportsEvent.StartUtc)
                        && events.All(existing => !string.Equals(existing.Id, sportsEvent.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        events.Add(sportsEvent);
                    }
                }

                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Supplemental big-club friendly events could not be loaded for team {TeamId}", teamId);
            }
        }

        return events;
    }

    private static bool IsTodayOrTomorrowLocal(DateTimeOffset startUtc)
    {
        var localDate = startUtc.ToLocalTime().Date;
        var today = DateTimeOffset.Now.Date;
        return localDate == today || localDate == today.AddDays(1);
    }

    private static SportsEventFeedModel MergeEvents(
        SportsEventFeedModel feed,
        IReadOnlyList<SportsEventModel> supplementalEvents)
    {
        var events = feed.Events
            .Concat(supplementalEvents)
            .DistinctBy(sportsEvent => sportsEvent.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return feed with { Events = events };
    }

    private static SportsEventFeedModel Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Empty events feed.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!TryReadInt(root, "schemaVersion", out var schemaVersion) || schemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException("Unsupported events feed version.");
        }

        if (!TryReadEpoch(root, "generatedAtEpochSeconds", out var generatedAt)
            || !TryReadEpoch(root, "validUntilEpochSeconds", out var validUntil))
        {
            throw new InvalidOperationException("Invalid events feed timestamps.");
        }

        var now = DateTimeOffset.UtcNow;
        if (generatedAt <= DateTimeOffset.UnixEpoch || generatedAt > now.AddHours(6))
        {
            throw new InvalidOperationException("Invalid events feed generation time.");
        }

        if (validUntil <= now || validUntil <= generatedAt)
        {
            throw new InvalidOperationException("Stale events feed.");
        }

        var earliest = now.Subtract(MaxPastWindow);
        var latest = now.Add(MaxFutureWindow);
        var events = new List<SportsEventModel>();

        if (root.TryGetProperty("events", out var eventsElement)
            && eventsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var eventElement in eventsElement.EnumerateArray())
            {
                if (TryParseEvent(eventElement, earliest, latest) is not { } parsedEvent
                    || events.Any(existing => string.Equals(existing.Id, parsedEvent.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                events.Add(parsedEvent);
            }
        }

        return new SportsEventFeedModel(
            SchemaVersion,
            generatedAt,
            validUntil,
            events);
    }

    private static SportsEventModel? TryParseEvent(JsonElement element, DateTimeOffset earliest, DateTimeOffset latest)
    {
        var id = TrimToLength(ReadString(element, "id"), 120);
        var title = TrimToLength(ReadString(element, "title"), 200);
        var sport = TrimToLength(ReadString(element, "sport"), 40)?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(sport)
            || !SupportedSports.Contains(sport)
            || !TryReadEpoch(element, "startUtcEpochSeconds", out var startUtc)
            || startUtc < earliest
            || startUtc > latest)
        {
            return null;
        }

        var status = ParseStatus(ReadString(element, "status"));
        var broadcasts = ParseBroadcasts(element);
        return new SportsEventModel(
            id,
            title,
            sport,
            TrimToLength(ReadString(element, "competition"), 120),
            startUtc,
            status,
            broadcasts,
            TrustedArtworkUri(ReadString(element, "artworkUrl")),
            TryParseTeam(element, "homeTeam"),
            TryParseTeam(element, "awayTeam"));
    }

    private static SportsEventModel? TryParseTheSportsDbFriendly(JsonElement element)
    {
        var id = TrimToLength(ReadString(element, "idEvent"), 80);
        var title = TrimToLength(ReadString(element, "strEvent"), 200);
        var league = TrimToLength(ReadString(element, "strLeague"), 120);

        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(title)
            || !IsFriendlyCompetition(league)
            || !TryReadTheSportsDbTimestamp(element, out var startUtc))
        {
            return null;
        }

        return new SportsEventModel(
            $"tsdb-{id}",
            title,
            "football",
            league,
            startUtc,
            ParseTheSportsDbStatus(ReadString(element, "strStatus")),
            Array.Empty<EventBroadcastModel>(),
            TrustedArtworkUri(ReadString(element, "strThumb"))
                ?? TrustedArtworkUri(ReadString(element, "strPoster"))
                ?? TrustedArtworkUri(ReadString(element, "strLeagueBadge")),
            TryParseTheSportsDbTeam(element, "strHomeTeam", "strHomeTeamBadge"),
            TryParseTheSportsDbTeam(element, "strAwayTeam", "strAwayTeamBadge"));
    }

    private static bool IsFriendlyCompetition(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("friend", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadTheSportsDbTimestamp(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        var timestamp = ReadString(element, "strTimestamp");
        if (!string.IsNullOrWhiteSpace(timestamp)
            && DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value))
        {
            return true;
        }

        var date = ReadString(element, "dateEvent");
        var time = ReadString(element, "strTime");
        if (string.IsNullOrWhiteSpace(date))
        {
            return false;
        }

        var dateTime = string.IsNullOrWhiteSpace(time) ? date : $"{date}T{time}";
        return DateTimeOffset.TryParse(
            dateTime,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static EventTeamModel? TryParseTheSportsDbTeam(
        JsonElement element,
        string teamPropertyName,
        string badgePropertyName)
    {
        var name = TrimToLength(ReadString(element, teamPropertyName), 120);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var badge = TrustedArtworkUri(ReadString(element, badgePropertyName));
        if (string.IsNullOrWhiteSpace(badge))
        {
            badge = TeamBadgeCatalog.ResolveBadge(name);
        }

        return new EventTeamModel(name, badge);
    }

    private static SportsEventStatus ParseTheSportsDbStatus(string? value)
        => value?.Trim().ToUpperInvariant() switch
        {
            "CANC" or "CANCELLED" => SportsEventStatus.Cancelled,
            "POST" or "PST" or "POSTPONED" => SportsEventStatus.Postponed,
            "NS" or "TBD" or "" or null => SportsEventStatus.Scheduled,
            "1H" or "2H" or "HT" or "ET" or "PEN" or "LIVE" => SportsEventStatus.Confirmed,
            _ => SportsEventStatus.Cancelled,
        };

    private static EventTeamModel? TryParseTeam(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var teamElement)
            || teamElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = TrimToLength(ReadString(teamElement, "name"), 120);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var badge = TrustedArtworkUri(ReadString(teamElement, "badgeUrl"));
        if (string.IsNullOrWhiteSpace(badge))
        {
            badge = TeamBadgeCatalog.ResolveBadge(name);
        }

        return new EventTeamModel(name, badge);
    }

    private static IReadOnlyList<EventBroadcastModel> ParseBroadcasts(JsonElement element)
    {
        if (!element.TryGetProperty("broadcasts", out var broadcastsElement)
            || broadcastsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<EventBroadcastModel>();
        }

        var broadcasts = new List<EventBroadcastModel>();
        foreach (var broadcastElement in broadcastsElement.EnumerateArray())
        {
            var channelName = TrimToLength(ReadString(broadcastElement, "channelName"), 120);
            if (string.IsNullOrWhiteSpace(channelName))
            {
                continue;
            }

            var aliases = ParseAliases(broadcastElement);
            var territory = TrimToLength(ReadString(broadcastElement, "territory"), 16);
            var confirmed = broadcastElement.TryGetProperty("confirmed", out var confirmedElement)
                && confirmedElement.ValueKind == JsonValueKind.True;

            broadcasts.Add(new EventBroadcastModel(channelName, aliases, territory, confirmed));
            if (broadcasts.Count >= MaxBroadcastsPerEvent)
            {
                break;
            }
        }

        return broadcasts;
    }

    private static IReadOnlyList<string> ParseAliases(JsonElement broadcastElement)
    {
        if (!broadcastElement.TryGetProperty("aliases", out var aliasesElement)
            || aliasesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return aliasesElement
            .EnumerateArray()
            .Select(alias => TrimToLength(alias.ValueKind == JsonValueKind.String ? alias.GetString() : null, 120))
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAliasesPerBroadcast)
            .ToArray()!;
    }

    private static SportsEventStatus ParseStatus(string? value)
        => value?.Trim().ToUpperInvariant() switch
        {
            "CONFIRMED" => SportsEventStatus.Confirmed,
            "POSTPONED" => SportsEventStatus.Postponed,
            "CANCELLED" => SportsEventStatus.Cancelled,
            _ => SportsEventStatus.Scheduled,
        };

    private static string? TrustedArtworkUri(string? value)
    {
        var uriText = TrimToLength(value, 1024);
        if (string.IsNullOrWhiteSpace(uriText)
            || !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
            uriText = uri.AbsoluteUri;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Host is null)
        {
            return null;
        }

        if (TrustedArtworkHosts.Contains(uri.Host)
            || uri.Host.EndsWith("thesportsdb.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("football-data.org", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("api-sports.io", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("api-football.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("espncdn.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("livescore.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("wikimedia.org", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("gstatic.com", StringComparison.OrdinalIgnoreCase))
        {
            return uriText;
        }

        return null;
    }

    private async Task SaveCacheAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cacheFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_cacheFilePath}.tmp";
        await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, _cacheFilePath, overwrite: true);
    }

    private static async Task<string> ReadUtf8WithLimitAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > maxBytes)
            {
                throw new InvalidOperationException("Events feed is too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static bool TryReadEpoch(JsonElement element, string propertyName, out DateTimeOffset value)
    {
        value = default;
        if (!TryReadInt64(element, propertyName, out var epochSeconds))
        {
            return false;
        }

        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static bool TryReadInt64(JsonElement element, string propertyName, out long value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    private static string? TrimToLength(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
