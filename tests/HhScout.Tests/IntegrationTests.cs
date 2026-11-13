using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HhScout.Api;
using HhScout.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace HhScout.Tests;

public class IntegrationTests
{
    private static Vacancy Job(string id = "1", string title = "Senior C# .NET developer") => new(
        id, title, "42", "Test Company", $"https://hh.ru/vacancy/{id}", "Москва", "3–6 лет", true,
        200_000, 300_000, "RUR", false, DateTimeOffset.UtcNow, "ASP.NET, PostgreSQL", "", ["C#", ".NET", "PostgreSQL"], true);

    private static Storage TempStorage() => new(Path.Combine(Path.GetTempPath(), "hh-scout-tests", Guid.NewGuid().ToString("N"), "scout.db"));

    [Fact]
    public async Task Http_workflow_syncs_saves_resume_and_filters_strictly()
    {
        var source = new FakeSource { Result = new([Job(), Job("2", "Java developer")], 2, false) };
        using var factory = new Factory(source);
        using var http = factory.CreateClient();
        (await http.PostAsJsonAsync("/api/sync", new { })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync("/api/profile", new ResumeProfile(["C#", ".NET", "PostgreSQL"]))).EnsureSuccessStatusCode();
        var response = await http.PostAsJsonAsync("/api/vacancies/query", new VacancyFilter(["PostgreSQL"], MinScore: 100));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items");
        Assert.Single(items.EnumerateArray());
        Assert.Equal("1", items[0].GetProperty("vacancy").GetProperty("id").GetString());
        Assert.Equal(100, items[0].GetProperty("match").GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Failed_sync_keeps_last_successful_snapshot_and_reports_error()
    {
        var db = TempStorage();
        var source = new FakeSource { Result = new([Job()], 1, false) };
        var sync = new SyncService(source, db, TimeProvider.System, NullLogger<SyncService>.Instance);
        Assert.True(await sync.RunAsync(default));
        var previous = db.Read<SnapshotInfo>("snapshot");
        source.Error = new HhException("hh.ru: 429");
        Assert.False(await sync.RunAsync(default));
        Assert.Single(db.Vacancies());
        Assert.Equal(previous, db.Read<SnapshotInfo>("snapshot"));
        Assert.Equal("hh.ru: 429", sync.State.Error);
        Assert.False(sync.State.Running);
        Assert.True(sync.State.NextAttempt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Concurrent_syncs_do_not_duplicate_external_calls()
    {
        var wait = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource { Pending = wait.Task };
        var sync = new SyncService(source, TempStorage(), TimeProvider.System, NullLogger<SyncService>.Instance);
        var first = sync.RunAsync(default);
        Assert.False(await sync.RunAsync(default));
        wait.SetResult(new([], 0, false));
        Assert.True(await first);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Rejects_invalid_filters_and_cross_origin_writes()
    {
        using var factory = new Factory(new FakeSource());
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/vacancies/query", new VacancyFilter([], MinScore: 101))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/profile", new { skills = new string?[] { null }, text = "" })).StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PutAsJsonAsync("/api/profile", new ResumeProfile(["C#"]))).StatusCode);
    }

    [Fact]
    public void Storage_survives_new_instance_and_replaces_instead_of_accumulating_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), "hh-scout-tests", Guid.NewGuid().ToString("N"), "scout.db");
        var first = new Storage(path);
        first.Save("profile", new ResumeProfile(["C#"]));
        first.Replace([Job(), Job()], new(), DateTimeOffset.UtcNow, 1, false);
        var second = new Storage(path);
        Assert.Single(second.Vacancies());
        Assert.Equal("C#", Assert.Single(second.Read<ResumeProfile>("profile")!.Skills));
        second.Replace([], new(), DateTimeOffset.UtcNow, 0, false);
        Assert.Empty(first.Vacancies());
    }

    [Fact]
    public async Task Company_notes_are_saved_and_used_by_http_filter()
    {
        using var factory = new Factory(new FakeSource { Result = new([Job()], 1, false) });
        using var http = factory.CreateClient();
        (await http.PostAsJsonAsync("/api/sync", new { })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync("/api/companies/42", new CompanyInfo("42", 500, 4.5m, "Тестовый источник"))).EnsureSuccessStatusCode();
        var response = await http.PostAsJsonAsync("/api/vacancies/query", new VacancyFilter([], MinEmployees: 100, MinRating: 4, IncludeUnknownCompanies: false));
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        var excluded = await http.PostAsJsonAsync("/api/vacancies/query", new VacancyFilter([], MinEmployees: 1000, IncludeUnknownCompanies: false));
        using var empty = JsonDocument.Parse(await excluded.Content.ReadAsStringAsync());
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
    }

    private sealed class FakeSource : IVacancySource
    {
        public SearchResult Result { get; set; } = new([], 0, false);
        public Exception? Error { get; set; }
        public Task<SearchResult>? Pending { get; init; }
        public int Calls { get; private set; }
        public Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct)
        {
            Calls++;
            return Error is not null ? Task.FromException<SearchResult>(Error) : Pending ?? Task.FromResult(Result);
        }
    }

    private sealed class Factory(FakeSource source) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Hh:PollingEnabled", "false");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Storage>();
                services.AddSingleton(TempStorage());
                services.RemoveAll<IVacancySource>();
                services.AddSingleton<IVacancySource>(source);
            });
        }
    }
}
