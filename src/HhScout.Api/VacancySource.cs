using HhScout.Core;

namespace HhScout.Api;

public interface IVacancySource
{
    Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct);
}

public sealed record SearchResult(Vacancy[] Items, int Found, bool Truncated);
public sealed class HhException(string message) : Exception(message);
