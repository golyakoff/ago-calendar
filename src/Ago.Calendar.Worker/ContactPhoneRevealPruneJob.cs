using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Calendar.Worker;

/// <summary>
/// `23-12`'s own Scope: "a reveal record... its own retention." <c>contact_phone_reveals</c>
/// accumulates one row per deliberate unmasking forever unless something prunes it - this job is that
/// something, the identical "bounded-batch delete past a configurable window, on a schedule" shape
/// `ago-chat`'s own <c>ContactRevealPruneJob</c> already establishes for the account-side twin of this
/// table.
///
/// <para><b>No metrics call, unlike its chat-side twin.</b> <c>ChatMetrics.RecordRetentionPruneCycle</c>
/// has no counterpart here: this host takes no <c>Ago.Platform.Observability</c> reference
/// (<see cref="PendingBookingSweepJob"/>'s own remarks record the identical, already-stated gap for
/// that job's own health signal) - so this job logs, the only telemetry route this host currently
/// has, and does not invent a metrics call this project cannot yet publish anywhere.</para>
/// </summary>
public sealed class ContactPhoneRevealPruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<ContactPhoneRevealPruneJobOptions> options,
    ILogger<ContactPhoneRevealPruneJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Contact phone reveal prune cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task PruneAsync(CancellationToken cancellationToken)
    {
        var olderThan = clock.UtcNow - options.Value.RetentionWindow;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var totalRemoved = 0;
        for (var batch = 0; batch < options.Value.MaxBatchesPerCycle; batch++)
        {
            var removed = await ContactPhoneRevealPruneQuery.DeleteOlderThanBatchAsync(
                connection, olderThan, options.Value.BatchSize, cancellationToken);
            totalRemoved += removed;

            if (removed < options.Value.BatchSize)
            {
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation(
                "Contact phone reveal prune removed {Count} row(s) older than {OlderThan}.", totalRemoved, olderThan);
        }
    }
}
