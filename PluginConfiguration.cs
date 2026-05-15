using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TvStations;

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        EnableMovies = true;
        EnableShows = true;
        MinItemsPerChannel = 1;
        MaxGenresPerType = 50;
    }

    public bool EnableMovies { get; set; }

    public bool EnableShows { get; set; }

    public int MinItemsPerChannel { get; set; }

    public int MaxGenresPerType { get; set; }
}
