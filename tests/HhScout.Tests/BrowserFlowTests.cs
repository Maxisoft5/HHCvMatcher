using HhScout.Api;
using HhScout.Core;

namespace HhScout.Tests;

public class BrowserFlowTests
{
    [Fact]
    public async Task Email_then_manual_code_unlocks_search_without_ever_exposing_the_code()
    {
        var driver = new FakeDriver();
        var account = new HhAccount(driver, TimeProvider.System);
        await Assert.ThrowsAsync<HhException>(() => account.SearchAsync(new(), default));
        var pending = await account.RequestCodeAsync("max@example.org", default);
        Assert.Equal("codeRequired", pending.Stage);
        Assert.DoesNotContain("max@example.org", pending.EmailHint ?? "");
        var signedIn = await account.SubmitCodeAsync("123456", default);
        Assert.Equal("authenticated", signedIn.Stage);
        await account.SearchAsync(new(), default);
        Assert.Equal(1, driver.Searches);
        Assert.Equal("123456", driver.LastCode);
        Assert.DoesNotContain("123456", System.Text.Json.JsonSerializer.Serialize(account.State));
    }

    [Fact]
    public async Task Rejects_code_without_pending_login_and_repeated_code_requests()
    {
        var driver = new FakeDriver();
        var account = new HhAccount(driver, TimeProvider.System);
        await Assert.ThrowsAsync<HhException>(() => account.SubmitCodeAsync("123456", default));
        await account.RequestCodeAsync("max@example.org", default);
        await Assert.ThrowsAsync<HhException>(() => account.RequestCodeAsync("max@example.org", default));
        Assert.Equal(1, driver.CodeRequests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@example.org\r\nfoo")]
    public async Task Invalid_email_never_reaches_the_browser(string email)
    {
        var driver = new FakeDriver();
        var account = new HhAccount(driver, TimeProvider.System);
        await Assert.ThrowsAsync<HhException>(() => account.RequestCodeAsync(email, default));
        Assert.Equal(0, driver.CodeRequests);
    }

    [Fact]
    public async Task Wrong_code_remains_pending_and_captcha_requires_manual_action()
    {
        var driver = new FakeDriver { SubmitResult = new("codeRequired", "Код неверный") };
        var account = new HhAccount(driver, TimeProvider.System);
        await account.RequestCodeAsync("max@example.org", default);
        Assert.Equal("codeRequired", (await account.SubmitCodeAsync("111111", default)).Stage);
        driver.SubmitResult = new("manualAction", "Проверка hh.ru");
        Assert.Equal("manualAction", (await account.SubmitCodeAsync("222222", default)).Stage);
        await Assert.ThrowsAsync<HhException>(() => account.SearchAsync(new(), default));
        Assert.Equal(0, driver.Searches);
    }

    [Fact]
    public async Task Session_expiry_and_disconnect_do_not_claim_to_be_authenticated()
    {
        var driver = new FakeDriver();
        var account = new HhAccount(driver, TimeProvider.System);
        await account.RequestCodeAsync("max@example.org", default);
        await account.SubmitCodeAsync("123456", default);
        driver.Expired = true;
        await Assert.ThrowsAsync<HhException>(() => account.SearchAsync(new(), default));
        Assert.Equal("disconnected", account.State.Stage);
        await account.DisconnectAsync(default);
        Assert.Null(account.State.EmailHint);
        Assert.True(driver.Disconnected);
    }

    private sealed class FakeDriver : IHhBrowserDriver
    {
        public bool HasSavedProfile => false;
        public int CodeRequests { get; private set; }
        public int Searches { get; private set; }
        public string? LastCode { get; private set; }
        public bool Expired { get; set; }
        public bool Disconnected { get; private set; }
        public LoginObservation SubmitResult { get; set; } = new("authenticated");
        public Task<LoginObservation> RequestCodeAsync(string email, CancellationToken ct) { CodeRequests++; return Task.FromResult(new LoginObservation("codeRequired")); }
        public Task<LoginObservation> SubmitCodeAsync(string code, CancellationToken ct) { LastCode = code; return Task.FromResult(SubmitResult); }
        public Task<LoginObservation> CheckAsync(CancellationToken ct) => Task.FromResult(new LoginObservation("disconnected"));
        public Task ShowAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) { Disconnected = true; return Task.CompletedTask; }
        public Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct)
        {
            Searches++;
            return Expired ? Task.FromException<SearchResult>(new HhSessionExpiredException("Войдите снова")) : Task.FromResult(new SearchResult([], 0, false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
