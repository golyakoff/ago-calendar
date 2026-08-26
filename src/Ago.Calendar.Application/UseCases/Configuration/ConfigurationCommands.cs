using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// The tenant-setup commands `20-06`'s console issues. Every one of them carries the acting
/// <see cref="OperatorId"/> and the <see cref="TenantId"/> the action names - the shape `20-04`'s
/// <c>BookingLifecycleCommands</c> established, and for the same reason: the tenant is never inferred
/// from the operator row, so a token claiming another tenant resolves to no roles rather than to its
/// own (<c>PermissionChecker</c>).
///
/// <para><b>All of them are gated on one permission, <see cref="Permission.CalendarConfigure"/>.</b>
/// adr/0016's granularity argument says a future dispatcher role may want to work the queue without
/// reshaping the schedule; it does not say every noun deserves its own permission. Splitting
/// configuration into calendar/worker/service/hours rights would produce four permissions that the
/// only role in existence grants together, which is four chances for a screen to check the wrong one.
/// When a role wants half of this, that role is what should split it.</para>
/// </summary>
/// <param name="TimeZone">An IANA zone id, and set once - <see cref="BookingCalendar.TimeZone"/> has
/// no setter, and its remarks say why re-zoning a live calendar is a data migration with a human in
/// the loop rather than an edit.</param>
public readonly record struct CreateCalendar(
    OperatorId OperatorId, TenantId TenantId, string Name, string TimeZone, int BufferMinutes, bool Publish);

/// <param name="Publish">The publish switch, sent on every update rather than as its own endpoint:
/// a console screen with a name field, a buffer field and a checkbox submits all three, and a separate
/// publish call would let the two drift while a user watched.</param>
public readonly record struct UpdateCalendar(
    OperatorId OperatorId, TenantId TenantId, CalendarId CalendarId, string Name, int BufferMinutes, bool Publish);

/// <param name="DurationMinutes">Whole minutes - <see cref="Service"/> refuses anything else, and its
/// remarks say why a slot boundary on a fraction of a minute is unreadable in every UI that renders
/// it.</param>
public readonly record struct CreateService(
    OperatorId OperatorId, TenantId TenantId, string Name, int DurationMinutes);

/// <param name="CalendarId">The one calendar this worker joins. v1 allows exactly one
/// (<see cref="Worker.JoinCalendar"/>), so this is a single value rather than a list - a list would
/// promise a shape the aggregate refuses.</param>
/// <param name="ServiceIds">What they perform. Empty is legal and means "nobody can book them yet",
/// which is a real intermediate state while a shop is being set up - and is exactly why
/// <c>MaterializeAvailabilityHandler</c> has to cope with a worker who offers nothing.</param>
public readonly record struct CreateWorker(
    OperatorId OperatorId,
    TenantId TenantId,
    string DisplayName,
    CalendarId CalendarId,
    IReadOnlyList<Guid> ServiceIds);

public readonly record struct AddWorkingHoursRule(
    OperatorId OperatorId,
    TenantId TenantId,
    CalendarId CalendarId,
    WorkerId WorkerId,
    DayOfWeek DayOfWeek,
    TimeOnly StartsAt,
    TimeOnly EndsAt);

/// <summary>
/// Replaces the tenant's whole allowed-origin list.
///
/// <para><b>`5-01` put this out of scope and named the reason: nothing but a seed script could give a
/// site an origin, and it deferred an editor to whichever item needed one.</b> This is that item. It
/// cannot be deferred again here, because `20-06`'s own Done-when is a stranger's page embedding a
/// script tag - and without an editor the only way to approve that page's origin is to write SQL,
/// which is not a product.</para>
/// </summary>
public readonly record struct SetAllowedOrigins(
    OperatorId OperatorId, TenantId TenantId, IReadOnlyList<string> Origins);

/// <summary>Everything the configuration screen draws, in one read.</summary>
public readonly record struct GetTenantConfiguration(OperatorId OperatorId, TenantId TenantId);
