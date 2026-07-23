using IptvPlayer.Contracts.Import;
using IptvPlayer.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Application.Services;

public sealed class SourceImportOrchestrator
{
    private readonly ISourceImportService _sourceImportService;
    private readonly ILogger<SourceImportOrchestrator> _logger;

    public SourceImportOrchestrator(
        ISourceImportService sourceImportService,
        ILogger<SourceImportOrchestrator> logger)
    {
        _sourceImportService = sourceImportService;
        _logger = logger;
    }

    public async Task<SourceImportResult> ImportAsync(
        SourceImportRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Import requested with mode {Mode}", request.Mode);
        return await _sourceImportService.ImportAsync(request, cancellationToken);
    }

    public async Task<SourceImportResult> UpdateAsync(
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating source {SourceId}", request.SourceId);
        return await _sourceImportService.UpdateSourceAsync(request, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting source {SourceId}", sourceId);
        return await _sourceImportService.DeleteSourceAsync(sourceId, cancellationToken);
    }
}
