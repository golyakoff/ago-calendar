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

    Task AddAsync(Worker worker, CancellationToken cancellationToken);

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
