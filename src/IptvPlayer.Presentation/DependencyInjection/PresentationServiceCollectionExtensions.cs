using IptvPlayer.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace IptvPlayer.Presentation.DependencyInjection;

public static class PresentationServiceCollectionExtensions
{
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        services.AddSingleton<MainShellViewModel>();
        return services;
    }
}
