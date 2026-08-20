using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IptvPlayer.Contracts.Models;

namespace IptvPlayer.Presentation.ViewModels;

internal static class EventPopularityRanker
{
    private static readonly Regex NonAlphaNumeric = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> SportScores = LoadSportScores();

    public static double Score(SportsEventModel sportsEvent)
        => new[] { sportsEvent.HomeTeam?.Name, sportsEvent.AwayTeam?.Name }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => SportScores.TryGetValue(sportsEvent.Sport, out var scores)
                && scores.TryGetValue(Normalize(name!), out var score) ? score : 0d)
            .DefaultIfEmpty(0d)
            .Max();

    public static IOrderedEnumerable<SportsEventModel> Rank(IEnumerable<SportsEventModel> events)
        => events.OrderByDescending(Score);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> LoadSportScores()
    {
        var scores = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase)
        {
            ["football"] = LoadFootballScores(),
        };
        var assembly = typeof(EventPopularityRanker).GetTypeInfo().Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("other-sport-popularity.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The other-sport popularity dataset is missing.");
        var dataset = JsonSerializer.Deserialize<OtherSportDataset>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("The other-sport popularity dataset is invalid.");
        foreach (var sport in dataset.Sports ?? new Dictionary<string, SportPopularity>(StringComparer.OrdinalIgnoreCase))
        {
            scores[sport.Key] = BuildScores(sport.Value.Entries);
        }
        return scores;
    }

    private static IReadOnlyDictionary<string, double> LoadFootballScores()
    {
        var assembly = typeof(EventPopularityRanker).GetTypeInfo().Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("global-club-popularity.json", StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The team popularity dataset is missing.");
        var dataset = JsonSerializer.Deserialize<TeamPopularityDataset>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("The team popularity dataset is invalid.");

        if (dataset.SchemaVersion != 3
            || dataset.MaxFollowers <= 0
            || string.IsNullOrWhiteSpace(dataset.SourceData)
            || string.IsNullOrWhiteSpace(dataset.SourceDate)
            || dataset.Entries is null
            || dataset.Entries.Count == 0)
        {
            throw new InvalidOperationException("The team popularity dataset has an unsupported schema.");
        }

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dataset.Entries)
        {
            if (entry is null || entry.Aliases is null
                || entry.Social.Raw < 0
                || entry.Social.Normalized < 0
                || Math.Abs(entry.Social.Normalized
                    - (1000d * entry.Social.Raw / dataset.MaxFollowers)) > 0.001d)
            {
                continue;
            }

            var score = entry.Social.Raw;
            foreach (var alias in entry.Aliases.Append(entry.CanonicalName))
            {
                var normalized = Normalize(alias);
                if (normalized.Length > 0)
                {
                    scores[normalized] = score;
                }
            }
        }

        return scores;
    }

    private static IReadOnlyDictionary<string, double> BuildScores(IReadOnlyList<SportPopularityEntry>? entries)
    {
        var max = entries?.Select(entry => entry.Raw).DefaultIfEmpty(0).Max() ?? 0;
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (max <= 0 || entries is null)
        {
            return scores;
        }

        foreach (var entry in entries)
        {
            if (entry.Raw <= 0 || entry.Aliases is null)
            {
                continue;
            }
            var score = 1000d * entry.Raw / max;
            foreach (var alias in entry.Aliases.Append(entry.CanonicalName))
            {
                var normalized = Normalize(alias);
                if (normalized.Length > 0)
                {
                    scores[normalized] = score;
                }
            }
        }
        return scores;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return NonAlphaNumeric.Replace(builder.ToString().ToLowerInvariant(), " ").Trim();
    }

    private sealed record TeamPopularityDataset(
        int SchemaVersion,
        long MaxFollowers,
        string SourceData,
        string SourceDate,
        IReadOnlyList<TeamPopularityEntry> Entries);

    private sealed record TeamPopularityEntry(
        string CanonicalName,
        IReadOnlyList<string> Aliases,
        SocialPopularity Social);

    private sealed record SocialPopularity(
        long Raw,
        double Normalized,
        string Source,
        string Date);

    private sealed record OtherSportDataset(
        int SchemaVersion,
        IReadOnlyDictionary<string, SportPopularity> Sports);

    private sealed record SportPopularity(
        string Source,
        string SourceDate,
        string Platform,
        IReadOnlyList<SportPopularityEntry> Entries);

    private sealed record SportPopularityEntry(
        string CanonicalName,
        IReadOnlyList<string> Aliases,
        long Raw);
}
