using HhScout.Core;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HhScout.Api;

public sealed record ParsedSearch(Vacancy[] Items, int Found, bool HasNext);
public sealed record ParsedSalary(decimal? From, decimal? To, string? Currency, bool? Gross, string Period);

public static class HhHtmlParser
{
    public static ParsedSearch Parse(string html, DateTimeOffset observedAt, Uri pageUrl)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var cards = document.QuerySelectorAll("[data-qa~='vacancy-serp__vacancy']");
        var jobs = new List<Vacancy>();
        foreach (var card in cards)
        {
            var title = card.QuerySelector("a[data-qa='serp-item__title']");
            if (title is null) continue;
            var href = title.GetAttribute("href") ?? "";
            var id = Match(href, @"(?:https://(?:[a-z]+\.)?hh\.ru)?/vacancy/(\d+)");
            if (id.Length == 0) id = card.QuerySelectorAll("[id]").Select(x => x.Id).FirstOrDefault(x => x is not null && x.Length > 0 && x.All(char.IsAsciiDigit)) ?? "";
            if (id.Length == 0) continue;
            var employer = card.QuerySelector("[data-qa='vacancy-serp__vacancy-employer']");
            var employerId = Match(employer?.GetAttribute("href") ?? "", @"/employer/(\d+)");
            var requirement = Text(card, "[data-qa='vacancy-serp__vacancy_snippet_requirement']");
            var responsibility = Text(card, "[data-qa='vacancy-serp__vacancy_snippet_responsibility']");
            var tags = card.QuerySelectorAll("[data-qa='skills-element'], [data-qa='bloko-tag__text']").Select(x => Clean(x.TextContent)).Where(x => x.Length > 0).ToArray();
            var salaryNode = card.QuerySelector("[data-qa='vacancy-serp__vacancy-compensation']") ??
                card.QuerySelector("data[value='RUB'], data[value='USD'], data[value='EUR'], data[value='KZT']")?.ParentElement;
            var salary = ParseSalary(salaryNode?.TextContent ?? "");
            var knownDate = DateTimeOffset.TryParse(card.QuerySelector("time[datetime]")?.GetAttribute("datetime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            var rating = decimal.TryParse(Text(card, "[data-qa='company-review-rating-value']").Replace(',', '.'), CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 5 ? value : (decimal?)null;
            var reviewDigits = Regex.Replace(Text(card, "[data-qa='company-review-rating-reviews-count']"), @"\D", "");
            jobs.Add(new(id, Clean(title.TextContent), employerId, Clean(employer?.TextContent ?? ""), $"https://hh.ru/vacancy/{id}",
                Text(card, "[data-qa='vacancy-serp__vacancy-address']"), Text(card, "[data-qa^='vacancy-serp__vacancy-work-experience']"),
                card.QuerySelector("[data-qa='vacancy-label-work-schedule-remote']") is not null,
                salary.From, salary.To, salary.Currency, salary.Gross, knownDate ? date : observedAt, requirement, responsibility,
                tags.Length > 0 ? SkillMatcher.Distinct(tags) : SkillMatcher.Extract($"{title.TextContent} {requirement} {responsibility}"),
                tags.Length > 0, knownDate, salary.Period, rating, int.TryParse(reviewDigits, out var reviews) ? reviews : null));
        }
        var heading = Clean(document.QuerySelector("h1")?.TextContent ?? "");
        var totalText = Match(heading, @"([\d\s]+)\s*ваканси");
        var totalDigits = Regex.Replace(totalText, @"\D", "");
        var found = int.TryParse(totalDigits, out var count) ? count : jobs.Count;
        var empty = Regex.IsMatch(heading + " " + Text(document.DocumentElement, "[data-qa='vacancy-serp__no-results']"),
            @"ничего не найдено|вакансий не найдено|найдено\s*0\s*вакансий|нет вакансий", RegexOptions.IgnoreCase);
        if (jobs.Count == 0 && !empty) throw new HhException("Не удалось распознать карточки hh.ru. Возможно, разметка изменилась. Предыдущий список сохранён.");
        var page = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(pageUrl.Query).TryGetValue("page", out var p) && int.TryParse(p, out var pageNumber) ? pageNumber : 0;
        var hasNext = document.QuerySelectorAll("a[data-qa='pager-page']").Any(a =>
            Uri.TryCreate(pageUrl, a.GetAttribute("href"), out var url) &&
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query).TryGetValue("page", out var n) && int.TryParse(n, out var next) && next > page);
        return new(jobs.DistinctBy(x => x.Id).ToArray(), empty ? 0 : found, hasNext);
    }

    public static ParsedSalary ParseSalary(string text)
    {
        var clean = Clean(text);
        var currency = Regex.Match(clean, @"₽|руб\.?|RUB|RUR|USD|\$|EUR|€|KZT|₸", RegexOptions.IgnoreCase);
        if (!currency.Success) return new(null, null, null, null, "unknown");
        var numbers = Regex.Matches(clean[..currency.Index], @"\d[\d\s]*(?:[.,]\d+)?").Select(x =>
            decimal.Parse(Regex.Replace(x.Value, @"\s", "").Replace(',', '.'), CultureInfo.InvariantCulture)).Take(2).ToArray();
        decimal? from = numbers.Length == 0 ? null : clean.StartsWith("до ", StringComparison.OrdinalIgnoreCase) ? null : numbers[0];
        decimal? to = numbers.Length >= 2 ? numbers[1] : clean.StartsWith("до ", StringComparison.OrdinalIgnoreCase) ? numbers.FirstOrDefault() : null;
        if (numbers.Length == 1 && !Regex.IsMatch(clean, @"^от\s|^до\s", RegexOptions.IgnoreCase)) to = from;
        var code = currency.Value.ToUpperInvariant() switch { "₽" or "РУБ" or "РУБ." or "RUB" or "RUR" => "RUR", "$" or "USD" => "USD", "€" or "EUR" => "EUR", _ => "KZT" };
        bool? gross = clean.Contains("до вычета", StringComparison.OrdinalIgnoreCase) ? true : clean.Contains("на руки", StringComparison.OrdinalIgnoreCase) ? false : null;
        var period = clean.Contains("месяц", StringComparison.OrdinalIgnoreCase) ? "month" : clean.Contains("час", StringComparison.OrdinalIgnoreCase) ? "hour"
            : clean.Contains("смен", StringComparison.OrdinalIgnoreCase) ? "shift" : clean.Contains("день", StringComparison.OrdinalIgnoreCase) ? "day" : "unknown";
        return new(from, to, code, gross, period);
    }

    private static string Text(IElement element, string selector) => Clean(element.QuerySelector(selector)?.TextContent ?? "");
    private static string Clean(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    private static string Match(string text, string pattern) { var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase); return match.Success ? match.Groups[1].Value : ""; }
}
