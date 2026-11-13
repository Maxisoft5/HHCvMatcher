using HhScout.Core;

namespace HhScout.Tests;

public class StackPolicyTests
{
    private static Vacancy Job(string title, string[] skills, bool verified = false) =>
        new("1", title, "42", "Company", "https://hh.ru/vacancy/1", "", "", false, null, null, null, null,
            DateTimeOffset.UtcNow, "", "", skills, verified);

    [Theory]
    [InlineData("Senior Java Developer")]
    [InlineData("Python backend")]
    [InlineData("Frontend React developer")]
    [InlineData("Go developer")]
    [InlineData("Java / C# developer")]
    [InlineData("PHP developer")]
    [InlineData("QA engineer")]
    [InlineData("QA .NET engineer")]
    [InlineData("Системный аналитик .NET")]
    [InlineData("Frontend React Developer (.NET backend)")]
    [InlineData("SDET C# .NET")]
    [InlineData("Младший инженер службы поддержки ИТ")]
    [InlineData("Специалист по промышленной автоматизации (по поддержке и обслуживанию MES)")]
    [InlineData("Инженер-геотехник")]
    [InlineData("AI/LLM operations lead / Лид направления AI/LLM")]
    [InlineData("Разработчик Unigine/Unity")]
    [InlineData("Инженер-программист C#, С++")]
    public void Rejects_different_primary_stack_even_if_description_mentions_dotnet(string title)
    {
        var vacancy = Job(title, ["C#", ".NET", "SQL"]);
        Assert.False(StackPolicy.IsDotNet(vacancy));
        Assert.Empty(VacancyRanking.Rank([vacancy], new(["C#", ".NET", "SQL"]), new([]), new Dictionary<string, CompanyInfo>()));
    }

    [Theory]
    [InlineData("Senior C# developer")]
    [InlineData("Разработчик ASP.NET Core")]
    [InlineData(".NET / React fullstack")]
    public void Allows_explicit_dotnet_title(string title) => Assert.True(StackPolicy.IsDotNet(Job(title, [])));

    [Fact]
    public void Neutral_title_requires_both_dotnet_and_csharp_in_unverified_snippet()
    {
        Assert.False(StackPolicy.IsDotNet(Job("Backend developer", [".NET"])));
        Assert.True(StackPolicy.IsDotNet(Job("Backend developer", ["C#", ".NET"])));
    }
}
