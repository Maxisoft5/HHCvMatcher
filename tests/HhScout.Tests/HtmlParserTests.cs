using HhScout.Api;

namespace HhScout.Tests;

public class HtmlParserTests
{
    [Fact]
    public void Reads_observed_hh_card_selectors_snippets_rating_and_pagination()
    {
        var html = """
            <h1>Найдено 123 вакансии «.NET»</h1>
            <section data-qa="vacancy-serp__results">
            <article data-qa="vacancy-serp__vacancy vacancy-serp-item_clickme"><div id="42">
              <a data-qa="serp-item__title" href="https://adsrv.hh.ru/click">Senior C# / .NET</a>
              <div class="compensation-labels--abc"><span><data value="250000">250 000</data> – <data value="350000">350 000</data><data value="RUB">₽</data> за месяц, на руки</span></div>
              <data data-qa="vacancy-serp__vacancy-work-experience-between3And6">Опыт 3-6 лет</data>
              <span data-qa="vacancy-label-work-schedule-remote">Можно удалённо</span>
              <a data-qa="vacancy-serp__vacancy-employer" href="/employer/100">Компания &amp; Co</a>
              <span data-qa="vacancy-serp__vacancy-address">Москва</span>
              <span data-qa="company-review-rating-value">4.7</span>
              <span data-qa="company-review-rating-reviews-count">17 отзывов</span>
              <div data-qa="vacancy-serp__vacancy_snippet_requirement">C#, .NET, PostgreSQL &amp; Docker</div>
            </div></article></section>
            <a data-qa="pager-page" href="/search/vacancy?page=1">2</a>
            """;
        var result = HhHtmlParser.Parse(html, DateTimeOffset.UtcNow, new("https://hh.ru/search/vacancy?page=0"));
        Assert.Equal(123, result.Found);
        Assert.True(result.HasNext);
        var job = Assert.Single(result.Items);
        Assert.Equal("42", job.Id);
        Assert.Equal("https://hh.ru/vacancy/42", job.Url);
        Assert.Equal("Компания & Co", job.Employer);
        Assert.True(job.Remote);
        Assert.Equal(250000, job.SalaryFrom);
        Assert.Equal("RUR", job.Currency);
        Assert.Contains("PostgreSQL", job.Skills);
        Assert.DoesNotContain("<", job.Requirement);
        Assert.Equal(4.7m, job.EmployerRating);
        Assert.Equal(17, job.EmployerReviewCount);
        Assert.False(job.PublicationDateKnown);
    }

    [Theory]
    [InlineData("от 200 000 ₽ за месяц, до вычета налогов", 200000, null, "RUR", true, "month")]
    [InlineData("до 3 000 $ за месяц, на руки", null, 3000, "USD", false, "month")]
    [InlineData("1 500 – 2 000 ₽ за час, на руки", 1500, 2000, "RUR", false, "hour")]
    public void Salary_parser_handles_bounds_taxes_currencies_and_periods(string text, int? from, int? to, string currency, bool gross, string period)
    {
        var salary = HhHtmlParser.ParseSalary(text);
        Assert.Equal(from, salary.From);
        Assert.Equal(to, salary.To);
        Assert.Equal(currency, salary.Currency);
        Assert.Equal(gross, salary.Gross);
        Assert.Equal(period, salary.Period);
    }

    [Fact]
    public void Recognizes_explicit_empty_results_but_rejects_unknown_markup()
    {
        Assert.Empty(HhHtmlParser.Parse("<h1>По вашему запросу ничего не найдено</h1>", DateTimeOffset.UtcNow, new("https://hh.ru/search/vacancy")).Items);
        Assert.Throws<HhException>(() => HhHtmlParser.Parse("<h1>Проверка соединения</h1>", DateTimeOffset.UtcNow, new("https://hh.ru/search/vacancy")));
    }
}
