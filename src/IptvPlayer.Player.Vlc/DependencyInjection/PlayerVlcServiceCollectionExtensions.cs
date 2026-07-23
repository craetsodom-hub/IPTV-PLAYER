using IptvPlayer.Contracts.Player;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Player.Vlc.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IptvPlayer.Player.Vlc.DependencyInjection;

public static class PlayerVlcServiceCollectionExtensions
{
    public static IServiceCollection AddPlayerVlcServices(this IServiceCollection services)
    {
        services.AddSingleton<VlcPlaybackService>();
        services.AddSingleton<IPlaybackService>(provider => provider.GetRequiredService<VlcPlaybackService>());
        services.AddSingleton<INativePlayerBridge>(provider => provider.GetRequiredService<VlcPlaybackService>());
        return services;
    }
}
