namespace IptvPlayer.Contracts.Models;

public sealed record SportsEventFeedModel(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ValidUntilUtc,
    IReadOnlyList<SportsEventModel> Events);

public sealed record SportsEventModel(
    string Id,
    string Title,
    string Sport,
    string? Competition,
    DateTimeOffset StartUtc,
    SportsEventStatus Status,
    IReadOnlyList<EventBroadcastModel> Broadcasts,
    string? ArtworkUri,
    EventTeamModel? HomeTeam,
    EventTeamModel? AwayTeam);

public sealed record EventTeamModel(
    string Name,
    string? BadgeUri);

public enum SportsEventStatus
{
    Scheduled,
    Confirmed,
    Postponed,
    Cancelled,
}

public sealed record EventBroadcastModel(
    string ChannelName,
    IReadOnlyList<string> Aliases,
    string? Territory,
    bool Confirmed);
