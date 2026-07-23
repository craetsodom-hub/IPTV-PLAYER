using IptvPlayer.Contracts.Services;
using IptvPlayer.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IptvPlayer.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddSingleton<SourceCatalogService>();
        services.AddSingleton<ISourceCatalogService>(provider => provider.GetRequiredService<SourceCatalogService>());
        services.AddSingleton<ISourceImportService>(provider => provider.GetRequiredService<SourceCatalogService>());
        services.AddSingleton<IUserStateStore, JsonUserStateStore>();
        services.AddSingleton<IOnDemandStateStore, JsonOnDemandStateStore>();
        return services;
    }
}
