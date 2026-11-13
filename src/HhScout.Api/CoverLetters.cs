using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HhScout.Core;

namespace HhScout.Api;

public sealed record VacancyDetails(string VacancyId, string Description, DateTimeOffset FetchedAt)
{
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Description)));
}
public sealed record LetterInput(Vacancy Vacancy, VacancyDetails Details, ResumeProfile Profile);
public sealed record GeneratedLetter(string Text, string Model);
public sealed record CoverLetter(string Id, string VacancyId, string Text, string OriginalText, string Model,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, LetterInput Context, string ProfileHash, string PromptVersion = "1");
public sealed record LetterView(string Id, string Text, string Model, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool ContextChanged);
public sealed record LetterContext(Vacancy Vacancy, VacancyDetails? Details, LetterView[] Letters);
public sealed record GenerateLetterRequest(string Model = "auto", bool Regenerate = false);
public sealed record EditLetterRequest(string Text);
public interface IVacancyDetailsSource { Task<VacancyDetails> ReadDetailsAsync(string id, CancellationToken ct); }
public interface ICoverLetterGenerator { Task<GeneratedLetter> GenerateAsync(LetterInput input, string model, CancellationToken ct); }
public sealed class CoverLetterException(string message, int statusCode = 400) : Exception(message)
{ public int StatusCode { get; } = statusCode; }

public sealed class CoverLetterService(Storage db, IVacancyDetailsSource source, ICoverLetterGenerator generator, TimeProvider time)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static string ProfileHash(ResumeProfile profile) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile)));
    private ResumeProfile Profile => db.Read<ResumeProfile>("profile") ?? new([]);
    private Vacancy Job(string id) => db.ArchivedVacancy(id) ?? throw new CoverLetterException("Вакансия не найдена в локальной базе.", 404);
    private LetterView View(CoverLetter letter) => new(letter.Id, letter.Text, letter.Model, letter.CreatedAt, letter.UpdatedAt,
        letter.ProfileHash != ProfileHash(Profile) || (db.Details(letter.VacancyId)?.Hash is string hash && hash != letter.Context.Details.Hash));

    // This path is deliberately database-only: opening the dialog must never run a model or browser.
    public LetterContext Context(string id) => new(Job(id), db.Details(id), db.Letters(id).Select(View).ToArray());

    public async Task<VacancyDetails> ReadDetailsAsync(string id, CancellationToken ct)
    {
        _ = Job(id);
        var details = await source.ReadDetailsAsync(id, ct);
        if (details.VacancyId != id || string.IsNullOrWhiteSpace(details.Description) || details.Description.Length > 200_000)
            throw new CoverLetterException("Не удалось получить полное описание вакансии.");
        db.SaveDetails(details);
        return details;
    }

    // Only the explicit POST endpoint calls this method. No worker, GET or startup hook references it.
    public async Task<LetterView> GenerateAsync(string id, GenerateLetterRequest request, CancellationToken ct)
    {
        if (request.Model is not ("auto" or "gpt-6.1-sol" or "gpt-6.1-terra")) throw new CoverLetterException("Выберите Sol, Terra или автоматический выбор.");
        if (!await gate.WaitAsync(0, ct)) throw new CoverLetterException("Письмо уже создаётся. Дождитесь завершения.", 409);
        try
        {
            var job = Job(id);
            var existing = db.Letters(id).FirstOrDefault();
            if (!request.Regenerate && existing is not null) return View(existing);
            var profile = Profile;
            if (string.IsNullOrWhiteSpace(profile.Text) && profile.Skills.Length == 0)
                throw new CoverLetterException("Сначала сохраните навыки или резюме в разделе «Мой профиль». Модель использует только эти сведения.");
            var details = db.Details(id) ?? await ReadDetailsAsync(id, ct);
            var input = new LetterInput(job, details, profile);
            var generated = await generator.GenerateAsync(input, request.Model, ct);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(generated.Text) || generated.Text.Length > 12_000) throw new CoverLetterException("Модель вернула пустое или слишком длинное письмо.", 502);
            var now = time.GetUtcNow();
            var letter = new CoverLetter(Guid.NewGuid().ToString("N"), id, generated.Text.Trim(), generated.Text.Trim(), generated.Model, now, now, input, ProfileHash(profile));
            db.SaveLetter(letter);
            return View(letter);
        }
        finally { gate.Release(); }
    }

    public LetterView Edit(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 12_000) throw new CoverLetterException("Введите текст письма до 12 000 символов.");
        var letter = db.Letter(id) ?? throw new CoverLetterException("Письмо не найдено.", 404);
        letter = letter with { Text = text.Trim(), UpdatedAt = time.GetUtcNow() };
        db.SaveLetter(letter);
        return View(letter);
    }
}
