using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvStations.LiveTv;

public record ChannelSummary(
    string Id,
    string Name,
    string Number,
    int ItemCount,
    bool IsDisabled,
    string? NameOverride);

[ApiController]
[Route("tvstations")]
[AllowAnonymous]
public sealed class TvStationsController : ControllerBase
{
    private readonly TvStationsService _service;

    public TvStationsController(TvStationsService service)
    {
        _service = service;
    }

    /// <summary>Returns an M3U playlist of all genre channels.</summary>
    [HttpGet("m3u")]
    [Produces("audio/x-mpegurl")]
    public async Task<IActionResult> GetM3U(CancellationToken cancellationToken)
    {
        var channels = (await _service.GetChannelsAsync(cancellationToken)).ToList();
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var sb = new StringBuilder($"#EXTM3U x-tvg-url=\"{baseUrl}/tvstations/xmltv\"\n");
        foreach (var channel in channels)
        {
            var escapedId = Uri.EscapeDataString(channel.Id);
            sb.Append($"#EXTINF:-1 tvg-id=\"{channel.Id}\"");
            sb.Append($" tvg-name=\"{channel.Name}\"");
            sb.Append($" tvg-chno=\"{channel.Number}\"");
            sb.Append($" tvg-logo=\"{baseUrl}/tvstations/image/{escapedId}\"");
            sb.Append($" group-title=\"{GetGroupTitle(channel.Id)}\"");
            sb.Append($",{channel.Name}\n");
            sb.Append($"{baseUrl}/tvstations/hls/{escapedId}.m3u8\n");
        }

        return Content(sb.ToString(), "audio/x-mpegurl", Encoding.UTF8);
    }

    /// <summary>Streams the currently scheduled item for a channel.</summary>
    [HttpGet("stream/{channelId}")]
    public IActionResult GetStream(string channelId)
    {
        channelId = Uri.UnescapeDataString(channelId);
        var items = _service.GetItemsForChannel(channelId);
        if (items.Count == 0)
            return NotFound("No content for this channel.");

        var scheduled = ChannelScheduler.GetCurrentItem(items, DateTime.UtcNow);
        if (scheduled is null)
            return NotFound("Nothing currently scheduled.");

        var path = scheduled.Item.Path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return NotFound("Media file not found.");

        return PhysicalFile(path, GetMimeType(path), enableRangeProcessing: true);
    }

    /// <summary>Serves a library media file directly by item GUID.</summary>
    [HttpGet("file/{itemId:guid}")]
    public IActionResult GetFile(Guid itemId)
    {
        var path = _service.GetFilePathById(itemId);
        if (path is null || !System.IO.File.Exists(path))
            return NotFound();
        return PhysicalFile(path, GetMimeType(path), enableRangeProcessing: true);
    }

    /// <summary>Returns an HLS VOD playlist for a channel covering ~12 hours of upcoming content.</summary>
    [HttpGet("hls/{channelId}.m3u8")]
    [Produces("application/vnd.apple.mpegurl")]
    public IActionResult GetHlsPlaylist(string channelId)
    {
        channelId = Uri.UnescapeDataString(channelId);
        var items = _service.GetItemsForChannel(channelId);
        if (items.Count == 0)
            return NotFound("No content for this channel.");

        var now = DateTime.UtcNow;
        var schedule = _service.GetScheduleForChannel(channelId, now.AddSeconds(-30), now.AddHours(12))
            .Take(50)
            .ToList();

        if (schedule.Count == 0)
            return NotFound("Nothing scheduled.");

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var maxDurSeconds = (int)Math.Ceiling(schedule.Max(s => (s.EndUtc - s.StartUtc).TotalSeconds));

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{maxDurSeconds}");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

        bool first = true;
        foreach (var s in schedule)
        {
            var dur = (s.EndUtc - s.StartUtc).TotalSeconds;
            if (!first) sb.AppendLine("#EXT-X-DISCONTINUITY");
            sb.AppendLine($"#EXTINF:{dur:F3},{Escape(TvStationsService.FormatItemName(s.Item))}");
            sb.AppendLine($"{baseUrl}/tvstations/file/{s.Item.Id}");
            first = false;
        }
        sb.AppendLine("#EXT-X-ENDLIST");

        return Content(sb.ToString(), "application/vnd.apple.mpegurl", Encoding.UTF8);
    }

    /// <summary>Returns an XMLTV EPG covering a configurable window (default 3 days).</summary>
    [HttpGet("xmltv")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetXmlTv(CancellationToken cancellationToken)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var cached = _service.GetCachedXmlTv(baseUrl);
        if (cached is not null)
            return Content(cached, "application/xml", Encoding.UTF8);

        var channels = (await _service.GetChannelsAsync(cancellationToken)).ToList();
        var now = DateTime.UtcNow;
        var epgConfig = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var epgDays = Math.Clamp(epgConfig.EpgWindowDays, 1, 14);
        var windowStart = now.AddHours(-1);
        var windowEnd = now.AddDays(epgDays);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE tv SYSTEM \"xmltv.dtd\">\n");
        sb.Append("<tv generator-info-name=\"TV Stations Plugin\">\n");

        foreach (var channel in channels)
        {
            var logoUrl = $"{baseUrl}/tvstations/image/{Uri.EscapeDataString(channel.Id)}";
            sb.Append($"  <channel id=\"{Escape(channel.Id)}\">");
            sb.Append($"<display-name>{Escape(channel.Name)}</display-name>");
            sb.Append($"<icon src=\"{logoUrl}\"/>");
            sb.Append("</channel>\n");
        }

        foreach (var channel in channels)
        {
            var schedule = _service.GetScheduleForChannel(channel.Id, windowStart, windowEnd);

            foreach (var s in schedule)
            {
                var item = s.Item;
                var start = s.StartUtc.ToString("yyyyMMddHHmmss") + " +0000";
                var stop = s.EndUtc.ToString("yyyyMMddHHmmss") + " +0000";
                var iconUrl = $"{baseUrl}/tvstations/itemimage/{item.Id}";
                sb.Append($"  <programme start=\"{start}\" stop=\"{stop}\" channel=\"{Escape(channel.Id)}\">\n");
                sb.Append($"    <title lang=\"en\">{Escape(TvStationsService.FormatItemName(item))}</title>\n");
                if (!string.IsNullOrEmpty(item.Overview))
                    sb.Append($"    <desc lang=\"en\">{Escape(item.Overview)}</desc>\n");
                sb.Append($"    <icon src=\"{iconUrl}\"/>\n");
                sb.Append("  </programme>\n");
            }
        }

        sb.Append("</tv>\n");
        var xml = sb.ToString();
        _service.SetCachedXmlTv(baseUrl, xml);
        return Content(xml, "application/xml", Encoding.UTF8);
    }

    /// <summary>Serves the primary image for a specific library item by its Jellyfin ID.</summary>
    [HttpGet("itemimage/{itemId:guid}")]
    public IActionResult GetItemImage(Guid itemId)
    {
        var imagePath = _service.GetItemImagePathById(itemId);
        if (imagePath is null || !System.IO.File.Exists(imagePath))
            return NotFound();
        Response.Headers["Cache-Control"] = "public, max-age=3600";
        return PhysicalFile(imagePath, GetImageMimeType(imagePath));
    }

    /// <summary>Serves the primary image of the currently playing item for a channel.</summary>
    [HttpGet("image/{channelId}")]
    public IActionResult GetChannelImage(string channelId)
    {
        channelId = Uri.UnescapeDataString(channelId);
        var imagePath = _service.GetChannelImagePath(channelId);

        if (imagePath is null || !System.IO.File.Exists(imagePath))
            return NotFound();

        Response.Headers["Cache-Control"] = "public, max-age=3600";
        return PhysicalFile(imagePath, GetImageMimeType(imagePath));
    }

    private static string GetGroupTitle(string channelId) => channelId switch
    {
        var id when id.StartsWith("tvstations-movies-", StringComparison.OrdinalIgnoreCase) => "TV Stations - Movies",
        var id when id.StartsWith("tvstations-shows-", StringComparison.OrdinalIgnoreCase) => "TV Stations - Shows",
        var id when id.StartsWith("tvstations-recent-", StringComparison.OrdinalIgnoreCase) => "TV Stations - Recently Added",
        var id when id.StartsWith("tvstations-toprated-", StringComparison.OrdinalIgnoreCase) => "TV Stations - Top Rated",
        var id when id.StartsWith("tvstations-decade-", StringComparison.OrdinalIgnoreCase) => "TV Stations - Decades",
        var id when id.StartsWith("tvstations-collection-", StringComparison.OrdinalIgnoreCase) => "TV Stations - Collections",
        _ => "TV Stations"
    };

    private static string GetImageMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };

    /// <summary>Returns a JSON list of all channels including disabled status and item counts.</summary>
    [HttpGet("channels")]
    [Produces("application/json")]
    public IActionResult GetChannels()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var disabled = new HashSet<string>(
            config.DisabledChannels ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        var overrides = (config.ChannelNameOverrides ?? new List<StringPair>())
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        var summaries = _service.GetAllChannelsIncludingDisabled()
            .Select(c => new ChannelSummary(
                c.Id,
                c.Name,
                c.Number ?? string.Empty,
                _service.GetItemsForChannel(c.Id).Count,
                disabled.Contains(c.Id),
                overrides.TryGetValue(c.Id, out var o) ? o : null))
            .ToList();

        return Ok(summaries);
    }

    private static string GetMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mkv" => "video/x-matroska",
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".ts" => "video/mp2t",
            ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            ".flv" => "video/x-flv",
            _ => "video/mp4"
        };

    private static string Escape(string? text) =>
        System.Security.SecurityElement.Escape(text ?? string.Empty) ?? string.Empty;
}
