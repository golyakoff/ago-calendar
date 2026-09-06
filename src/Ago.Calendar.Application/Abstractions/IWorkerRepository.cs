using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="Worker"/>, loaded with its calendar memberships and service
/// offerings - the aggregate is not useful without them, since every invariant it enforces
/// (<c>WorksIn</c>, <c>Offers</c>, the v1 one-calendar limit) is a question about those collections.
/// A port that returned a worker with its joins lazily unloaded would let a caller silently get the
/// wrong answer from <c>Offers</c>, which is worse than not offering the method.
/// </summary>
public interface IWorkerRepository
{
    Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken cancellationToken);

    /// <summary>The active workers on one calendar - who `20-02` materialises slots for, and who the
    /// public booking page offers a customer.</summary>
    Task<IReadOnlyList<Worker>> ListActiveForCalendarAsync(CalendarId calendarId, CancellationToken cancellationToken);

    /// <summary>Every worker of one tenant, inactive ones included - the configuration console's list
    /// (`20-06`). An inactive worker keeps their history and has to stay visible to whoever might
    /// reactivate them; hiding them would make deactivation look like deletion, which
    /// <see cref="Worker.IsActive"/>'s own remarks rule out.</summary>
    Task<IReadOnlyList<Worker>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// `22-07`/rule 8: the atomic compare-and-set this product's own worker-quota enforcement rests
    /// on - <c>docs/architecture/data-model.md</c>'s own contrast between <c>operators.active_chats</c>
    /// (a denormalized counter, for a high-frequency contended path) and
    /// <c>OperatorInviteRedemptionRepository</c>'s seat-limit check (a <c>SELECT ... FOR UPDATE</c> row
    /// lock plus a real <c>COUNT(*)</c>, for a rare, low-contention one). Worker creation is the second
    /// profile - at most a handful of calls ever per tenant - so this locks the tenant row directly
    /// (<c>SELECT worker_quota FROM tenants WHERE id = @tenantId FOR UPDATE</c>) and counts real
    /// <c>workers</c> rows inside that lock, rather than adding a denormalized counter that would need
    /// its own symmetric decrement on every deactivation.
    ///
    /// <para>This replaces what used to be a plain <c>AddAsync</c> - there is no unconditional insert
    /// left in this port, because there is no caller (<see cref="Worker"/> creation always happens
    /// under a tenant's granted quota) for whom the condition would be wrong to check.</para>
    /// </summary>
    /// <returns><see langword="true"/> if the worker was added; <see langword="false"/> if the
    /// tenant's active worker count already equals or exceeds its granted
    /// <see cref="Tenant.WorkerQuota"/> - nothing is written on this path, and the caller's own
    /// transaction (if it has one) is left uncommitted by this call alone.</returns>
    Task<bool> TryAddWithinQuotaAsync(Worker worker, CancellationToken cancellationToken);

    /// <summary>
    /// `22-23`: the reactivation half of the same invariant <see cref="TryAddWithinQuotaAsync"/>
    /// enforces for creation. A downgrade deactivates the excess rather than deleting it
    /// (`adr/0125`), which is exactly what makes <c>PUT /workers/{id}</c> a second door onto the same
    /// quota - a caller can flip <see cref="Worker.IsActive"/> back on for a worker the quota already
    /// has no room for. This shares <see cref="TryAddWithinQuotaAsync"/>'s own lock-and-count shape
    /// (see <c>WorkerRepository.TryReactivateWithinQuotaAsync</c> for the identical
    /// <c>SELECT ... FOR UPDATE</c> plus <c>COUNT(*)</c> statement) rather than a second, differently
    /// shaped check for the same fact - a second shape is how the two drift.
    ///
    /// <para>The caller applies <see cref="Worker.Reactivate"/> (and any other pending change, such
    /// as a rename from the same request) to <paramref name="worker"/> <b>before</b> calling this -
    /// the same order <c>CreateWorkerHandler</c> already uses against <see cref="TryAddWithinQuotaAsync"/>.
    /// The count this method takes excludes <paramref name="worker"/> itself by id, so it reads the
    /// same "how many workers other than this one are active" answer regardless of whether the caller
    /// already flipped the in-memory flag - the decision does not depend on statement ordering.</para>
    /// </summary>
    /// <returns><see langword="true"/> if the reactivation (and any other pending change on
    /// <paramref name="worker"/>) was persisted; <see langword="false"/> if the tenant's active count,
    /// not counting this worker, already equals or exceeds its granted <see cref="Tenant.WorkerQuota"/>
    /// - nothing is written on this path, including any unrelated field the same request changed.</returns>
    Task<bool> TryReactivateWithinQuotaAsync(Worker worker, CancellationToken cancellationToken);

    Task SaveAsync(Worker worker, CancellationToken cancellationToken);

    /// <summary>
    /// `20-13`: deletes a worker who has never been booked, and refuses - deleting nothing - if he
    /// has. "Never booked" means no <see cref="Event"/> of his is or ever was in
    /// <see cref="EventStatus.PendingConfirmation"/>, <see cref="EventStatus.Booked"/> or
    /// <see cref="EventStatus.NoShow"/>; a worker with only <see cref="EventStatus.Available"/> rows
    /// (or none) is fair game, and those rows go with him - along with his working-hours rules and
    /// his one calendar/service join - through the same <c>ON DELETE CASCADE</c> the schema already
    /// declares for every one of those tables.
    ///
    /// <para><b>The check and the delete are one call, not two.</b> A caller that read "is this
    /// worker booked?" through a separate query and deleted afterward would leave a gap a booking
    /// could land in between the two - exactly the case this port exists to close. The adapter
    /// implements the whole thing as one guarded <c>DELETE</c> statement so there is no gap to land
    /// in: see <c>WorkerRepository.DeleteIfNeverBookedAsync</c> for the exact statement.</para>
    /// </summary>
    /// <returns><see langword="true"/> if the worker existed and was deleted; <see langword="false"/>
    /// if he does not exist, belongs to another tenant, or has booking history and was therefore left
    /// untouched. The caller distinguishes those cases with its own follow-up read - see
    /// <c>DeleteWorkerHandler</c>.</returns>
    Task<bool> DeleteIfNeverBookedAsync(WorkerId id, TenantId tenantId, CancellationToken cancellationToken);
}
