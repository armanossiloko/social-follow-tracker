using ConnectionTracker.Core;

namespace ConnectionTracker.Tests;

public class DiffTests
{
    internal static Snapshot Snap(int day, string[] followers, string[] following, string source = SnapshotSource.Fetch) => new()
    {
        TakenAt = new DateTimeOffset(2026, 10, day, 9, 0, 0, TimeSpan.Zero),
        Source = source,
        Followers = [.. followers.Select(u => new Person(u))],
        Following = [.. following.Select(u => new Person(u))],
    };

    [Fact]
    public void Splits_into_not_following_back_fans_and_mutual()
    {
        var r = Diff.Relate(Snap(1, followers: ["ann", "ben", "cat"], following: ["ben", "cat", "dan", "eve"]));
        Assert.Equal(["dan", "eve"], r.NotFollowingBack);
        Assert.Equal(["ann"], r.Fans);
        Assert.Equal(["ben", "cat"], r.Mutual);
    }

    private static (EventKind, string)[] Kinds(IEnumerable<FollowEvent> events) => [.. events.Select(e => (e.Kind, e.Username))];

    [Fact]
    public void Classifies_follows_unfollows_and_removals()
    {
        var before = Snap(1, followers: ["ann", "ben", "eve"], following: ["ben", "cat"]);
        var after = Snap(2, followers: ["ben", "eve", "zoe"], following: ["dan"]);

        var events = Diff.Changes(before, after);

        Assert.Equal(
        [
            (EventKind.FollowedYou, "zoe"),
            (EventKind.UnfollowedYou, "ann"),
            (EventKind.YouFollowed, "dan"),
            (EventKind.LeftYourFollowing, "ben"), // still follows you: you unfollowed them, or they removed you
            (EventKind.LeftYourFollowing, "cat"),
        ], Kinds(events));
        Assert.All(events, e => Assert.Equal(before.TakenAt, e.After));
        Assert.All(events, e => Assert.Equal(after.TakenAt, e.At));
    }

    [Fact]
    public void Gone_from_both_lists_at_once_is_one_vanished_event()
    {
        var events = Diff.Changes(
            Snap(1, followers: ["ann", "ben"], following: ["ann", "ben"]),
            Snap(2, followers: ["ben"], following: ["ben"]));

        Assert.Equal([(EventKind.Vanished, "ann")], Kinds(events));
    }

    [Fact]
    public void A_rename_is_matched_by_id_instead_of_reported_as_unfollow_and_follow()
    {
        var before = new Snapshot
        {
            TakenAt = DateTimeOffset.UnixEpoch, Source = SnapshotSource.Fetch,
            Followers = [new Person("ann", Pk: "1"), new Person("ben", Pk: "2")],
            Following = [new Person("ann", Pk: "1")],
        };
        var after = new Snapshot
        {
            TakenAt = DateTimeOffset.UnixEpoch.AddDays(1), Source = SnapshotSource.Fetch,
            Followers = [new Person("ann.new", Pk: "1")], // renamed; ben (2) unfollowed
            Following = [new Person("ann.new", Pk: "1")],
        };

        var events = Diff.Changes(before, after);

        Assert.Equal([(EventKind.Renamed, "ann.new"), (EventKind.UnfollowedYou, "ben")], Kinds(events));
        Assert.Equal("ann", events[0].PreviousUsername);
    }

    [Fact]
    public void Unfollow_events_remember_how_long_the_follow_lasted()
    {
        var snaps = new[]
        {
            Snap(1, followers: ["ann"], following: []),
            Snap(2, followers: ["ann", "ben"], following: []),
            Snap(3, followers: [], following: []),
        };

        var lost = Diff.BuildHistory(snaps).Events.Where(e => e.Kind == EventKind.UnfollowedYou).ToDictionary(e => e.Username);

        Assert.Equal(snaps[0].TakenAt, lost["ann"].FollowedSince!.At);
        Assert.True(lost["ann"].FollowedSince!.AtOrBefore); // was there from the first snapshot
        Assert.Equal(snaps[1].TakenAt, lost["ben"].FollowedSince!.At);
        Assert.Equal(snaps[1].TakenAt, lost["ben"].After);
    }

    [Fact]
    public void History_tracks_since_dates_and_resets_after_a_gap()
    {
        var snaps = new[]
        {
            Snap(1, followers: ["ann"], following: []),
            Snap(2, followers: ["ann", "ben"], following: []),
            Snap(3, followers: ["ben"], following: []),
            Snap(4, followers: ["ann", "ben"], following: []),
        };

        var h = Diff.BuildHistory(snaps);

        Assert.Equal(
            [(EventKind.FollowedYou, "ben"), (EventKind.UnfollowedYou, "ann"), (EventKind.FollowedYou, "ann")],
            h.Events.Select(e => (e.Kind, e.Username)));
        Assert.Equal(snaps[1].TakenAt, h.FollowerSince["ben"].At);
        Assert.False(h.FollowerSince["ben"].AtOrBefore);
        Assert.Equal(snaps[3].TakenAt, h.FollowerSince["ann"].At); // came back on day 4
    }

    [Fact]
    public void Exact_follow_time_from_an_export_wins_over_first_seen()
    {
        var exact = new DateTimeOffset(2024, 3, 3, 0, 0, 0, TimeSpan.Zero);
        var first = Snap(1, followers: ["ann"], following: []);
        var export = new Snapshot
        {
            TakenAt = first.TakenAt.AddDays(1),
            Source = SnapshotSource.Export,
            Followers = [new Person("ann", FollowedAt: exact)],
        };

        var h = Diff.BuildHistory([first, export, Snap(3, followers: ["ann"], following: [])]);

        Assert.Equal(exact, h.FollowerSince["ann"].At);
        Assert.True(h.FollowerSince["ann"].Exact);
    }
}
