namespace Following.Core;

/// <summary>Where everything lives. Defaults to %LOCALAPPDATA%\following, override with FOLLOWING_DATA.</summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = root;
    public string Snapshots => Path.Combine(Root, "snapshots");
    public string Inbox => Path.Combine(Root, "inbox");
    public string Config => Path.Combine(Root, "config.json");
    public string State => Path.Combine(Root, "state.json");
    public string Report => Path.Combine(Root, "report.html");
    public string Log => Path.Combine(Root, "following.log");

    public static AppPaths Default() => new(
        Environment.GetEnvironmentVariable("FOLLOWING_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "following"));

    /// <summary>Honors (and removes) a <c>--data &lt;dir&gt;</c> option, otherwise <see cref="Default"/>.</summary>
    public static AppPaths FromArgs(List<string> args)
    {
        var i = args.IndexOf("--data");
        if (i < 0 || i + 1 >= args.Count) return Default();
        var dir = Path.GetFullPath(args[i + 1]);
        args.RemoveRange(i, 2);
        return new AppPaths(dir);
    }

    public AppPaths EnsureCreated()
    {
        Directory.CreateDirectory(Snapshots);
        Directory.CreateDirectory(Inbox);
        return this;
    }
}
