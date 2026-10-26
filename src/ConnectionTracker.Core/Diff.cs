namespace ConnectionTracker.Core;

public enum EventKind
{
    FollowedYou,
    /// <summary>They stopped following you.</summary>
    UnfollowedYou,
    YouFollowed,
    /// <summary>Gone from your following list only: you unfollowed them, or they removed you as a follower.</summary>
    LeftYourFollowing,
    /// <summary>Gone from both lists at once: they blocked you, or deactivated/deleted their account.</summary>
    Vanished,
    /// <summary>Same account (matched by Instagram's id) under a new username.</summary>
    Renamed,
}

/// <param name="At">The snapshot in which the change was first seen.</param>
/// <param name="After">The snapshot before it: the change happened between <paramref name="After"/> and <paramref name="At"/>.</param>
/// <param name="PreviousUsername">For <see cref="EventKind.Renamed"/>: the old username.</param>
/// <param name="FollowedSince">For removals: since when the follow had existed.</param>
public sealed record FollowEvent(
    DateTimeOffset At,
    EventKind Kind,
    string Username,
    DateTimeOffset? After = null,
    string? PreviousUsername = null,
    Since? FollowedSince = null);

/// <param name="Exact">True when the export told us the follow time; false means "first seen in this snapshot".</param>
/// <param name="AtOrBefore">True when present in the very first snapshot, so the follow happened at or before <paramref name="At"/>.</param>
public sealed record Since(DateTimeOffset At, bool Exact, bool AtOrBefore);

public sealed record Relationships(List<string> NotFollowingBack, List<string> Fans, List<string> Mutual);

public sealed record History(List<FollowEvent> Events, Dictionary<string, Since> FollowerSince, Dictionary<string, Since> FollowingSince);

public static class Diff
{
    public static Relationships Relate(Snapshot snapshot)
    {
        var followers = snapshot.Followers.Select(p => p.Username).ToHashSet();
        var following = snapshot.Following.Select(p => p.Username).ToHashSet();
        return new(
            NotFollowingBack: [.. following.Except(followers).Order(StringComparer.Ordinal)], // you follow them, they don't follow you
            Fans: [.. followers.Except(following).Order(StringComparer.Ordinal)], // they follow you, you don't follow them
            Mutual: [.. followers.Intersect(following).Order(StringComparer.Ordinal)]);
    }

    public static List<FollowEvent> Changes(Snapshot previous, Snapshot current)
    {
        // Translate renamed accounts to their new name first, so a rename isn't reported as unfollow + follow.
        var renames = Renames(previous, current);
        HashSet<string> Before(List<Person> people) => people.Select(p => renames.GetValueOrDefault(p.Username, p.Username)).ToHashSet();
        HashSet<string> After(List<Person> people) => people.Select(p => p.Username).ToHashSet();

        var (followersBefore, followersAfter) = (Before(previous.Followers), After(current.Followers));
        var (followingBefore, followingAfter) = (Before(previous.Following), After(current.Following));
        var lostFollowers = followersBefore.Except(followersAfter).ToHashSet();
        var lostFollowing = followingBefore.Except(followingAfter).ToHashSet();
        var vanished = lostFollowers.Intersect(lostFollowing).ToHashSet();

        var events = new List<FollowEvent>();
        events.AddRange(renames.OrderBy(r => r.Value, StringComparer.Ordinal)
            .Select(r => new FollowEvent(current.TakenAt, EventKind.Renamed, r.Value, previous.TakenAt, PreviousUsername: r.Key)));
        Add(EventKind.FollowedYou, followersAfter.Except(followersBefore));
        Add(EventKind.UnfollowedYou, lostFollowers.Except(vanished));
        Add(EventKind.Vanished, vanished);
        Add(EventKind.YouFollowed, followingAfter.Except(followingBefore));
        Add(EventKind.LeftYourFollowing, lostFollowing.Except(vanished));
        return events;

        void Add(EventKind kind, IEnumerable<string> usernames) =>
            events.AddRange(usernames.Order(StringComparer.Ordinal).Select(u => new FollowEvent(current.TakenAt, kind, u, previous.TakenAt)));
    }

    /// <summary>Old username to new username, for accounts whose id appears under a different name. Needs ids, i.e. fetched snapshots.</summary>
    internal static Dictionary<string, string> Renames(Snapshot previous, Snapshot current)
    {
        static Dictionary<string, string> ById(Snapshot s) => s.Followers.Concat(s.Following)
            .Where(p => !string.IsNullOrEmpty(p.Pk))
            .DistinctBy(p => p.Pk)
            .ToDictionary(p => p.Pk!, p => p.Username);

        var now = ById(current);
        var renames = new Dictionary<string, string>();
        foreach (var (pk, oldName) in ById(previous))
            if (now.TryGetValue(pk, out var newName) && newName != oldName)
                renames[oldName] = newName;
        return renames;
    }

    /// <summary>All change events (oldest first), plus how long each current person has been on each list.</summary>
    public static History BuildHistory(IReadOnlyList<Snapshot> snapshots)
    {
        var events = new List<FollowEvent>();
        var followerSince = new Dictionary<string, Since>();
        var followingSince = new Dictionary<string, Since>();
        for (var i = 0; i < snapshots.Count; i++)
        {
            if (i > 0)
            {
                foreach (var e in Changes(snapshots[i - 1], snapshots[i]))
                {
                    if (e.Kind == EventKind.Renamed)
                    {
                        Move(followerSince, e.PreviousUsername!, e.Username);
                        Move(followingSince, e.PreviousUsername!, e.Username);
                    }
                    // Record how long the follow lasted before Track() forgets it.
                    var since = e.Kind switch
                    {
                        EventKind.UnfollowedYou or EventKind.Vanished => followerSince.GetValueOrDefault(e.Username),
                        EventKind.LeftYourFollowing => followingSince.GetValueOrDefault(e.Username),
                        _ => null,
                    };
                    events.Add(since is null ? e : e with { FollowedSince = since });
                }
            }
            Track(snapshots[i].Followers, followerSince, snapshots[i], i == 0);
            Track(snapshots[i].Following, followingSince, snapshots[i], i == 0);
        }
        return new(events, followerSince, followingSince);

        static void Move(Dictionary<string, Since> since, string from, string to)
        {
            if (since.Remove(from, out var value)) since[to] = value;
        }

        static void Track(List<Person> people, Dictionary<string, Since> since, Snapshot snapshot, bool first)
        {
            var present = people.Select(p => p.Username).ToHashSet();
            foreach (var gone in since.Keys.Where(k => !present.Contains(k)).ToList())
                since.Remove(gone);
            foreach (var person in people)
            {
                if (person.FollowedAt is { } exact)
                    since[person.Username] = new Since(exact, Exact: true, AtOrBefore: false);
                else if (!since.ContainsKey(person.Username))
                    since[person.Username] = new Since(snapshot.TakenAt, Exact: false, AtOrBefore: first);
            }
        }
    }
}
