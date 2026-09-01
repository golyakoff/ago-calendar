using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Domain;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// Keeps every published calendar's availability generated - out to each worker's own
/// <c>WorkerSchedule.HorizonDays</c> since `20-14` moved the horizon from this job's own
/// configuration to a per-worker schedule - so that `20-03`'s booking claim always has a real row to
/// compare-and-set against.
///
/// <para><b>Shape copied from <c>PartitionMaintenanceJob</c> deliberately</b> (data-model.md,
/// `2-06`): a <see cref="PeriodicTimer"/> loop that runs once immediately and then on an interval,
/// catching and continuing on anything that is not cancellation, because a transient Postgres blip
/// must not permanently kill the loop that keeps rows ahead of need. The two jobs also share the
/// property that makes that safe - both are idempotent by construction, so a missed tick costs
/// nothing and a doubled one changes nothing.</para>
///
/// <para><b>Safe with two replicas running it at the same instant.</b> Nothing here coordinates:
/// no lease, no advisory lock, no leader election. Two replicas both find the same empty day, both
/// generate the same slots, and the database drops the loser's rows through
/// <c>ON CONFLICT DO NOTHING</c> against the no-overlap constraint - no exception, no partial day,
/// no duplicate. Coordination would have been the other design and it is strictly worse here: it
/// adds a component that can fail to a job whose whole failure mode is already handled by a
/// constraint that cannot (concurrency.md, adr/0053).</para>
///
/// <para><b>Not directly covered by a test, and the reason is a naming collision worth recording
/// before `20-04` rediscovers it.</b> A test project that references this host cannot name the
/// <c>Worker</c> *aggregate* at all: from inside any <c>Ago.Calendar.*</c> namespace the simple name
/// <c>Worker</c> resolves to the enclosing namespace <c>Ago.Calendar.Worker</c> before a
/// <c>using</c>-imported type is considered (CS0118 - the same trap
/// <see cref="Domain.BookingCalendar"/> was renamed to escape). So this loop is covered only through
/// its parts: <c>MaterializeAvailabilityHandler</c> and <c>ITenantRepository.ListIdsAsync</c> both
/// have their own integration tests, and what is left untested here is the walk itself. The two ways
/// out - renaming the <c>Worker</c> aggregate, or giving this project a <c>RootNamespace</c> that is
/// not <c>Ago.Calendar.Worker</c> - both touch decisions made in `20-00`/`20-01`, so neither is taken
/// on the way past.</para>
///
/// <para><b>Scope per calendar, not per tick.</b> The handler and its repositories are scoped
/// (a <c>DbContext</c> is not thread-safe and a unit of work is not a day long), so each calendar
/// gets its own scope. That also contains the blast radius: a calendar whose zone the host cannot
/// resolve fails alone and the rest of the tenant still gets its slots.</para>
/// </summary>
public sealed class AvailabilityMaterializationJob(
    IServiceScopeFactory scopeFactory,
    IOptions<AvailabilityMaterializationJobOptions> options,
    ILogger<AvailabilityMaterializationJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await MaterializeEveryCalendarAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Availability materialisation cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task MaterializeEveryCalendarAsync(CancellationToken cancellationToken)
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
                await MaterializeTenantAsync(tenantId, cancellationToken);
            }

            after = tenantIds[^1];
        }
    }

    private async Task MaterializeTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<CalendarId> calendarIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var calendars = scope.ServiceProvider.GetRequiredService<IBookingCalendarRepository>();
            var published = await calendars.ListPublishedAsync(tenantId, cancellationToken);
            calendarIds = published.Select(calendar => calendar.Id).ToList();
        }

        foreach (var calendarId in calendarIds)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<MaterializeAvailabilityHandler>();

            try
            {
                var result = await handler.HandleAsync(new MaterializeAvailability(calendarId), cancellationToken);

                if (result.SlotsInserted > 0)
                {
                    logger.LogInformation(
                        "Materialised {SlotsInserted} slot(s) for calendar {CalendarId} across {DaysFilled} day(s); {DaysSkipped} day(s) already had events.",
                        result.SlotsInserted,
                        calendarId.Value,
                        result.DaysConsidered - result.DaysSkipped,
                        result.DaysSkipped);
                }
            }
            catch (UnknownCalendarTimeZoneException ex)
            {
                // A deployment fault with an owner, not a data problem: the calendar's zone id was
                // well-formed when it was stored and this host cannot resolve it. Logged per
                // calendar and skipped, so one bad zone does not stop every other tenant's slots
                // from being generated.
                logger.LogError(
                    ex, "Calendar {CalendarId} has a time zone this host cannot resolve; skipping it.", calendarId.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Materialising calendar {CalendarId} failed; skipping it this cycle.", calendarId.Value);
            }
        }
    }
}
