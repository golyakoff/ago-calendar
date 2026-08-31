namespace Ago.Calendar.Contracts;

/// <summary>
/// The wire shapes of the operator console (`20-06`). Separate from
/// <see cref="BookingSurfaceResponse"/> and friends because the two surfaces have opposite audiences:
/// everything here is behind adr/0022's OIDC scheme and may be precise about what went wrong, and
/// everything there is public and must not be.
/// </summary>
public sealed record CreateCalendarRequest(string Name, string TimeZone, int BufferMinutes, bool Publish);

public sealed record UpdateCalendarRequest(string Name, int BufferMinutes, bool Publish);

public sealed record CreateServiceRequest(string Name, int DurationMinutes);

/// <param name="ServiceIds">May be empty while a shop is still being set up - see
/// <c>CreateWorker</c> for why that is a real state rather than a validation gap.</param>
public sealed record CreateWorkerRequest(string DisplayName, Guid CalendarId, IReadOnlyList<Guid> ServiceIds);

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
    int BufferMinutes,
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
/// `20-01` said the provisioning transaction that seeds a tenant, its operator role and its first
/// operator "belongs to `20-06`". This is its request - and see <c>DevProvisioningEndpoints</c> for
/// why the route that accepts it exists only outside Production.
/// </summary>
/// <param name="ExternalSubjectId">The Keycloak <c>sub</c> this tenant's first operator signs in as.
/// A value the realm already contains, never one this call creates: adr/0022 provisions Keycloak by
/// realm import so the demo user's id is deterministic, and this endpoint only writes it into
/// <c>operators.external_subject_id</c>.</param>
public sealed record RegisterTenantRequest(
    string Name, string PublicKey, string OperatorDisplayName, string ExternalSubjectId,
    IReadOnlyList<string>? AllowedOrigins);

public sealed record RegisterTenantResponse(Guid TenantId, Guid OperatorId, string PublicKey);
