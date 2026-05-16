using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TvStations;

public class StringPair
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        EnableMovies = true;
        EnableShows = true;
        MinItemsPerChannel = 1;
        MaxGenresPerType = 50;
        MovieChannelStart = 100;
        ShowChannelStart = 200;
        RecentlyAddedChannelStart = 300;
        TopRatedChannelStart = 310;
        DecadeChannelStart = 320;
        CollectionChannelStart = 400;
        CacheExpiryMinutes = 15;
        ChannelNameOverrides = new List<StringPair>();
        DisabledChannels = new List<string>();
    }

    public bool EnableMovies { get; set; }
    public bool EnableShows { get; set; }
    public bool EnableRecentlyAdded { get; set; }
    public bool EnableTopRated { get; set; }
    public bool EnableDecadeChannels { get; set; }
    public bool EnableCollections { get; set; }
    public bool ShuffleChannels { get; set; }

    public int MinItemsPerChannel { get; set; }
    public int MaxGenresPerType { get; set; }
    public int MovieChannelStart { get; set; }
    public int ShowChannelStart { get; set; }
    public int RecentlyAddedChannelStart { get; set; }
    public int TopRatedChannelStart { get; set; }
    public int DecadeChannelStart { get; set; }
    public int CollectionChannelStart { get; set; }
    public int CacheExpiryMinutes { get; set; }

    public List<StringPair> ChannelNameOverrides { get; set; }
    public List<string> DisabledChannels { get; set; }
}
