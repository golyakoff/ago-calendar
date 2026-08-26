using Ago.Platform.Kernel;

namespace Ago.Calendar.Api.Http;

/// <summary>
/// api-design.md: "Errors are RFC 7807 problem details with a stable machine-readable <c>type</c>...
/// clients branch on <c>type</c>, never on the message." This product's error codes
/// (<c>BookingErrors</c>, <c>AvailabilityErrors</c>) are that vocabulary; this is the one place they
/// become a status code.
///
/// <para>The mapping is a <c>switch</c> over codes rather than a status carried on
/// <see cref="Error"/> itself, matching ago-chat's own <c>ErrorExtensions</c>: an HTTP status is a
/// statement about a protocol, and Application must not know there is one.</para>
///
/// <para><c>type</c> is the bare code, not a resolvable URL - the same call ago-chat made, and for
/// the same reason: a dereferenceable type URI is a promise to host a document at it.</para>
/// </summary>
public static class ErrorExtensions
{
    public static IResult ToProblem(this Error error, HttpContext httpContext, TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var statusCode = error.Code switch
        {
            "booking.calendar_not_found" => StatusCodes.Status404NotFound,
            // 409, not 404: the slot exists and the request was well-formed - somebody else simply
            // got there first. A client that sees this refreshes availability and offers another
            // time, which is a different behaviour from "that URL is wrong".
            "booking.slot_unavailable" => StatusCodes.Status409Conflict,
            "booking.invalid_phone" or "booking.service_not_offered" => StatusCodes.Status400BadRequest,
            "booking.rate_limited" => StatusCodes.Status429TooManyRequests,
            "availability.day_not_materialized" => StatusCodes.Status404NotFound,
            "availability.day_has_bookings" or "availability.day_changed_concurrently" =>
                StatusCodes.Status409Conflict,
            "availability.invalid_day_boundary" or "availability.worker_not_on_calendar" =>
                StatusCodes.Status400BadRequest,
            "availability.calendar_not_found" => StatusCodes.Status404NotFound,
            // Anything unmapped is a bug in this switch, not a client error - a 500 says so honestly
            // instead of inventing a 400 that would make a caller retry something that cannot work.
            _ => StatusCodes.Status500InternalServerError,
        };

        // A real header, not a sentence in the body. api-design.md promises a widget-facing 429
        // carries Retry-After and that clients honour it with jittered backoff; a value only a human
        // can read is a value no client backs off on. This is why BookEventHandler returns its own
        // outcome type rather than Result<T> - see BookingOutcome.
        if (retryAfter is { } wait)
        {
            httpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Results.Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode,
            type: error.Code,
            extensions: new Dictionary<string, object?> { ["traceId"] = httpContext.TraceIdentifier });
    }
}
