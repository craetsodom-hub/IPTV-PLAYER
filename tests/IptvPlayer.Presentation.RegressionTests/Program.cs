using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.ViewModels;

var failures = new List<string>();

var regionalEvent = Event(
    "regional",
    "Club A vs Club B",
    new EventBroadcastModel("DAZN Spain", [], "ES", true));
var regionalMatches = EventChannelMatcher.MatchAll([regionalEvent],
[
    Channel("dazn-es", "DAZN Spain"),
    Channel("dazn", "DAZN"),
    Channel("dazn-news", "DAZN News"),
    Channel("dazn-sports", "DAZN Sports")
])["regional"];
AssertEqual(["dazn-es"], regionalMatches.Select(option => option.Channel.Id), "regional service exactness");

var aliasEvent = Event(
    "alias",
    "Club A vs Club B",
    new EventBroadcastModel("Paramount+ US", ["Paramount Plus US", "US Paramount Plus"], "US", true));
var aliasMatches = EventChannelMatcher.MatchAll([aliasEvent],
[
    Channel("paramount", "US | Paramount Plus US HD"),
    Channel("paramount-news", "US | Paramount News")
])["alias"];
AssertEqual(["paramount"], aliasMatches.Select(option => option.Channel.Id), "explicit alias matching");

var qualityEvent = Event(
    "quality",
    "Club A vs Club B",
    new EventBroadcastModel("beIN Sports 1", [], "AR", true));
var qualityMatches = EventChannelMatcher.MatchAll([qualityEvent],
[
    Channel("ar", "AR| beIN Sports 1 FHD"),
    Channel("fr", "FR| beIN Sports 1 FHD"),
    Channel("news", "AR| beIN Sports News FHD")
])["quality"];
AssertEqual(["ar"], qualityMatches.Select(option => option.Channel.Id), "territory and channel identity");

var unconfirmedEvent = Event(
    "unconfirmed",
    "Club A vs Club B",
    new EventBroadcastModel("DAZN Spain", [], "ES", false));
var unconfirmedMatches = EventChannelMatcher.MatchAll([unconfirmedEvent], [Channel("dazn-es", "DAZN Spain")])["unconfirmed"];
AssertEqual([], unconfirmedMatches.Select(option => option.Channel.Id), "unconfirmed broadcaster fail-closed");

var rankingNow = DateTimeOffset.UtcNow;
var realMadrid = TeamEvent("real", "Ponferradina vs Real Madrid", [], "football", "Ponferradina", "Real Madrid")
    with { StartUtc = rankingNow.AddHours(8) };
var flamengo = TeamEvent("flamengo", "Flamengo vs Club G", [], "football", "Flamengo", "Club G");
var barcelona = TeamEvent("barcelona", "Barcelona vs Al Ahly", [], "football", "FC Barcelona", "Al Ahly")
    with { StartUtc = rankingNow.AddHours(9) };
var leuvenU23 = TeamEvent("leuven", "Oud-Heverlee Leuven U23 vs Genk U23", [], "football", "Oud-Heverlee Leuven U23", "Genk U23")
    with { StartUtc = rankingNow.AddHours(1) };
var coruxo = TeamEvent("coruxo", "Coruxo vs Pontevedra", [], "football", "Coruxo", "Pontevedra")
    with { StartUtc = rankingNow.AddHours(2) };
var rankedIds = EventPopularityRanker.Rank([leuvenU23, coruxo, realMadrid, barcelona])
    .Select(sportsEvent => sportsEvent.Id)
    .ToArray();
if (rankedIds.Take(2).ToHashSet(StringComparer.OrdinalIgnoreCase)
    .SetEquals(["real", "barcelona"]) is false)
{
    failures.Add($"Participant popularity did not put Real Madrid and Barcelona first: [{string.Join(", ", rankedIds)}]");
}

if (EventPopularityRanker.Score(realMadrid) <= EventPopularityRanker.Score(flamengo))
{
    failures.Add("Real Madrid did not outrank Flamengo using the measured popularity dataset");
}

var marqueeMatch = TeamEvent("marquee", "Real Madrid vs Barcelona", [], "football", "Real Madrid", "FC Barcelona");
if (EventPopularityRanker.Score(marqueeMatch) != EventPopularityRanker.Score(realMadrid))
{
    failures.Add("Event score was not exactly the maximum participating-team score");
}

var titleOnly = new SportsEventModel(
    "title-only",
    "Real Madrid vs Club H",
    "football",
    null,
    rankingNow,
    SportsEventStatus.Confirmed,
    [],
    null,
    null,
    null);
if (EventPopularityRanker.Score(titleOnly) != 0)
{
    failures.Add("Popularity score used event title when participating teams were absent");
}

var manchesterUnitedU23 = TeamEvent("development", "Manchester United U23 vs Club F", [], "football", "Manchester United U23", "Club F");
var unsupportedSport = TeamEvent("basketball", "Los Angeles Lakers vs Club E", [], "basketball", "Los Angeles Lakers", "Club E");
if (EventPopularityRanker.Score(unsupportedSport) != 0
    || EventPopularityRanker.Score(manchesterUnitedU23) >= EventPopularityRanker.Score(realMadrid))
{
    failures.Add("Football-only CIES popularity or exact development-team matching produced an invalid score");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Sports event regression tests failed:");
    foreach (var failure in failures) Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine("PASS: strict event channel matching and cross-sport popularity checks");
return 0;

void AssertEqual(IEnumerable<string> expected, IEnumerable<string> actual, string name)
{
    var expectedValues = expected.ToArray();
    var actualValues = actual.ToArray();
    if (!expectedValues.SequenceEqual(actualValues, StringComparer.OrdinalIgnoreCase))
    {
        failures.Add($"{name}: expected [{string.Join(", ", expectedValues)}], got [{string.Join(", ", actualValues)}]");
    }
}

ChannelItemViewModel Channel(string id, string name)
    => new(id, "sports", name, new Uri("https://example.invalid/" + id), null, null, null, null, null, null, null, null, null, false);

SportsEventModel Event(
    string id,
    string title,
    params EventBroadcastModel[] broadcasts)
    => TeamEvent(id, title, broadcasts, "football", "Club A", "Club B");

SportsEventModel TeamEvent(
    string id,
    string title,
    IReadOnlyList<EventBroadcastModel> broadcasts,
    string sport,
    string home,
    string away)
    => new(id, title, sport, null, DateTimeOffset.UtcNow, SportsEventStatus.Confirmed, broadcasts,
        null, new EventTeamModel(home, null), new EventTeamModel(away, null));
