using HhScout.Core;

namespace HhScout.Tests;

public class MatchingTests
{
    private static Vacancy Job(string id = "1", string[]? skills = null, decimal? from = 200_000,
        decimal? to = 300_000, string currency = "RUR", bool remote = true) => new(
        id, ".NET Developer", "42", "Company", "https://hh.ru/vacancy/1", "Москва", "3–6 лет", remote,
        from, to, currency, false, DateTimeOffset.UtcNow, "", "", skills ?? ["C#", "SQL", "React"], true);

    [Fact]
    public void Matching_explains_coverage_of_vacancy_skills()
    {
        var result = SkillMatcher.Match(new(["c#", "react", "React"]), Job());
        Assert.Equal(67, result.Score);
        Assert.Equal(["C#", "React"], result.Matched);
        Assert.Equal(["SQL"], result.Missing);
    }

    [Fact]
    public void Unknown_requirements_do_not_pretend_to_be_a_match()
    {
        var result = SkillMatcher.Match(new(["C#"]), Job(skills: []));
        Assert.Null(result.Score);
    }

    [Fact]
    public void Empty_resume_has_no_score()
    {
        Assert.Null(SkillMatcher.Match(new([]), Job()).Score);
    }

    [Fact]
    public void Extracts_aliases_without_matching_java_inside_javascript_or_c_inside_words()
    {
        var result = SkillMatcher.Extract("Опыт ASP.NET Core, c sharp, PostgreSQL и JavaScript. Docker. React Native. react.js");
        Assert.Contains(".NET", result);
        Assert.Contains("C#", result);
        Assert.Contains("PostgreSQL", result);
        Assert.Contains("JavaScript", result);
        Assert.DoesNotContain("Java", result);
    }

    [Fact]
    public void Explicit_skills_and_resume_text_are_combined()
    {
        var result = SkillMatcher.Match(new(["C#"], "Работал с SQL и React"), Job());
        Assert.Equal(100, result.Score);
    }

    [Theory]
    [InlineData("ASP.NET Core", "ASP.NET")]
    [InlineData("React.js", "React")]
    [InlineData("SQL Server", "MS SQL")]
    [InlineData("dotnet", ".NET")]
    [InlineData("С#", "C#")]
    public void Normalizes_compound_aliases(string alias, string expected)
        => Assert.Equal(expected, SkillMatcher.Normalize(alias));

    [Fact]
    public void React_dot_js_is_not_an_additional_javascript_skill()
        => Assert.Equal(["React"], SkillMatcher.Extract("react.js"));

    [Fact]
    public void Filters_require_all_skills_and_matching_currency()
    {
        var jobs = new[] { Job("1"), Job("2", currency: "USD"), Job("3", skills: ["C#"]) };
        var result = VacancyRanking.Rank(jobs, new(["C#"]), new(["c#", "react"], MinSalary: 250_000), new Dictionary<string, CompanyInfo>());
        Assert.Single(result);
        Assert.Equal("1", result[0].Vacancy.Id);
    }

    [Fact]
    public void Salary_with_only_lower_bound_is_open_ended()
    {
        Assert.Single(VacancyRanking.Rank([Job(to: null)], new([]), new([], MinSalary: 250_000), new Dictionary<string, CompanyInfo>()));
    }

    [Fact]
    public void Unknown_salary_is_excluded_by_salary_filter()
    {
        Assert.Empty(VacancyRanking.Rank([Job(from: null, to: null)], new([]), new([], MinSalary: 1), new Dictionary<string, CompanyInfo>()));
    }

    [Fact]
    public void Hourly_compensation_is_not_compared_with_monthly_salary()
    {
        var hourly = Job() with { SalaryPeriod = "hour" };
        Assert.Empty(VacancyRanking.Rank([hourly], new([]), new([], MinSalary: 1), new Dictionary<string, CompanyInfo>()));
    }

    [Fact]
    public void Website_rating_is_used_unless_user_supplied_company_notes()
    {
        var job = Job() with { EmployerRating = 4.8m, EmployerReviewCount = 20 };
        var filter = new VacancyFilter([], MinRating: 4, IncludeUnknownCompanies: false);
        var observed = Assert.Single(VacancyRanking.Rank([job], new([]), filter, new Dictionary<string, CompanyInfo>()));
        Assert.True(observed.Company!.FromWebsite);
        var manual = new Dictionary<string, CompanyInfo> { ["42"] = new("42", 100, 3, "Моя оценка") };
        Assert.Empty(VacancyRanking.Rank([job], new([]), filter, manual));
    }

    [Fact]
    public void Company_unknown_policy_is_explicit()
    {
        var filter = new VacancyFilter([], MinRating: 4, MinEmployees: 50, IncludeUnknownCompanies: false);
        Assert.Empty(VacancyRanking.Rank([Job()], new([]), filter, new Dictionary<string, CompanyInfo>()));
        Assert.Single(VacancyRanking.Rank([Job()], new([]), filter with { IncludeUnknownCompanies = true }, new Dictionary<string, CompanyInfo>()));
        var companies = new Dictionary<string, CompanyInfo> { ["42"] = new("42", 100, 3, "Моя оценка") };
        Assert.Empty(VacancyRanking.Rank([Job()], new([]), filter with { IncludeUnknownCompanies = true }, companies));
    }

    [Fact]
    public void Sorts_by_score_then_publication_and_deduplicates_ids()
    {
        var result = VacancyRanking.Rank([Job("1"), Job("2", skills: ["C#"]), Job("1")], new(["C#"]), new([]), new Dictionary<string, CompanyInfo>());
        Assert.Equal(["2", "1"], result.Select(x => x.Vacancy.Id));
    }

    [Fact]
    public void Unknown_dates_preserve_website_order_instead_of_fetching_last_page_first()
    {
        var newest = Job("new") with { PublicationDateKnown = false, SearchOrder = 0, PublishedAt = DateTimeOffset.UtcNow.AddSeconds(-5) };
        var older = Job("old") with { PublicationDateKnown = false, SearchOrder = 20, PublishedAt = DateTimeOffset.UtcNow };
        var result = VacancyRanking.Rank([older, newest], new([]), new([]), new Dictionary<string, CompanyInfo>());
        Assert.Equal(["new", "old"], result.Select(x => x.Vacancy.Id));
    }
}
