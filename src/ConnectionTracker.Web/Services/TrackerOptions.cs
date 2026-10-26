namespace ConnectionTracker.Web.Services;

/// <summary>Bound from the "Tracker" config section (env vars: Tracker__DailyAt etc.).</summary>
public sealed class TrackerOptions
{
    public const string Section = "Tracker";

    /// <summary>Local time (container TZ) the daily run starts at.</summary>
    public TimeOnly DailyAt { get; set; } = new(9, 0);

    /// <summary>Up to this many minutes are added at random so the request time varies from day to day.</summary>
    public int RandomDelayMinutes { get; set; } = 30;

    /// <summary>Folder scanned for official export zips (instagram-*.zip) on every daily run.</summary>
    public string? ExportsPath { get; set; } = "/imports";

    /// <summary>Where the data-protection keys that encrypt the session cookie are kept. Must persist across restarts.</summary>
    public string KeysPath { get; set; } = "/keys";

    /// <summary>
    /// Id of the browser extension allowed to post the session cookie. It's fixed by the "key" in
    /// extension/manifest.json, so it's the same wherever the extension is loaded from.
    /// </summary>
    public string ExtensionId { get; set; } = "cmijdjdeopoddpijkiccfoolhbeenjoh";
}
