using HhScout.Api;
using HhScout.Core;
using Microsoft.Playwright;

namespace HhScout.Tests;

[Trait("Category", "Browser")]
public sealed class PlaywrightDriverTests
{
    [Fact]
    public async Task Real_chromium_performs_email_code_and_reads_two_rendered_pages()
    {
        using var playwright = await CreatePlaywright();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var authenticated = false;
        var requests = new List<string>();
        await context.RouteAsync("https://hh.ru/**", async route =>
        {
            var uri = new Uri(route.Request.Url);
            requests.Add(uri.AbsolutePath);
            var html = uri.AbsolutePath switch
            {
                "/account/login" => LoginHtml,
                "/applicant/resumes" => AuthenticatedHtml,
                "/search/vacancy" => SearchHtml(uri.Query.Contains("page=1", StringComparison.Ordinal) ? "102" : "101", authenticated),
                _ => "<h1>Unexpected test URL</h1>"
            };
            if (uri.AbsolutePath == "/applicant/resumes") authenticated = true;
            await route.FulfillAsync(new() { Status = 200, ContentType = "text/html; charset=utf-8", Body = html });
        });
        await using var driver = new PlaywrightHhDriver(new("unused-test-profile", true, 2), TimeProvider.System, context);
        var account = new HhAccount(driver, TimeProvider.System);
        Assert.Equal("codeRequired", (await account.RequestCodeAsync("max@example.org", default)).Stage);
        var metadata = await driver.DiagnosticAsync(default);
        Assert.Contains(metadata.Fields, x => x.Name == "code");
        Assert.Equal("codeRequired", (await account.SubmitCodeAsync("111111", default)).Stage);
        Assert.Equal("authenticated", (await account.SubmitCodeAsync("123456", default)).Stage);
        var result = await account.SearchAsync(new(".NET", "113"), default);
        Assert.Equal(2, result.Items.Length);
        Assert.All(result.Items, v => Assert.Contains("PostgreSQL", v.Skills));
        Assert.Equal(2, requests.Count(x => x == "/search/vacancy"));
        Assert.Equal("authenticated", account.State.Stage);
    }

    [Fact]
    public async Task Captcha_is_manual_and_guest_header_cannot_confirm_authentication()
    {
        using var playwright = await CreatePlaywright();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://hh.ru/**", route => route.FulfillAsync(new()
        {
            Status = 200, ContentType = "text/html", Body = "<div data-qa='captcha'>Подтвердите, что вы не робот</div>"
        }));
        await using var driver = new PlaywrightHhDriver(new("unused-test-profile", true), TimeProvider.System, context);
        Assert.Equal("manualAction", (await driver.RequestCodeAsync("max@example.org", default)).Stage);
        var page = Assert.Single(context.Pages);
        await page.SetContentAsync("<a data-qa='mainmenu_profile-link mainmenu_loginButton' href='/account/login'>Войти</a>");
        Assert.Equal("disconnected", (await driver.CheckAsync(default)).Stage);
    }

    private static async Task<IPlaywright> CreatePlaywright()
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/HhScout.Api/.cache/playwright")));
        return await Playwright.CreateAsync();
    }

    [Fact]
    public async Task Full_description_is_read_from_job_page_without_submitting_any_form()
    {
        using var playwright = await CreatePlaywright();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var paths = new List<string>();
        await context.RouteAsync("https://hh.ru/**", async route =>
        {
            Assert.Equal("GET", route.Request.Method);
            paths.Add(new Uri(route.Request.Url).AbsolutePath);
            await route.FulfillAsync(new() { Status = 200, ContentType = "text/html; charset=utf-8", Body = AuthenticatedHtml.Replace("</body>", "<div data-qa='vacancy-description'><p>Полное описание вакансии</p><ul><li>ASP.NET Core и SQL</li></ul></div></body>") });
        });
        await using var driver = new PlaywrightHhDriver(new("unused-test-profile", true), TimeProvider.System, context);
        var details = await driver.ReadDetailsAsync("123", default);
        Assert.Contains("ASP.NET Core и SQL", details.Description);
        Assert.Equal("123", details.VacancyId);
        Assert.Equal("/vacancy/123", Assert.Single(paths));
        await Assert.ThrowsAsync<HhException>(() => driver.ReadDetailsAsync("../applicant/resumes", default));
    }

    [Fact]
    public async Task Role_selection_recovers_from_late_react_hydration_without_resending_email()
    {
        using var playwright = await CreatePlaywright();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var delayed = LoginHtml.Replace("onclick=\"document.querySelector('#email-form').hidden=false; this.remove()\"", "")
            .Replace("function emailStep()", "setTimeout(()=>document.querySelector('#enter').onclick=()=>{document.querySelector('#email-form').hidden=false;document.querySelector('#enter').remove();},1000); function emailStep()");
        await context.RouteAsync("https://hh.ru/**", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = delayed }));
        await using var driver = new PlaywrightHhDriver(new("unused-test-profile", true), TimeProvider.System, context);
        Assert.Equal("codeRequired", (await driver.RequestCodeAsync("max@example.org", default)).Stage);
    }

    [Fact]
    public async Task Email_tab_ignores_hidden_responsive_duplicates()
    {
        using var playwright = await CreatePlaywright();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var html = LoginHtml.Replace("<body>", "<body><span style='display:none'>Почта</span>");
        await context.RouteAsync("https://hh.ru/**", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = html }));
        await using var driver = new PlaywrightHhDriver(new("unused-test-profile", true), TimeProvider.System, context);
        Assert.Equal("codeRequired", (await driver.RequestCodeAsync("max@example.org", default)).Stage);
    }

    private const string AuthenticatedHtml = """
        <!doctype html><html><body><a data-qa="mainmenu_applicantProfile" href="/applicant/resumes">Мои резюме</a></body></html>
        """;

    private const string LoginHtml = """
        <!doctype html><html><meta charset="utf-8"><body>
        <button id="enter" onclick="document.querySelector('#email-form').hidden=false; this.remove()">Войти</button>
        <form id="email-form" hidden onsubmit="event.preventDefault(); emailStep()">
        <label><input type="radio" name="type" data-qa="credential-type-email" aria-label="Почта">Почта</label>
        <input data-qa="applicant-login-input-email" name="login" type="text">
        <button data-qa="submit-button">Дальше</button></form>
        <script>
        function emailStep(){document.body.innerHTML='<form onsubmit="event.preventDefault(); codeStep()"><input autocomplete="one-time-code" name="code"><button data-qa="submit-button">Войти</button><p data-qa="login-error" role="alert" hidden>Код неверный</p></form>';}
        function codeStep(){if(document.querySelector('input[name=code]').value==='123456'){location.href='/applicant/resumes';}else{document.querySelector('[role=alert]').hidden=false;}}
        </script></body></html>
        """;

    private static string SearchHtml(string id, bool authenticated) => $$"""
        <!doctype html><html><meta charset="utf-8"><body>
        {{(authenticated ? AuthenticatedHtml : "<a data-qa='mainmenu_profile-link mainmenu_loginButton'>Войти</a>")}}
        <h1>Найдено 2 вакансии</h1>
        <article data-qa="vacancy-serp__vacancy"><div id="{{id}}">
        <a data-qa="serp-item__title" href="/vacancy/{{id}}">Senior C# .NET</a>
        <a data-qa="vacancy-serp__vacancy-employer" href="/employer/42">Fixture company</a>
        <div data-qa="vacancy-serp__vacancy_snippet_requirement">C# .NET PostgreSQL Docker</div>
        </div></article>
        {{(id == "101" ? "<a data-qa='pager-page' href='/search/vacancy?page=1'>2</a>" : "")}}
        </body></html>
        """;
}
