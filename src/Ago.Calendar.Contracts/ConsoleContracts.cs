namespace Ago.Calendar.Contracts;

/// <summary>
/// The wire shapes of the operator console (`20-06`). Separate from
/// <see cref="BookingSurfaceResponse"/> and friends because the two surfaces have opposite audiences:
/// everything here is behind adr/0022's OIDC scheme and may be precise about what went wrong, and
/// everything there is public and must not be.
/// </summary>
public sealed record CreateCalendarRequest(string Name, string TimeZone, bool Publish);

public sealed record UpdateCalendarRequest(string Name, bool Publish);

public sealed record CreateServiceRequest(string Name, int DurationMinutes);

/// <param name="MiddleName">Отчество - optional.</param>
/// <param name="DisplayName">`20-13`. Non-null means the console's own display-name field was
/// edited by hand before this request was sent; <see langword="null"/> means let the server derive
/// it from <paramref name="FirstName"/>/<paramref name="LastName"/>.</param>
/// <param name="ServiceIds">May be empty while a shop is still being set up - see
/// <c>CreateWorker</c> for why that is a real state rather than a validation gap.</param>
public sealed record CreateWorkerRequest(
    string LastName,
    string FirstName,
    string? MiddleName,
    string? DisplayName,
    Guid CalendarId,
    IReadOnlyList<Guid> ServiceIds);

/// <summary>`20-13`. See <see cref="CreateWorkerRequest.DisplayName"/> for what <c>null</c> means
/// here too.</summary>
public sealed record UpdateWorkerRequest(
    string LastName, string FirstName, string? MiddleName, string? DisplayName, bool IsActive);

/// <summary>`20-13`: one worker, in full - the workers table's own row shape and the edit card's
/// prefill, in one response so the console never needs a second request to open a card for a worker
/// it has already listed.</summary>
public sealed record WorkerResponse(
    Guid WorkerId,
    string LastName,
    string FirstName,
    string? MiddleName,
    string DisplayName,
    bool DisplayNameIsCustom,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="DayOfWeek">0 = Sunday, matching <see cref="System.DayOfWeek"/>. An integer rather
/// than a name because a name would need a culture to parse and this is a machine boundary.</param>
/// <param name="StartsAt">Wall clock in the calendar's own zone - <c>"09:00"</c>, never an instant.
/// See <c>WorkingHoursRule</c>: an offset chosen at configuration time is wrong for half the year in
/// any zone with DST.</param>
public sealed record AddWorkingHoursRuleRequest(
    Guid CalendarId, Guid WorkerId, int DayOfWeek, TimeOnly StartsAt, TimeOnly EndsAt);

public sealed record SetAllowedOriginsRequest(IReadOnlyList<string> Origins);

/// <param name="PublicKey">What the shop pastes into its own page's script tag. Shown only here.
/// </param>
public sealed record TenantConfigurationResponse(
    string TenantName,
    string PublicKey,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<ConfiguredCalendarResponse> Calendars,
    IReadOnlyList<ConfiguredWorkerResponse> Workers,
    IReadOnlyList<ConfiguredServiceResponse> Services);

public sealed record ConfiguredCalendarResponse(
    Guid CalendarId,
    string Name,
    string TimeZone,
    bool IsPublished,
    IReadOnlyList<Guid> WorkerIds,
    IReadOnlyList<WorkingHoursRuleResponse> WorkingHours);

public sealed record ConfiguredWorkerResponse(
    Guid WorkerId, string DisplayName, bool IsActive, IReadOnlyList<Guid> ServiceIds);

public sealed record ConfiguredServiceResponse(Guid ServiceId, string Name, int DurationMinutes);

public sealed record WorkingHoursRuleResponse(
    Guid RuleId, Guid WorkerId, int DayOfWeek, TimeOnly StartsAt, TimeOnly EndsAt);

/// <param name="IsOverdue">The sweep's health on the one screen a human already looks at - see
/// <c>PendingBookingRow</c>. Carried to the console rather than filtered out server-side, for exactly
/// the reason `20-04` gives: hiding overdue rows makes a broken sweep invisible to the only person in
/// a position to notice.</param>
/// <param name="Phone">`20-12`. <see langword="null"/> means the caller does not hold
/// <c>customer:read</c> for this tenant - see <c>PendingBookingRow.Phone</c>'s own remarks for why
/// that is the only thing a null here can mean. The console renders this as "hidden - you don't have
/// contact-visibility permission", never as an empty cell indistinguishable from "no phone
/// recorded".</param>
public sealed record PendingBookingResponse(
    Guid BookingId,
    Guid CalendarId,
    Guid WorkerId,
    Guid ServiceId,
    Guid CustomerId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    DateTimeOffset ConfirmationDeadline,
    bool IsOverdue,
    string? Phone);

/// <summary>`20-12`: a tenant provisions a second role. <paramref name="Permissions"/> is any non-empty
/// subset of the catalogue's own wire names (<c>Permission.Value</c>, e.g. <c>"customer:read"</c>) -
/// <c>Role.Create</c> is already fully general, so this request adds no rule of its own.</summary>
public sealed record CreateRoleRequest(string Name, IReadOnlyList<string> Permissions);

public sealed record RoleResponse(Guid RoleId, string Name, IReadOnlyList<string> Permissions);

public sealed record OperatorResponse(
    Guid OperatorId, string DisplayName, bool IsAccountOwner, IReadOnlyList<Guid> RoleIds);

/// <param name="NoShowCount">Read honestly - see <c>ContactRow.NoShowCount</c>'s own remarks on why
/// this is zero for every customer in this product's v1, not a bug in the report.</param>
public sealed record ContactResponse(
    Guid CustomerId,
    string Phone,
    string? DisplayName,
    string? Notes,
    int NoShowCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <param name="LocalDate">The business-local day, as the shop names it - not an instant range. See
/// <c>DeleteDayOff</c>.</param>
public sealed record DayOffRequest(Guid CalendarId, Guid WorkerId, DateOnly LocalDate);

public sealed record DayBoundaryRequest(
    Guid CalendarId, Guid WorkerId, DateOnly LocalDate, TimeOnly OpensAt, TimeOnly ClosesAt);

/// <summary>
/// `20-14`: the request behind <c>PUT /workers/{id}/schedule</c>. <paramref name="Kind"/> is the
/// string <c>"Weekly"</c> or <c>"Cycle"</c> - a stable wire name rather than the ordinal
/// <see cref="System.Text.Json"/> would otherwise serialise a bare C# enum as, which the console's
/// own TypeScript union type can then mirror verbatim.
/// </summary>
/// <param name="CycleAnchor">ISO <c>yyyy-MM-dd</c>, required when <paramref name="Kind"/> is
/// <c>"Cycle"</c> and ignored otherwise.</param>
/// <param name="CycleStartsAt">Wall clock <c>"HH:mm"</c> in the worker's calendar's own zone -
/// see <c>AddWorkingHoursRuleRequest.StartsAt</c> for the same convention.</param>
/// <param name="MaterializeFrom">Refused if it would move the schedule's cursor backwards - see
/// <c>SaveWorkerSchedule</c>'s own remarks.</param>
public sealed record SaveWorkerScheduleRequest(
    string Kind,
    DateOnly? CycleAnchor,
    int? CycleWorkingDays,
    int? CycleRestDays,
    TimeOnly? CycleStartsAt,
    TimeOnly? CycleEndsAt,
    int SlotMinutes,
    int BufferMinutes,
    int HorizonDays,
    DateOnly MaterializeFrom);

/// <summary>`20-14`: one worker's schedule, in full - the schedule section of `20-13`'s worker card
/// prefills straight from this, the same one-shape-for-read-and-edit pattern <see cref="WorkerResponse"/>
/// already uses.</summary>
public sealed record WorkerScheduleResponse(
    Guid ScheduleId,
    Guid WorkerId,
    string Kind,
    DateOnly? CycleAnchor,
    int? CycleWorkingDays,
    int? CycleRestDays,
    TimeOnly? CycleStartsAt,
    TimeOnly? CycleEndsAt,
    int SlotMinutes,
    int BufferMinutes,
    int HorizonDays,
    DateOnly MaterializeFrom,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// `20-15`: one worker's materialised schedule, whatever it currently is. The item's own plainest-
/// possible-screen scope: a list of rows, not a calendar grid and not an aggregate.
/// </summary>
/// <param name="Weekday">0 = Sunday, matching <see cref="System.DayOfWeek"/> - derived server-side
/// from <see cref="LocalDate"/>, which is already the business-local day (adr/0049), so the derivation
/// needs no zone and cannot disagree with it.</param>
/// <param name="Status">The domain enum's wire name verbatim - <c>"Available"</c>,
/// <c>"PendingConfirmation"</c>, <c>"Booked"</c>, <c>"Cancelled"</c>, <c>"NoShow"</c> or
/// <c>"Blocked"</c>.</param>
/// <param name="ServiceName">Null on a <c>Blocked</c> row - a closure is not a service.</param>
/// <param name="CustomerId">Not personal data - a foreign key - so never gated. What tells
/// <see cref="CustomerDisplayName"/>/<see cref="Phone"/>'s two null-reasons apart: null here means
/// nobody holds the slot; non-null with those two null means somebody does and this operator may not
/// see who.</param>
/// <param name="CustomerDisplayName">`20-12`'s own gate. Null either because
/// <see cref="CustomerId"/> is null too (nobody holds the slot), or because this operator does not
/// hold <c>customer:read</c> for this tenant - see <see cref="CustomerId"/> for the discriminator.
/// </param>
/// <param name="Phone">Same two-reasons-for-null story as <see cref="CustomerDisplayName"/>.</param>
public sealed record WorkerSlotResponse(
    Guid EventId,
    DateOnly LocalDate,
    int Weekday,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    Guid? ServiceId,
    string? ServiceName,
    Guid? CustomerId,
    string? CustomerDisplayName,
    string? Phone);

/// <summary>
/// `20-01` said the provisioning transaction that seeds a tenant, its operator role and its first
/// operator "belongs to `20-06`". This is its request - and see <c>DevProvisioningEndpoints</c> for
/// why the route that accepts it exists only outside Production.
/// </summary>
/// <param name="ExternalSubjectId">The Keycloak <c>sub</c> this tenant's first operator signs in as.
/// A value the realm already contains, never one this call creates: adr/0022 provisions Keycloak by
/// realm import so the demo user's id is deterministic, and this endpoint only writes it into
/// <c>operators.external_subject_id</c>.</param>
/// <summary>`20-16`: the request behind <c>POST /workers/{id}/schedule/recut/preview</c>.</summary>
public sealed record RecutPreviewRequest(DateOnly From);

/// <param name="Fingerprint">Opaque - hand back exactly what the preview response carried.</param>
public sealed record RecutPreviewResponse(IReadOnlyList<RecutDayPreviewResponse> Days, string Fingerprint);

public sealed record RecutDayPreviewResponse(
    DateOnly LocalDate, int AvailableSlotsToDelete, IReadOnlyList<RecutBookingPreviewResponse> Bookings);

/// <param name="Status">The domain enum's wire name verbatim - <c>"PendingConfirmation"</c>,
/// <c>"Booked"</c> or <c>"NoShow"</c>; this list never carries any other status.</param>
/// <param name="CanDecide"><see langword="false"/> only for a <c>"NoShow"</c> row - the console should
/// show it with no cancel/keep control at all, since it always forces its day to be skipped.</param>
public sealed record RecutBookingPreviewResponse(
    Guid BookingId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    Guid? ServiceId,
    string? ServiceName,
    Guid? CustomerId,
    string? CustomerDisplayName,
    string? Phone,
    bool CanDecide);

/// <summary>`20-16`: the request behind <c>POST /workers/{id}/schedule/recut</c>.</summary>
/// <param name="Decisions">One entry per <see cref="RecutBookingPreviewResponse.CanDecide"/> booking
/// the preview showed - see <c>RecutConfirm</c>'s own remarks on how an extra or missing entry is
/// treated.</param>
public sealed record RecutConfirmRequest(
    DateOnly From, string Fingerprint, IReadOnlyList<RecutDecisionRequest> Decisions);

/// <param name="Decision"><c>"Cancel"</c> or <c>"Keep"</c>.</param>
public sealed record RecutDecisionRequest(Guid BookingId, string Decision);

public sealed record RecutConfirmResponse(
    IReadOnlyList<DateOnly> RecutDays,
    IReadOnlyList<DateOnly> SkippedDays,
    int SlotsDeleted,
    int SlotsInserted,
    int BookingsCancelled);

public sealed record RegisterTenantRequest(
    string Name, string PublicKey, string OperatorDisplayName, string ExternalSubjectId,
    IReadOnlyList<string>? AllowedOrigins);

public sealed record RegisterTenantResponse(Guid TenantId, Guid OperatorId, string PublicKey);
