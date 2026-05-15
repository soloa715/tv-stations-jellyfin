using Jellyfin.Plugin.TvStations.LiveTv;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TvStations;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register as concrete type so TvStationsController can inject it directly.
        serviceCollection.AddSingleton<TvStationsService>();

        // Also expose via ILiveTvService (may or may not be picked up by LiveTvManager
        // depending on DI resolution order in Jellyfin 10.11.x).
        serviceCollection.AddSingleton<ILiveTvService>(
            sp => sp.GetRequiredService<TvStationsService>());

        // Ensure our controller is registered as an MVC application part.
        serviceCollection.AddControllers()
            .AddApplicationPart(typeof(ServiceRegistrator).Assembly);
    }
}
