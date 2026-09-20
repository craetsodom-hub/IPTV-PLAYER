using IptvPlayer.Contracts.Import;

namespace IptvPlayer.Contracts.Services;

public interface ISourceImportService
{
    Task<SourceImportResult> ImportAsync(
        SourceImportRequest request,
        IProgress<SourceImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SourceImportResult> UpdateSourceAsync(
        SourceUpdateRequest request,
        IProgress<SourceImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SourceImportResult> RefreshSourceAsync(
        Guid sourceId,
        IProgress<SourceImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
}
