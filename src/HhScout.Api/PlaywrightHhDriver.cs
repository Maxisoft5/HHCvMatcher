using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using HhScout.Core;
using Microsoft.Playwright;

namespace HhScout.Api;

public sealed record HhBrowserOptions(string ProfileDirectory, bool Headless = false, int MaxPages = 3);

public sealed class PlaywrightHhDriver(HhBrowserOptions options, TimeProvider time, IBrowserContext? suppliedContext = null) : IHhBrowserDriver
{
    private IPlaywright? playwright;
    private IBrowserContext? context = suppliedContext;
    private readonly bool ownsContext = suppliedContext is null;
    private IPage? authPage;
    private IPage? searchPage;
    private IPage? detailsPage;
    private IPage? activePage;
    private string lastStep = "idle";
    private const string LoginUrl = "https://hh.ru/account/login?backurl=%2F";
    private const string EmailSelector = "input[data-qa='applicant-login-input-email'], input[name='login'][type='email']";
    private const string CodeSelector = "input[autocomplete='one-time-code']:visible, input[name='code']:visible, input[name='otp']:visible, input[data-qa*='code'][type='text']:visible, input[data-qa*='code'][type='tel']:visible, input[inputmode='numeric'][maxlength='1']:visible";
    private const string AuthenticatedSelector = "[data-qa~='mainmenu_applicantProfile'], a[data-qa~='mainmenu_profile-link']:not([data-qa~='mainmenu_loginButton']), a[href='/applicant/resumes'], a[href^='/applicant/negotiations']";
    public bool HasSavedProfile => Directory.Exists(options.ProfileDirectory);

    private async Task<IBrowserContext> BrowserAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context is not null) return context;
        playwright ??= await Playwright.CreateAsync();
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(options.ProfileDirectory, new()
            {
                Headless = options.Headless, Locale = "ru-RU", ViewportSize = new() { Width = 1365, Height = 900 },
                AcceptDownloads = false
            });
        }
        catch (PlaywrightException)
        {
            throw new HhException("Не удалось запустить Chromium. Выполните start.ps1 или команду установки браузера из README. Если браузер уже открыт, закройте второй экземпляр проекта.");
        }
        context.SetDefaultTimeout(10_000);
        context.SetDefaultNavigationTimeout(40_000);
        context.Close += (_, _) => { context = null; authPage = null; searchPage = null; detailsPage = null; activePage = null; };
        return context;
    }

    private async Task<IPage> AuthPageAsync(CancellationToken ct)
    {
        var browser = await BrowserAsync(ct);
        if (authPage is null || authPage.IsClosed) authPage = await browser.NewPageAsync();
        activePage = authPage;
        return authPage;
    }

    public async Task<LoginObservation> RequestCodeAsync(string email, CancellationToken ct)
    {
        var preparation = await PrepareEmailAsync(ct);
        if (preparation is not null) return preparation;
        var page = authPage!;
        lastStep = "fill-email";
        await page.Locator(EmailSelector).First.FillAsync(email);
        ct.ThrowIfCancellationRequested();
        lastStep = "request-email-code";
        await page.Locator("button[data-qa='submit-button']").ClickAsync();
        lastStep = "wait-code-step";
        return await WaitForObservationAsync(page, false, ct);
    }

    // Navigates to the email field without filling credentials or requesting any message.
    public async Task<LoginObservation?> PrepareEmailAsync(CancellationToken ct)
    {
        var page = await AuthPageAsync(ct);
        lastStep = "open-login";
        await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var existing = await ObserveAsync(page);
        if (existing.Stage is "authenticated" or "manualAction") return existing;
        var emailRadio = page.Locator("input[data-qa~='credential-type-email']")
            .Or(page.GetByRole(AriaRole.Radio, new() { Name = "Почта", Exact = true })).First;
        if (!await emailRadio.IsVisibleAsync())
        {
            var enter = page.GetByRole(AriaRole.Button, new() { Name = "Войти", Exact = true });
            if (await enter.IsVisibleAsync())
            {
                lastStep = "select-applicant";
                await enter.ClickAsync();
                try { await emailRadio.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 4_000 }); }
                catch (System.TimeoutException)
                {
                    // hh.ru can render the role button before React attaches its click handler.
                    // Retry only this navigation button, never the email/OTP submission.
                    if (await enter.IsVisibleAsync()) await enter.ClickAsync();
                    await emailRadio.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                }
            }
        }
        lastStep = "select-email-tab";
        if (await emailRadio.IsVisibleAsync()) await page.GetByText("Почта", new() { Exact = true }).Filter(new() { Visible = true }).First.ClickAsync();
        var emailInput = page.Locator(EmailSelector).First;
        lastStep = "wait-email-input";
        await emailInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        lastStep = "email-form-ready";
        return null;
    }

    public async Task<LoginObservation> SubmitCodeAsync(string code, CancellationToken ct)
    {
        var page = await AuthPageAsync(ct);
        var before = await ObserveAsync(page);
        if (before.Stage is "authenticated" or "manualAction") return before;
        var inputs = page.Locator(CodeSelector);
        var count = await inputs.CountAsync();
        if (count == 0) return new("manualAction", "Поле кода не найдено. Откройте браузер, проверьте форму и нажмите «Проверить вход».");
        if (count > 1 && await inputs.First.GetAttributeAsync("maxlength") == "1")
        {
            if (count != code.Length) throw new HhException($"В форме hh.ru ожидается {count} цифр кода.");
            for (var i = 0; i < count; i++) await inputs.Nth(i).FillAsync(code[i].ToString());
        }
        else await inputs.First.FillAsync(code);
        ct.ThrowIfCancellationRequested();
        var submit = page.Locator("button[data-qa='submit-button']");
        if (await submit.IsVisibleAsync()) await submit.ClickAsync();
        else
        {
            var fallback = page.GetByRole(AriaRole.Button, new() { NameRegex = new("^(Войти|Дальше|Подтвердить(?: код)?|Продолжить)$", RegexOptions.IgnoreCase) }).First;
            if (await fallback.IsVisibleAsync()) await fallback.ClickAsync();
        }
        // Some versions submit automatically when the last OTP digit is entered.
        return await WaitForObservationAsync(page, true, ct);
    }

    public async Task<LoginObservation> CheckAsync(CancellationToken ct)
    {
        var page = activePage is { IsClosed: false } ? activePage : await AuthPageAsync(ct);
        if (page.Url == "about:blank") await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        return await ObserveAsync(page);
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        if (options.Headless) throw new HhException("Включён Headless. Перезапустите backend с Browser__Headless=false, чтобы открыть окно для ручной проверки.");
        var page = activePage is { IsClosed: false } ? activePage : await AuthPageAsync(ct);
        if (page.Url == "about:blank") await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.BringToFrontAsync();
    }

    public async Task<BrowserDiagnostic> DiagnosticAsync(CancellationToken ct)
    {
        var page = activePage is { IsClosed: false } ? activePage : await AuthPageAsync(ct);
        // Only field metadata, never values, cookies, page source or arbitrary body text.
        var fieldsJson = await page.Locator("input:visible").EvaluateAllAsync<string>("els => JSON.stringify(els.map(e => ({name:e.name||null,type:e.type||null,qa:e.getAttribute('data-qa')})))");
        var fields = JsonSerializer.Deserialize<BrowserField[]>(fieldsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var buttons = await page.Locator("button:visible").AllInnerTextsAsync();
        var headings = await page.Locator("h1:visible,h2:visible,h3:visible").AllInnerTextsAsync();
        return new(new Uri(page.Url).AbsolutePath, fields, buttons.Take(20).Select(SanitizeDiagnostic).ToArray(), headings.Take(10).Select(SanitizeDiagnostic).ToArray(), lastStep);
    }

    private static string SanitizeDiagnostic(string value) => Regex.Replace(Regex.Replace(value.Trim(), @"[^\s@]+@[^\s@]+", "[почта]"), @"\b\d{4,8}\b", "[цифры]");

    public async Task DisconnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context is not null) await context.CloseAsync();
        context = null; authPage = null; searchPage = null; detailsPage = null; activePage = null;
        if (ownsContext && Directory.Exists(options.ProfileDirectory))
        {
            var profile = Path.GetFullPath(options.ProfileDirectory);
            if (Path.GetFileName(profile) != "hh-browser" || Path.GetFileName(Path.GetDirectoryName(profile)) != "data")
                throw new HhException("Путь браузерного профиля не прошёл проверку. Локальную сессию нельзя очистить автоматически.");
            Directory.Delete(profile, recursive: true);
        }
    }

    public async Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct)
    {
        var browser = await BrowserAsync(ct);
        if (searchPage is null || searchPage.IsClosed) searchPage = await browser.NewPageAsync();
        var all = new List<Vacancy>();
        var found = 0;
        var hasNext = false;
        for (var page = 0; page < Math.Clamp(options.MaxPages, 1, 10); page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = BuildSearchUrl(search, page);
            var response = await searchPage.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var observation = await ObserveAsync(searchPage);
            if (observation.Stage == "manualAction" || response?.Status is 403 or 429)
            {
                activePage = searchPage;
                throw new HhManualActionException("hh.ru запросил ручную проверку или ограничил доступ. Откройте браузер и затем нажмите «Проверить вход».");
            }
            if (observation.Stage != "authenticated")
            {
                activePage = searchPage;
                throw new HhSessionExpiredException("Сессия hh.ru не подтверждена или истекла. Войдите по почте и коду снова.");
            }
            if (response is not null && response.Status >= 400) throw new HhException($"Страница вакансий вернула HTTP {response.Status}. Предыдущий список сохранён.");
            // enable_snippets=true requests the website's own expanded list, not its API.
            await searchPage.Locator("[data-qa~='vacancy-serp__vacancy'], h1").First.WaitForAsync();
            var parsed = HhHtmlParser.Parse(await searchPage.ContentAsync(), time.GetUtcNow(), new(searchPage.Url));
            all.AddRange(parsed.Items);
            found = parsed.Found;
            hasNext = parsed.HasNext;
            if (!hasNext) break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        var ordered = all.DistinctBy(x => x.Id).Select((vacancy, index) => vacancy with { SearchOrder = index }).ToArray();
        return new(ordered, Math.Max(found, ordered.Length), hasNext);
    }

    public async Task<VacancyDetails> ReadDetailsAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 20 || !id.All(char.IsAsciiDigit)) throw new HhException("Некорректный номер вакансии.");
        var browser = await BrowserAsync(ct);
        if (detailsPage is null || detailsPage.IsClosed) detailsPage = await browser.NewPageAsync();
        var response = await detailsPage.GotoAsync($"https://hh.ru/vacancy/{id}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        ct.ThrowIfCancellationRequested();
        var observation = await ObserveAsync(detailsPage);
        if (observation.Stage == "manualAction" || response?.Status is 403 or 429)
        { activePage = detailsPage; throw new HhManualActionException("hh.ru запросил ручную проверку. Откройте браузер и проверьте вход."); }
        if (observation.Stage != "authenticated")
        { activePage = detailsPage; throw new HhSessionExpiredException("Сессия hh.ru истекла. Проверьте вход."); }
        if (response?.Status >= 400) throw new HhException($"Не удалось прочитать вакансию: HTTP {response.Status}. Сохранённый контекст не изменён.");
        await detailsPage.Locator("[data-qa='vacancy-description']").First.WaitForAsync();
        ct.ThrowIfCancellationRequested();
        return VacancyDetailsParser.Parse(await detailsPage.ContentAsync(), id, time.GetUtcNow());
    }

    public static string BuildSearchUrl(SearchSettings search, int page)
    {
        var values = new Dictionary<string, string?>
        {
            ["text"] = search.Text, ["order_by"] = "publication_time", ["enable_snippets"] = "true",
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture), ["items_on_page"] = "50"
        };
        if (search.Area.Length > 0) values["area"] = search.Area;
        if (search.RemoteOnly) values["work_format"] = "REMOTE";
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("https://hh.ru/search/vacancy", values);
    }

    private static async Task<LoginObservation> ObserveAsync(IPage page)
    {
        var captcha = page.Locator("iframe[src*='captcha']:visible, [data-qa*='captcha']:visible, input[name='captcha']:visible");
        if (await captcha.CountAsync() > 0 || await page.GetByText(new Regex("Подтвердите, что вы не робот|Введите символы с картинки|Доступ временно ограничен", RegexOptions.IgnoreCase)).First.IsVisibleAsync())
            return new("manualAction", "hh.ru требует ручную проверку. Выполните её в окне браузера; приложение не решает CAPTCHA.");
        if (await page.Locator(AuthenticatedSelector).First.IsVisibleAsync()) return new("authenticated", "Сессия hh.ru подключена.");
        if (new Uri(page.Url).AbsolutePath.StartsWith("/account/signup", StringComparison.Ordinal))
            return new("manualAction", "hh.ru предлагает регистрацию. Приложение не создаёт аккаунты автоматически; проверьте почту или продолжите вручную.");
        var error = page.Locator("[data-qa*='error']:visible, [role='alert']:visible").First;
        var message = await error.IsVisibleAsync() ? (await error.InnerTextAsync()).Trim() : null;
        // Never copy arbitrary page text into status: it could include the entered email or code.
        var safeError = string.IsNullOrWhiteSpace(message) ? null : "hh.ru не принял данные. Проверьте введённую почту или код в окне браузера.";
        if (await page.Locator(CodeSelector).CountAsync() > 0) return new("codeRequired", safeError ?? "Введите код из письма.");
        return new("disconnected", safeError ?? "Войдите по почте и коду.");
    }

    private static async Task<LoginObservation> WaitForObservationAsync(IPage page, bool submittedCode, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        LoginObservation observation;
        do
        {
            ct.ThrowIfCancellationRequested();
            observation = await ObserveAsync(page);
            if (observation.Stage is "authenticated" or "manualAction") return observation;
            if (observation.Stage == "codeRequired" && (!submittedCode || observation.Message?.StartsWith("hh.ru не принял", StringComparison.Ordinal) == true)) return observation;
            if (observation.Message?.StartsWith("hh.ru не принял", StringComparison.Ordinal) == true) return observation;
            await Task.Delay(300, ct);
        } while (clock.Elapsed < TimeSpan.FromSeconds(20));
        return observation.Stage == "codeRequired" ? observation : new("manualAction", "Форма hh.ru не перешла к ожидаемому шагу. Откройте браузер и нажмите «Проверить вход» после ручного действия.");
    }

    public async ValueTask DisposeAsync()
    {
        if (context is not null) await context.CloseAsync();
        playwright?.Dispose();
    }
}
