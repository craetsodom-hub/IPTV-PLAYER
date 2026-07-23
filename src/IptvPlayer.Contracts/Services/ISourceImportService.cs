using IptvPlayer.Contracts.Import;

namespace IptvPlayer.Contracts.Services;

public interface ISourceImportService
{
    Task<SourceImportResult> ImportAsync(SourceImportRequest request, CancellationToken cancellationToken = default);

    Task<SourceImportResult> UpdateSourceAsync(SourceUpdateRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
}
