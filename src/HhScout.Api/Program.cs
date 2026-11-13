using HhScout.Api;
using HhScout.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH",
    Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? Path.Combine(builder.Environment.ContentRootPath, ".cache", "playwright"));
if (args.Contains("--install-browser", StringComparer.Ordinal))
{
    Environment.ExitCode = Microsoft.Playwright.Program.Main(args.Contains("--with-deps", StringComparer.Ordinal)
        ? ["install", "--with-deps", "chromium"] : ["install", "chromium"]);
    return;
}
if (args.Contains("--probe-hh", StringComparer.Ordinal))
{
    Environment.ExitCode = await HhBrowserDiagnostics.ProbeAsync();
    return;
}
if (args.Contains("--probe-login", StringComparer.Ordinal))
{
    Environment.ExitCode = await HhBrowserDiagnostics.ProbeLoginAsync();
    return;
}
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new Storage(Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:Path"] ?? "data/scout.db")));
builder.Services.AddSingleton(new HhBrowserOptions(Path.Combine(builder.Environment.ContentRootPath, "data", "hh-browser"),
    builder.Configuration.GetValue("Browser:Headless", false), builder.Configuration.GetValue("Hh:MaxPages", 3)));
builder.Services.AddSingleton<IHhBrowserDriver, PlaywrightHhDriver>();
builder.Services.AddSingleton<HhAccount>();
builder.Services.AddSingleton<IHhAccount>(sp => sp.GetRequiredService<HhAccount>());
builder.Services.AddSingleton<IVacancySource>(sp => sp.GetRequiredService<HhAccount>());
builder.Services.AddSingleton<IVacancyDetailsSource>(sp => sp.GetRequiredService<HhAccount>());
builder.Services.AddSingleton(new CodexOptions(Path.Combine(builder.Environment.ContentRootPath, "data", "codex-letters"),
    builder.Configuration["Codex:Executable"], builder.Configuration["Codex:Home"] ??
    Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
    builder.Configuration.GetValue("Codex:TimeoutSeconds", 180)));
builder.Services.AddSingleton<ICodexProcess, CodexProcess>();
builder.Services.AddSingleton<CodexLetterGenerator>();
builder.Services.AddSingleton<ICoverLetterGenerator>(sp => sp.GetRequiredService<CodexLetterGenerator>());
builder.Services.AddSingleton<CoverLetterService>();
builder.Services.AddSingleton<SyncService>();
if (builder.Configuration.GetValue("Hh:Demo", false)) builder.Services.AddSingleton<IVacancySource, DemoSource>();
if (builder.Configuration.GetValue("Hh:PollingEnabled", true)) builder.Services.AddHostedService<PollingWorker>();

var app = builder.Build();
app.UseExceptionHandler();
// Local single-user service: reject cross-origin mutations and form-based localhost attacks.
app.Use(async (context, next) =>
{
    if (context.Request.Method is "POST" or "PUT" or "DELETE")
    {
        var origin = context.Request.Headers.Origin.ToString();
        var allowed = $"{context.Request.Scheme}://{context.Request.Host}";
        if ((origin.Length > 0 && origin != allowed && origin != "http://127.0.0.1:5173" && origin != "http://localhost:5173") ||
            !context.Request.HasJsonContentType())
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/state", (Storage db, SyncService sync, IConfiguration config, IHhAccount account) => new
{
    mode = config.GetValue("Hh:Demo", false) ? "demo" : "browser",
    auth = account.State,
    search = db.Read<SearchSettings>("search") ?? new(),
    profile = db.Read<ResumeProfile>("profile") ?? new ResumeProfile([]),
    snapshot = db.Read<SnapshotInfo>("snapshot"), sync = sync.State,
    intervalSeconds = (int)SyncService.Interval.TotalSeconds
});
var auth = app.MapGroup("/api/hh-auth").AddEndpointFilter(async (context, next) =>
{
    if (builder.Configuration.GetValue("Hh:Demo", false)) return Results.BadRequest(new { detail = "В демо вход в hh.ru отключён. Запустите приложение без -Demo." });
    try { return await next(context); }
    catch (HhException ex) { return Results.BadRequest(new { detail = ex.Message }); }
});
auth.MapGet("/status", (IHhAccount account) => account.State);
auth.MapPost("/email", async (EmailLoginRequest request, IHhAccount account, CancellationToken ct) =>
    Results.Ok(await account.RequestCodeAsync(request.Email, ct)));
auth.MapPost("/code", async (CodeLoginRequest request, IHhAccount account, CancellationToken ct) =>
    Results.Ok(await account.SubmitCodeAsync(request.Code, ct)));
auth.MapPost("/check", async (IHhAccount account, CancellationToken ct) => Results.Ok(await account.CheckAsync(ct)));
auth.MapPost("/show-browser", async (IHhAccount account, CancellationToken ct) => { await account.ShowAsync(ct); return Results.NoContent(); });
auth.MapPost("/disconnect", async (IHhAccount account, CancellationToken ct) => { await account.DisconnectAsync(ct); return Results.NoContent(); });
if (builder.Configuration.GetValue("Browser:Diagnostics", false))
    auth.MapPost("/diagnostics", async (IHhAccount account, CancellationToken ct) => Results.Ok(await account.DiagnosticAsync(ct)));
app.MapPut("/api/search", (SearchSettings search, Storage db, SyncService sync) =>
{
    if (search.Text is null || search.Text.Trim().Length is < 1 or > 200 || search.Area is null ||
        (search.Area.Length > 0 && (!search.Area.All(char.IsAsciiDigit) || search.Area.Length > 10)))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["search"] = ["Введите запрос до 200 символов и корректный код региона."] });
    if (sync.State.Running) return Results.Conflict(new { detail = "Дождитесь текущего обновления." });
    db.Save("search", search with { Text = search.Text.Trim() });
    return Results.NoContent();
});
app.MapPut("/api/profile", (ResumeProfile profile, Storage db) =>
{
    if (profile.Skills is null || profile.Text is null || profile.Skills.Length > 100 || profile.Skills.Any(x => x is null || x.Length > 100) || profile.Text.Length > 30_000)
        return Results.BadRequest(new { detail = "До 100 навыков и 30 000 символов текста резюме." });
    var normalized = profile with { Skills = SkillMatcher.Distinct(profile.Skills) };
    db.Save("profile", normalized);
    return Results.Ok(new { profile = normalized, detected = SkillMatcher.Extract(profile.Text) });
});
app.MapPost("/api/sync", async (SyncService sync, CancellationToken ct) =>
{
    if (sync.State.Running) return Results.Conflict(new { detail = "Обновление уже выполняется." });
    await sync.RunAsync(ct);
    return Results.Ok(sync.State);
});
app.MapPost("/api/vacancies/query", (VacancyFilter filter, Storage db) =>
{
    if (filter.Skills is null || filter.Skills.Length > 100 || filter.Skills.Any(x => x is null || x.Length > 100) ||
        filter.Employer is null || filter.Employer.Length > 200 || filter.MinScore is < 0 or > 100 || filter.MinSalary < 0 ||
        filter.MinRating is < 0 or > 5 || filter.MinEmployees < 0 || filter.MaxEmployees < 0 ||
        filter.MinEmployees > filter.MaxEmployees || filter.Currency is not ("RUR" or "USD" or "EUR"))
        return Results.BadRequest(new { detail = "Проверьте значения фильтров." });
    var cached = db.Vacancies();
    var items = VacancyRanking.Rank(cached, db.Read<ResumeProfile>("profile") ?? new([]), filter, db.Companies());
    return Results.Ok(new { items, cached = cached.Length });
});
app.MapPut("/api/companies/{id}", (string id, CompanyInfo info, Storage db) =>
{
    if (id != info.EmployerId || id.Length is < 1 or > 30 || !id.All(char.IsAsciiDigit) ||
        info.Rating is < 0 or > 5 || info.Employees < 0 || info.Source?.Length > 500)
        return Results.BadRequest(new { detail = "Укажите численность от 0, оценку от 0 до 5 и источник до 500 символов." });
    db.SaveCompany(info);
    return Results.NoContent();
});
var letters = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    try { return await next(context); }
    catch (CoverLetterException ex) { return Results.Json(new { detail = ex.Message }, statusCode: ex.StatusCode); }
    catch (HhException ex) { return Results.BadRequest(new { detail = ex.Message }); }
});
letters.MapGet("/cover-letters/provider", (CodexLetterGenerator provider, CancellationToken ct) => provider.StatusAsync(ct));
letters.MapGet("/vacancies/{id}/context", (string id, CoverLetterService service) => service.Context(id));
letters.MapPost("/vacancies/{id}/description", (string id, CoverLetterService service, CancellationToken ct) => service.ReadDetailsAsync(id, ct));
letters.MapPost("/vacancies/{id}/cover-letter", (string id, GenerateLetterRequest request, CoverLetterService service, CancellationToken ct) => service.GenerateAsync(id, request, ct));
letters.MapPut("/cover-letters/{id}", (string id, EditLetterRequest request, CoverLetterService service) => service.Edit(id, request.Text));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
