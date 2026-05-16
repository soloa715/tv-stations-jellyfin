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

        var totalTicks = items.Sum(GetRunTimeTicks);
        if (totalTicks <= 0)
            yield break;

        var elapsedAtWindow = (long)((windowStart - Epoch).TotalSeconds * TimeSpan.TicksPerSecond) % totalTicks;
        if (elapsedAtWindow < 0) elapsedAtWindow += totalTicks;

        // Find the item playing at windowStart and the offset into it
        long accumulated = 0;
        int startIdx = 0;
        long offsetIntoItem = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var dur = GetRunTimeTicks(items[i]);
            if (dur <= 0) continue;
            if (accumulated + dur > elapsedAtWindow)
            {
                startIdx = i;
                offsetIntoItem = elapsedAtWindow - accumulated;
                break;
            }
            accumulated += dur;
        }

        var cursor = windowStart - TimeSpan.FromTicks(offsetIntoItem);
        int idx = startIdx;

        while (cursor < windowEnd)
        {
            var item = items[idx % items.Count];
            var dur = TimeSpan.FromTicks(GetRunTimeTicks(item));
            var end = cursor + dur;

            if (end > windowStart)
                yield return new ScheduledItem { Item = item, StartUtc = cursor, EndUtc = end };

            cursor = end;
            idx++;
        }
    }

    public static IReadOnlyList<BaseItem> ShuffleItems(IReadOnlyList<BaseItem> items, string channelId)
    {
        var list = items.ToList();
        var seed = channelId.Aggregate(0, (acc, c) => acc * 31 + c);
        var rng = new Random(seed);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private static long GetRunTimeTicks(BaseItem item)
    {
        return item.RunTimeTicks ?? TimeSpan.FromHours(2).Ticks;
    }
}
