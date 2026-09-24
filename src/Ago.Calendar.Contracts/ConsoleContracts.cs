namespace Ago.Calendar.Contracts;

/// <summary>
/// The wire shapes of the operator console (`20-06`). Separate from
/// <see cref="BookingSurfaceResponse"/> and friends because the two surfaces have opposite audiences:
/// everything here is behind adr/0022's OIDC scheme and may be precise about what went wrong, and
/// everything there is public and must not be.
/// </summary>
public sealed record CreateCalendarRequest(string Name, string TimeZone, bool Publish);

public sealed record UpdateCalendarRequest(string Name, bool Publish);

/// <param name="PriceMinorUnits">`23-35`. Kopecks, or <see langword="null"/> for no stated price - no
/// currency alongside it: v1 accepts exactly one, chosen server-side (<c>Ago.Calendar.Domain.Money</c>'s
/// own remarks say why).</param>
/// <param name="PriceIsFrom">"от" pricing - ignored server-side when <paramref name="PriceMinorUnits"/>
/// is <see langword="null"/>.</param>
public sealed record CreateServiceRequest(
    string Name,
    int DurationMinutes,
    int? PriceMinorUnits = null,
    bool PriceIsFrom = false,
    string? Description = null);

/// <summary>
/// `26-96`: <c>PUT /services/{serviceId}</c> - <see cref="CreateServiceRequest"/>'s own five fields
/// plus the one thing a create has no opinion about. Before this the product had no way at all to
/// correct a service, and the price it carries is visitor-facing.
/// </summary>
/// <param name="PriceMinorUnits">Kopecks, or <see langword="null"/> for "no stated price". Unlike
/// <see cref="CreateServiceRequest"/>'s, this one has no default: an edit form always holds the whole
/// record, and a defaulted field would let a caller that forgot to send it silently clear a price
/// (replace semantics, the same objection <see cref="UpdateWorkerRequest.ServiceIds"/>'s own remarks
/// raise against sending a delta).</param>
/// <param name="IsActive"><see langword="false"/> withdraws the service - it stops being offered on
/// the public booking surface and a claim naming it is refused, while every worker who performs it
/// and every booking that used it keeps resolving its name. See
/// <c>Ago.Calendar.Domain.Service.IsActive</c> for why this product has no <c>DELETE /services/{id}</c>
/// at all.</param>
public sealed record UpdateServiceRequest(
    string Name,
    int DurationMinutes,
    int? PriceMinorUnits,
    bool PriceIsFrom,
    string? Description,
    bool IsActive);

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

/// <summary>`20-13`/`25-74`. See <see cref="CreateWorkerRequest.DisplayName"/> for what <c>null</c>
/// means here too.</summary>
/// <param name="ServiceIds">`25-74`: the worker's complete, desired set of services - replace
/// semantics, matching <see cref="CreateWorkerRequest.ServiceIds"/>. Before this, editing a worker
/// had no way to reach the service list at all - it could only be set once, at creation.</param>
public sealed record UpdateWorkerRequest(
    string LastName,
    string FirstName,
    string? MiddleName,
    string? DisplayName,
    bool IsActive,
    IReadOnlyList<Guid> ServiceIds);

/// <summary>`20-13`: one worker, in full - the workers table's own row shape and the edit card's
/// prefill, in one response so the console never needs a second request to open a card for a worker
/// it has already listed.</summary>
/// <param name="ServiceIds">`25-74`: what this worker offers today, so the edit form's checkbox set
/// can pre-check the right boxes instead of always starting empty.</param>
public sealed record WorkerResponse(
    Guid WorkerId,
    string LastName,
    string FirstName,
    string? MiddleName,
    string DisplayName,
    bool DisplayNameIsCustom,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<Guid> ServiceIds);

/// <param name="DayOfWeek">0 = Sunday, matching <see cref="System.DayOfWeek"/>. An integer rather
/// than a name because a name would need a culture to parse and this is a machine boundary.</param>
/// <param name="StartsAt">Wall clock in the calendar's own zone - <c>"09:00"</c>, never an instant.
/// See <c>WorkingHoursRule</c>: an offset chosen at configuration time is wrong for half the year in
/// any zone with DST.</param>
public sealed record AddWorkingHoursRuleRequest(
    Guid CalendarId, Guid WorkerId, int DayOfWeek, TimeOnly StartsAt, TimeOnly EndsAt);

/// <summary>`26-97`: <c>PUT /working-hours/{ruleId}</c>. No calendar or worker id, unlike
/// <see cref="AddWorkingHoursRuleRequest"/> - a rule is corrected where it is, never moved; see
/// <c>WorkingHoursRule.ChangeTo</c>.</summary>
/// <param name="DayOfWeek">0 = Sunday, the same convention
/// <see cref="AddWorkingHoursRuleRequest.DayOfWeek"/> uses.</param>
public sealed record UpdateWorkingHoursRuleRequest(int DayOfWeek, TimeOnly StartsAt, TimeOnly EndsAt);

/// <summary>`26-97`: the answer to both <c>PUT</c> and <c>DELETE /working-hours/{ruleId}</c>.</summary>
/// <param name="Rule">The rule as it now stands, or <see langword="null"/> when it was deleted.</param>
public sealed record WorkingHoursRuleChangeResponse(
    WorkingHoursRuleResponse? Rule, WorkingHoursReconciliationResponse Reconciliation);

/// <summary>
/// `26-97`: what the correction did <b>not</b> reach - the days this worker's schedule had already
/// materialised from the old hours, which no edit to a rule can retroactively re-cut.
///
/// <para><b>Present on every successful edit and delete, including the ones with nothing in
/// them.</b> A client that only ever saw this field when something was wrong would have no way to
/// tell "nothing to do" from "this build does not send that field" - see
/// <c>WorkingHoursReconciler</c> for why the whole point of this block is that the consequence is
/// never silent.</para>
/// </summary>
/// <param name="RecutFrom">Feed it straight to <c>POST /workers/{id}/schedule/recut/preview</c>.
/// <see langword="null"/> exactly when <paramref name="AlreadyCutDays"/> is empty.</param>
/// <param name="AlreadyCutDays">Business-local dates, oldest first - the calendar's own zone, never
/// UTC.</param>
/// <param name="LiveBookingCount">Pending, confirmed and no-show bookings on those days. Zero means
/// a re-cut would cancel nothing.</param>
public sealed record WorkingHoursReconciliationResponse(
    DateOnly? RecutFrom, IReadOnlyList<DateOnly> AlreadyCutDays, int LiveBookingCount);

public sealed record SetAllowedOriginsRequest(IReadOnlyList<string> Origins);

/// <param name="PublicKey">What the shop pastes into its own page's script tag. Shown only here.
/// </param>
public sealed record TenantConfigurationResponse(
    string TenantName,
    string PublicKey,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<ConfiguredCalendarResponse> Calendars,
    IReadOnlyList<ConfiguredWorkerResponse> Workers,
    IReadOnlyList<ConfiguredServiceResponse> Services,
    int WorkerQuota);

public sealed record ConfiguredCalendarResponse(
    Guid CalendarId,
    string Name,
    string TimeZone,
    bool IsPublished,
    IReadOnlyList<Guid> WorkerIds,
    IReadOnlyList<WorkingHoursRuleResponse> WorkingHours);

public sealed record ConfiguredWorkerResponse(
    Guid WorkerId, string DisplayName, bool IsActive, IReadOnlyList<Guid> ServiceIds);

/// <param name="PriceMinorUnits">`23-35`. Kopecks, or <see langword="null"/> when the tenant has
/// stated no price.</param>
/// <param name="PriceCurrencyCode"><see langword="null"/> exactly when <paramref name="PriceMinorUnits"/>
/// is - stated rather than assumed, even though v1 only ever writes <c>"RUB"</c>.</param>
/// <param name="IsActive">`26-96`. <see langword="false"/> means the tenant has taken this service
/// out of rotation. Still returned by <c>GET /configuration</c> rather than filtered out, and that is
/// the whole point of the flag: a worker card listing this service, and a past booking naming it,
/// both resolve it through this list. It is the *public* surface that stops offering it
/// (<c>BookingSurfaceReadStore</c>), never this one.</param>
public sealed record ConfiguredServiceResponse(
    Guid ServiceId,
    string Name,
    int DurationMinutes,
    int? PriceMinorUnits,
    string? PriceCurrencyCode,
    bool PriceIsFrom,
    string? Description,
    bool IsActive);

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
/// <param name="BookingId">
/// `20-18`: the run's own anchor id, now that a booking may be several consecutive slots claimed as
/// one - this response is already one row per booking, not per slot, so the field name did not need
/// to change even though what it points at can now be a multi-slot run.
/// </param>
/// <param name="Masked">`23-12`: whether <see cref="Phone"/> above is the masked display form -
/// meaningful only when <see cref="Phone"/> is non-null. The console must not infer this from the
/// string's own shape.</param>
/// <param name="WorkerDisplayName">`26-50`: never gated - a worker's own name is the shop's own
/// roster, not personal data about a customer, the identical reasoning
/// <see cref="ConfirmedBookingResponse.WorkerDisplayName"/> already states for its own sibling
/// field.</param>
/// <param name="ServiceName">`26-50`: never gated, the same reasoning
/// <see cref="ConfirmedBookingResponse.ServiceName"/> already carries.</param>
/// <param name="CustomerDisplayName">`26-50`: gated exactly the way <see cref="Phone"/> already is -
/// <see langword="null"/> for a caller who does not hold <c>customer:read</c>, and also
/// <see langword="null"/> for a customer who has simply never had a name recorded (unlike
/// <see cref="Phone"/>, where the second reason cannot occur - see
/// <c>PendingBookingRow.CustomerDisplayName</c>'s own remarks for why the two fields' null-stories
/// differ). Never a display name invented from a phone number.</param>
public sealed record PendingBookingResponse(
    Guid BookingId,
    Guid CalendarId,
    Guid WorkerId,
    string WorkerDisplayName,
    Guid ServiceId,
    string? ServiceName,
    Guid CustomerId,
    string? CustomerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    DateTimeOffset ConfirmationDeadline,
    bool IsOverdue,
    string? Phone,
    bool Masked);

// `22-05`/`adr/0093`: CreateRoleRequest/RoleResponse/InviteOperatorRequest/OperatorResponse removed -
// there is no calendar-owned `roles`/`operators` table left to manage from this console. A person's
// calendar permissions are granted on the account side now (`22-06`'s console screen).

/// <param name="Masked">`23-12`: whether <see cref="Phone"/> is the masked display form. The console
/// renders a reveal control exactly when this is <see langword="true"/>.</param>
/// <param name="PhoneVerifiedAt">`20-09`'s own fact: when this number was proven reachable by SMS
/// code, or <see langword="null"/> if it never has been.</param>
/// <param name="PhoneConfirmedByOperatorAt">`23-12`'s own distinct fact: when an operator recorded
/// "I called and it is them", or <see langword="null"/> if nobody has. Never merged with
/// <see cref="PhoneVerifiedAt"/> - see <c>Customer.PhoneConfirmedByOperatorAt</c>'s own remarks.</param>
/// <param name="NoShowCount">Read honestly - see <c>ContactRow.NoShowCount</c>'s own remarks on why
/// this is zero for every customer in this product's v1, not a bug in the report.</param>
/// <param name="DuplicatePhoneCustomerIds">`23-60`/`adr/0147`: every other live customer in this
/// tenant sharing this row's own phone - what the console's "shares a contact detail" hint and its
/// Merge action are built from. Empty for the ordinary case.</param>
public sealed record ContactResponse(
    Guid CustomerId,
    string Phone,
    bool Masked,
    string? DisplayName,
    string? Notes,
    int NoShowCount,
    DateTimeOffset? PhoneVerifiedAt,
    DateTimeOffset? PhoneConfirmedByOperatorAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<Guid> DuplicatePhoneCustomerIds);

/// <summary>`23-12`: the response to a deliberate reveal - the real number, and nothing else. Never
/// returned by any list endpoint.</summary>
public sealed record CustomerPhoneRevealResponse(string Phone);

/// <summary>
/// `23-34`: one confirmed booking - an appointment, not a slot, the same one-row-per-<c>booking_id</c>
/// shape <see cref="PendingBookingResponse"/> already established. Field names match
/// <c>Ago.Calendar.Application.Abstractions.ConfirmedBookingRow</c> verbatim.
/// </summary>
/// <param name="WorkerDisplayName">Never gated - a worker's own name is the shop's own roster, not
/// personal data about a customer.</param>
/// <param name="ServiceName">Never gated, the same reasoning <see cref="WorkerSlotResponse.ServiceName"/>
/// already carries.</param>
/// <param name="CustomerDisplayName"><see langword="null"/> only when the customer has never had a
/// name recorded - unrelated to this response's own permission gate, since every row already passed
/// <c>customer:read</c> (<c>IConfirmedBookingReadStore</c>'s own remarks on why this screen has no
/// contact-free row the way <see cref="PendingBookingResponse"/> and <see cref="WorkerSlotResponse"/>
/// both do).</param>
/// <param name="Weekday">0 = Sunday, matching <see cref="WorkerSlotResponse.Weekday"/>'s own
/// convention and the identical reasoning: computed server-side from <see cref="LocalDate"/> so the
/// console never derives a weekday from a bare date string in its own, possibly different, zone.</param>
/// <param name="Phone">Always populated - masked or real, never <see langword="null"/>, because every
/// row on this screen already passed <c>customer:read</c>.</param>
/// <param name="Masked">`23-12`: whether <see cref="Phone"/> is the tenant's own rung-masked display
/// value rather than the real number.</param>
public sealed record ConfirmedBookingResponse(
    Guid BookingId,
    Guid CalendarId,
    Guid WorkerId,
    string WorkerDisplayName,
    Guid ServiceId,
    string? ServiceName,
    Guid CustomerId,
    string? CustomerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate,
    int Weekday,
    string Phone,
    bool Masked);

/// <summary>`23-12`: what the console posts to reveal one customer's phone - which screen asked, for
/// the reveal record's own "which surface" field.</summary>
public sealed record RevealCustomerPhoneRequest(string Surface);

/// <summary>`23-12`: the response to a successful "I called and it is them" confirmation.</summary>
public sealed record ConfirmOperatorVerifiedPhoneResponse(DateTimeOffset ConfirmedAt);

/// <summary>`23-12`'s own audit view - `decisions.md` §5's amendment: individual reveals, never an
/// aggregated count.</summary>
public sealed record ContactPhoneRevealResponse(
    Guid Id, DateTimeOffset OccurredAt, Guid CustomerId, Guid OperatorId, string Surface);

/// <param name="NextBefore">Keyset cursor for the next page - <see langword="null"/> once the oldest
/// row has been reached.</param>
public sealed record ContactPhoneRevealPageResponse(
    IReadOnlyList<ContactPhoneRevealResponse> Items, Guid? NextBefore);

/// <summary>`23-60`/`adr/0147`: the two candidate ids, unordered - <c>MergeCustomers</c>'s own doc
/// comment explains why neither the preview request nor this one lets the caller name a
/// "survivor".</summary>
public sealed record CustomerMergePreviewRequest(Guid FirstCustomerId, Guid SecondCustomerId);

/// <param name="Status">The CLR enum member name (<c>Available</c>/<c>PendingConfirmation</c>/
/// <c>Booked</c>/<c>Cancelled</c>/<c>NoShow</c>/<c>Blocked</c>), verbatim - a closed vocabulary the
/// console already has its own copy of for the pending/confirmed-bookings screens.</param>
public sealed record CustomerMergePreviewBookingResponse(
    Guid BookingId,
    string Status,
    string? ServiceName,
    string WorkerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate);

/// <param name="WillSurvive">`adr/0161`: whether the server would keep this candidate if the
/// operator goes on to confirm - display-only, re-decided (never trusted) by the merge itself.</param>
public sealed record CustomerMergeCandidateResponse(
    Guid CustomerId,
    string Source,
    bool WillSurvive,
    string Phone,
    bool Masked,
    string? DisplayName,
    int NoShowCount,
    IReadOnlyList<CustomerMergePreviewBookingResponse> Bookings);

public sealed record CustomerMergePreviewResponse(
    CustomerMergeCandidateResponse First, CustomerMergeCandidateResponse Second);

/// <summary>`23-60`/`adr/0147`: the merge itself - the same two unordered ids the preview above was
/// asked about, now acted on.</summary>
public sealed record MergeCustomersRequest(Guid FirstCustomerId, Guid SecondCustomerId);

/// <param name="SurvivorCustomerId">Which of the two the operator's request actually kept - decided
/// by the handler, not the request; see <c>MergeCustomersHandler</c>'s own doc comment for why an
/// operator cannot choose this.</param>
public sealed record CustomerMergeOutcomeResponse(Guid SurvivorCustomerId, Guid AbsorbedCustomerId, int BookingsMoved);

/// <summary>`23-60`/`adr/0147`'s own Done-when: "the merge is recorded ... and is visible
/// afterwards." One row, one merge - the same shape <see cref="ContactPhoneRevealResponse"/> already
/// establishes for a different audit trail.</summary>
public sealed record CustomerMergeResponse(
    Guid Id, DateTimeOffset MergedAt, Guid SurvivorCustomerId, Guid AbsorbedCustomerId, Guid OperatorId, int BookingsMoved);

/// <param name="NextBefore">Keyset cursor for the next page - <see langword="null"/> once the oldest
/// row has been reached, the identical shape <see cref="ContactPhoneRevealPageResponse"/> already
/// establishes.</param>
public sealed record CustomerMergePageResponse(IReadOnlyList<CustomerMergeResponse> Items, Guid? NextBefore);

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
/// <param name="BuffersCountTowardServiceDuration">
/// `20-18`: labelled on the console's own schedule form exactly
/// «Перерывы внутри длинной записи считаются рабочим временем» - the author's own wording, kept
/// verbatim. See <c>WorkerSchedule.BuffersCountTowardServiceDuration</c> for the arithmetic this
/// decides between.
/// </param>
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
    DateOnly MaterializeFrom,
    bool BuffersCountTowardServiceDuration = true);

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
    DateTimeOffset UpdatedAt,
    bool BuffersCountTowardServiceDuration);

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
/// <param name="Masked">`23-12`: whether <see cref="Phone"/> is the masked display form -
/// meaningful only when <see cref="Phone"/> is non-null.</param>
/// <param name="BookingId">
/// `20-18`: which booking this slot belongs to, null exactly when <see cref="Status"/> is
/// <c>"Available"</c> or <c>"Blocked"</c>. Two rows sharing this value are two slots of one run - the
/// console uses it to show them as the same booking without merging the rows themselves (this item's
/// own scope keeps a slot as one row with one status).
/// </param>
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
    string? Phone,
    bool Masked,
    Guid? BookingId);

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
    bool Masked,
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

// `22-05`/`adr/0093`: no `OperatorDisplayName`/`ExternalSubjectId` any more - provisioning a tenant no
// longer provisions a calendar-owned operator alongside it (there is no local `operators` table left
// to put one in). The account owner's calendar access arrives through the projection instead.
public sealed record RegisterTenantRequest(string Name, string PublicKey, IReadOnlyList<string>? AllowedOrigins);

public sealed record RegisterTenantResponse(Guid TenantId, string PublicKey);

/// <summary>
/// `23-23`: one entry per calendar the tenant has created, plus the placeholder entry
/// <c>GetBookingReadinessHandler</c> returns for a tenant with none - <see cref="CalendarId"/> is
/// null exactly then, the only null this field ever carries.
/// </summary>
public sealed record CalendarReadinessResponse(
    Guid? CalendarId,
    string? CalendarName,
    bool IsBookable,
    IReadOnlyList<PreconditionStateResponse> Preconditions);

/// <param name="Precondition">The domain enum's wire name verbatim - <c>"CalendarPublished"</c>,
/// <c>"WorkerOnCalendar"</c>, <c>"ServiceOffered"</c>, <c>"WorkingHoursConfigured"</c>,
/// <c>"ScheduleSaved"</c> or <c>"SlotsMaterialized"</c>, in that stable order.</param>
public sealed record PreconditionStateResponse(string Precondition, bool IsMet);
