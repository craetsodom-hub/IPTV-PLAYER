using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

internal static class EventChannelMatcher
{
    private static readonly HashSet<string> QualityTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "sd",
        "hd",
        "fhd",
        "fullhd",
        "uhd",
        "4k",
        "hevc",
        "h265",
        "h264",
        "raw",
        "1080",
        "1080p",
        "2160",
        "2160p",
        "720",
        "720p",
    };

    private static readonly Regex NonAlphaNumeric = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex RepeatedWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> OptionalTailTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "tv",
        "channel",
    };
    private static readonly Regex QualityPrefixedChannelNumber = new(
        @"^(?:sd|hd|fhd|uhd|4k)(?<number>\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PipeLeadingCountryCode = new(
        @"^\s*\|?\s*(?<code>[A-Za-z]{2,3})\s*\|",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingCountryCode = new(
        @"^\s*(?:[\|\[\(【]\s*(?<code>[A-Za-z]{2,3})\s*[\|\]\)】]|(?<code>[A-Z]{2,3})\s*[:\-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, IReadOnlyList<EventChannelOptionViewModel>> MatchAll(
        IReadOnlyList<SportsEventModel> events,
        IReadOnlyList<ChannelItemViewModel> channels)
    {
        if (events.Count == 0 || channels.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<EventChannelOptionViewModel>>(StringComparer.OrdinalIgnoreCase);
        }

        var index = BuildIndex(channels);
        return events.ToDictionary(
            sportsEvent => sportsEvent.Id,
            sportsEvent => (IReadOnlyList<EventChannelOptionViewModel>)Match(sportsEvent, index, channels),
            StringComparer.OrdinalIgnoreCase);
    }

    private static List<EventChannelOptionViewModel> Match(
        SportsEventModel sportsEvent,
        Dictionary<string, List<IndexedChannel>> index,
        IReadOnlyList<ChannelItemViewModel> channels)
    {
        if (index.Count == 0)
        {
            return [];
        }

        var matched = new Dictionary<string, MatchedEventChannelOption>(StringComparer.OrdinalIgnoreCase);
        var broadcastOrder = 0;
        var broadcasts = sportsEvent.Broadcasts.Where(broadcast => broadcast.Confirmed).ToArray();
        if (broadcasts.Length == 0)
        {
            return [];
        }

        foreach (var broadcast in broadcasts)
        {
            var keys = new[] { broadcast.ChannelName }
                .Concat(broadcast.Aliases)
                .SelectMany(BuildMatchKeys)
                .Where(tokens => tokens.Count > 0)
                .Distinct(TokenListComparer.Instance)
                .ToArray();

            if (keys.Length == 0)
            {
                continue;
            }

            var candidates = keys
                .SelectMany(key => CandidateLookupTokens(key)
                    .SelectMany(token => index.TryGetValue(token, out var entries) ? entries : []))
                .DistinctBy(candidate => candidate.Channel.Id);

            foreach (var candidate in candidates)
            {
                var score = keys
                    .Select(key => ScoreBroadcastMatch(candidate.Tokens, key, broadcast.Territory))
                    .Where(value => value.HasValue)
                    .Min();
                if (!score.HasValue)
                {
                    continue;
                }

                var broadcastTerritoryCode = NormalizeTerritoryCode(broadcast.Territory);
                var candidateTerritoryCode = InferTerritoryCode(candidate.Channel.Name);
                if (!IsTerritoryCompatible(broadcastTerritoryCode, candidateTerritoryCode))
                {
                    continue;
                }

                var territoryCode = string.IsNullOrWhiteSpace(broadcastTerritoryCode)
                    ? candidateTerritoryCode
                    : broadcastTerritoryCode;
                var matchKey = $"{candidate.Channel.Id}\u001F{territoryCode}";
                var option = new MatchedEventChannelOption(
                    new EventChannelOptionViewModel(
                        broadcast.ChannelName,
                        territoryCode,
                        FormatTerritory(territoryCode),
                        candidate.Channel),
                    broadcastOrder,
                    score.Value);
                if (!matched.TryGetValue(matchKey, out var existing)
                    || option.Score < existing.Score
                    || option.BroadcastOrder < existing.BroadcastOrder)
                {
                    matched[matchKey] = option;
                }
            }

            broadcastOrder++;
        }

        return matched.Values
            .OrderBy(match => match.BroadcastOrder)
            .ThenBy(match => match.Score)
            .ThenBy(match => PopularityRank(match.Option))
            .ThenBy(match => NormalizeTokens(match.Option.Channel.Name).Count)
            .ThenBy(match => match.Option.Channel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Option)
            .ToList();
    }

    private static Dictionary<string, List<IndexedChannel>> BuildIndex(IReadOnlyList<ChannelItemViewModel> channels)
    {
        var entries = channels
            .DistinctBy(channel => channel.Id)
            .Select(channel => new IndexedChannel(channel, NormalizeTokens(channel.Name)))
            .Where(entry => entry.Tokens.Count > 0)
            .ToArray();

        var index = new Dictionary<string, List<IndexedChannel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var token in entry.Tokens.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!index.TryGetValue(token, out var bucket))
                {
                    bucket = [];
                    index[token] = bucket;
                }

                bucket.Add(entry);
            }
        }

        return index;
    }

    private static List<string> NormalizeTokens(string value)
    {
        var normalized = RemoveDiacritics(value)
            .ToLowerInvariant()
            .Replace('&', ' ')
            .Replace("+", " plus ");
        normalized = NonAlphaNumeric.Replace(normalized, " ");
        normalized = RepeatedWhitespace.Replace(normalized, " ").Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        var rawTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(rawTokens.Length);
        for (var index = 0; index < rawTokens.Length; index++)
        {
            var token = rawTokens[index];
            if (QualityTokens.Contains(token))
            {
                continue;
            }

            if (token.All(char.IsDigit))
            {
                result.Add(token.TrimStart('0') is { Length: > 0 } number ? number : "0");
                continue;
            }

            var qualityNumberMatch = QualityPrefixedChannelNumber.Match(token);
            if (qualityNumberMatch.Success)
            {
                result.Add(qualityNumberMatch.Groups["number"].Value);
                continue;
            }

            if (token == "be" && rawTokens.ElementAtOrDefault(index + 1) == "in")
            {
                result.Add("bein");
                index++;
                continue;
            }

            result.Add(token == "sport" ? "sports" : token);
        }

        return result;
    }

    private static IEnumerable<IReadOnlyList<string>> BuildMatchKeys(string value)
    {
        var tokens = NormalizeTokens(value);
        if (tokens.Count == 0)
        {
            yield break;
        }

        yield return tokens;

        if (tokens.Count > 1 && OptionalTailTokens.Contains(tokens[^1]))
        {
            yield return tokens.Take(tokens.Count - 1).ToArray();
        }
    }

    private static IEnumerable<string> CandidateLookupTokens(IReadOnlyList<string> key)
        => key
            .Where(token => !OptionalTailTokens.Contains(token))
            .DefaultIfEmpty(key[0])
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static int? ScoreBroadcastMatch(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> expected,
        string? territory)
    {
        var candidateCore = BuildCoreTokens(candidate);
        var expectedCore = BuildCoreTokens(expected);
        if (AreEquivalent(candidateCore, expectedCore))
        {
            return 0;
        }

        var territoryTokens = GetTerritoryTokens(territory);
        var candidateHasTerritory = candidateCore.Any(territoryTokens.Contains);
        var expectedHasTerritory = expectedCore.Any(territoryTokens.Contains);
        var candidateWithoutTerritory = RemoveTerritoryTokens(candidateCore, territoryTokens);
        var expectedWithoutTerritory = RemoveTerritoryTokens(expectedCore, territoryTokens);
        if (candidateWithoutTerritory.Count == 0 || expectedWithoutTerritory.Count == 0)
        {
            return null;
        }

        if (expectedHasTerritory && !candidateHasTerritory)
        {
            return null;
        }

        return AreEquivalent(candidateWithoutTerritory, expectedWithoutTerritory) ? 1 : null;
    }

    private static bool AreEquivalent(IReadOnlyList<string> candidate, IReadOnlyList<string> expected)
    {
        if (candidate.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (candidate.Count != expected.Count)
        {
            return false;
        }

        var candidateCounts = candidate
            .GroupBy(token => token, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var expectedCounts = expected
            .GroupBy(token => token, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return candidateCounts.Count == expectedCounts.Count
               && expectedCounts.All(pair => candidateCounts.TryGetValue(pair.Key, out var count) && count == pair.Value);
    }

    private static IReadOnlyList<string> RemoveTerritoryTokens(
        IReadOnlyList<string> tokens,
        IReadOnlySet<string> territoryTokens)
    {
        return territoryTokens.Count == 0
            ? tokens
            : tokens.Where(token => !territoryTokens.Contains(token)).ToArray();
    }

    private static IReadOnlySet<string> GetTerritoryTokens(string? territory)
    {
        var code = NormalizeTerritoryCode(territory);
        if (code.Length == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { code.ToLowerInvariant() };
        try
        {
            var region = new RegionInfo(code);
            foreach (var name in new[] { region.EnglishName, region.NativeName })
            {
                foreach (var token in NormalizeTokens(name))
                {
                    tokens.Add(token);
                }
            }
        }
        catch (ArgumentException)
        {
        }

        if (code == "GB")
        {
            tokens.Add("uk");
        }

        return tokens;
    }

    private static IReadOnlyList<string> BuildCoreTokens(IReadOnlyList<string> tokens)
        => tokens.Count > 1 && OptionalTailTokens.Contains(tokens[^1])
            ? tokens.Take(tokens.Count - 1).ToArray()
            : tokens;

    private static bool IsTerritoryCompatible(string expectedTerritoryCode, string candidateTerritoryCode)
        => string.IsNullOrWhiteSpace(expectedTerritoryCode)
           || string.IsNullOrWhiteSpace(candidateTerritoryCode)
           || string.Equals(expectedTerritoryCode, candidateTerritoryCode, StringComparison.OrdinalIgnoreCase);

    private static string FormatTerritory(string? territory)
    {
        if (string.IsNullOrWhiteSpace(territory))
        {
            return UiLocalization.Current.GetString("Worldwide");
        }

        try
        {
            var region = new RegionInfo(territory.Trim().ToUpperInvariant());
            return region.DisplayName;
        }
        catch (ArgumentException)
        {
            return territory.Trim().ToUpperInvariant();
        }
    }

    private static string NormalizeTerritoryCode(string? territory)
        => string.IsNullOrWhiteSpace(territory)
            ? string.Empty
            : territory.Trim().ToUpperInvariant();

    private static string InferTerritoryCode(string channelName)
    {
        var pipePrefixMatch = PipeLeadingCountryCode.Match(channelName);
        if (pipePrefixMatch.Success
            && TryNormalizeCountryCode(pipePrefixMatch.Groups["code"].Value, out var pipePrefixCode))
        {
            return pipePrefixCode;
        }

        var prefixMatch = LeadingCountryCode.Match(channelName);
        if (prefixMatch.Success
            && TryNormalizeCountryCode(prefixMatch.Groups["code"].Value, out var prefixCode))
        {
            return prefixCode;
        }

        return string.Empty;
    }

    private static bool TryNormalizeCountryCode(string value, out string code)
    {
        code = value.Trim().ToUpperInvariant() switch
        {
            "UK" => "GB",
            "ENG" => "GB",
            "SCO" => "GB",
            "WAL" => "GB",
            "NIR" => "GB",
            "UAE" => "AE",
            "KSA" => "SA",
            "DEU" => "DE",
            "GER" => "DE",
            "FRA" => "FR",
            "ESP" => "ES",
            "ITA" => "IT",
            "POR" => "PT",
            "NED" => "NL",
            "TUR" => "TR",
            "USA" => "US",
            "BRA" => "BR",
            "ARG" => "AR",
            "MEX" => "MX",
            "CAN" => "CA",
            "AUS" => "AU",
            _ => value.Trim().ToUpperInvariant(),
        };

        if (code.Length != 2)
        {
            code = string.Empty;
            return false;
        }

        try
        {
            _ = new RegionInfo(code);
            return true;
        }
        catch (ArgumentException)
        {
            code = string.Empty;
            return false;
        }
    }

    private static int PopularityRank(EventChannelOptionViewModel option)
    {
        var value = $"{option.BroadcasterName} {option.Channel.Name}".ToLowerInvariant();
        var brands = new[]
        {
            "bein",
            "sky sports",
            "espn",
            "tnt sports",
            "dazn",
            "canal+",
            "canal plus",
            "movistar",
            "eurosport",
            "prime video",
            "paramount",
            "fox sports",
            "nbc sports",
            "cbs sports",
            "super sport",
            "supersport",
            "viaplay",
            "sport tv",
            "ziggo sport",
            "rts",
            "rai",
            "tf1",
            "m6",
        };

        for (var index = 0; index < brands.Length; index++)
        {
            if (value.Contains(brands[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return brands.Length;
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed record IndexedChannel(ChannelItemViewModel Channel, IReadOnlyList<string> Tokens);

    private sealed record MatchedEventChannelOption(EventChannelOptionViewModel Option, int BroadcastOrder, int Score);

    private sealed class TokenListComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static TokenListComparer Instance { get; } = new();

        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (x is null || y is null || x.Count != y.Count)
            {
                return false;
            }

            return x.SequenceEqual(y, StringComparer.OrdinalIgnoreCase);
        }

        public int GetHashCode(IReadOnlyList<string> obj)
        {
            var hash = new HashCode();
            foreach (var value in obj)
            {
                hash.Add(value, StringComparer.OrdinalIgnoreCase);
            }

            return hash.ToHashCode();
        }
    }
}
