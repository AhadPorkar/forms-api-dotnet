using Forms.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Forms.Api.Infrastructure;

/// <summary>Deletes autosave drafts that have not been touched within the retention period.</summary>
public sealed partial class DraftCleanupService(
    IServiceScopeFactory scopes, IOptions<FormsOptions> options, TimeProvider clock, ILogger<DraftCleanupService> logger)
    : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FormsDbContext>();
        var now = clock.GetUtcNow();
        return await db.SubmissionDrafts.Where(d => d.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.DraftCleanupInterval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var deleted = await RunOnceAsync(stoppingToken);
                if (deleted > 0)
                {
                    LogDeleted(deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired autosave drafts")]
    private partial void LogDeleted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up expired drafts failed; will retry on the next tick")]
    private partial void LogFailed(Exception exception);
}