using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HhScout.Api;
using HhScout.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HhScout.Tests;

public class CoverLetterApiTests
{
    [Fact]
    public async Task Http_context_is_read_only_and_drafts_are_saved_edited_and_reopened_without_inference()
    {
        using var factory = new Factory(); using var http = factory.CreateClient();
        var context = await http.GetAsync("/api/vacancies/123/context"); context.EnsureSuccessStatusCode();
        Assert.Equal(0, factory.Source.Calls); Assert.Equal(0, factory.Model.Calls);
        (await http.PutAsJsonAsync("/api/profile", new ResumeProfile(["C#"], "PRIVATE_PROFILE_TEXT"))).EnsureSuccessStatusCode();
        var response = await http.PostAsJsonAsync("/api/vacancies/123/cover-letter", new GenerateLetterRequest());
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("id").GetString();
        (await http.PutAsJsonAsync($"/api/cover-letters/{id}", new EditLetterRequest("Редактированное письмо"))).EnsureSuccessStatusCode();
        var reopened = await http.GetStringAsync("/api/vacancies/123/context");
        Assert.Contains("Редактированное письмо", System.Text.RegularExpressions.Regex.Unescape(reopened));
        Assert.DoesNotContain("PRIVATE_PROFILE_TEXT", reopened);
        Assert.Equal(1, factory.Model.Calls); Assert.Equal(1, factory.Source.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/vacancies/404/context")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/vacancies/123/cover-letter", new GenerateLetterRequest("invalid"))).StatusCode);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        public Details Source = new(); public Generator Model = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Hh:PollingEnabled", "false");
            builder.ConfigureServices(services =>
            {
                var db = new Storage(Path.Combine(Path.GetTempPath(), "hh-api-letters", Guid.NewGuid().ToString("N"), "scout.db"));
                db.Replace([new("123", "C# developer", "42", "Тестовая компания", "https://hh.ru/vacancy/123", "Москва", "", true,
                    null, null, null, null, DateTimeOffset.UtcNow, "Краткий текст", "", ["C#"])], new(), DateTimeOffset.UtcNow, 1, false);
                services.RemoveAll<Storage>(); services.AddSingleton(db);
                services.RemoveAll<IVacancyDetailsSource>(); services.AddSingleton<IVacancyDetailsSource>(Source);
                services.RemoveAll<ICoverLetterGenerator>(); services.AddSingleton<ICoverLetterGenerator>(Model);
            });
        }
    }
    private sealed class Details : IVacancyDetailsSource
    {
        public int Calls;
        public Task<VacancyDetails> ReadDetailsAsync(string id, CancellationToken ct)
        { Calls++; return Task.FromResult(new VacancyDetails(id, "Полное описание вакансии ASP.NET Core", DateTimeOffset.UtcNow)); }
    }
    private sealed class Generator : ICoverLetterGenerator
    {
        public int Calls;
        public Task<GeneratedLetter> GenerateAsync(LetterInput input, string model, CancellationToken ct)
        { Calls++; return Task.FromResult(new GeneratedLetter("Здравствуйте! Тестовый черновик письма.", "gpt-6.1-sol")); }
    }
}
