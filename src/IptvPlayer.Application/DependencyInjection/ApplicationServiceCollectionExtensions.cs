using IptvPlayer.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IptvPlayer.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<CatalogOrchestrator>();
        services.AddSingleton<SourceImportOrchestrator>();
        services.AddSingleton<PlaybackOrchestrator>();
        services.AddSingleton<SessionOrchestrator>();
        return services;
    }
}
