using HhScout.Api;
using HhScout.Core;

namespace HhScout.Tests;

public class CoverLetterTests
{
    private static readonly Vacancy Job = new("123", "C# developer", "42", "Компания", "https://hh.ru/vacancy/123", "Москва", "", true,
        null, null, null, null, DateTimeOffset.UtcNow, "Краткий текст", "", ["C#"]);
    private static Storage Db() => new(Path.Combine(Path.GetTempPath(), "hh-cover-tests", Guid.NewGuid().ToString("N"), "scout.db"));

    [Fact]
    public async Task Reading_context_never_fetches_browser_or_calls_model()
    {
        var db = Db(); db.Replace([Job], new(), DateTimeOffset.UtcNow, 1, false);
        var source = new Details(); var model = new Generator();
        var service = new CoverLetterService(db, source, model, TimeProvider.System);
        Assert.Empty(service.Context("123").Letters);
        Assert.Null(service.Context("123").Details);
        Assert.Equal(0, source.Calls); Assert.Equal(0, model.Calls);
        await Assert.ThrowsAsync<CoverLetterException>(() => service.GenerateAsync("123", new(), default));
        Assert.Equal(0, source.Calls); Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Explicit_generation_uses_full_description_and_resume_and_survives_snapshot_replacement()
    {
        var db = Db(); db.Replace([Job], new(), DateTimeOffset.UtcNow, 1, false);
        db.Save("profile", new ResumeProfile(["C#"], "Разрабатывал ASP.NET сервисы."));
        var source = new Details(); var model = new Generator();
        var service = new CoverLetterService(db, source, model, TimeProvider.System);
        var letter = await service.GenerateAsync("123", new(), default);
        Assert.Equal(Details.FullText, model.Input!.Details.Description);
        Assert.Equal("Разрабатывал ASP.NET сервисы.", model.Input.Profile.Text);
        Assert.Equal(letter.Id, (await service.GenerateAsync("123", new(), default)).Id);
        Assert.Equal(1, model.Calls); Assert.Equal(1, source.Calls);
        db.Replace([], new(), DateTimeOffset.UtcNow, 0, false);
        var context = service.Context("123");
        Assert.Equal(Job.Title, context.Vacancy.Title);
        Assert.Equal(Details.FullText, context.Details!.Description);
        Assert.Single(context.Letters);
        Assert.Equal("Моя редакция", service.Edit(letter.Id, "Моя редакция").Text);
        model.Error = true;
        await Assert.ThrowsAsync<CoverLetterException>(() => service.GenerateAsync("123", new(Regenerate: true), default));
        Assert.Equal("Моя редакция", Assert.Single(service.Context("123").Letters).Text);
    }

    [Fact]
    public async Task Archive_and_original_context_survive_database_reopen_even_after_job_disappears()
    {
        var path = Path.Combine(Path.GetTempPath(), "hh-cover-tests", Guid.NewGuid().ToString("N"), "scout.db");
        var first = new Storage(path); first.Replace([Job], new(), DateTimeOffset.UtcNow, 1, false);
        first.Save("profile", new ResumeProfile(["C#"], "Исходное резюме"));
        var service = new CoverLetterService(first, new Details(), new Generator(), TimeProvider.System);
        var letter = await service.GenerateAsync("123", new(), default);
        first.Replace([], new(), DateTimeOffset.UtcNow, 0, false);
        var second = new Storage(path);
        Assert.Empty(second.Vacancies()); Assert.NotNull(second.ArchivedVacancy("123"));
        Assert.Equal(Details.FullText, second.Details("123")!.Description);
        Assert.Equal("Исходное резюме", second.Letter(letter.Id)!.Context.Profile.Text);
        second.Save("profile", new ResumeProfile(["C#"], "Изменённое резюме"));
        var reopened = new CoverLetterService(second, new Details(), new Generator(), TimeProvider.System);
        Assert.True(Assert.Single(reopened.Context("123").Letters).ContextChanged);
        Assert.Equal("Исходное резюме", second.Letter(letter.Id)!.Context.Profile.Text);
    }

    [Fact]
    public async Task Concurrent_generation_is_rejected_without_second_model_call()
    {
        var db = Db(); db.Replace([Job], new(), DateTimeOffset.UtcNow, 1, false);
        db.Save("profile", new ResumeProfile(["C#"]));
        var pending = new TaskCompletionSource<GeneratedLetter>();
        var model = new Generator { Pending = pending.Task };
        var service = new CoverLetterService(db, new Details(), model, TimeProvider.System);
        var first = service.GenerateAsync("123", new(), default);
        var error = await Assert.ThrowsAsync<CoverLetterException>(() => service.GenerateAsync("123", new(), default));
        Assert.Equal(409, error.StatusCode);
        pending.SetResult(new("Здравствуйте!", "gpt-6.1-sol"));
        await first; Assert.Equal(1, model.Calls);
    }

    [Fact]
    public void Detail_parser_requires_full_description_and_preserves_readable_paragraphs()
    {
        var parsed = VacancyDetailsParser.Parse("<div data-qa='vacancy-description'><p>Наша команда</p><ul><li>ASP.NET</li><li>SQL</li></ul><script>secret()</script></div>", "123", DateTimeOffset.UtcNow);
        Assert.Contains("Наша команда\n", parsed.Description);
        Assert.DoesNotContain("secret", parsed.Description);
        Assert.Throws<HhException>(() => VacancyDetailsParser.Parse("<h1>Не найдена</h1>", "123", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Codex_output_requires_completed_turn_and_schema_and_rejects_tool_use()
    {
        const string message = "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"letter\\\":\\\"Здравствуйте!\\\"}\"}}\n";
        Assert.Equal("Здравствуйте!", CodexLetterGenerator.ParseOutput(message + "{\"type\":\"turn.completed\"}"));
        Assert.Throws<CoverLetterException>(() => CodexLetterGenerator.ParseOutput(message));
        Assert.Throws<CoverLetterException>(() => CodexLetterGenerator.ParseOutput("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\"}}\n" + message + "{\"type\":\"turn.completed\"}"));
    }

    [Fact]
    public async Task Codex_auto_falls_back_only_for_unavailable_model_and_passes_private_context_through_stdin()
    {
        var runner = new FakeCodex { Failure = "model_not_found" };
        var options = new CodexOptions(Path.Combine(Path.GetTempPath(), "hh-codex-tests", Guid.NewGuid().ToString("N")), Environment.ProcessPath);
        var provider = new CodexLetterGenerator(options, runner);
        Assert.True((await provider.StatusAsync(default)).ChatGptLogin);
        Assert.Empty(runner.Models);
        var result = await provider.GenerateAsync(new(Job, new("123", Details.FullText, DateTimeOffset.UtcNow), new(["C#"], "PRIVATE_RESUME")), "auto", default);
        Assert.Equal("gpt-6.1-terra", result.Model);
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6.1-terra" }, runner.Models);
        Assert.Contains("PRIVATE_RESUME", runner.Input!);
        Assert.DoesNotContain(runner.Args!, x => x.Contains("PRIVATE_RESUME"));
        Assert.Contains("--ignore-user-config", runner.Args!);
        Assert.Contains("--ephemeral", runner.Args!);
        Assert.Contains("read-only", runner.Args!);
        runner.Models.Clear(); runner.Failure = "quota_exceeded";
        await Assert.ThrowsAsync<CoverLetterException>(() => provider.GenerateAsync(new(Job, new("123", Details.FullText, DateTimeOffset.UtcNow), new(["C#"])), "auto", default));
        Assert.Single(runner.Models);
        runner.Models.Clear(); runner.ApiLogin = true;
        await Assert.ThrowsAsync<CoverLetterException>(() => provider.GenerateAsync(new(Job, new("123", Details.FullText, DateTimeOffset.UtcNow), new(["C#"])), "auto", default));
        Assert.Empty(runner.Models);
    }

    private sealed class FakeCodex : ICodexProcess
    {
        public string? Failure; public bool ApiLogin; public List<string> Models = []; public string? Input; public string[]? Args;
        public Task<CodexProcessResult> RunAsync(string executable, string[] arguments, string? input, CodexOptions options, CancellationToken ct)
        {
            if (arguments.Contains("status")) return Task.FromResult(new CodexProcessResult(0, "", ApiLogin ? "Logged in using an API key" : "Logged in using ChatGPT"));
            Args = arguments; Input = input;
            var model = arguments[Array.IndexOf(arguments, "--model") + 1]; Models.Add(model);
            return Task.FromResult(model == "gpt-6.1-sol" && Failure is not null ? new CodexProcessResult(1, "", Failure) :
                new CodexProcessResult(0, "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"letter\\\":\\\"Здравствуйте!\\\"}\"}}\n{\"type\":\"turn.completed\"}", ""));
        }
    }

    private sealed class Details : IVacancyDetailsSource
    {
        public const string FullText = "Полное описание: создавать сервисы на ASP.NET и PostgreSQL.";
        public int Calls;
        public Task<VacancyDetails> ReadDetailsAsync(string id, CancellationToken ct)
        { Calls++; return Task.FromResult(new VacancyDetails(id, FullText, DateTimeOffset.UtcNow)); }
    }
    private sealed class Generator : ICoverLetterGenerator
    {
        public int Calls; public LetterInput? Input; public bool Error; public Task<GeneratedLetter>? Pending;
        public Task<GeneratedLetter> GenerateAsync(LetterInput input, string model, CancellationToken ct)
        { Calls++; Input = input; return Error ? Task.FromException<GeneratedLetter>(new CoverLetterException("Ошибка модели", 503)) : Pending ?? Task.FromResult(new GeneratedLetter("Здравствуйте! Меня заинтересовала вакансия.", "gpt-6.1-sol")); }
    }
}
