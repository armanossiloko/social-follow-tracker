using System.Diagnostics;
using System.Text;
using Following.Core;
using Following.Core.Instagram;
using Following.Core.Storage;

var argList = args.ToList();
var paths = AppPaths.FromArgs(argList).EnsureCreated();
var log = new Log(paths.Log, console: true);
var jobs = new Jobs(paths, log);
var command = argList.Count > 0 ? argList[0].ToLowerInvariant() : "help";
var rest = argList.Skip(1).ToList();

try
{
    return command switch
    {
        "login" => await Login(),
        "logout" => Logout(),
        "fetch" => await Fetch(),
        "import" => Import(),
        "daily" => await jobs.DailyAsync(),
        "report" => Report(),
        "open" => OpenReport(),
        "status" => Status(),
        "schedule" => Schedule(),
        "help" or "--help" or "-h" or "/?" => Help(),
        _ => Usage($"Unknown command '{command}'."),
    };
}
catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 1;
}

async Task<int> Login()
{
    var config = AppConfig.Load(paths);
    Console.WriteLine("""
        Paste your Instagram session cookie. To find it:
          1. Log in at https://www.instagram.com in your browser.
          2. Open DevTools (F12) > Application (Chrome/Edge) or Storage (Firefox) > Cookies > https://www.instagram.com
          3. Copy the value of the "sessionid" cookie.
        It is stored encrypted with Windows DPAPI, readable only by your Windows account.
        Note: automated access is against Instagram's Terms of Use. This tool fetches once a day, slowly.

        """);
    var username = Prompt($"Instagram username{(config.Username is null ? "" : $" [{config.Username}]")}: ")?.Trim().TrimStart('@');
    if (string.IsNullOrEmpty(username)) username = config.Username;
    var sessionId = ReadSecret("sessionid: ");
    if (string.IsNullOrWhiteSpace(sessionId)) return Usage("No sessionid entered.");

    var client = new InstagramClient(sessionId, config.CsrfToken, log: log);
    Console.WriteLine($"Checking the session for user id {client.UserId}...");
    try
    {
        await client.CheckSessionAsync();
    }
    catch (InstagramException e)
    {
        Console.Error.WriteLine($"Session check failed: {e.Message}");
        return 1;
    }
    config.Username = username;
    config.SetSessionId(sessionId.Trim());
    config.Save(paths);
    Console.WriteLine("Logged in. Run `following fetch` to take a snapshot now, and `following schedule install` for daily runs.");
    return 0;
}

int Logout()
{
    var config = AppConfig.Load(paths);
    config.SetSessionId(null);
    config.CsrfToken = null;
    config.Save(paths);
    Console.WriteLine("Session cookie removed. Daily runs will only import exports from now on.");
    return 0;
}

async Task<int> Fetch()
{
    var config = AppConfig.Load(paths);
    if (!config.HasSession) return Usage("Not logged in. Run `following login` first.");
    var ok = await jobs.FetchAsync(config);
    jobs.WriteReport(config);
    return ok ? 0 : 1;
}

int Import()
{
    var force = rest.Remove("--force");
    if (rest.Count == 0) return Usage("Usage: following import <export.zip | extracted folder> [--force]");
    var ok = true;
    foreach (var path in rest)
        ok &= jobs.Import(Path.GetFullPath(path), force) is not null;
    jobs.WriteReport();
    return ok ? 0 : 1;
}

int Report()
{
    var report = jobs.WriteReport();
    if (rest.Contains("--open")) Open(report);
    return 0;
}

int OpenReport()
{
    if (!File.Exists(paths.Report)) jobs.WriteReport();
    Open(paths.Report);
    return 0;
}

int Status()
{
    var config = AppConfig.Load(paths);
    var state = AppState.Load(paths);
    var snapshots = new SnapshotStore(paths).LoadAll();
    Console.WriteLine($"Data folder:  {paths.Root}");
    Console.WriteLine($"Report:       {paths.Report}");
    Console.WriteLine($"Account:      {(config.Username is null ? "(not set)" : "@" + config.Username)}");
    Console.WriteLine($"Session:      {(config.HasSession ? "stored (encrypted)" : "none - exports only")}");
    Console.WriteLine($"Export dirs:  {string.Join("; ", config.EffectiveExportDirs(paths))}");
    Console.WriteLine($"Snapshots:    {snapshots.Count}" +
                      (snapshots.Count > 0 ? $" (latest {snapshots[^1].TakenAt.ToLocalTime():g}, {snapshots[^1].Source})" : ""));
    foreach (var kind in new[] { RunKind.Fetch, RunKind.Import })
        if (state.LastRun(kind) is { } run)
            Console.WriteLine($"Last {kind,-7} {run.At.ToLocalTime():g} {(run.Ok ? "ok" : "FAILED")}: {run.Message}");
    return 0;
}

int Schedule()
{
    var sub = rest.Count > 0 ? rest[0].ToLowerInvariant() : "status";
    switch (sub)
    {
        case "install":
        {
            var at = new TimeOnly(9, 0);
            var atIndex = rest.IndexOf("--at");
            if (atIndex >= 0 && (atIndex + 1 >= rest.Count || !TimeOnly.TryParse(rest[atIndex + 1], out at)))
                return Usage("--at expects a time like 09:00");
            var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "following-background.exe" : "following-background");
            if (!File.Exists(exe))
                return Usage($"{exe} not found. Publish both apps into one folder first (see README: publish.ps1).");
            var (ok, output) = DailySchedule.Install(exe, paths.Root, at);
            Console.WriteLine(ok ? $"Scheduled daily at {at:HH:mm} (+ up to 30 min random delay). {output}" : $"Failed: {output}");
            return ok ? 0 : 1;
        }
        case "remove":
        {
            var (ok, output) = DailySchedule.Remove();
            Console.WriteLine(ok ? "Scheduled task removed." : $"Failed: {output}");
            return ok ? 0 : 1;
        }
        case "status":
        {
            var (ok, output) = DailySchedule.Status();
            Console.WriteLine(ok ? output : "No scheduled task found. Install it with `following schedule install [--at 09:00]`.");
            return ok ? 0 : 1;
        }
        default:
            return Usage("Usage: following schedule install [--at HH:mm] | remove | status");
    }
}

int Help()
{
    Console.WriteLine("""
        following - daily Instagram followers/following report

        Usage: following <command> [--data <folder>]

          login                  Store your Instagram sessionid cookie (encrypted) for automatic fetches
          logout                 Forget the cookie (exports-only mode)
          fetch                  Fetch followers/following now, then rebuild the report
          import <zip|folder>    Import an official Instagram export (JSON format) [--force]
          daily                  What the scheduled task runs: import new exports, fetch, rebuild report
          report [--open]        Rebuild report.html
          open                   Open the report in your browser
          status                 Show configuration, snapshots and last runs
          schedule install       Register the daily Windows scheduled task [--at 09:00]
          schedule remove        Remove the scheduled task
          schedule status        Show the scheduled task's next/last run

        Any instagram-*.zip in your Downloads folder is imported automatically on the next daily run.
        """);
    return 0;
}

int Usage(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

static string? Prompt(string label)
{
    Console.Write(label);
    return Console.ReadLine();
}

static string ReadSecret(string label)
{
    Console.Write(label);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var value = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0) value.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
        {
            value.Append(key.KeyChar);
        }
    }
    Console.WriteLine($"({value.Length} characters)");
    return value.ToString();
}
