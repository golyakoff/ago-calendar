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
            // `20-06`. The public surface's two failures are one status as well as one message: a
            // 403 for the origin case would tell a page that the tenant it named exists, which is
            // precisely what PublicBookingErrors refuses to say in words.
            "booking.surface_not_found" or "booking.origin_not_allowed" => StatusCodes.Status404NotFound,
            // `20-04`'s operator-facing codes, which had no HTTP surface until this item gave them
            // one. 403 rather than 404 for a permission failure: the caller is an authenticated
            // operator of a known tenant, so "you may not" is a thing they are entitled to be told.
            "booking.forbidden" or "availability.forbidden" or "configuration.forbidden" =>
                StatusCodes.Status403Forbidden,
            "booking.not_found" or "configuration.not_found" => StatusCodes.Status404NotFound,
            // A state the caller can see and act on, not a fault - and 409 rather than 400 because
            // nothing about the request was malformed; the world moved.
            "booking.invalid_state" or "booking.concurrency_conflict" => StatusCodes.Status409Conflict,
            "configuration.invalid" or "provisioning.invalid" => StatusCodes.Status400BadRequest,
            // `20-07`. A deployment fault - this host's static ChatModule:* configuration does not
            // resolve to a real, published calendar - not something the caller (Ago.Chat.*) can act
            // on by retrying differently, so it is mapped to 500 explicitly rather than left to the
            // catch-all below, which exists to catch a bug in this switch rather than to describe a
            // real operational state on purpose.
            "chat_module_task.not_configured" => StatusCodes.Status500InternalServerError,
            "chat_module_task.not_found" => StatusCodes.Status404NotFound,
            // 409, not 404: the task exists and the request was well-formed - it simply already
            // finished. The same reasoning booking.slot_unavailable's own comment gives.
            "chat_module_task.already_complete" => StatusCodes.Status409Conflict,
            "chat_module_task.kind_mismatch" or "chat_module_task.invalid_reply_value" =>
                StatusCodes.Status400BadRequest,
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
