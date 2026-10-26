using Following.Core.Instagram;
using Following.Core.Report;
using Following.Web.Data;
using Following.Web.Pages;
using Following.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

const long maxUpload = 1L << 30; // full exports that include media can be large

var builder = WebApplication.CreateBuilder(args);
var section = builder.Configuration.GetSection(FollowingOptions.Section);
var options = section.Get<FollowingOptions>() ?? new FollowingOptions();
builder.Services.Configure<FollowingOptions>(section);

builder.Services.AddDbContext<FollowingDb>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Following"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddDataProtection()
    .SetApplicationName("following")
    .PersistKeysToFileSystem(new DirectoryInfo(options.KeysPath));
builder.Services.AddAntiforgery();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxUpload);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InstagramClientFactory>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<SchedulerState>();
builder.Services.AddScoped<SnapshotRepository>();
builder.Services.AddScoped<SessionStore>();
builder.Services.AddScoped<FollowJobs>();
builder.Services.AddScoped<SessionUpdater>();
builder.Services.AddHostedService<DailyScheduler>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<FollowingDb>().Database.MigrateAsync();

app.UseAntiforgery();

// Log every call from the browser extension (including rejected ones) so a broken setup is diagnosable.
app.Use(async (http, next) =>
{
    await next(http);
    if (http.Request.Path.StartsWithSegments("/api"))
        app.Logger.LogInformation("{Method} {Path} from origin {Origin} -> {Status}",
            http.Request.Method, http.Request.Path, http.Request.Headers.Origin.ToString() is { Length: > 0 } o ? o : "(none)", http.Response.StatusCode);
});

// Every state-changing endpoint checks the form token. The built-in middleware only checks requests that carry
// form data, which would let another site trigger a bodiless POST (Fetch now, Remove cookie) from your browser.
var forms = app.MapGroup("/settings").AddEndpointFilter(RequireFormToken);

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/", async (
    HttpContext http, IAntiforgery antiforgery, SnapshotRepository repo, SessionStore session,
    JobRunner runner, SchedulerState scheduler, TimeProvider clock, CancellationToken ct) =>
{
    var context = new ReportContext(
        await repo.LoadAllAsync(ct),
        await session.GetUsernameAsync(ct),
        await session.HasSessionAsync(ct),
        SnapshotRepository.Summary(await repo.LastRunAsync(RunKind.Fetch, okOnly: false, ct)),
        SnapshotRepository.Summary(await repo.LastRunAsync(RunKind.Fetch, okOnly: true, ct)),
        scheduler.NextRunAt,
        clock.GetUtcNow(),
        runner.Running,
        antiforgery.GetAndStoreTokens(http).RequestToken);
    return Results.Content(ReportBuilder.Render(context), "text/html; charset=utf-8");
});

// The report's Refresh button: starts a fetch, then polls GET /refresh and reloads once nothing is running.
app.MapPost("/refresh", async (JobRunner runner, SessionStore session, CancellationToken ct) =>
{
    if (!await session.HasSessionAsync(ct))
        return Results.Json(new { error = "Store a session cookie on the Settings page first." }, statusCode: StatusCodes.Status400BadRequest);
    // If another job is already running (e.g. the daily run), the page just waits for that one instead.
    runner.TryStart("Fetch", (jobs, c) => jobs.FetchAsync(c));
    return Results.Json(new { running = runner.Running });
}).AddEndpointFilter(RequireFormToken);

app.MapGet("/refresh", (JobRunner runner) => Results.Json(new { running = runner.Running }));

app.MapGet("/settings", async (
    HttpContext http, IAntiforgery antiforgery, SnapshotRepository repo, SessionStore session,
    JobRunner runner, SchedulerState scheduler, IOptions<FollowingOptions> opts,
    string? msg, string? error, CancellationToken ct) =>
{
    var model = new SettingsModel(
        antiforgery.GetAndStoreTokens(http),
        error ?? msg,
        error is not null,
        await session.GetInfoAsync(ct),
        await repo.LastRunAsync(RunKind.Fetch, okOnly: false, ct),
        opts.Value.DailyAt,
        opts.Value.RandomDelayMinutes,
        scheduler.NextRunAt,
        runner.Running,
        opts.Value.ExportsPath,
        await repo.RecentRunsAsync(20, ct));
    return Results.Content(SettingsPage.Render(model), "text/html; charset=utf-8");
});

forms.MapPost("/session", async (HttpContext http, [FromForm] string sessionId, SessionUpdater updater, CancellationToken ct) =>
{
    var result = await updater.UpdateAsync(sessionId, SessionSource.Manual, sameAccountOnly: false, ct);
    return result.Ok ? Back(http, result.Message) : Back(http, error: result.Message);
});

// The browser extension posts the sessionid cookie here: right after install, whenever it changes, and hourly.
// Only that extension is accepted: browsers always send the real Origin, and a website can't claim to be an
// extension. The app only contacts Instagram when the cookie actually changed.
app.MapPost("/api/session", async (
    HttpContext http, SessionCookie body, SessionUpdater updater, SessionStore session,
    IOptions<FollowingOptions> opts, TimeProvider clock, CancellationToken ct) =>
{
    if (http.Request.Headers.Origin != $"chrome-extension://{opts.Value.ExtensionId}")
        return Results.Json(new { error = "Only the Following browser extension may post here." }, statusCode: StatusCodes.Status403Forbidden);
    await session.MarkExtensionSeenAsync(clock.GetUtcNow(), ct);
    var result = await updater.UpdateAsync(body.SessionId, SessionSource.Extension, sameAccountOnly: true, ct);
    return result.Ok
        ? Results.Json(new { message = result.Message, changed = result.Changed })
        : Results.Json(new { error = result.Message }, statusCode: result.WrongAccount ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
});

forms.MapPost("/session/remove", async (HttpContext http, SessionStore session, CancellationToken ct) =>
{
    await session.ClearSessionAsync(ct);
    return Back(http, "Cookie removed. Daily runs will only import exports from now on.");
});

forms.MapPost("/fetch", async (HttpContext http, JobRunner runner, SessionStore session, CancellationToken ct) =>
{
    if (!await session.HasSessionAsync(ct))
        return Back(http, error: "Store a session cookie first.");
    return runner.TryStart("Fetch", (jobs, c) => jobs.FetchAsync(c))
        ? Back(http, "Fetch started. It takes a minute or two; the result appears under Recent runs.")
        : Back(http, error: $"{runner.Running ?? "Another job"} is already in progress; try again when it's done.");
});

forms.MapPost("/import", async (HttpContext http, IFormFile file, JobRunner runner, TimeProvider clock, CancellationToken ct) =>
{
    // The zip is read twice (hash, then contents), so buffer the upload in a seekable temp file.
    await using var temp = new FileStream(
        Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    await file.CopyToAsync(temp, ct);
    temp.Position = 0;
    var name = Path.GetFileName(file.FileName);
    var result = await runner.RunAsync("Import", (jobs, c) => jobs.ImportAsync(temp, name, clock.GetUtcNow(), force: false, c), ct);
    return result.Ok ? Back(http, result.Message) : Back(http, error: result.Message);
});

app.Run();

static async ValueTask<object?> RequireFormToken(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
    return await antiforgery.IsRequestValidAsync(context.HttpContext)
        ? await next(context)
        : Results.BadRequest("Missing or invalid form token. Reload the page and try again.");
}

static IResult Back(HttpContext http, string? msg = null, string? error = null)
{
    var query = error is not null ? "?error=" + Uri.EscapeDataString(error)
        : msg is not null ? "?msg=" + Uri.EscapeDataString(msg)
        : "";
    return Results.Redirect($"{http.Request.PathBase}/settings{query}");
}
