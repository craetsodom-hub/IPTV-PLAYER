namespace IptvPlayer.Contracts.Import;

public sealed record SourceImportRequest(
    SourceImportMode Mode,
    string PrimaryInput,
    string? Username = null,
    string? Password = null,
    string? DisplayName = null);
