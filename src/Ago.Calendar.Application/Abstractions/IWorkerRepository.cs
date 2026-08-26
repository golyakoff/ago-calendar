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
}
