using System.IO.Compression;
using Following.Core;

namespace Following.Tests;

public class ExportReaderTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    // Older shape: username in string_list_data[0].value, followers file is a bare array.
    private const string FollowersValueShape = """
        [
          {"title": "", "media_list_data": [], "string_list_data": [{"href": "https://www.instagram.com/Alice", "value": "Alice", "timestamp": 1700000000}]},
          {"title": "", "media_list_data": [], "string_list_data": [{"href": "https://www.instagram.com/bob", "value": "bob", "timestamp": 1710000000}]}
        ]
        """;

    // Newer shape: username in title, href uses /_u/, no value.
    private const string FollowingTitleShape = """
        {"relationships_following": [
          {"title": "bob", "string_list_data": [{"href": "https://www.instagram.com/_u/bob", "timestamp": 1690000000}]},
          {"title": "carol", "string_list_data": [{"href": "https://www.instagram.com/_u/carol", "timestamp": 1695000000}]}
        ]}
        """;

    [Fact]
    public void Parses_both_entry_shapes_and_normalizes_usernames()
    {
        var snap = ExportReader.Parse(
            new() { ["followers_1.json"] = FollowersValueShape, ["following.json"] = FollowingTitleShape }, T, "x.zip");

        Assert.Equal(["alice", "bob"], snap.Followers.Select(p => p.Username).Order());
        Assert.Equal(["bob", "carol"], snap.Following.Select(p => p.Username).Order());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), snap.Followers.Single(p => p.Username == "alice").FollowedAt);
        Assert.Equal(SnapshotSource.Export, snap.Source);
    }

    [Fact]
    public void Merges_multiple_follower_files_and_wrapped_arrays()
    {
        var second = """{"relationships_followers": [{"title": "", "string_list_data": [{"href": "https://www.instagram.com/dave", "value": "dave", "timestamp": 1}]}]}""";
        var snap = ExportReader.Parse(
            new() { ["followers_1.json"] = FollowersValueShape, ["followers_2.json"] = second, ["following.json"] = "[]" }, T, "x");
        Assert.Equal(3, snap.Followers.Count);
        Assert.Empty(snap.Following);
    }

    [Fact]
    public void Falls_back_to_href_when_value_and_title_are_missing()
    {
        var followers = """[{"title": "", "string_list_data": [{"href": "https://www.instagram.com/erin/"}]}]""";
        var snap = ExportReader.Parse(new() { ["followers_1.json"] = followers, ["following.json"] = "[]" }, T, "x");
        Assert.Equal("erin", snap.Followers.Single().Username);
        Assert.Null(snap.Followers.Single().FollowedAt);
    }

    [Fact]
    public void Missing_files_explain_how_to_export()
    {
        var e = Assert.Throws<ExportException>(() => ExportReader.Parse(new() { ["following.json"] = "[]" }, T, "x"));
        Assert.Contains("JSON format", e.Message);
    }

    [Fact]
    public void Loads_a_zip_and_ignores_unrelated_files()
    {
        var dir = Directory.CreateTempSubdirectory("following-test-");
        try
        {
            var zipPath = Path.Combine(dir.FullName, "instagram-me-2026-10-25.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                Add(zip, "connections/followers_and_following/followers_1.json", FollowersValueShape);
                Add(zip, "connections/followers_and_following/following.json", FollowingTitleShape);
                Add(zip, "connections/followers_and_following/blocked_profiles.json", "[]");
                Add(zip, "your_instagram_activity/likes/liked_posts.json", "[]");
            }

            Assert.True(ExportReader.ContainsFollowData(zipPath));
            var snap = ExportReader.Load(zipPath);
            Assert.Equal(2, snap.Followers.Count);
            Assert.Equal(2, snap.Following.Count);
            Assert.Equal("instagram-me-2026-10-25.zip", snap.SourceRef);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    internal static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
