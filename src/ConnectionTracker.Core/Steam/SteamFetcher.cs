using Microsoft.Extensions.Logging;

namespace ConnectionTracker.Core.Steam;

public static class SteamFetcher
{
    /// <summary>
    /// Takes a snapshot of the friends list. People are keyed by SteamID64, which never changes; their profile name
    /// (which people change often) is kept as the display name.
    /// </summary>
    public static async Task<Snapshot> FetchAsync(
        SteamClient client, string steamId, ListCounts? previous, ILogger log, CancellationToken ct = default)
    {
        var takenAt = DateTimeOffset.UtcNow;
        var friends = await client.GetFriendsAsync(steamId, ct);
        FetchGuard.Check("friends", friends.Count, expected: null, previous?.Friends);
        var players = await client.GetPlayersAsync(friends.Select(f => f.SteamId), ct);
        log.LogInformation("Steam: {Count} friends", friends.Count);

        return new Snapshot
        {
            Platform = Platforms.Steam,
            TakenAt = takenAt,
            Source = SnapshotSource.Fetch,
            Friends = [.. friends.Select(f => new Person(f.SteamId, players.GetValueOrDefault(f.SteamId)?.Name, FollowedAt: f.Since))],
        };
    }
}
