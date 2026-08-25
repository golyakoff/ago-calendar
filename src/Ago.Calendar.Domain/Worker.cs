namespace Ago.Calendar.Domain;

/// <summary>
/// The person a customer books: a barber, a stylist, a doctor. Not an <see cref="Operator"/> - a
/// worker may have no login at all (an administrator keeps their card and their schedule), and an
/// operator may perform no services. In a one-person business the same human is both; the model has
/// to allow that without asserting it.
///
/// <para><b>Belongs to exactly one tenant, structurally.</b> <see cref="TenantId"/> is a single
/// get-only field, so "two tenants" has no representation to begin with - the strongest form the
/// rule can take. What is left to enforce is the way the rule is actually broken in practice, which
/// is sideways: attaching this worker to another tenant's calendar or service. Both
/// <see cref="JoinCalendar"/> and <see cref="Offer"/> therefore take the whole related aggregate
/// rather than its id, because an id cannot answer "whose is this?" and passing one would push the
/// check out to every caller.</para>
///
/// <para><b>This aggregate owns both joins.</b> <c>calendar_workers</c> and <c>worker_services</c>
/// hang off <see cref="Worker"/> rather than off <see cref="BookingCalendar"/>/<see cref="Service"/>
/// because the invariants they carry are statements about a worker - one calendar in v1, and the
/// services this worker performs. An aggregate that owns a relationship is the one that can enforce
/// a rule about it in a single transaction; splitting them across two aggregates would make the v1
/// one-calendar rule a cross-aggregate check, which is a race, not an invariant.</para>
/// </summary>
public sealed class Worker
{
    private readonly List<CalendarMembership> _calendars = [];
    private readonly List<ServiceOffering> _services = [];

    public WorkerId Id { get; }

    public TenantId TenantId { get; }

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>An inactive worker keeps their history and their materialised events; `20-02` simply
    /// stops extending their horizon. Deleting a worker who has bookings is not a thing this product
    /// should offer.</summary>
    public bool IsActive { get; private set; }

    public IReadOnlyList<CalendarMembership> Calendars => _calendars;

    public IReadOnlyList<ServiceOffering> Services => _services;

    private Worker(WorkerId id, TenantId tenantId, string displayName)
    {
        Id = id;
        TenantId = tenantId;
        DisplayName = displayName;
        IsActive = true;
    }

    // EF Core materialization only - never called by domain code.
    private Worker()
    {
    }

    public static Worker Create(WorkerId id, TenantId tenantId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new Worker(id, tenantId, displayName.Trim());
    }

    /// <summary>
    /// v1 admits exactly one calendar per worker - see <see cref="WorkerCalendarLimitException"/>
    /// for why that is a deletable check rather than a shape decision. Re-joining the calendar this
    /// worker is already in is a no-op, so a retried provisioning step does not fail.
    /// </summary>
    public void JoinCalendar(BookingCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (calendar.TenantId != TenantId)
        {
            throw new TenantMismatchException(
                $"Calendar {calendar.Id.Value} belongs to tenant {calendar.TenantId.Value}, " +
                $"worker {Id.Value} to {TenantId.Value}.");
        }

        if (_calendars.Exists(membership => membership.CalendarId == calendar.Id))
        {
            return;
        }

        if (_calendars.Count > 0)
        {
            throw new WorkerCalendarLimitException(
                $"Worker {Id.Value} already participates in calendar {_calendars[0].CalendarId.Value}; " +
                "v1 allows exactly one calendar per worker.");
        }

        _calendars.Add(new CalendarMembership(Id, calendar.Id));
    }

    public void Offer(Service service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.TenantId != TenantId)
        {
            throw new TenantMismatchException(
                $"Service {service.Id.Value} belongs to tenant {service.TenantId.Value}, " +
                $"worker {Id.Value} to {TenantId.Value}.");
        }

        if (_services.Exists(offering => offering.ServiceId == service.Id))
        {
            return;
        }

        _services.Add(new ServiceOffering(Id, service.Id));
    }

    public bool WorksIn(CalendarId calendarId) =>
        _calendars.Exists(membership => membership.CalendarId == calendarId);

    public bool Offers(ServiceId serviceId) =>
        _services.Exists(offering => offering.ServiceId == serviceId);

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public void Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
    }
}
