namespace IptvPlayer.Contracts.Models;

public sealed record PlaylistSource(
    Guid Id,
    string Name,
    SourceKind Kind,
    string Endpoint,
    AccountStatusInfo StatusInfo);
