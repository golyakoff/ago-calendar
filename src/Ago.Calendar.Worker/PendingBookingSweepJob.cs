using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// The second half of `20-03`'s two-step booking mechanic: a booking whose veto window closed with
/// nobody acting becomes <see cref="EventStatus.Booked"/>.
///
/// <para><b>The same architectural shape as two mechanisms already shipped, not a new one.</b> AGO
/// Chat's <c>ConversationAssignmentJob</c> (`4-02`) and <c>OutboxDispatcher</c> (`2-04`) are both a
/// <see cref="PeriodicTimer"/> loop around a <c>SELECT ... FOR UPDATE SKIP LOCKED</c> batch claim
/// inside one transaction per batch, catching and continuing on anything that is not cancellation.
/// Every property that makes those correct makes this correct: <c>SKIP LOCKED</c> means two replicas
/// split the work instead of racing for one row, a row somebody else holds is simply picked up next
/// tick with no un-claim step, and the batch bound keeps one transaction short. The reasoning lives
/// in full on <see cref="IExpiredBookingConfirmer"/>; this class is the loop around it, and it is a
/// loop this repository already has two of.</para>
///
/// <para><b>What is genuinely unusual here, and it is not the mechanism.</b> In every other sweep in
/// this codebase, doing nothing means nothing happens. Here <b>doing nothing confirms the
/// booking</b> - so a sweep that quietly stops running does not fail loudly, it converts every
/// pending booking into one that never confirms, while the customer has already been told they are
/// booked. See <see cref="ReportHealthAsync"/> for what makes that visible.</para>
/// </summary>
public sealed class PendingBookingSweepJob(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<PendingBookingSweepJobOptions> options,
    ILogger<PendingBookingSweepJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await SweepEveryTenantAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres
                // blip must not permanently kill the loop. Logged at Error rather than Warning
                // precisely because of this job's inverted default: a dead sweep is a growing pile of
                // bookings that will never confirm, not a deferred cleanup.
                logger.LogError(ex, "Pending-booking sweep cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task SweepEveryTenantAsync(CancellationToken cancellationToken)
    {
        TenantId? after = null;

        while (true)
        {
            IReadOnlyList<TenantId> tenantIds;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                tenantIds = await tenants.ListIdsAsync(after, options.Value.TenantPageSize, cancellationToken);
            }

            if (tenantIds.Count == 0)
            {
                return;
            }

            foreach (var tenantId in tenantIds)
            {
                await SweepTenantAsync(tenantId, cancellationToken);
            }

            after = tenantIds[^1];
        }
    }

    private async Task SweepTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        // One scope per tenant, so a scoped DbContext is never shared across two tenants'
        // transactions and one tenant's failure cannot poison another's change tracker - the same
        // containment AvailabilityMaterializationJob uses per calendar.
        await using var scope = scopeFactory.CreateAsyncScope();
        var confirmer = scope.ServiceProvider.GetRequiredService<IExpiredBookingConfirmer>();

        // Read once per tenant, not once per batch: every row claimed inside this tick is compared
        // against the same instant, so a booking cannot be confirmed by one batch and judged
        // not-yet-due by the next one microseconds later. This is the single value in this product
        // where a clock decides an outcome (CLAUDE.md rule 11), which is exactly why it is read here,
        // passed down as a parameter, and never read again further in.
        var now = clock.UtcNow;

        try
        {
            var confirmed = 0;
            int batch;
            do
            {
                batch = await confirmer.ConfirmExpiredAsync(
                    tenantId, now, options.Value.BatchSize, cancellationToken);
                confirmed += batch;
            }

            // Drains a backlog within one tick rather than one batch per interval - a shop coming
            // back from an outage should not wait BatchSize-per-30-seconds for its bookings to
            // settle. Terminates because every iteration either confirms rows (which stop matching
            // the claim's predicate) or returns zero.
            while (batch == options.Value.BatchSize);

            if (confirmed > 0)
            {
                logger.LogInformation(
                    "Confirmed {Confirmed} booking(s) for tenant {TenantId} whose veto window had closed.",
                    confirmed, tenantId.Value);
            }

            await ReportHealthAsync(confirmer, tenantId, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per tenant, so one tenant's bad data or lock contention does not stop every other
            // tenant's bookings from confirming.
            logger.LogError(ex, "Sweeping tenant {TenantId} failed; skipping it this cycle.", tenantId.Value);
        }
    }

    /// <summary>
    /// <b>The one thing that keeps this job's failure from being silent.</b>
    ///
    /// <para>After a tenant's sweep, count what is still past its deadline and still pending. On a
    /// healthy tick that is zero: everything overdue was just claimed, or is held by another replica's
    /// open transaction and will be gone within a tick. A number that is not zero, tick after tick,
    /// means bookings are sitting unconfirmed while the customers who made them have already been
    /// told otherwise.</para>
    ///
    /// <para><b>Why an outcome count and not a liveness signal.</b> A heartbeat would say the loop is
    /// running; it would say nothing about a loop that runs and throws on every tenant, or a claim
    /// whose predicate stopped matching after a schema change. This counts the thing that actually
    /// matters, so it climbs under all three failures - and it is read *outside* the claim's
    /// transaction, because it is a measurement and nothing branches on it.</para>
    ///
    /// <para><b>Where it surfaces today, honestly.</b> A <c>Warning</c> log line per tenant per tick,
    /// which reaches the node's log pipeline and is the only telemetry route
    /// <c>Ago.Calendar.Worker</c> currently has - it deliberately takes no
    /// <c>Ago.Platform.Observability</c> reference (`7-09`/`20-00`), so there is no meter to publish a
    /// gauge to and no scrape endpoint to publish it from. That is a real gap and it is stated rather
    /// than papered over: **`15-03`'s alerting cannot fire on this until this host has a metrics
    /// pipeline**, and until then the signal is a log line plus the overdue flag the operator queue
    /// already shows (<c>PendingBookingRow.IsOverdue</c>), which puts it in front of the one person
    /// who can act on it.</para>
    /// </summary>
    private async Task ReportHealthAsync(
        IExpiredBookingConfirmer confirmer, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var overdue = await confirmer.CountOverdueAsync(tenantId, now, cancellationToken);
        if (overdue == 0)
        {
            return;
        }

        logger.LogWarning(
            "{Overdue} booking(s) for tenant {TenantId} are past their confirmation deadline and still pending "
            + "after a sweep. Doing nothing confirms a booking, so this number should be zero - a number that "
            + "stays above zero means customers have been told they are booked for visits this system has not "
            + "confirmed.",
            overdue, tenantId.Value);
    }
}
