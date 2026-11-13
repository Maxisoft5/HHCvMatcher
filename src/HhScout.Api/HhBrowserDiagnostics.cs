using System.Text.Json;
using HhScout.Core;
using Microsoft.Playwright;

namespace HhScout.Api;

public static class HhBrowserDiagnostics
{
    public static async Task<int> ProbeLoginAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { Locale = "ru-RU" });
        await using var driver = new PlaywrightHhDriver(new("unused-public-probe", true), TimeProvider.System, context);
        try
        {
            var observation = await driver.PrepareEmailAsync(default);
            Console.WriteLine(JsonSerializer.Serialize(new { emailFormReady = observation is null, observation, metadata = await driver.DiagnosticAsync(default) }));
            return observation is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            // This probe uses a new empty context and never enters personal information.
            Console.Error.WriteLine(ex.Message);
            Console.WriteLine(JsonSerializer.Serialize(await driver.DiagnosticAsync(default)));
            return 1;
        }
    }

    // Explicit diagnostic: one public page, a temporary context, no account, no email or cookie export.
    public static async Task<int> ProbeAsync()
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { Locale = "ru-RU" });
            var page = await context.NewPageAsync();
            var response = await page.GotoAsync(PlaywrightHhDriver.BuildSearchUrl(new SearchSettings(), 0), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 40_000 });
            if (response is null || response.Status >= 400) throw new HhException($"hh.ru: HTTP {response?.Status}. Ручные проверки не обходятся.");
            await page.Locator("[data-qa~='vacancy-serp__vacancy']").First.WaitForAsync(new() { Timeout = 15_000 });
            var parsed = HhHtmlParser.Parse(await page.ContentAsync(), DateTimeOffset.UtcNow, new(page.Url));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                cards = parsed.Items.Length, found = parsed.Found, hasNext = parsed.HasNext,
                withDescriptions = parsed.Items.Count(x => x.Requirement.Length > 0),
                withRatings = parsed.Items.Count(x => x.EmployerRating.HasValue),
                dotnet = parsed.Items.Count(StackPolicy.IsDotNet)
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is HhException ? ex.Message : "Не удалось проверить публичную разметку в Chromium. Возможна проверка hh.ru или сетевая ошибка. Это не проверка авторизации аккаунта.");
            return 1;
        }
    }
}
