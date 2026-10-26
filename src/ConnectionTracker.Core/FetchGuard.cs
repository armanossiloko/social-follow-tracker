namespace ConnectionTracker.Core;

/// <summary>A fetch failed in a way worth reporting to the user (as opposed to a bug).</summary>
public class FetchException(string message) : Exception(message);

/// <summary>A fetched list that came back truncated.</summary>
public sealed class IncompleteFetchException(string message) : FetchException(message);

/// <summary>List sizes of an earlier snapshot, to compare a new fetch against.</summary>
public sealed record ListCounts(int Followers, int Following, int Friends);

/// <summary>
/// A truncated list would show up as a wave of fake "unfollowed you" or "no longer friends" events, so fetches that
/// look incomplete are refused rather than saved.
/// </summary>
public static class FetchGuard
{
    /// <param name="expected">The total the platform reports, if it does.</param>
    /// <param name="previousCount">The size of this list in the previous fetch.</param>
    public static void Check(string name, int got, int? expected, int? previousCount)
    {
        // Profile totals include deactivated/restricted accounts the list endpoint skips, so allow some slack.
        if (expected is int e && got < e * 0.9 && e - got > 3)
            throw new IncompleteFetchException($"Got {got} {name} but the profile says {e}; not saving this snapshot.");
        if (got == 0 && previousCount > 10)
            throw new IncompleteFetchException($"Got 0 {name} (previously {previousCount}); not saving this snapshot.");
    }
}
