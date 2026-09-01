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
            // `20-09`. A caller error, not a fault - the identical reasoning `booking.invalid_phone`'s
            // own placement already gives: the request was well-formed but incomplete, and 400 (not
            // 403 - nobody is being denied a permission they hold) is what tells a caller integrating
            // against this endpoint that no retry of this exact request will ever succeed.
            "booking.invalid_phone" or "booking.service_not_offered" or "booking.phone_not_verified" =>
                StatusCodes.Status400BadRequest,
            "booking.rate_limited" => StatusCodes.Status429TooManyRequests,
            // `20-10`. Mirrors `ago-chat`'s own `14-15` mapping for the identical five confirm
            // outcomes, not by reference: a wrong code is the caller's own mistake to fix (400, the
            // same group as booking.invalid_phone); already-consumed and expired are each a state that
            // moved rather than a malformed request (409/410 - 410 specifically because a fresh code,
            // not a retry of this one, is the only remedy, the same distinction ago-chat's own mapping
            // draws for `OperatorInvite.Expired`); locked-out shares the rate-limited group for the
            // identical "a fresh attempt, not a permission, is the remedy" reasoning, even though it
            // carries no Retry-After.
            "phone_verification.calendar_not_found" or "phone_verification.not_found" =>
                StatusCodes.Status404NotFound,
            "phone_verification.invalid_phone" or "phone_verification.wrong_code" =>
                StatusCodes.Status400BadRequest,
            "phone_verification.already_consumed" => StatusCodes.Status409Conflict,
            "phone_verification.expired" => StatusCodes.Status410Gone,
            "phone_verification.rate_limited" or "phone_verification.locked_out" =>
                StatusCodes.Status429TooManyRequests,
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
            // `20-13`. 409, not 400: the request was well-formed and the worker exists - what refuses
            // it is a state the deletion rule itself protects (the same "the world moved" reasoning
            // booking.invalid_state's own comment gives), and deactivation is the alternative action
            // this response's own message points the caller at.
            "configuration.worker_has_booking_history" => StatusCodes.Status409Conflict,
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
            // `20-12`'s own access-control and contacts-report codes, the same 403/404/400 shape as
            // configuration.* above - these are operator-facing failures an authenticated caller is
            // entitled to see the reason for, not faults.
            "access.forbidden" or "contacts.forbidden" => StatusCodes.Status403Forbidden,
            "access.not_found" => StatusCodes.Status404NotFound,
            "access.invalid" => StatusCodes.Status400BadRequest,
            // 409, not 403: the caller *is* allowed to revoke roles in general (they already hold
            // calendar:configure) - what refuses this particular request is a state the aggregate
            // itself protects, the same "the world moved" reasoning booking.invalid_state's own
            // comment gives, not a permission the caller lacks.
            "access.account_owner_requires_contact_access" => StatusCodes.Status409Conflict,
            // 2026-09-01: PublicBookingApiGate's own kill switch. 403, not the 404 that
            // booking.surface_not_found/booking.origin_not_allowed use two cases above - those hide a
            // caller-specific fact (whether a tenant/origin exists); this refusal is identical for
            // every caller, so a 404 would only risk reading as a stale route rather than a deliberate
            // one. See PublicBookingApiGate's own remarks for the full reasoning.
            "booking.public_api_disabled" => StatusCodes.Status403Forbidden,
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
