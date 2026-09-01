using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Api.Booking;

/// <summary>
/// The product's one public write surface, and the first HTTP endpoint AGO Calendar has.
///
/// <para><b>Unauthenticated, on purpose, and that is the whole security story here.</b> A customer
/// books with a phone number and no account - <see cref="Customer"/> has no password by design - so
/// there is no token to check and nothing to check it against. What stands in for authentication is
/// therefore doing real work: the route's own calendar id is the only thing binding a request to a
/// tenant, and it is carried into the claim's <c>WHERE</c> clause rather than merely validated
/// (<c>IBookingStore</c>); every failure that could distinguish "this id exists" from "this id does
/// not" is collapsed into one message; and both rate-limit buckets are correctness properties with
/// tests, not settings nobody exercises.</para>
///
/// <para><b>Why <c>POST .../book</c> and not a <c>bookings</c> collection.</b> api-design.md: no
/// verbs in paths, and an action that is not CRUD becomes a sub-resource - the shape
/// <c>POST /api/v1/conversations/{id}/close</c> already established in AGO Chat. A booking is not a
/// new resource here: a slot and the booking that takes it are one row (adr/0049), so
/// <c>POST /bookings</c> would name a collection that does not exist.</para>
///
/// <para><b>200, not 201.</b> api-design.md says a <c>POST</c> that creates returns <c>201</c> with a
/// <c>Location</c>. This one creates nothing - it transitions a row that already existed - so there
/// is no new URL to point at, and a <c>Location</c> header pointing at a <c>GET</c> this product does
/// not serve would be a promise broken by the first client that followed it. Stated here rather than
/// silently deviating.</para>
/// </summary>
public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/calendars/{calendarId:guid}/events/{eventId:guid}/book", HandleBookAsync)
            .WithName("BookEvent")
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> HandleBookAsync(
        Guid calendarId,
        Guid eventId,
        BookEventRequest request,
        BookEventHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var outcome = await handler.HandleAsync(
            new BookEvent(
                new CalendarId(calendarId),
                // Qualified, not imported: an ASP.NET Core host's implicit usings bring in
                // Microsoft.Extensions.Logging, which has its own EventId, and the two are ambiguous
                // (CS0104). Qualifying the one call site is the smallest honest fix - a `using`
                // alias is this project's stated non-answer to a name collision, and renaming the
                // domain's own EventId to escape a logging type would be the tail wagging the dog.
                // Third collision of this family in this repository, after BookingCalendar (CS0118)
                // and Ago.Calendar.Worker vs the Worker aggregate (`20-02`).
                new Domain.EventId(eventId),
                new ServiceId(request.ServiceId),
                request.Phone,
                request.DisplayName,
                // `20-06`, layer 2. Read here and passed in, never reached for from inside the
                // handler: Application must not know there is an HttpContext (see BookEvent.Origin).
                PublicBookingEndpoints.OriginOf(httpContext)),
            cancellationToken);

        if (outcome.Booking is not { } booking)
        {
            // outcome.Error is non-null exactly when Booking is null - BookingOutcome's two factories
            // are the only way to construct one. RetryAfter is null except for the rate-limited case,
            // and ToProblem writes the header only when it is not.
            return outcome.Error!.Value.ToProblem(httpContext, outcome.RetryAfter);
        }

        // Nothing here says "pending", and BookingConfirmedResponse has no field that could.
        // `20-18`: BookingId is the run's own anchor id - the same value every row of a multi-slot
        // booking now carries as its own Event.BookingId, and the id the operator's own
        // cancel/reject/no-show routes resolve the whole run from.
        return Results.Ok(new BookingConfirmedResponse(
            booking.BookingId.Value,
            booking.WorkerId.Value,
            booking.Slot.StartsAt,
            booking.Slot.EndsAt,
            booking.LocalDate));
    }
}
