using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStations.LiveTv;

public sealed class TvStationsService : ILiveTvService
{
    private const string ChannelMoviePrefix = "tvstations-movies-";
    private const string ChannelShowPrefix = "tvstations-shows-";
    private const int MovieChannelBase = 100;
    private const int ShowChannelBase = 200;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TvStationsService> _logger;

    public TvStationsService(ILibraryManager libraryManager, ILogger<TvStationsService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "TV Stations";

    public string HomePageUrl => "https://github.com/soloa715/tv-stations-jellyfin";

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var channels = new List<ChannelInfo>();

        if (config.EnableMovies)
        {
            var movieGenres = GetGenresForKind(BaseItemKind.Movie);
            int idx = 0;
            foreach (var genre in movieGenres.Take(config.MaxGenresPerType))
            {
                var items = GetItemsForGenre(BaseItemKind.Movie, genre);
                if (items.Count < config.MinItemsPerChannel)
                    continue;

                channels.Add(new ChannelInfo
                {
                    Id = ChannelMoviePrefix + ToSlug(genre),
                    Name = genre + " Movies",
                    Number = (MovieChannelBase + idx).ToString(),
                    ChannelType = ChannelType.TV,
                    ImageUrl = GetGenreImageUrl(BaseItemKind.Movie, genre)
                });
                idx++;
            }
        }

        if (config.EnableShows)
        {
            var showGenres = GetGenresForKind(BaseItemKind.Episode);
            int idx = 0;
            foreach (var genre in showGenres.Take(config.MaxGenresPerType))
            {
                var items = GetItemsForGenre(BaseItemKind.Episode, genre);
                if (items.Count < config.MinItemsPerChannel)
                    continue;

                channels.Add(new ChannelInfo
                {
                    Id = ChannelShowPrefix + ToSlug(genre),
                    Name = genre + " Shows",
                    Number = (ShowChannelBase + idx).ToString(),
                    ChannelType = ChannelType.TV,
                    ImageUrl = GetGenreImageUrl(BaseItemKind.Episode, genre)
                });
                idx++;
            }
        }

        _logger.LogInformation("TV Stations: providing {Count} channels", channels.Count);
        return Task.FromResult<IEnumerable<ChannelInfo>>(channels);
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

        var isMovie = channelId.StartsWith(ChannelMoviePrefix, StringComparison.OrdinalIgnoreCase);

        foreach (var scheduled in ChannelScheduler.GetSchedule(items, startDateUtc, endDateUtc))
        {
            var item = scheduled.Item;
            programs.Add(new ProgramInfo
            {
                ChannelId = channelId,
                Id = $"{channelId}-{item.Id}-{scheduled.StartUtc.Ticks}",
                Name = item.Name ?? "Unknown",
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

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var source = BuildMediaSource(channelId);
        return Task.FromResult(source is not null
            ? new List<MediaSourceInfo> { source }
            : new List<MediaSourceInfo>());
    }

    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        var source = BuildMediaSource(channelId);
        if (source is null)
            throw new InvalidOperationException($"No stream available for channel {channelId}");
        return Task.FromResult(source);
    }

    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ResetTuner(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo { RecordNewOnly = true });

    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<TimerInfo>());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo updatedTimer, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private IReadOnlyList<BaseItem> GetItemsForChannel(string channelId)
    {
        if (channelId.StartsWith(ChannelMoviePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var genre = FromSlug(channelId[ChannelMoviePrefix.Length..]);
            return GetItemsForGenre(BaseItemKind.Movie, genre);
        }

        if (channelId.StartsWith(ChannelShowPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var genre = FromSlug(channelId[ChannelShowPrefix.Length..]);
            return GetItemsForGenre(BaseItemKind.Episode, genre);
        }

        return Array.Empty<BaseItem>();
    }

    private List<string> GetGenresForKind(BaseItemKind kind)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true
        };

        var result = _libraryManager.GetItemsResult(query);
        return result.Items
            .SelectMany(i => i.Genres ?? Array.Empty<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<BaseItem> GetItemsForGenre(BaseItemKind kind, string genre)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true,
            Genres = new[] { genre }
        };

        var result = _libraryManager.GetItemsResult(query);
        return result.Items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private MediaSourceInfo? BuildMediaSource(string channelId)
    {
        var items = GetItemsForChannel(channelId);
        if (items.Count == 0)
            return null;

        var scheduled = ChannelScheduler.GetCurrentItem(items, DateTime.UtcNow);
        if (scheduled is null)
            return null;

        var item = scheduled.Item;
        if (string.IsNullOrEmpty(item.Path))
            return null;

        return new MediaSourceInfo
        {
            Id = $"{channelId}-stream",
            Path = item.Path,
            Protocol = MediaProtocol.File,
            IsRemote = false,
            ReadAtNativeFramerate = false,
            SupportsTranscoding = true,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            Name = item.Name ?? "Unknown",
            Container = Path.GetExtension(item.Path).TrimStart('.'),
            RunTimeTicks = item.RunTimeTicks
        };
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

    private string? GetGenreImageUrl(BaseItemKind kind, string genre)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true,
            Genres = new[] { genre },
            Limit = 1
        };

        var item = _libraryManager.GetItemsResult(query).Items.FirstOrDefault();
        return item is not null ? GetItemImageUrl(item) : null;
    }

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
