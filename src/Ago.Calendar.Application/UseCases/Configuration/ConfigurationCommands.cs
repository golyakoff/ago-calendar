using Ago.Calendar.Application.Abstractions;
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
    OperatorId OperatorId, TenantId TenantId, string Name, string TimeZone, bool Publish);

/// <param name="Publish">The publish switch, sent on every update rather than as its own endpoint:
/// a console screen with a name field and a checkbox submits both, and a separate publish call would
/// let the two drift while a user watched.</param>
public readonly record struct UpdateCalendar(
    OperatorId OperatorId, TenantId TenantId, CalendarId CalendarId, string Name, bool Publish);

/// <param name="DurationMinutes">Whole minutes - <see cref="Service"/> refuses anything else, and its
/// remarks say why a slot boundary on a fraction of a minute is unreadable in every UI that renders
/// it.</param>
public readonly record struct CreateService(
    OperatorId OperatorId, TenantId TenantId, string Name, int DurationMinutes);

/// <param name="MiddleName">Отчество - optional, unlike <paramref name="LastName"/> and
/// <paramref name="FirstName"/>.</param>
/// <param name="DisplayName">`20-13`. Non-null means the console's own display-name field was
/// edited by hand this call, so the worker is created with a custom name from the start
/// (<see cref="Worker.SetDisplayName"/>); <see langword="null"/> means let it derive from
/// <paramref name="FirstName"/>/<paramref name="LastName"/> the normal way.</param>
/// <param name="CalendarId">The one calendar this worker joins. v1 allows exactly one
/// (<see cref="Worker.JoinCalendar"/>), so this is a single value rather than a list - a list would
/// promise a shape the aggregate refuses.</param>
/// <param name="ServiceIds">What they perform. Empty is legal and means "nobody can book them yet",
/// which is a real intermediate state while a shop is being set up - and is exactly why
/// <c>MaterializeAvailabilityHandler</c> has to cope with a worker who offers nothing.</param>
public readonly record struct CreateWorker(
    OperatorId OperatorId,
    TenantId TenantId,
    string LastName,
    string FirstName,
    string? MiddleName,
    string? DisplayName,
    CalendarId CalendarId,
    IReadOnlyList<Guid> ServiceIds);

/// <summary>`20-13`. Names, the optional custom display name, and the activity toggle - not the
/// calendar or the services, which keep the surface they had: v1 is one calendar per worker and
/// moving one is out of this item's scope (see the item's own "out of scope" section).</summary>
/// <param name="DisplayName">Non-null means a human edited the display-name field directly this
/// call - see <see cref="CreateWorker.DisplayName"/> for the same rule at creation time.</param>
public readonly record struct UpdateWorker(
    OperatorId OperatorId,
    TenantId TenantId,
    WorkerId WorkerId,
    string LastName,
    string FirstName,
    string? MiddleName,
    string? DisplayName,
    bool IsActive);

/// <summary>`20-13`. Refused - deleting nothing - if the worker has ever been booked; see
/// <see cref="IWorkerRepository.DeleteIfNeverBookedAsync"/> for exactly what that means and why the
/// check has to run in the same statement as the delete.</summary>
public readonly record struct DeleteWorker(OperatorId OperatorId, TenantId TenantId, WorkerId WorkerId);

/// <summary>`20-13`. One worker, for the card the console opens to edit him.</summary>
public readonly record struct GetWorker(OperatorId OperatorId, TenantId TenantId, WorkerId WorkerId);

/// <summary>`20-13`. Every worker of one tenant - the console's own table. No paging and no filter:
/// the item's own scope says ten workers is a lot for this product, by the author's own measure.</summary>
public readonly record struct ListWorkersForTenant(OperatorId OperatorId, TenantId TenantId);

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

/// <summary>`20-14`: <c>GET /workers/{id}/schedule</c>.</summary>
public readonly record struct GetWorkerSchedule(OperatorId OperatorId, TenantId TenantId, WorkerId WorkerId);

/// <summary>
/// `20-14`: <c>PUT /workers/{id}/schedule</c> - create-or-replace, the same upsert shape a schedule's
/// "one worker, one schedule" rule makes natural: there is nothing a second, separate create call
/// would let a caller do that this one cannot.
/// </summary>
/// <param name="Kind">Which five fields below are meaningful. Cycle fields are ignored when
/// <see cref="ScheduleKind.Weekly"/> is requested and required when <see cref="ScheduleKind.Cycle"/>
/// is - <see cref="SaveWorkerScheduleHandler"/> is where that is enforced, because a
/// <see langword="record struct"/> cannot make five nullable fields "required together" on its own.</param>
/// <param name="MaterializeFrom">Refused if it would move the schedule's own cursor backwards - see
/// <see cref="WorkerSchedule"/>'s own remarks for why that check lives on the aggregate rather than
/// here.</param>
public readonly record struct SaveWorkerSchedule(
    OperatorId OperatorId,
    TenantId TenantId,
    WorkerId WorkerId,
    ScheduleKind Kind,
    DateOnly? CycleAnchor,
    int? CycleWorkingDays,
    int? CycleRestDays,
    TimeOnly? CycleStartsAt,
    TimeOnly? CycleEndsAt,
    int SlotMinutes,
    int BufferMinutes,
    int HorizonDays,
    DateOnly MaterializeFrom);
