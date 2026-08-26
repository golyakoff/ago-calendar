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
public readonly record struct BookEvent(
    CalendarId CalendarId,
    EventId EventId,
    ServiceId ServiceId,
    string Phone,
    string? DisplayName,
    string? Origin = null);
