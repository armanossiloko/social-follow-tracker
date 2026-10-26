using System.Reflection;
using System.Text;
using ConnectionTracker.Core;
using ConnectionTracker.Web.Data;
using ConnectionTracker.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using static System.Net.WebUtility;

namespace ConnectionTracker.Web.Pages;

public sealed record SettingsModel(
    AntiforgeryTokenSet Antiforgery,
    string? Notice,
    bool NoticeIsError,
    SessionInfo Session,
    RunRow? InstagramLastFetch,
    SteamInfo Steam,
    RunRow? SteamLastFetch,
    RunRow? FacebookLastImport,
    TimeOnly DailyAt,
    int RandomDelayMinutes,
    DateTimeOffset? NextRunAt,
    string? Running,
    string? ExportsPath,
    IReadOnlyList<RunRow> RecentRuns);

/// <summary>Server-rendered settings page: the HTML template with {{tokens}} filled in (all values HTML-encoded).</summary>
public static class SettingsPage
{
    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ConnectionTracker.Web.settings.html")
                           ?? throw new InvalidOperationException("settings.html resource missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Render(SettingsModel m)
    {
        var token = $"""<input type="hidden" name="{HtmlEncode(m.Antiforgery.FormFieldName)}" value="{HtmlEncode(m.Antiforgery.RequestToken)}">""";
        var session = m.Session;
        var hasSession = session.HasSession;
        var steam = m.Steam;
        var anyConnected = hasSession || steam.Connected;

        var values = new Dictionary<string, string>
        {
            ["refresh"] = m.Running is null ? "" : """<meta http-equiv="refresh" content="8">""",
            ["notice"] = m.Notice is null ? "" : $"""<div class="notice{(m.NoticeIsError ? " error" : "")}">{HtmlEncode(m.Notice)}</div>""",
            ["antiforgery"] = token,
            ["session_status"] = InstagramStatus(session, m.InstagramLastFetch),
            ["extension_status"] = session.ExtensionLastSeen is { } seen
                ? $"""<p>Browser extension: <b class="ok">connected</b> <span class="muted">(last check-in {Local(seen)}; it sends the cookie whenever it changes)</span></p>"""
                : """<p>Browser extension: <b>not set up</b>. In Brave, open <code>brave://extensions</code>, turn on Developer mode, click <i>Load unpacked</i> and pick the <code>extension</code> folder of this project. Stay logged in to instagram.com in Brave.</p>""",
            ["remove_form"] = !hasSession ? "" : $"""
                <form method="post" action="settings/session/remove">{token}
                  <button type="submit" class="danger">Remove cookie</button>
                </form>
                """,
            ["steam_status"] = SteamStatus(steam, m.SteamLastFetch),
            ["steam_profile"] = HtmlEncode(steam.SteamId ?? ""),
            ["steam_button"] = steam.Connected ? "Check and save" : "Connect",
            ["steam_remove_form"] = !steam.Connected ? "" : $"""
                <form method="post" action="settings/steam/remove">{token}
                  <button type="submit" class="danger">Remove API key</button>
                </form>
                """,
            ["facebook_status"] = m.FacebookLastImport is { } import
                ? $"""<p>Status: <b class="ok">imported</b> <span class="muted">({Local(import.At)}, {HtmlEncode(import.Message)})</span></p>"""
                : """<p>Status: <b>no export imported yet</b></p>""",
            ["schedule"] = $"Runs every day at {m.DailyAt:HH:mm} ({HtmlEncode(TimeZoneInfo.Local.Id)}), plus up to {m.RandomDelayMinutes} minutes. " +
                           "It imports new exports from the watched folder, then fetches Instagram and Steam if they're connected.",
            ["next_run"] = m.NextRunAt is { } next ? Local(next) : "—",
            ["last_fetches"] = $"Last Instagram fetch: {LastFetch(m.InstagramLastFetch)}<br>Last Steam fetch: {LastFetch(m.SteamLastFetch)}",
            ["fetch_disabled"] = anyConnected && m.Running is null ? "" : "disabled",
            ["running"] = m.Running is not null ? $"{HtmlEncode(m.Running)} in progress. This page refreshes itself."
                : anyConnected ? "" : "Connect Instagram or Steam first.",
            ["exports_hint"] = string.IsNullOrEmpty(m.ExportsPath)
                ? "No watched folder is configured."
                : "Or put it in your EXPORTS_DIR folder: every <code>instagram-*.zip</code> and <code>facebook-*.zip</code> there is imported on the next daily run.",
            ["runs"] = Runs(m.RecentRuns),
        };

        var html = new StringBuilder(Template.Value);
        foreach (var (key, value) in values)
            html.Replace("{{" + key + "}}", value);
        return html.ToString();
    }

    private static string InstagramStatus(SessionInfo session, RunRow? lastFetch)
    {
        if (!session.HasSession)
            return """<p>Status: <b>no cookie stored</b> <span class="muted">(only exports are imported)</span></p>""";
        if (lastFetch is { Ok: false } failed)
            return $"""<p>Status: <b class="fail">the last fetch failed</b>. {HtmlEncode(failed.Message)}</p>""";
        var account = session.Username is null ? "" : $" for @{HtmlEncode(session.Username)}";
        var updated = session.UpdatedAt is { } at
            ? $""" <span class="muted">(from {(session.Source == SessionSource.Extension ? "the browser extension" : "manual paste")}, {Local(at)})</span>"""
            : "";
        return $"""<p>Status: <b class="ok">cookie stored</b>{account}{updated}</p>""";
    }

    private static string SteamStatus(SteamInfo steam, RunRow? lastFetch)
    {
        if (!steam.Connected)
            return """<p>Status: <b>not connected</b></p>""";
        if (lastFetch is { Ok: false } failed)
            return $"""<p>Status: <b class="fail">the last fetch failed</b>. {HtmlEncode(failed.Message)}</p>""";
        var who = HtmlEncode(steam.Name ?? steam.SteamId);
        return $"""<p>Status: <b class="ok">connected</b> as <a href="{HtmlEncode(steam.ProfileUrl)}" target="_blank" rel="noopener noreferrer">{who}</a>""" +
               (steam.UpdatedAt is { } at ? $""" <span class="muted">(since {Local(at)})</span>""" : "") + "</p>";
    }

    private static string LastFetch(RunRow? run) =>
        run is null ? "never" : $"{Local(run.At)} · {(run.Ok ? "ok" : "failed")} · {HtmlEncode(run.Message)}";

    private static string Runs(IReadOnlyList<RunRow> runs)
    {
        if (runs.Count == 0) return """<p class="muted">Nothing has run yet.</p>""";
        var rows = string.Concat(runs.Select(r => $"""
            <tr><td>{Local(r.At)}</td><td>{HtmlEncode(What(r))}</td>
            <td class="{(r.Ok ? "ok" : "fail")}">{(r.Ok ? "ok" : "failed")}</td><td>{HtmlEncode(r.Message)}</td></tr>
            """));
        return $"""<table><thead><tr><th>When</th><th>What</th><th>Result</th><th>Details</th></tr></thead><tbody>{rows}</tbody></table>""";
    }

    private static string What(RunRow run) => Platforms.Find(run.Platform) is { } p ? $"{p.Name} {run.Kind}" : run.Kind;

    private static string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm");
}
