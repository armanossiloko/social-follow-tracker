namespace ConnectionTracker.Core;

/// <param name="Username">
/// The platform's key for the person: the Instagram username, the Steam id, or the name a Facebook export lists.
/// </param>
/// <param name="FullName">Display name, if known.</param>
/// <param name="Pk">Instagram's account id, which survives renames. Only fetched Instagram data has it.</param>
/// <param name="FollowedAt">
/// When they were added to this list. Exports and Steam carry this. On <see cref="ConnectionList.RemovedFriends"/>
/// it's when you removed them.
/// </param>
public sealed record Person(string Username, string? FullName = null, string? Pk = null, DateTimeOffset? FollowedAt = null);

public static class SnapshotSource
{
    public const string Fetch = "fetch";
    public const string Export = "export";
}

/// <param name="Friends">
/// True for platforms with mutual friendships (one friends list) rather than followers and following.
/// </param>
/// <param name="CanFetch">True if the app can fetch the lists itself; otherwise data only comes from exports.</param>
public sealed record PlatformInfo(string Id, string Name, bool Friends, bool CanFetch);

public static class Platforms
{
    public const string Instagram = "instagram";
    public const string Facebook = "facebook";
    public const string Steam = "steam";

    public static readonly IReadOnlyList<PlatformInfo> All =
    [
        new(Instagram, "Instagram", Friends: false, CanFetch: true),
        new(Facebook, "Facebook", Friends: true, CanFetch: false),
        new(Steam, "Steam", Friends: true, CanFetch: true),
    ];

    public static PlatformInfo? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    public static PlatformInfo Get(string id) => Find(id) ?? throw new ArgumentException($"Unknown platform {id}.", nameof(id));
}

/// <summary>One account's connections on one platform as they were at one point in time.</summary>
public sealed class Snapshot
{
    public string Platform { get; init; } = Platforms.Instagram;
    public required DateTimeOffset TakenAt { get; init; }
    public required string Source { get; init; }
    /// <summary>File name of the export this snapshot was imported from.</summary>
    public string? SourceRef { get; init; }
    /// <summary>Totals the profile reported at fetch time, for sanity checks.</summary>
    public int? FollowersExpected { get; init; }
    public int? FollowingExpected { get; init; }
    public List<Person> Followers { get; init; } = [];
    public List<Person> Following { get; init; } = [];
    public List<Person> Friends { get; init; } = [];
    /// <summary>People the platform says you removed as a friend (Facebook exports list them).</summary>
    public List<Person> RemovedFriends { get; init; } = [];

    public List<Person> List(ConnectionList list) => list switch
    {
        ConnectionList.Followers => Followers,
        ConnectionList.Following => Following,
        ConnectionList.Friends => Friends,
        _ => RemovedFriends,
    };
}

/// <remarks>Stored as a number; don't renumber.</remarks>
public enum ConnectionList { Followers = 0, Following = 1, Friends = 2, RemovedFriends = 3 }

public static class Usernames
{
    public static string Normalize(string username) => username.Trim().TrimStart('@').ToLowerInvariant();
}
