using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStations.LiveTv;

public sealed class TvStationsService : IDisposable
{
    private const string ChannelMoviePrefix = "tvstations-movies-";
    private const string ChannelShowPrefix = "tvstations-shows-";
    private const string ChannelRecentMovies = "tvstations-recent-movies";
    private const string ChannelRecentShows = "tvstations-recent-shows";
    private const string ChannelTopRatedMovies = "tvstations-toprated-movies";
    private const string ChannelTopRatedShows = "tvstations-toprated-shows";
    private const string ChannelDecadePrefix = "tvstations-decade-";
    private const string ChannelCollectionPrefix = "tvstations-collection-";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TvStationsService> _logger;

    private readonly Dictionary<string, (IReadOnlyList<BaseItem> Items, DateTime Expiry)> _itemCache = new();
    private readonly Dictionary<string, (List<string> Genres, DateTime Expiry)> _genreCache = new();
    private readonly object _cacheLock = new();

    public TvStationsService(ILibraryManager libraryManager, ILogger<TvStationsService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        lock (_cacheLock)
        {
            _itemCache.Clear();
            _genreCache.Clear();
        }
        _logger.LogDebug("TV Stations: library changed, cache cleared");
    }

    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
    }

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        => Task.FromResult(BuildChannels(includeDisabled: false));

    internal IEnumerable<ChannelInfo> GetAllChannelsIncludingDisabled()
        => BuildChannels(includeDisabled: true);

    private IEnumerable<ChannelInfo> BuildChannels(bool includeDisabled)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var channels = new List<ChannelInfo>();

        if (config.EnableMovies)
            BuildGenreChannels(channels, BaseItemKind.Movie, ChannelMoviePrefix, "Movies", config.MovieChannelStart, config);

        if (config.EnableShows)
            BuildGenreChannels(channels, BaseItemKind.Episode, ChannelShowPrefix, "Shows", config.ShowChannelStart, config);

        if (config.EnableRecentlyAdded)
        {
            TryAddChannel(channels, ChannelRecentMovies, "Recently Added Movies",
                config.RecentlyAddedChannelStart, config,
                () => QueryRecentlyAdded(BaseItemKind.Movie));

            TryAddChannel(channels, ChannelRecentShows, "Recently Added Shows",
                config.RecentlyAddedChannelStart + 1, config,
                () => QueryRecentlyAdded(BaseItemKind.Episode));
        }

        if (config.EnableTopRated)
        {
            TryAddChannel(channels, ChannelTopRatedMovies, "Top Rated Movies",
                config.TopRatedChannelStart, config,
                () => QueryTopRated(BaseItemKind.Movie));

            TryAddChannel(channels, ChannelTopRatedShows, "Top Rated Shows",
                config.TopRatedChannelStart + 1, config,
                () => QueryTopRated(BaseItemKind.Episode));
        }

        if (config.EnableDecadeChannels)
        {
            int[] decades = { 1970, 1980, 1990, 2000, 2010, 2020 };
            int decadeIdx = 0;
            foreach (var decade in decades)
            {
                var d = decade;
                var items = GetItemsCached($"decade-{d}", () => QueryByDecade(d));
                if (items.Count >= config.MinItemsPerChannel)
                {
                    channels.Add(MakeChannel(
                        $"{ChannelDecadePrefix}{d}s",
                        $"{d}s",
                        (config.DecadeChannelStart + decadeIdx).ToString(),
                        GetFirstImageUrl(items)));
                    decadeIdx++;
                }
            }
        }

        if (config.EnableCollections)
        {
            var collections = GetItemsCached("collections-list", QueryCollections);
            int colIdx = 0;
            foreach (var collection in collections.Take(config.MaxGenresPerType))
            {
                var slug = ToSlug(collection.Name ?? "unknown");
                var colId = collection.Id;
                var items = GetItemsCached($"collection-{slug}", () => QueryCollectionItems(colId));
                if (items.Count >= config.MinItemsPerChannel)
                {
                    channels.Add(MakeChannel(
                        $"{ChannelCollectionPrefix}{slug}",
                        collection.Name ?? "Unknown Collection",
                        (config.CollectionChannelStart + colIdx).ToString(),
                        GetFirstImageUrl(items)));
                    colIdx++;
                }
            }
        }

        if (!includeDisabled)
        {
            var disabled = new HashSet<string>(
                config.DisabledChannels ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            channels = channels.Where(c => !disabled.Contains(c.Id)).ToList();
        }

        var nameMap = (config.ChannelNameOverrides ?? new List<StringPair>())
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        channels = channels
            .Select(c => nameMap.TryGetValue(c.Id, out var customName)
                ? MakeChannel(c.Id, customName, c.Number ?? string.Empty, c.ImageUrl)
                : c)
            .ToList();

        _logger.LogInformation("TV Stations: providing {Count} channels", channels.Count);
        return channels;
    }

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        var items = GetItemsForChannel(channelId);
        var programs = new List<ProgramInfo>();

        if (items.Count == 0)
            return Task.FromResult<IEnumerable<ProgramInfo>>(programs);

        var isMovie = channelId.StartsWith(ChannelMoviePrefix, StringComparison.OrdinalIgnoreCase)
            || channelId.Equals(ChannelRecentMovies, StringComparison.OrdinalIgnoreCase)
            || channelId.Equals(ChannelTopRatedMovies, StringComparison.OrdinalIgnoreCase);

        foreach (var scheduled in ChannelScheduler.GetSchedule(items, startDateUtc, endDateUtc))
        {
            var item = scheduled.Item;
            programs.Add(new ProgramInfo
            {
                ChannelId = channelId,
                Id = $"{channelId}-{item.Id}-{scheduled.StartUtc.Ticks}",
                Name = FormatItemName(item),
                Overview = item.Overview,
                StartDate = scheduled.StartUtc,
                EndDate = scheduled.EndUtc,
                Genres = item.Genres?.ToList() ?? new List<string>(),
                IsMovie = isMovie,
                IsSeries = !isMovie,
                ImageUrl = GetItemImageUrl(item),
                OfficialRating = item.OfficialRating,
                CommunityRating = item.CommunityRating,
                ProductionYear = item.ProductionYear
            });
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    internal IReadOnlyList<BaseItem> GetItemsForChannel(string channelId)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        IReadOnlyList<BaseItem> items;

        if (channelId.StartsWith(ChannelMoviePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var genre = FromSlug(channelId[ChannelMoviePrefix.Length..]);
            items = GetItemsCached($"genre-movie-{genre}", () => QueryByGenre(BaseItemKind.Movie, genre));
        }
        else if (channelId.StartsWith(ChannelShowPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var genre = FromSlug(channelId[ChannelShowPrefix.Length..]);
            items = GetItemsCached($"genre-show-{genre}", () => QueryByGenre(BaseItemKind.Episode, genre));
        }
        else if (channelId.Equals(ChannelRecentMovies, StringComparison.OrdinalIgnoreCase))
        {
            items = GetItemsCached("recent-movies", () => QueryRecentlyAdded(BaseItemKind.Movie));
        }
        else if (channelId.Equals(ChannelRecentShows, StringComparison.OrdinalIgnoreCase))
        {
            items = GetItemsCached("recent-shows", () => QueryRecentlyAdded(BaseItemKind.Episode));
        }
        else if (channelId.Equals(ChannelTopRatedMovies, StringComparison.OrdinalIgnoreCase))
        {
            items = GetItemsCached("toprated-movies", () => QueryTopRated(BaseItemKind.Movie));
        }
        else if (channelId.Equals(ChannelTopRatedShows, StringComparison.OrdinalIgnoreCase))
        {
            items = GetItemsCached("toprated-shows", () => QueryTopRated(BaseItemKind.Episode));
        }
        else if (channelId.StartsWith(ChannelDecadePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var decadeStr = channelId[ChannelDecadePrefix.Length..].TrimEnd('s');
            items = int.TryParse(decadeStr, out var decade)
                ? GetItemsCached($"decade-{decade}", () => QueryByDecade(decade))
                : Array.Empty<BaseItem>();
        }
        else if (channelId.StartsWith(ChannelCollectionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var slug = channelId[ChannelCollectionPrefix.Length..];
            items = GetItemsCached($"collection-{slug}", () => QueryCollectionBySlug(slug));
        }
        else
        {
            items = Array.Empty<BaseItem>();
        }

        if (config.ShuffleChannels && items.Count > 0)
            items = ChannelScheduler.ShuffleItems(items, channelId);

        return items;
    }

    private void TryAddChannel(
        List<ChannelInfo> channels,
        string id,
        string name,
        int number,
        PluginConfiguration config,
        Func<IReadOnlyList<BaseItem>> factory)
    {
        var items = GetItemsCached(id, factory);
        if (items.Count >= config.MinItemsPerChannel)
            channels.Add(MakeChannel(id, name, number.ToString(), GetFirstImageUrl(items)));
    }

    private void BuildGenreChannels(
        List<ChannelInfo> channels,
        BaseItemKind kind,
        string prefix,
        string suffix,
        int baseNumber,
        PluginConfiguration config)
    {
        var genres = GetGenresCached($"genres-{kind}", () =>
        {
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                IsVirtualItem = false,
                Recursive = true
            };
            return _libraryManager.GetItemsResult(q).Items
                .SelectMany(i => i.Genres ?? Array.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        int idx = 0;
        foreach (var genre in genres.Take(config.MaxGenresPerType))
        {
            var g = genre;
            var items = GetItemsCached($"genre-{kind}-{g}", () => QueryByGenre(kind, g));
            if (items.Count < config.MinItemsPerChannel)
                continue;

            channels.Add(MakeChannel(
                prefix + ToSlug(g),
                $"{g} {suffix}",
                (baseNumber + idx).ToString(),
                GetFirstImageUrl(items)));
            idx++;
        }
    }

    private IReadOnlyList<BaseItem> GetItemsCached(string key, Func<IReadOnlyList<BaseItem>> factory)
    {
        var expiry = TimeSpan.FromMinutes(Plugin.Instance?.Configuration.CacheExpiryMinutes ?? 15);
        lock (_cacheLock)
        {
            if (_itemCache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Items;
            var result = factory();
            _itemCache[key] = (result, DateTime.UtcNow + expiry);
            return result;
        }
    }

    private List<string> GetGenresCached(string key, Func<List<string>> factory)
    {
        var expiry = TimeSpan.FromMinutes(Plugin.Instance?.Configuration.CacheExpiryMinutes ?? 15);
        lock (_cacheLock)
        {
            if (_genreCache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Genres;
            var result = factory();
            _genreCache[key] = (result, DateTime.UtcNow + expiry);
            return result;
        }
    }

    private IReadOnlyList<BaseItem> QueryByGenre(BaseItemKind kind, string genre)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true,
            Genres = new[] { genre }
        };
        return _libraryManager.GetItemsResult(query).Items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<BaseItem> QueryRecentlyAdded(BaseItemKind kind)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true,
            MinDateLastSaved = DateTime.UtcNow.AddDays(-(Plugin.Instance?.Configuration.RecentlyAddedDays ?? 90)),
            Limit = 200
        };
        return _libraryManager.GetItemsResult(query).Items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .OrderByDescending(i => i.DateCreated)
            .ToList();
    }

    private IReadOnlyList<BaseItem> QueryTopRated(BaseItemKind kind)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true,
            MinCommunityRating = Plugin.Instance?.Configuration.TopRatedMinRating ?? 7.5,
            Limit = 100
        };
        return _libraryManager.GetItemsResult(query).Items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .OrderByDescending(i => i.CommunityRating)
            .ToList();
    }

    private IReadOnlyList<BaseItem> QueryByDecade(int decade)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
            MinPremiereDate = new DateTime(decade, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MaxPremiereDate = new DateTime(decade + 9, 12, 31, 23, 59, 59, DateTimeKind.Utc)
        };
        return _libraryManager.GetItemsResult(query).Items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .OrderBy(i => i.PremiereDate)
            .ToList();
    }

    private IReadOnlyList<BaseItem> QueryCollections()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            IsVirtualItem = false,
            Recursive = true
        };
        return _libraryManager.GetItemsResult(query).Items
            .OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<BaseItem> QueryCollectionItems(Guid collectionId)
    {
        var query = new InternalItemsQuery
        {
            ParentId = collectionId,
            IsVirtualItem = false,
            Recursive = true
        };
        return _libraryManager.GetItemsResult(query).Items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<BaseItem> QueryCollectionBySlug(string slug)
    {
        var collections = GetItemsCached("collections-list", QueryCollections);
        var match = collections.FirstOrDefault(c => ToSlug(c.Name ?? string.Empty) == slug);
        return match is not null
            ? QueryCollectionItems(match.Id)
            : Array.Empty<BaseItem>();
    }

    private static ChannelInfo MakeChannel(string id, string name, string number, string? imageUrl) =>
        new ChannelInfo
        {
            Id = id,
            Name = name,
            Number = number,
            ChannelType = ChannelType.TV,
            ImageUrl = imageUrl
        };

    internal static string FormatItemName(BaseItem item)
    {
        if (item is Episode episode)
        {
            var series = episode.SeriesName ?? "Unknown Series";
            var s = episode.ParentIndexNumber?.ToString("D2") ?? "??";
            var e = episode.IndexNumber?.ToString("D2") ?? "??";
            var epName = episode.Name;
            return string.IsNullOrWhiteSpace(epName)
                ? $"{series} - S{s}E{e}"
                : $"{series} - S{s}E{e} - {epName}";
        }

        return item.Name ?? "Unknown";
    }

    private string? GetItemImageUrl(BaseItem item)
    {
        if (item.HasImage(ImageType.Primary))
        {
            var imgInfo = item.GetImageInfo(ImageType.Primary, 0);
            return imgInfo?.Path;
        }
        return null;
    }

    internal IReadOnlyList<ScheduledItem> GetScheduleForChannel(
        string channelId, DateTime startDateUtc, DateTime endDateUtc)
    {
        var items = GetItemsForChannel(channelId);
        if (items.Count == 0)
            return Array.Empty<ScheduledItem>();
        return ChannelScheduler.GetSchedule(items, startDateUtc, endDateUtc).ToList();
    }

    internal string? GetItemImagePathById(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
            return null;
        var path = GetItemImagePath(item);
        if (path is not null)
            return path;
        if (item is Episode episode)
        {
            var series = episode.Series;
            if (series is not null)
                return GetItemImagePath(series);
        }
        return null;
    }

    internal string? GetChannelImagePath(string channelId)
    {
        var items = GetItemsForChannel(channelId);
        if (items.Count == 0)
            return null;

        var scheduled = ChannelScheduler.GetCurrentItem(items, DateTime.UtcNow);
        var item = scheduled?.Item ?? items[0];

        var path = GetItemImagePath(item);
        if (path is not null)
            return path;

        // For episodes fall back to series art
        if (item is Episode episode)
        {
            var series = episode.Series;
            if (series is not null)
                return GetItemImagePath(series);
        }

        return null;
    }

    private static string? GetItemImagePath(BaseItem item)
    {
        if (item.HasImage(ImageType.Primary))
        {
            var info = item.GetImageInfo(ImageType.Primary, 0);
            if (info?.Path is not null)
                return info.Path;
        }
        return null;
    }

    private string? GetFirstImageUrl(IReadOnlyList<BaseItem> items)
        => items.Select(GetItemImageUrl).FirstOrDefault(u => u is not null);

    private static string ToSlug(string genre)
        => genre.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('/', '-')
            .Replace('&', '-')
            .Replace('\'', '-');

    private static string FromSlug(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w => w.Length > 0
            ? char.ToUpperInvariant(w[0]) + w[1..]
            : w));
    }
}
