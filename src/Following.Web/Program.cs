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
builder.Services.AddHostedService<DailyScheduler>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<FollowingDb>().Database.MigrateAsync();

app.UseAntiforgery();

// Every state-changing endpoint checks the form token. The built-in middleware only checks requests that carry
// form data, which would let another site trigger a bodiless POST (Fetch now, Remove cookie) from your browser.
var forms = app.MapGroup("/settings").AddEndpointFilter(async (context, next) =>
{
    var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
    return await antiforgery.IsRequestValidAsync(context.HttpContext)
        ? await next(context)
        : Results.BadRequest("Missing or invalid form token. Reload the Settings page and try again.");
});

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/", async (SnapshotRepository repo, SessionStore session, SchedulerState scheduler, TimeProvider clock, CancellationToken ct) =>
{
    var context = new ReportContext(
        await repo.LoadAllAsync(ct),
        await session.GetUsernameAsync(ct),
        await session.HasSessionAsync(ct),
        SnapshotRepository.Summary(await repo.LastRunAsync(RunKind.Fetch, okOnly: false, ct)),
        SnapshotRepository.Summary(await repo.LastRunAsync(RunKind.Fetch, okOnly: true, ct)),
        scheduler.NextRunAt,
        clock.GetUtcNow());
    return Results.Content(ReportBuilder.Render(context), "text/html; charset=utf-8");
});

app.MapGet("/settings", async (
    HttpContext http, IAntiforgery antiforgery, SnapshotRepository repo, SessionStore session,
    JobRunner runner, SchedulerState scheduler, IOptions<FollowingOptions> opts,
    string? msg, string? error, CancellationToken ct) =>
{
    var model = new SettingsModel(
        antiforgery.GetAndStoreTokens(http),
        error ?? msg,
        error is not null,
        await session.GetUsernameAsync(ct),
        await session.HasSessionAsync(ct),
        await repo.LastRunAsync(RunKind.Fetch, okOnly: false, ct),
        opts.Value.DailyAt,
        opts.Value.RandomDelayMinutes,
        scheduler.NextRunAt,
        runner.Running,
        opts.Value.ExportsPath,
        await repo.RecentRunsAsync(20, ct));
    return Results.Content(SettingsPage.Render(model), "text/html; charset=utf-8");
});

forms.MapPost("/session", async (
    HttpContext http, [FromForm] string? username, [FromForm] string sessionId,
    SessionStore session, InstagramClientFactory clients, CancellationToken ct) =>
{
    InstagramClient client;
    try
    {
        client = clients.Create(sessionId);
    }
    catch (ArgumentException e)
    {
        return Back(http, error: e.Message);
    }
    try
    {
        await client.CheckSessionAsync(ct);
    }
    catch (InstagramException e)
    {
        return Back(http, error: "Instagram didn't accept that cookie: " + e.Message);
    }
    await session.SaveAsync(username, sessionId, ct);
    return Back(http, "Cookie checked and saved. Daily runs will use it; Fetch now takes a snapshot right away.");
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

static IResult Back(HttpContext http, string? msg = null, string? error = null)
{
    var query = error is not null ? "?error=" + Uri.EscapeDataString(error)
        : msg is not null ? "?msg=" + Uri.EscapeDataString(msg)
        : "";
    return Results.Redirect($"{http.Request.PathBase}/settings{query}");
}
