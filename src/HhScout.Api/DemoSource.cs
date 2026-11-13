using HhScout.Core;

namespace HhScout.Api;

// Synthetic fixtures for an explicitly selected demo mode. No hh.ru requests or real employers.
public sealed class DemoSource(TimeProvider time) : IVacancySource
{
    public Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        Vacancy Create(string id, string title, string employer, string[] skills, bool remote, decimal? from, decimal? to, string requirement, int hours) =>
            new(id, title, id, $"Пример: {employer}", $"https://hh.ru/search/vacancy?text={Uri.EscapeDataString(title)}", "Москва", "От 3 до 6 лет", remote,
                from, to, "RUR", false, now.AddHours(-hours), requirement, "Демонстрационная вакансия для проверки интерфейса. Не является предложением о работе.", skills, true);
        Vacancy[] jobs =
        [
            Create("10001", "Senior C# / .NET Developer", "Северная платформа", ["C#", ".NET", "PostgreSQL", "Docker", "Redis"], true, 280000, 380000,
                "Разработка backend на C# и .NET. Опыт работы с PostgreSQL, Docker и Redis. Все данные в этой карточке демонстрационные.", 1),
            Create("10002", "Backend-разработчик .NET", "Контур решений", ["C#", ".NET", "ASP.NET", "SQL", "RabbitMQ"], true, 230000, 320000,
                "Проектирование API на ASP.NET, интеграция сервисов, работа с SQL и RabbitMQ. Демонстрационный пример.", 2),
            Create("10003", "Fullstack .NET / React Developer", "Студия продуктов", ["C#", ".NET", "React", "TypeScript", "Docker"], true, 250000, 350000,
                "Создание продуктовых сервисов на .NET и React. TypeScript на frontend, Docker для окружений. Демонстрационный пример.", 3),
            Create("10004", "Разработчик C# / WPF", "Инженерные системы", ["C#", ".NET", "WPF", "SQL"], false, 180000, 260000,
                "Разработка desktop-приложений на C#, .NET и WPF. Работа в офисе. Демонстрационный пример.", 5),
            Create("10005", "Senior Java Developer", "Другой стек", ["Java", "C#", ".NET", "PostgreSQL"], true, 300000, 400000,
                "Основная разработка на Java; интеграции с .NET. Строгий фильтр должен скрыть эту демонстрационную вакансию.", 1),
            Create("10006", "Frontend React Developer", "Другой frontend", ["React", "TypeScript", ".NET"], true, 200000, 300000,
                "Основная работа на React. Backend команды использует .NET. Строгий фильтр скрывает этот демонстрационный пример.", 2)
        ];
        var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = jobs.Where(v => words.All(w => $"{v.Title} {v.Requirement}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Where(v => !search.RemoteOnly || v.Remote).ToArray();
        return Task.FromResult(new SearchResult(selected, selected.Length, false));
    }
}
