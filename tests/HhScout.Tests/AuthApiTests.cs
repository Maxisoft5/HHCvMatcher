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

public sealed class AuthApiTests
{
    [Fact]
    public async Task Http_auth_flow_unlocks_real_source_contract_and_never_returns_credentials()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/hh-auth/code", new { code = "123456" })).StatusCode);
        var email = await http.PostAsJsonAsync("/api/hh-auth/email", new { email = "private@example.org" });
        email.EnsureSuccessStatusCode();
        using var pending = JsonDocument.Parse(await email.Content.ReadAsStringAsync());
        Assert.Equal("codeRequired", pending.RootElement.GetProperty("stage").GetString());
        (await http.PostAsJsonAsync("/api/hh-auth/code", new { code = "123456" })).EnsureSuccessStatusCode();
        var sync = await http.PostAsJsonAsync("/api/sync", new { });
        sync.EnsureSuccessStatusCode();
        using var syncJson = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, syncJson.RootElement.GetProperty("error").ValueKind);
        var state = await http.GetStringAsync("/api/state");
        Assert.DoesNotContain("private@example.org", state);
        Assert.DoesNotContain("123456", state);
        Assert.DoesNotContain("applicationToken", state, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(state);
        Assert.Equal("authenticated", json.RootElement.GetProperty("auth").GetProperty("stage").GetString());
        (await http.PostAsJsonAsync("/api/hh-auth/disconnect", new { })).EnsureSuccessStatusCode();
        var auth = await http.GetFromJsonAsync<HhAuthState>("/api/hh-auth/status");
        Assert.Equal("disconnected", auth!.Stage);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Hh:PollingEnabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Storage>();
                services.AddSingleton(new Storage(Path.Combine(Path.GetTempPath(), "hh-scout-tests", Guid.NewGuid().ToString("N"), "scout.db")));
                services.RemoveAll<IHhBrowserDriver>();
                services.AddSingleton<IHhBrowserDriver, Driver>();
            });
        }
    }

    private sealed class Driver : IHhBrowserDriver
    {
        public bool HasSavedProfile => false;
        public Task<LoginObservation> RequestCodeAsync(string email, CancellationToken ct) => Task.FromResult(new LoginObservation("codeRequired"));
        public Task<LoginObservation> SubmitCodeAsync(string code, CancellationToken ct) => Task.FromResult(new LoginObservation("authenticated"));
        public Task<LoginObservation> CheckAsync(CancellationToken ct) => Task.FromResult(new LoginObservation("authenticated"));
        public Task ShowAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct) => Task.FromResult(new SearchResult([], 0, false));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
