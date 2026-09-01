using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.BookEvent;

/// <summary>
/// A visitor books one slot. Unauthenticated: everything here arrives from the public internet and
/// none of it is trusted - the tenant is resolved from the calendar, never taken from the request,
/// and the phone number is a raw string because the caller has no way to construct a validated
/// <see cref="PhoneNumber"/> and the handler is where a bad one turns into an ordinary rejection
/// rather than an exception crossing a layer.
/// </summary>
/// <param name="CalendarId">From the route.</param>
/// <param name="EventId">From the route - the slot the customer picked.</param>
/// <param name="ServiceId">What they are booking. v1 takes exactly one: the product spec defers
/// several services in one visit, with two adjacent slots as the stated workaround.</param>
/// <param name="Phone">Raw, as typed. Normalised by <see cref="PhoneNumber"/> so that
/// <c>+7 (999) 123-45-67</c> and <c>+79991234567</c> are one lead card.</param>
/// <param name="DisplayName">Optional.</param>
/// <param name="Origin">
/// The request's own <c>Origin</c> header, or null when there is none - `20-06`'s layer-2 check.
///
/// <para><b>Passed in rather than read from an ambient context, because Application must not know
/// there is an HTTP request.</b> The alternative, injecting <c>IHttpContextAccessor</c>, would put a
/// hosting type inside a use case and make this handler untestable without a request pipeline - the
/// exact coupling <c>ForbiddenTypeTests</c> exists to catch. It is a parameter for the same reason
/// <c>DateTimeOffset now</c> is one everywhere else in this repository.</para>
/// </param>
/// <param name="RequiresVerifiedPhone">
/// `20-09`/`20-10`: whether *this calling surface* enforces the phone-verification gate at all - a fact
/// about which caller is booking, not about whether verification happened to occur. <c>true</c> from
/// both callers this command has today: the chat-originated flow
/// (<see cref="Ago.Calendar.Application.UseCases.ChatModuleTask.ReplyToModuleTaskHandler"/>), which
/// obtains a real assertion via `14-15` and passes it directly as <see cref="PhoneVerifiedAt"/>; and,
/// as of `20-10`, the public booking widget itself (<see cref="Ago.Calendar.Api.Booking.BookingEndpoints"/>),
/// which has its own independent verification mechanism now (<c>PendingPhoneVerification</c>) instead of
/// the self-asserted value that would have been the only alternative - see that item's own backlog file
/// for why a universal gate had to wait for a real mechanism rather than either shipping a forgeable
/// field or leaving the widget permanently unable to book. There is no longer a caller of this command
/// that supplies <see langword="false"/>; the parameter default remains <see langword="false"/> only so
/// that a future third caller must decide the question explicitly rather than inheriting a value that
/// happens to have been safe for the two callers that exist today.
/// </param>
/// <param name="PhoneVerifiedAt">
/// The calling side's own assertion that <paramref name="Phone"/> has been proven reachable, when the
/// caller already has one in hand - the chat-originated flow's own shape, unchanged since `20-09`. Null
/// from the public widget, which instead supplies <see cref="PhoneVerificationId"/>/
/// <see cref="PhoneVerificationProofToken"/> for <see cref="BookEventHandler"/>'s own
/// <c>PhoneVerificationAssertionResolver</c> to resolve into an equivalent instant - see that type's own
/// remarks for the three sources it tries, in order. Refused by <see cref="BookEventHandler"/> whenever
/// <see cref="RequiresVerifiedPhone"/> is true and every source the resolver tries comes back
/// empty.
/// </param>
/// <param name="PhoneVerificationId">
/// `20-10`: the id of a <c>PendingPhoneVerification</c> the public widget's own confirm step returned -
/// paired with <paramref name="PhoneVerificationProofToken"/>, the caller's evidence that
/// <paramref name="Phone"/> was actually verified through this item's own mechanism. Null on the
/// chat-originated path, which never has one.
/// </param>
/// <param name="PhoneVerificationProofToken">
/// `20-10`: the plaintext bearer proof <c>ConfirmPhoneVerificationHandler</c> minted, once, on a
/// confirmed verification - unforgeable (only its hash is ever stored) and phone-bound
/// (<c>PendingPhoneVerification.IsProofValid</c> refuses it against any phone number other than the one
/// it was issued for).
/// </param>
public readonly record struct BookEvent(
    CalendarId CalendarId,
    EventId EventId,
    ServiceId ServiceId,
    string Phone,
    string? DisplayName,
    string? Origin = null,
    bool RequiresVerifiedPhone = false,
    DateTimeOffset? PhoneVerifiedAt = null,
    Guid? PhoneVerificationId = null,
    string? PhoneVerificationProofToken = null);
