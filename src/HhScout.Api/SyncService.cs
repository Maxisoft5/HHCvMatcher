using HhScout.Core;

namespace HhScout.Api;

public sealed record SyncState(bool Running, DateTimeOffset? LastAttempt, DateTimeOffset? NextAttempt, string? Error);

public sealed class SyncService(IVacancySource source, Storage storage, TimeProvider time, ILogger<SyncService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SyncState state = new(false, null, null, null);
    public SyncState State => Volatile.Read(ref state);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return false;
        try
        {
            state = new(true, time.GetUtcNow(), null, null);
            var settings = storage.Read<SearchSettings>("search") ?? new();
            var result = await source.SearchAsync(settings, ct);
            storage.Replace(result.Items, settings, time.GetUtcNow(), result.Found, result.Truncated);
            state = state with { Error = null };
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Vacancy synchronization failed");
            state = state with { Error = ex is HhException ? ex.Message : "Не удалось обновить вакансии. Проверьте соединение; предыдущие данные сохранены." };
            return false;
        }
        finally
        {
            state = state with { Running = false, NextAttempt = time.GetUtcNow() + Interval };
            gate.Release();
        }
    }
}

public sealed class PollingWorker(SyncService sync, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var clock = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!sync.State.Running && (sync.State.NextAttempt is null || sync.State.NextAttempt <= time.GetUtcNow()))
                await sync.RunAsync(stoppingToken);
            if (!await clock.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
