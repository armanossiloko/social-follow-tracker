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
            ? """<p class="status"><span class="dot"></span>No cookie stored <small>exports only</small></p>"""
            : failedFetch
                ? $"""<p class="status bad"><span class="dot"></span>Cookie stored, but the last fetch failed <small>{HtmlEncode(m.LastFetch!.Message)}</small></p>"""
                : $"""<p class="status ok"><span class="dot"></span>Cookie stored{(m.Username is null ? "" : $" for @{HtmlEncode(m.Username)}")} <small>encrypted at rest</small></p>""";

        var values = new Dictionary<string, string>
        {
            ["refresh"] = m.Running is null ? "" : """<meta http-equiv="refresh" content="8">""",
            ["notice"] = m.Notice is null ? "" : $"""<div class="notice{(m.NoticeIsError ? " error" : "")}">{HtmlEncode(m.Notice)}</div>""",
            ["antiforgery"] = token,
            ["session_status"] = sessionStatus,
            ["username"] = HtmlEncode(m.Username ?? ""),
            ["remove_form"] = !m.HasSession ? "" : $"""
                <form method="post" action="settings/session/remove" style="margin-top:10px">{token}
                  <button type="submit" class="danger">Remove cookie</button>
                </form>
                """,
            ["schedule"] = $"Every day at {m.DailyAt:HH:mm} ({HtmlEncode(TimeZoneInfo.Local.Id)}) plus up to {m.RandomDelayMinutes} minutes",
            ["next_run"] = m.NextRunAt is { } next ? Local(next) : "—",
            ["last_fetch"] = m.LastFetch is null ? "never" : $"{Local(m.LastFetch.At)} · {(m.LastFetch.Ok ? "ok" : "failed")} · {HtmlEncode(m.LastFetch.Message)}",
            ["fetch_disabled"] = m.HasSession && m.Running is null ? "" : "disabled",
            ["running"] = m.Running is not null ? $"{HtmlEncode(m.Running)} in progress… this page refreshes itself."
                : m.HasSession ? "Fetches right away, even if today's run already happened." : "Store a cookie first.",
            ["exports_hint"] = string.IsNullOrEmpty(m.ExportsPath)
                ? "No watched folder is configured."
                : $"Zips named <code>instagram-*.zip</code> in the watched folder (<code>{HtmlEncode(m.ExportsPath)}</code> in the container, EXPORTS_DIR on your PC) are imported on every daily run.",
            ["runs"] = Runs(m.RecentRuns),
        };

        var html = new StringBuilder(Template.Value);
        foreach (var (key, value) in values)
            html.Replace("{{" + key + "}}", value);
        return html.ToString();
    }

    private static string Runs(IReadOnlyList<RunRow> runs)
    {
        if (runs.Count == 0) return """<p class="lede">Nothing has run yet.</p>""";
        var rows = string.Concat(runs.Select(r => $"""
            <tr><td class="when">{Local(r.At)}</td><td class="kind">{HtmlEncode(r.Kind)}</td>
            <td class="{(r.Ok ? "ok" : "fail")}">{(r.Ok ? "ok" : "failed")}</td><td>{HtmlEncode(r.Message)}</td></tr>
            """));
        return $"""<table><thead><tr><th>When</th><th>What</th><th>Result</th><th>Details</th></tr></thead><tbody>{rows}</tbody></table>""";
    }

    private static string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm");
}
