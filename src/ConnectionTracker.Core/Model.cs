namespace ConnectionTracker.Core;

/// <param name="FollowedAt">When the follow happened. Only official exports carry this.</param>
public sealed record Person(string Username, string? FullName = null, string? Pk = null, DateTimeOffset? FollowedAt = null);

public static class SnapshotSource
{
    public const string Fetch = "fetch";
    public const string Export = "export";
}

/// <summary>Full follower and following lists as they were at one point in time.</summary>
public sealed class Snapshot
{
    public required DateTimeOffset TakenAt { get; init; }
    public required string Source { get; init; }
    /// <summary>File name of the export this snapshot was imported from.</summary>
    public string? SourceRef { get; init; }
    /// <summary>Totals the profile reported at fetch time, for sanity checks.</summary>
    public int? FollowersExpected { get; init; }
    public int? FollowingExpected { get; init; }
    public List<Person> Followers { get; init; } = [];
    public List<Person> Following { get; init; } = [];

    public List<Person> List(FollowList list) => list == FollowList.Followers ? Followers : Following;
}

public enum FollowList { Followers, Following }

public static class Usernames
{
    public static string Normalize(string username) => username.Trim().TrimStart('@').ToLowerInvariant();
}
