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
/// `20-09`: whether *this calling surface* enforces the phone-verification gate at all - a fact about
/// which caller is booking, not about whether verification happened to occur. <c>true</c> only from the
/// chat-originated flow (<see cref="Ago.Calendar.Application.UseCases.ChatModuleTask.ReplyToModuleTaskHandler"/>),
/// which can obtain a real assertion via `14-15`. <c>false</c> from the public booking widget
/// (<see cref="BookEventHandler"/>'s own remarks): making the gate universal without first building a
/// *secure* way for an anonymous, browser-reachable endpoint to supply this assertion would mean either
/// leaving the widget permanently unable to book, or accepting a self-asserted value any caller could
/// forge - neither acceptable, so this item's own scope is chat-only until a real widget-side
/// verification mechanism exists (a named, separate follow-up item, not silently deferred).
/// </param>
/// <param name="PhoneVerifiedAt">
/// The calling side's assertion that <paramref name="Phone"/> has been proven reachable by the visitor
/// making this booking - `14-15`'s own verification, run to completion on the other product's side of
/// the wire before this command was ever built. Null when no verification happened, which is
/// unconditionally true for the public widget today (see <see cref="RequiresVerifiedPhone"/>) and
/// refused by <see cref="BookEventHandler"/> whenever <see cref="RequiresVerifiedPhone"/> is true and
/// this is still null.
/// </param>
public readonly record struct BookEvent(
    CalendarId CalendarId,
    EventId EventId,
    ServiceId ServiceId,
    string Phone,
    string? DisplayName,
    string? Origin = null,
    bool RequiresVerifiedPhone = false,
    DateTimeOffset? PhoneVerifiedAt = null);
