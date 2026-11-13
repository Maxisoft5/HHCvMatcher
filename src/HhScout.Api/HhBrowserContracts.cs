using HhScout.Core;

namespace HhScout.Api;

public sealed record HhAuthState(string Stage = "disconnected", bool Busy = false, string? EmailHint = null,
    string? Message = null, DateTimeOffset? RetryAt = null, bool CanRestore = false);
public sealed record LoginObservation(string Stage, string? Message = null);
public sealed record EmailLoginRequest(string Email);
public sealed record CodeLoginRequest(string Code);
public sealed record BrowserField(string? Name, string? Type, string? Qa);
public sealed record BrowserDiagnostic(string Path, BrowserField[] Fields, string[] Buttons, string[] Headings, string Step = "");

public interface IHhBrowserDriver : IAsyncDisposable
{
    bool HasSavedProfile { get; }
    Task<LoginObservation> RequestCodeAsync(string email, CancellationToken ct);
    Task<LoginObservation> SubmitCodeAsync(string code, CancellationToken ct);
    Task<LoginObservation> CheckAsync(CancellationToken ct);
    Task ShowAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct);
    Task<VacancyDetails> ReadDetailsAsync(string id, CancellationToken ct) => throw new HhException("Чтение описания недоступно.");
    Task<BrowserDiagnostic> DiagnosticAsync(CancellationToken ct) => throw new HhException("Диагностика браузера недоступна.");
}

public interface IHhAccount
{
    HhAuthState State { get; }
    Task<HhAuthState> RequestCodeAsync(string email, CancellationToken ct);
    Task<HhAuthState> SubmitCodeAsync(string code, CancellationToken ct);
    Task<HhAuthState> CheckAsync(CancellationToken ct);
    Task ShowAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<BrowserDiagnostic> DiagnosticAsync(CancellationToken ct);
}

public sealed class HhAccount(IHhBrowserDriver driver, TimeProvider time) : IHhAccount, IVacancySource, IVacancyDetailsSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HhAuthState state = new();
    private bool restored;
    public HhAuthState State => Volatile.Read(ref state) with { CanRestore = driver.HasSavedProfile };

    public async Task<HhAuthState> RequestCodeAsync(string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Any(char.IsControl) ||
            !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email.Trim())
            throw new HhException("Введите корректный адрес почты.");
        return await ExecuteAsync(async () =>
        {
            if (state.Stage == "authenticated") throw new HhException("Сессия уже подключена. Для другой почты сначала отключите её.");
            if (state.RetryAt > time.GetUtcNow()) throw new HhException("Повторный запрос кода доступен через минуту после предыдущего.");
            var hint = email[..1] + "***@" + address.Host;
            // Set the cooldown before the external click: a timeout may happen after hh.ru sends the email.
            state = state with { EmailHint = hint, RetryAt = time.GetUtcNow().AddMinutes(1) };
            var observation = await driver.RequestCodeAsync(email.Trim(), ct);
            Apply(observation);
            return State;
        }, ct);
    }

    public Task<HhAuthState> SubmitCodeAsync(string code, CancellationToken ct)
    {
        if (code is null || code.Length is < 4 or > 8 || !code.All(char.IsAsciiDigit))
            throw new HhException("Введите код из письма: от 4 до 8 цифр.");
        return ExecuteAsync(async () =>
        {
            if (state.Stage != "codeRequired") throw new HhException("Сначала запросите код или проверьте состояние входа.");
            Apply(await driver.SubmitCodeAsync(code, ct));
            return State;
        }, ct);
    }

    public Task<HhAuthState> CheckAsync(CancellationToken ct) => ExecuteAsync(async () =>
    {
        Apply(await driver.CheckAsync(ct));
        restored = true;
        return State;
    }, ct);

    public async Task ShowAsync(CancellationToken ct) => await ExecuteAsync(async () =>
    {
        await driver.ShowAsync(ct);
        return true;
    }, ct);

    public Task<BrowserDiagnostic> DiagnosticAsync(CancellationToken ct) => ExecuteAsync(() => driver.DiagnosticAsync(ct), ct);

    public async Task DisconnectAsync(CancellationToken ct) => await ExecuteAsync(async () =>
    {
        await driver.DisconnectAsync(ct);
        state = new();
        restored = true;
        return true;
    }, ct);

    public Task<SearchResult> SearchAsync(SearchSettings search, CancellationToken ct) => ExecuteAsync(async () =>
    {
        if (!restored && state.Stage == "disconnected" && driver.HasSavedProfile)
        {
            restored = true;
            Apply(await driver.CheckAsync(ct));
        }
        if (state.Stage != "authenticated") throw new HhException("Подключите hh.ru: введите почту и код из письма в блоке входа.");
        try { return await driver.SearchAsync(search, ct); }
        catch (HhSessionExpiredException ex) { state = state with { Stage = "disconnected", Message = ex.Message }; throw new HhException(ex.Message); }
        catch (HhManualActionException ex) { state = state with { Stage = "manualAction", Message = ex.Message }; throw new HhException(ex.Message); }
    }, ct);

    private void Apply(LoginObservation observation) => state = state with
    {
        Stage = observation.Stage,
        Message = observation.Message,
        RetryAt = observation.Stage == "authenticated" ? null : state.RetryAt
    };

    public Task<VacancyDetails> ReadDetailsAsync(string id, CancellationToken ct) => ExecuteAsync(async () =>
    {
        if (string.IsNullOrEmpty(id) || id.Length > 20 || !id.All(char.IsAsciiDigit)) throw new HhException("Некорректный номер вакансии.");
        if (!restored && state.Stage == "disconnected" && driver.HasSavedProfile)
        { restored = true; Apply(await driver.CheckAsync(ct)); }
        if (state.Stage != "authenticated") throw new HhException("Подключите hh.ru для чтения полного описания.");
        return await driver.ReadDetailsAsync(id, ct);
    }, ct);

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new HhException("Браузер занят. Дождитесь завершения текущего действия.");
        state = state with { Busy = true };
        try { return await action(); }
        catch (HhManualActionException ex) { Apply(new("manualAction", ex.Message)); throw new HhException(ex.Message); }
        catch (HhSessionExpiredException ex) { Apply(new("disconnected", ex.Message)); throw new HhException(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (System.TimeoutException) { Apply(new("manualAction", "hh.ru не завершил ожидаемый переход. Откройте браузер и проверьте текущий шаг, затем нажмите «Проверить вход».")); throw new HhException(state.Message!); }
        catch (HhException ex) { state = state with { Message = ex.Message }; throw; }
        catch (Exception) { state = state with { Message = "Не удалось выполнить действие в браузере. Проверьте окно hh.ru и попробуйте проверить состояние входа." }; throw new HhException(state.Message); }
        finally { state = state with { Busy = false }; gate.Release(); }
    }
}

public sealed class HhManualActionException(string message) : Exception(message);
public sealed class HhSessionExpiredException(string message) : Exception(message);
