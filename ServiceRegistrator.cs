using Jellyfin.Plugin.TvStations.LiveTv;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TvStations;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TvStationsService>();

        // Ensure our controller is registered as an MVC application part.
        serviceCollection.AddControllers()
            .AddApplicationPart(typeof(ServiceRegistrator).Assembly);
    }
}
