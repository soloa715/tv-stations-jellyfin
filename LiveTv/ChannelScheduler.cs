using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.TvStations.LiveTv;

public sealed class ScheduledItem
{
    public required BaseItem Item { get; init; }
    public required DateTime StartUtc { get; init; }
    public required DateTime EndUtc { get; init; }
}

public static class ChannelScheduler
{
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static ScheduledItem? GetCurrentItem(IReadOnlyList<BaseItem> items, DateTime utcNow)
    {
        if (items.Count == 0)
            return null;

        var totalTicks = items.Sum(i => GetRunTimeTicks(i));
        if (totalTicks <= 0)
            return null;

        var elapsedTicks = (long)((utcNow - Epoch).TotalSeconds * TimeSpan.TicksPerSecond) % totalTicks;
        if (elapsedTicks < 0)
            elapsedTicks += totalTicks;

        long accumulated = 0;
        foreach (var item in items)
        {
            var duration = GetRunTimeTicks(item);
            if (duration <= 0)
                continue;

            if (accumulated + duration > elapsedTicks)
            {
                var itemStartOffset = elapsedTicks - accumulated;
                var startUtc = utcNow - TimeSpan.FromTicks(itemStartOffset);
                var endUtc = startUtc + TimeSpan.FromTicks(duration);
                return new ScheduledItem { Item = item, StartUtc = startUtc, EndUtc = endUtc };
            }

            accumulated += duration;
        }

        var last = items[^1];
        return new ScheduledItem
        {
            Item = last,
            StartUtc = utcNow,
            EndUtc = utcNow + TimeSpan.FromTicks(GetRunTimeTicks(last))
        };
    }

    public static IEnumerable<ScheduledItem> GetSchedule(
        IReadOnlyList<BaseItem> items,
        DateTime windowStart,
        DateTime windowEnd)
    {
        if (items.Count == 0)
            yield break;

        var totalTicks = items.Sum(i => GetRunTimeTicks(i));
        if (totalTicks <= 0)
            yield break;

        var current = GetCurrentItem(items, windowStart);
        if (current is null)
            yield break;

        var cursor = current.StartUtc;

        while (cursor < windowEnd)
        {
            var item = GetCurrentItem(items, cursor + TimeSpan.FromSeconds(1));
            if (item is null)
                break;

            if (item.EndUtc > windowStart)
                yield return item;

            cursor = item.EndUtc;

            if (cursor >= windowEnd)
                break;
        }
    }

    private static long GetRunTimeTicks(BaseItem item)
    {
        return item.RunTimeTicks ?? TimeSpan.FromHours(2).Ticks;
    }
}
