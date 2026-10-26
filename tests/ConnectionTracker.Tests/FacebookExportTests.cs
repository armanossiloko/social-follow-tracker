using System.IO.Compression;
using ConnectionTracker.Core;
using ConnectionTracker.Core.Facebook;

namespace ConnectionTracker.Tests;

public class FacebookExportTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    // "Zoë" as Meta writes it: each UTF-8 byte escaped separately.
    private const string Friends = """
        {"friends_v2": [
          {"name": "Ann Example", "timestamp": 1700000000},
          {"name": "ZoÃ«  Smith", "timestamp": 1710000000},
          {"name": "Ben Same", "timestamp": 1690000000},
          {"name": "Ben Same", "timestamp": 1695000000}
        ]}
        """;

    private const string Removed = """{"deleted_friends_v2": [{"name": "Cat Gone", "timestamp": 1720000000}]}""";

    [Fact]
    public void Reads_friends_fixes_the_encoding_and_keeps_namesakes_apart()
    {
        var snap = FacebookExport.Parse(new() { ["your_friends.json"] = Friends, ["removed_friends.json"] = Removed }, T, "fb.zip");

        Assert.Equal(Platforms.Facebook, snap.Platform);
        Assert.Equal(["ann example", "zoë smith", "ben same", "ben same #2"], snap.Friends.Select(p => p.Username));
        Assert.Equal("Zoë  Smith", snap.Friends[1].FullName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), snap.Friends[0].FollowedAt);
        Assert.Equal("cat gone", Assert.Single(snap.RemovedFriends).Username);
        Assert.Empty(snap.Followers);
    }

    [Fact]
    public void Text_that_isnt_double_encoded_is_left_alone()
    {
        Assert.Equal("José", FacebookExport.FixEncoding("José")); // é alone isn't valid UTF-8 as bytes
        Assert.Equal("José", FacebookExport.FixEncoding("JosÃ©"));
        Assert.Equal("東京", FacebookExport.FixEncoding("東京"));
    }

    [Fact]
    public void Older_exports_and_the_title_shape_are_read_too()
    {
        var old = """{"friends": [{"name": "Ann"}]}""";
        var titled = """[{"title": "Ben", "string_list_data": [{"timestamp": 1700000000}]}]""";

        Assert.Equal("ann", Assert.Single(FacebookExport.Parse(new() { ["friends.json"] = old }, T, "x").Friends).Username);
        var ben = Assert.Single(FacebookExport.Parse(new() { ["your_friends.json"] = titled }, T, "x").Friends);
        Assert.Equal(("ben", "Ben"), (ben.Username, ben.FullName));
        Assert.NotNull(ben.FollowedAt);
    }

    [Fact]
    public void A_zip_is_recognized_as_facebook_by_its_contents()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ExportReaderTests.Add(zip, "connections/friends/your_friends.json", Friends);
            ExportReaderTests.Add(zip, "connections/friends/removed_friends.json", Removed);
            ExportReaderTests.Add(zip, "connections/friends/sent_friend_requests.json", "[]");
        }
        stream.Position = 0;

        var snap = Assert.Single(ExportReader.LoadZip(stream, "facebook-me.zip", T));

        Assert.Equal(Platforms.Facebook, snap.Platform);
        Assert.Equal(4, snap.Friends.Count);
        Assert.Single(snap.RemovedFriends);
    }

    [Fact]
    public void A_zip_without_connections_explains_what_to_export()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            ExportReaderTests.Add(zip, "your_activity/posts.json", "[]");
        stream.Position = 0;

        var e = Assert.Throws<ExportException>(() => ExportReader.LoadZip(stream, "x.zip", T));
        Assert.Contains("\"Friends\" (Facebook)", e.Message);
    }
}
