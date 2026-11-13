namespace HhScout.Core;

public sealed record Vacancy(
    string Id, string Title, string EmployerId, string Employer, string Url,
    string Area, string Experience, bool Remote, decimal? SalaryFrom, decimal? SalaryTo,
    string? Currency, bool? Gross, DateTimeOffset PublishedAt, string Requirement,
    string Responsibility, string[] Skills, bool SkillsVerified = false,
    bool PublicationDateKnown = true, string SalaryPeriod = "month", decimal? EmployerRating = null, int? EmployerReviewCount = null,
    int SearchOrder = int.MaxValue);

public sealed record SearchSettings(string Text = ".NET", string Area = "113", bool RemoteOnly = false);
public sealed record ResumeProfile(string[] Skills, string Text = "");
public sealed record CompanyInfo(string EmployerId, int? Employees, decimal? Rating, string? Source, bool FromWebsite = false, int? Reviews = null);
public sealed record MatchResult(int? Score, string[] Matched, string[] Missing, string[] Required);
public sealed record RankedVacancy(Vacancy Vacancy, MatchResult Match, CompanyInfo? Company);
public sealed record VacancyFilter(
    string[] Skills, int MinScore = 0, decimal? MinSalary = null, string Currency = "RUR",
    bool RemoteOnly = false, int? MinEmployees = null, int? MaxEmployees = null,
    decimal? MinRating = null, bool IncludeUnknownCompanies = true, string Employer = "", bool StrictDotNet = true);

public static class SkillMatcher
{
    private static readonly (string Name, string Pattern)[] Catalog =
    [
        ("C#", @"[cс]#|c\s*sharp|си\s*шарп"),
        (".NET", @"\.?net(?:\s*(?:core|framework))?|asp\.?net(?:\s*core)?|dotnet"),
        ("React", @"react(?:\.js|js)?"), ("TypeScript", @"typescript"),
        ("JavaScript", @"javascript|(?<!\.)js"), ("SQL", @"sql|t-sql"),
        ("PostgreSQL", @"postgres(?:ql)?"), ("MS SQL", @"ms\s*sql(?:\s*server)?|mssql|sql\s*server"),
        ("Docker", @"docker"), ("Kubernetes", @"kubernetes|k8s"),
        ("Redis", @"redis"), ("RabbitMQ", @"rabbit\s*mq"), ("Kafka", @"kafka"),
        ("Entity Framework", @"entity\s*framework(?:\s*core)?|ef\s*core"),
        ("ASP.NET", @"asp\.?net(?:\s*core)?"), ("REST", @"rest(?:ful)?"),
        ("GraphQL", @"graphql"), ("gRPC", @"grpc"), ("Git", @"git"),
        ("CI/CD", @"ci\s*/\s*cd"), ("Azure", @"azure"), ("AWS", @"aws"),
        ("Linux", @"linux"), ("MongoDB", @"mongo(?:db)?"), ("Angular", @"angular"),
        ("Vue", @"vue(?:\.js|js)?"), ("Java", @"java"), ("Python", @"python"),
        ("Go", @"golang|go"), ("C++", @"[cс]\+\+"), ("F#", @"f#"), ("Blazor", @"blazor"),
        ("Unity", @"unity"), ("Unigine", @"unigine"),
        ("xUnit", @"xunit"), ("NUnit", @"nunit"), ("Playwright", @"playwright"),
        ("Microservices", @"microservices|микросервис[\p{L}]*"),
        ("HTML", @"html5?"), ("CSS", @"css3?"), ("WPF", @"wpf"),
        ("PHP", @"php"), ("Ruby", @"ruby"), ("Kotlin", @"kotlin"), ("Swift", @"swift"),
        ("Scala", @"scala"), ("Flutter", @"flutter"), ("Dart", @"dart")
    ];

    private static readonly (string Name, System.Text.RegularExpressions.Regex Extract, System.Text.RegularExpressions.Regex Alias)[] Rules = Catalog.Select(x =>
        (x.Name, Regex(@"(?<![\p{L}\p{N}_])(?:" + x.Pattern + @")(?![\p{L}\p{N}_])"), Regex("^(?:" + x.Pattern + ")$"))).ToArray();
    private static readonly (string Name, System.Text.RegularExpressions.Regex Extract, System.Text.RegularExpressions.Regex Alias)[] Aliases = Rules.OrderBy(x => x.Name == ".NET" ? 1 : 0).ToArray();

    private static System.Text.RegularExpressions.Regex Regex(string pattern) => new(pattern,
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    public static string[] Extract(string text) => Rules
        .Where(x => x.Extract.IsMatch(text))
        .Select(x => x.Name).ToArray();

    public static string Normalize(string skill)
    {
        var value = skill.Trim();
        foreach (var entry in Aliases)
            if (entry.Alias.IsMatch(value)) return entry.Name;
        return value;
    }

    public static string[] Distinct(IEnumerable<string> skills) => skills.Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static MatchResult Match(ResumeProfile resume, Vacancy vacancy)
    {
        var owned = Distinct(resume.Skills.Concat(Extract(resume.Text)));
        var required = Distinct(vacancy.Skills);
        var matched = required.Intersect(owned, StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = required.Except(owned, StringComparer.OrdinalIgnoreCase).ToArray();
        int? score = required.Length == 0 || owned.Length == 0 ? null
            : (int)Math.Round(matched.Length * 100d / required.Length, MidpointRounding.AwayFromZero);
        return new(score, matched, missing, required);
    }
}

public static class VacancyRanking
{
    public static RankedVacancy[] Rank(IEnumerable<Vacancy> vacancies, ResumeProfile resume,
        VacancyFilter filter, IReadOnlyDictionary<string, CompanyInfo> companies) => vacancies.DistinctBy(x => x.Id)
        .Select(v => new RankedVacancy(v, SkillMatcher.Match(resume, v), companies.GetValueOrDefault(v.EmployerId) ??
            (v.EmployerRating.HasValue ? new CompanyInfo(v.EmployerId, null, v.EmployerRating, "Рейтинг на странице hh.ru", true, v.EmployerReviewCount) : null)))
        .Where(x => !filter.StrictDotNet || StackPolicy.IsDotNet(x.Vacancy))
        .Where(x => filter.Skills.All(s => x.Vacancy.Skills.Any(v => string.Equals(SkillMatcher.Normalize(v), SkillMatcher.Normalize(s), StringComparison.OrdinalIgnoreCase))))
        .Where(x => filter.MinScore <= 0 || x.Match.Score >= filter.MinScore)
        .Where(x => !filter.RemoteOnly || x.Vacancy.Remote)
        .Where(x => x.Vacancy.Employer.Contains(filter.Employer, StringComparison.OrdinalIgnoreCase))
        .Where(x => filter.MinSalary is null ||
            (x.Vacancy.Currency == filter.Currency && x.Vacancy.SalaryPeriod == "month" &&
             (x.Vacancy.SalaryTo >= filter.MinSalary || (x.Vacancy.SalaryTo is null && x.Vacancy.SalaryFrom is not null))))
        .Where(x => InRange(x.Company?.Employees, filter.MinEmployees, filter.MaxEmployees, filter.IncludeUnknownCompanies))
        .Where(x => InRange(x.Company?.Rating, filter.MinRating, null, filter.IncludeUnknownCompanies))
        .OrderByDescending(x => x.Match.Score ?? -1).ThenBy(x => x.Vacancy.SearchOrder)
        .ThenByDescending(x => x.Vacancy.PublicationDateKnown ? x.Vacancy.PublishedAt : DateTimeOffset.MinValue).ThenBy(x => x.Vacancy.Id).ToArray();

    private static bool InRange(decimal? value, decimal? min, decimal? max, bool includeUnknown)
        => min is null && max is null || (value is null ? includeUnknown :
            (min is null || value >= min) && (max is null || value <= max));
}

public static class StackPolicy
{
    public static bool IsDotNet(Vacancy vacancy)
    {
        var titleSkills = SkillMatcher.Extract(vacancy.Title);
        if (System.Text.RegularExpressions.Regex.IsMatch(vacancy.Title,
            @"\bqa\b|\bsdet\b|\btester\b|тестировщик|аналитик|\banalyst\b|дизайнер|devops|поддержк|обслуживан|\bsupport\b|\bunity\b|\bunigine\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))) return false;
        // Strict mode intentionally rejects mixed language titles as well.
        string[] foreign = ["Java", "Python", "Go", "PHP", "Ruby", "Kotlin", "Swift", "Scala", "Flutter", "Dart", "C++"];
        if (titleSkills.Intersect(foreign, StringComparer.OrdinalIgnoreCase).Any()) return false;
        var frontend = System.Text.RegularExpressions.Regex.IsMatch(vacancy.Title, @"front[\s-]*end|фронтенд",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        var fullstack = System.Text.RegularExpressions.Regex.IsMatch(vacancy.Title, @"full[\s-]*stack|фул[\s-]*ст[еэ]к",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        if (frontend && !fullstack && !titleSkills.Contains("Blazor")) return false;
        if (titleSkills.Contains(".NET") || titleSkills.Contains("C#")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(vacancy.Title,
            @"front[\s-]*end|фронтенд|android|ios|\bqa\b|тестировщик|аналитик|дизайнер|devops",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(vacancy.Title,
            @"разработ|программист|\bdeveloper\b|\bprogrammer\b|back[\s-]*end|б[эе]к[\s-]*енд|full[\s-]*stack|фул[\s-]*ст[еэ]к|\barchitect\b|архитектор|(?:tech|team)[\s-]*lead|техлид",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))) return false;
        var skills = SkillMatcher.Distinct(vacancy.Skills);
        return vacancy.SkillsVerified ? skills.Contains("C#") || skills.Contains(".NET")
            : skills.Contains("C#") && skills.Contains(".NET");
    }
}
