namespace IptvPlayer.Contracts.Import;

public sealed record SourceUpdateRequest(
    Guid SourceId,
    string? DisplayName = null,
    string? PrimaryInput = null,
    string? Username = null,
    string? Password = null);
