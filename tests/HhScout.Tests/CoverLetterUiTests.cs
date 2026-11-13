using System.Text.Json;
using HhScout.Api;
using HhScout.Core;
using Microsoft.Playwright;

namespace HhScout.Tests;

[Trait("Category", "UI")]
public class CoverLetterUiTests
{
    [Fact]
    public async Task Dialog_generates_only_on_click_then_saves_edits_and_reopens_existing_draft()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var dist = Path.Combine(root, "web", "dist");
        Assert.True(File.Exists(Path.Combine(dist, "index.html")), "Build UI first: cd web; npm ci; npm run build");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Path.Combine(root, "src/HhScout.Api/.cache/playwright"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1365, Height = 980 } });
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var vacancy = new Vacancy("123", "Senior C# / .NET Developer", "42", "Тестовая компания", "https://hh.ru/vacancy/123", "Москва", "3–6 лет", true,
            200000, 300000, "RUR", false, DateTimeOffset.UtcNow, "ASP.NET Core, PostgreSQL", "Создание сервисов", ["C#", "ASP.NET Core"]);
        var letter = new LetterView("version1", "Здравствуйте! Это тестовый черновик для проверки интерфейса.", "gpt-6.1-sol", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);
        var generated = false; var calls = 0; var saved = false;
        await page.RouteAsync("http://scout.test/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            object? value = path switch
            {
                "/api/state" => new { mode = "browser", auth = new HhAuthState("authenticated"), search = new SearchSettings(), profile = new ResumeProfile(["C#"], "Тестовое резюме"), snapshot = new SnapshotInfo(new(), DateTimeOffset.UtcNow, 1, false), sync = new { running = false, nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5) }, intervalSeconds = 300 },
                "/api/vacancies/query" => new { items = new[] { new RankedVacancy(vacancy, new(100, ["C#"], [], ["C#"]), null) }, cached = 1 },
                "/api/cover-letters/provider" => new CodexStatus(true, true, "Тестовый провайдер. Модель в тесте не вызывается.", CodexLetterGenerator.Models),
                "/api/vacancies/123/context" => new LetterContext(vacancy, generated ? new("123", "Полное тестовое описание вакансии", DateTimeOffset.UtcNow) : null, generated ? [letter] : []),
                _ => null
            };
            if (path == "/api/vacancies/123/cover-letter") { Assert.Equal("POST", route.Request.Method); calls++; generated = true; value = letter; }
            if (path == "/api/cover-letters/version1")
            {
                Assert.Equal("PUT", route.Request.Method); saved = true;
                using var body = JsonDocument.Parse(route.Request.PostData!); letter = letter with { Text = body.RootElement.GetProperty("text").GetString()! }; value = letter;
            }
            if (value is not null) { await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = JsonSerializer.Serialize(value, json) }); return; }
            var file = Path.GetFullPath(Path.Combine(dist, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            if (path == "/") file = Path.Combine(dist, "index.html");
            Assert.StartsWith(dist + Path.DirectorySeparatorChar, file);
            var mime = Path.GetExtension(file) switch { ".js" => "application/javascript", ".css" => "text/css", ".woff2" => "font/woff2", _ => "text/html" };
            await route.FulfillAsync(new() { Status = 200, ContentType = mime, BodyBytes = await File.ReadAllBytesAsync(file) });
        });
        await page.GotoAsync("http://scout.test/");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сопроводительное", Exact = true }).ClickAsync();
        await page.GetByText("Письмо ещё не создавалось.", new() { Exact = false }).WaitForAsync();
        Assert.Equal(0, calls);
        await page.GetByRole(AriaRole.Button, new() { Name = "Сгенерировать письмо", Exact = true }).ClickAsync();
        var text = page.GetByLabel("Текст письма · можно редактировать");
        await text.WaitForAsync(); Assert.Equal(1, calls);
        await text.FillAsync("Здравствуйте! Отредактированное письмо.");
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Создать новую версию", Exact = true }).IsDisabledAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить правки", Exact = true }).ClickAsync();
        await page.GetByText("Изменения сохранены в локальной базе.", new() { Exact = true }).WaitForAsync();
        Assert.True(saved);
        await page.GetByRole(AriaRole.Button, new() { Name = "Закрыть", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Сопроводительное", Exact = true }).ClickAsync();
        await text.WaitForAsync(); Assert.Equal("Здравствуйте! Отредактированное письмо.", await text.InputValueAsync()); Assert.Equal(1, calls);
        var artifacts = Path.Combine(root, "artifacts"); Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "cover-letter-ui.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.True(await page.GetByRole(AriaRole.Dialog).IsVisibleAsync());
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
    }
}
