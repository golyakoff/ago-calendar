namespace Ago.Calendar.Domain;

/// <summary>
/// `22-07`: which workers become the excess when a granted quota drops below the tenant's current
/// active headcount - the one part the backlog item this ships against explicitly refused to leave
/// unstated. Picking by row order would be choosing for the tenant; this is a rule instead, stated in
/// writing so a tenant can predict and verify it without reading a line of code.
///
/// <para><b>The rule: keep the workers who have been active longest.</b> Deactivate the most
/// <i>recently created</i> active workers first, until the active count matches the new quota. A
/// tenant who sorts their own worker list by "added on" sees exactly which ones a downgrade would
/// cut - the newest entries, always. Nothing a shop typed is destroyed (<see cref="Worker.Deactivate"/>,
/// not a delete), and the rule does not depend on which calendar or services a worker happens to be
/// tied to, so it stays predictable regardless of how the tenant has organised the rest of their
/// configuration.</para>
///
/// <para><b>Ties break on <see cref="WorkerId"/>, descending.</b> <see cref="Worker.CreatedAt"/> comes
/// from <c>IClock</c>, whose granularity is not guaranteed finer than a second
/// (`docs/conventions/date-and-time.md`), so two workers created in the same tick are a real
/// possibility, not a hypothetical. <see cref="WorkerId"/> wraps a UUIDv7 (`IIdGenerator`), which is
/// itself time-ordered - so breaking the tie on it is a finer-grained instance of the identical "newest
/// first" rule, not a second, arbitrary one.</para>
///
/// <para>A pure function over an already-loaded list rather than a query, so the exact rule can be unit
/// tested with no database at all - <see cref="Infrastructure.WorkerQuotaGrantStore"/> (the real
/// Postgres adapter, `Ago.Calendar.Infrastructure.Postgres`) is what applies its result inside the
/// locked transaction rule 8 requires; this type only ever decides which workers, never writes
/// anything.</para>
/// </summary>
public static class WorkerQuotaPolicy
{
    /// <param name="activeWorkers">Every currently-active worker of one tenant. Order does not
    /// matter on the way in - this method imposes its own.</param>
    /// <param name="quota">The tenant's newly granted worker quota.</param>
    /// <returns>The workers to deactivate, newest-created first, or an empty list when
    /// <paramref name="activeWorkers"/> already fits within <paramref name="quota"/>.</returns>
    public static IReadOnlyList<Worker> SelectWorkersToDeactivate(IReadOnlyList<Worker> activeWorkers, int quota)
    {
        ArgumentNullException.ThrowIfNull(activeWorkers);
        ArgumentOutOfRangeException.ThrowIfNegative(quota);

        var excess = activeWorkers.Count - quota;
        if (excess <= 0)
        {
            return [];
        }

        return [.. activeWorkers
            .OrderByDescending(worker => worker.CreatedAt)
            .ThenByDescending(worker => worker.Id.Value)
            .Take(excess)];
    }
}
