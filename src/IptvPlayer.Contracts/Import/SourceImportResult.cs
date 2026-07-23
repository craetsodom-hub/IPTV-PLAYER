using IptvPlayer.Contracts.Models;

namespace IptvPlayer.Contracts.Import;

public sealed record SourceImportResult(
    bool Success,
    string Message,
    PlaylistSource? Source)
{
    public static SourceImportResult Succeeded(PlaylistSource source, string message)
        => new(true, message, source);

    public static SourceImportResult Failed(string message)
        => new(false, message, null);
}
