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

        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var channel in channels)
        {
            var escapedId = Uri.EscapeDataString(channel.Id);
            sb.Append($"#EXTINF:-1 tvg-id=\"{channel.Id}\"");
            sb.Append($" tvg-name=\"{channel.Name}\"");
            sb.Append($" tvg-chno=\"{channel.Number}\"");
            sb.Append($" tvg-logo=\"{baseUrl}/tvstations/image/{escapedId}\"");
            sb.Append($" group-title=\"TV Stations\"");
            sb.Append($",{channel.Name}\n");
            sb.Append($"{baseUrl}/tvstations/stream/{escapedId}\n");
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

    /// <summary>Returns an XMLTV EPG covering the next 24 hours.</summary>
    [HttpGet("xmltv")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetXmlTv(CancellationToken cancellationToken)
    {
        var channels = (await _service.GetChannelsAsync(cancellationToken)).ToList();
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-1);
        var windowEnd = now.AddHours(24);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE tv SYSTEM \"xmltv.dtd\">\n");
        sb.Append("<tv generator-info-name=\"TV Stations Plugin\">\n");

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
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
            var programs = await _service.GetProgramsAsync(
                channel.Id, windowStart, windowEnd, cancellationToken);

            foreach (var p in programs)
            {
                var start = p.StartDate.ToString("yyyyMMddHHmmss") + " +0000";
                var stop = p.EndDate.ToString("yyyyMMddHHmmss") + " +0000";
                sb.Append($"  <programme start=\"{start}\" stop=\"{stop}\" channel=\"{Escape(channel.Id)}\">\n");
                sb.Append($"    <title lang=\"en\">{Escape(p.Name)}</title>\n");
                if (!string.IsNullOrEmpty(p.Overview))
                    sb.Append($"    <desc lang=\"en\">{Escape(p.Overview)}</desc>\n");
                sb.Append("  </programme>\n");
            }
        }

        sb.Append("</tv>\n");
        return Content(sb.ToString(), "application/xml", Encoding.UTF8);
    }

    /// <summary>Redirects to the primary image of the currently playing item for a channel.</summary>
    [HttpGet("image/{channelId}")]
    public IActionResult GetChannelImage(string channelId)
    {
        channelId = Uri.UnescapeDataString(channelId);
        var items = _service.GetItemsForChannel(channelId);
        if (items.Count == 0)
            return NotFound();

        var scheduled = ChannelScheduler.GetCurrentItem(items, DateTime.UtcNow);
        var item = scheduled?.Item ?? items[0];
        return Redirect($"/Items/{item.Id}/Images/Primary");
    }

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
