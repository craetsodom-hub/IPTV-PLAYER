namespace IptvPlayer.Domain.Entities;

public sealed record Category(
    string Id,
    string Name,
    int SortOrder);
