using System.Reflection;
using System.Text;
using Following.Web.Data;
using Microsoft.AspNetCore.Antiforgery;
using static System.Net.WebUtility;

namespace Following.Web.Pages;

public sealed record SettingsModel(
    AntiforgeryTokenSet Antiforgery,
    string? Notice,
    bool NoticeIsError,
    string? Username,
    bool HasSession,
    RunRow? LastFetch,
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
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Following.Web.settings.html")
                           ?? throw new InvalidOperationException("settings.html resource missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Render(SettingsModel m)
    {
        var token = $"""<input type="hidden" name="{HtmlEncode(m.Antiforgery.FormFieldName)}" value="{HtmlEncode(m.Antiforgery.RequestToken)}">""";
        var failedFetch = m.HasSession && m.LastFetch is { Ok: false };
        var sessionStatus = !m.HasSession
            ? """<p>Status: <b>no cookie stored</b> <span class="muted">(only exports are imported)</span></p>"""
            : failedFetch
                ? $"""<p>Status: <b class="fail">the last fetch failed</b>. {HtmlEncode(m.LastFetch!.Message)}</p>"""
                : $"""<p>Status: <b class="ok">cookie stored</b>{(m.Username is null ? "" : $" for @{HtmlEncode(m.Username)}")}</p>""";

        var values = new Dictionary<string, string>
        {
            ["refresh"] = m.Running is null ? "" : """<meta http-equiv="refresh" content="8">""",
            ["notice"] = m.Notice is null ? "" : $"""<div class="notice{(m.NoticeIsError ? " error" : "")}">{HtmlEncode(m.Notice)}</div>""",
            ["antiforgery"] = token,
            ["session_status"] = sessionStatus,
            ["username"] = HtmlEncode(m.Username ?? ""),
            ["remove_form"] = !m.HasSession ? "" : $"""
                <form method="post" action="settings/session/remove">{token}
                  <button type="submit" class="danger">Remove cookie</button>
                </form>
                """,
            ["schedule"] = $"Runs every day at {m.DailyAt:HH:mm} ({HtmlEncode(TimeZoneInfo.Local.Id)}), plus up to {m.RandomDelayMinutes} minutes.",
            ["next_run"] = m.NextRunAt is { } next ? Local(next) : "—",
            ["last_fetch"] = m.LastFetch is null ? "never" : $"{Local(m.LastFetch.At)} · {(m.LastFetch.Ok ? "ok" : "failed")} · {HtmlEncode(m.LastFetch.Message)}",
            ["fetch_disabled"] = m.HasSession && m.Running is null ? "" : "disabled",
            ["running"] = m.Running is not null ? $"{HtmlEncode(m.Running)} in progress. This page refreshes itself."
                : m.HasSession ? "" : "Store a cookie first.",
            ["exports_hint"] = string.IsNullOrEmpty(m.ExportsPath)
                ? "No watched folder is configured."
                : "Or put it in your EXPORTS_DIR folder: every <code>instagram-*.zip</code> there is imported on the next daily run.",
            ["runs"] = Runs(m.RecentRuns),
        };

        var html = new StringBuilder(Template.Value);
        foreach (var (key, value) in values)
            html.Replace("{{" + key + "}}", value);
        return html.ToString();
    }

    private static string Runs(IReadOnlyList<RunRow> runs)
    {
        if (runs.Count == 0) return """<p class="muted">Nothing has run yet.</p>""";
        var rows = string.Concat(runs.Select(r => $"""
            <tr><td>{Local(r.At)}</td><td>{HtmlEncode(r.Kind)}</td>
            <td class="{(r.Ok ? "ok" : "fail")}">{(r.Ok ? "ok" : "failed")}</td><td>{HtmlEncode(r.Message)}</td></tr>
            """));
        return $"""<table><thead><tr><th>When</th><th>What</th><th>Result</th><th>Details</th></tr></thead><tbody>{rows}</tbody></table>""";
    }

    private static string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm");
}
