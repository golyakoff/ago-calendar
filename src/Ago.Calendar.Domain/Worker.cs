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
///
/// <para><b>`20-13`: the name is four fields, not one.</b> <see cref="LastName"/> and
/// <see cref="FirstName"/> are required, <see cref="MiddleName"/> is optional, and
/// <see cref="DisplayName"/> is what the booking surface and every list actually render. Two shapes
/// were weighed for the last one - a nullable override with a computed property, or a stored column
/// with a flag - and the stored column won: every read model that selects a worker's name (the
/// public booking surface, `20-15`'s slot table, `20-12`'s contacts report) selects one column
/// instead of reproducing the derivation rule in SQL. <see cref="DisplayNameIsCustom"/> is that
/// flag, and <see cref="Rename"/>'s own remarks say exactly when it stops the recomputation.</para>
/// </summary>
public sealed class Worker
{
    private readonly List<CalendarMembership> _calendars = [];
    private readonly List<ServiceOffering> _services = [];

    public WorkerId Id { get; }

    public TenantId TenantId { get; }

    public string LastName { get; private set; } = string.Empty;

    public string FirstName { get; private set; } = string.Empty;

    public string? MiddleName { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Whether a human typed <see cref="DisplayName"/> directly, through
    /// <see cref="SetDisplayName"/>. While this is <see langword="false"/>, <see cref="Rename"/> keeps
    /// recomputing <see cref="DisplayName"/> from the name fields on every call; the moment it becomes
    /// <see langword="true"/> it stays that way until nothing (there is no "un-custom" operation, the
    /// same way there is no route back to <see cref="EventStatus.Available"/> on <see cref="Event"/> -
    /// a human who typed a name meant it, and a later rename should not silently discard that).</summary>
    public bool DisplayNameIsCustom { get; private set; }

    /// <summary>An inactive worker keeps their history and their materialised events; `20-02` simply
    /// stops extending their horizon. Deleting a worker who has bookings is not a thing this product
    /// should offer - see <see cref="IWorkerRepository"/>'s deletion port for the narrower case it
    /// does offer instead.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<CalendarMembership> Calendars => _calendars;

    public IReadOnlyList<ServiceOffering> Services => _services;

    private Worker(
        WorkerId id, TenantId tenantId, string lastName, string firstName, string? middleName, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        LastName = lastName;
        FirstName = firstName;
        MiddleName = middleName;
        DisplayName = Derive(firstName, lastName);
        DisplayNameIsCustom = false;
        IsActive = true;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private Worker()
    {
    }

    public static Worker Create(
        WorkerId id, TenantId tenantId, string lastName, string firstName, string? middleName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);

        return new Worker(id, tenantId, lastName.Trim(), firstName.Trim(), NormalizeMiddleName(middleName), now);
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

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }

    public void Reactivate(DateTimeOffset now)
    {
        IsActive = true;
        UpdatedAt = now;
    }

    /// <summary>
    /// Changes the three name fields and recomputes <see cref="DisplayName"/> - but only while
    /// <see cref="DisplayNameIsCustom"/> is still <see langword="false"/>.
    ///
    /// <para><b>The order of operations is the whole guarantee.</b> The check reads the flag *before*
    /// this call can possibly change it - <see cref="Rename"/> never sets
    /// <see cref="DisplayNameIsCustom"/>, only <see cref="SetDisplayName"/> does - so calling this
    /// method a second time after a human has set a custom name is a no-op on
    /// <see cref="DisplayName"/> itself: "если ввели руками, а потом меняют Имя или Фамилию -
    /// значение уже не рассчитывается" is exactly the branch below.</para>
    /// </summary>
    public void Rename(string lastName, string firstName, string? middleName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);

        LastName = lastName.Trim();
        FirstName = firstName.Trim();
        MiddleName = NormalizeMiddleName(middleName);

        if (!DisplayNameIsCustom)
        {
            DisplayName = Derive(FirstName, LastName);
        }

        UpdatedAt = now;
    }

    /// <summary>A human overrides <see cref="DisplayName"/> directly. Raises
    /// <see cref="DisplayNameIsCustom"/>, which is what stops every later <see cref="Rename"/> from
    /// recomputing over it.</summary>
    public void SetDisplayName(string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        DisplayName = displayName.Trim();
        DisplayNameIsCustom = true;
        UpdatedAt = now;
    }

    /// <summary>First-name-space-last-name, with whitespace collapsed - not just trimmed, because a
    /// stray double space typed into either field should not survive into what the booking surface
    /// renders.</summary>
    private static string Derive(string firstName, string lastName) =>
        string.Join(
            ' ',
            $"{firstName} {lastName}".Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? NormalizeMiddleName(string? middleName) =>
        string.IsNullOrWhiteSpace(middleName) ? null : middleName.Trim();
}
