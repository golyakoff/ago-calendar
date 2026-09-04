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
            // `22-20` folds `recut.stale` and `recut.day_changed_concurrently` into this arm - the
            // identical "the world moved" shape, not a coincidence of naming: both are genuine
            // optimistic-concurrency detections, not bounds checks that would be equally true on a
            // fresh call. `recut.stale` is `RecutConfirmHandler` recomputing `RecutFingerprint` over
            // every day in range and finding it disagrees with the fingerprint the client's preview
            // carried - a booking landed in the gap between preview and confirm. `recut.day_changed_
            // concurrently` is the same handler catching `SlotOverlapException` out of
            // `IEventRepository.ReplaceDayAsync` itself - a claim that landed in the narrower gap
            // between the staleness check and this day's own write. Both are the same client remedy
            // `availability.day_changed_concurrently` already documents: reload and retry, not resend.
            "availability.day_has_bookings" or "availability.day_changed_concurrently"
                or "recut.stale" or "recut.day_changed_concurrently" => StatusCodes.Status409Conflict,
            // `22-20` widens this arm to the same shape for two later use cases:
            // `recut.worker_not_on_a_calendar` and `recut.worker_has_no_schedule` (and
            // `availability.worker_has_no_schedule`, the manual-day-edit sibling
            // `RecutErrors.WorkerHasNoSchedule`'s own remarks point at directly) are none of them a
            // missing resource - the worker id is fine - and none of them a race - nothing about
            // "has this worker joined a calendar" or "does this worker have a schedule" can have "just
            // changed" the way a booking count can. They are a request that cannot be carried out
            // until the tenant finishes configuring that worker, the identical caller-can-fix-it-by-
            // configuring-first shape `availability.worker_not_on_calendar` already has this arm for.
            "availability.invalid_day_boundary" or "availability.worker_not_on_calendar"
                or "availability.worker_has_no_schedule" or "recut.worker_not_on_a_calendar"
                or "recut.worker_has_no_schedule" => StatusCodes.Status400BadRequest,
            "availability.calendar_not_found" => StatusCodes.Status404NotFound,
            // `20-06`. The public surface's two failures are one status as well as one message: a
            // 403 for the origin case would tell a page that the tenant it named exists, which is
            // precisely what PublicBookingErrors refuses to say in words.
            "booking.surface_not_found" or "booking.origin_not_allowed" => StatusCodes.Status404NotFound,
            // `20-04`'s operator-facing codes, which had no HTTP surface until this item gave them
            // one. 403 rather than 404 for a permission failure: the caller is an authenticated
            // operator of a known tenant, so "you may not" is a thing they are entitled to be told.
            // `22-20` folds in `recut.forbidden` and `worker_slots.forbidden` - two later use cases
            // (`20-15`, `20-16`) that reused this file's own permission-refusal vocabulary rather than
            // inventing their own; the reasoning is unchanged.
            "booking.forbidden" or "availability.forbidden" or "configuration.forbidden"
                or "recut.forbidden" or "worker_slots.forbidden" => StatusCodes.Status403Forbidden,
            "booking.not_found" or "configuration.not_found" => StatusCodes.Status404NotFound,
            // `22-20`. A worker id that does not resolve in this tenant - the same "does not exist,
            // or belongs to someone else" vagueness `ConfigurationErrors.NotFound`'s own remarks give
            // for the identical cross-tenant-leak reason, kept as its own arm because these two
            // producers are worker-specific rather than routed through that generic "what" parameter.
            "recut.worker_not_found" or "worker_slots.worker_not_found" => StatusCodes.Status404NotFound,
            // `22-20`. `GET /workers/{id}/schedule` when the worker exists but has never been given a
            // schedule - `ConfigurationErrors.NoSchedule`'s own remarks are explicit that this is a
            // real, common state distinct from the worker not existing at all (the arm above), and a
            // GET whose target sub-resource simply is not there yet is the same shape
            // `booking.calendar_not_found` already gives any other missing resource.
            "configuration.no_schedule" => StatusCodes.Status404NotFound,
            // A state the caller can see and act on, not a fault - and 409 rather than 400 because
            // nothing about the request was malformed; the world moved.
            "booking.invalid_state" or "booking.concurrency_conflict" => StatusCodes.Status409Conflict,
            "configuration.invalid" or "provisioning.invalid" => StatusCodes.Status400BadRequest,
            // `22-20`. `RecutPreviewHandler`/`RecutConfirmHandler`'s own three bounds checks on the
            // `From` field they both take - before today, at or past the schedule's cursor, or past
            // the horizon - are each a plain comparison against state read fresh on every call. None
            // of them detects that a value changed between two reads the way `recut.stale` and
            // `recut.day_changed_concurrently` above do; a retry with the identical `From` fails the
            // identical way a moment later, which is exactly the 400 shape, not 409's.
            //
            // This item's own backlog entry flagged `recut.not_a_regression` as a 409 candidate
            // alongside the two genuine races, on the grounds that all three read as "the world
            // moved". Reading the handlers says otherwise: `recut.stale` recomputes a fingerprint and
            // compares it, and `recut.day_changed_concurrently` is caught out of an actual write
            // throwing `SlotOverlapException` - both are *detections* of a change between two points
            // in time. `From >= schedule.MaterializeFrom` is not that; it is just as true or false on
            // the very first call as on any later one; nothing about the request "used to be valid".
            // It belongs with the other two bounds checks, not with the races.
            //
            // `recut.missing_decision` (a booking in range with no cancel-or-keep decision attached)
            // is the same "well-formed but incomplete" shape `booking.invalid_phone`'s own comment
            // gives - no retry of this exact body succeeds until the missing field is added.
            // `recut.invalid` is the identical caller mistake one layer up, at `ToDecision`: an
            // unrecognised decision string. `worker_slots.invalid_range` joins them for `To < From` -
            // no different in kind from `availability.invalid_day_boundary`'s own malformed-range
            // check two arms up, just a second endpoint with its own producer.
            "recut.from_before_today" or "recut.not_a_regression" or "recut.horizon_before_from"
                or "recut.missing_decision" or "recut.invalid" or "worker_slots.invalid_range" =>
                StatusCodes.Status400BadRequest,
            // `20-13`. 409, not 400: the request was well-formed and the worker exists - what refuses
            // it is a state the deletion rule itself protects (the same "the world moved" reasoning
            // booking.invalid_state's own comment gives), and deactivation is the alternative action
            // this response's own message points the caller at.
            "configuration.worker_has_booking_history" => StatusCodes.Status409Conflict,
            // `20-07`/`22-04`. Before per-site resolution this was a single deployment-wide fault
            // (mapped to 500); now it is per call - this site's own tenant is not provisioned, or is
            // not configured with exactly one published calendar - the same "does not confirm what
            // exists to a caller not entitled to know" reasoning booking.surface_not_found's own
            // comment gives, and this route already sits behind a proven credential so there is no
            // stranger-enumeration concern driving vagueness beyond that.
            "chat_module_task.not_configured" => StatusCodes.Status404NotFound,
            "chat_module_task.not_found" => StatusCodes.Status404NotFound,
            // 409, not 404: the task exists and the request was well-formed - it simply already
            // finished. The same reasoning booking.slot_unavailable's own comment gives.
            "chat_module_task.already_complete" => StatusCodes.Status409Conflict,
            "chat_module_task.kind_mismatch" or "chat_module_task.invalid_reply_value" =>
                StatusCodes.Status400BadRequest,
            // `20-12`'s own contacts-report code, the same 403 shape as configuration.* above - an
            // operator-facing failure an authenticated caller is entitled to see the reason for, not
            // a fault. This arm used to also carry `access.forbidden`/`access.not_found`/
            // `access.invalid`/`access.account_owner_requires_contact_access` from
            // `AccessControlErrors`; `22-05` deleted that producer along with the rest of this
            // product's identity model, and `22-15` removed the now-unreachable arms rather than
            // leave a mapping that reads as evidence something still produces them.
            "contacts.forbidden" => StatusCodes.Status403Forbidden,
            // 2026-09-01: PublicBookingApiGate's own kill switch. 403, not the 404 that
            // booking.surface_not_found/booking.origin_not_allowed use two cases above - those hide a
            // caller-specific fact (whether a tenant/origin exists); this refusal is identical for
            // every caller, so a 404 would only risk reading as a stale route rather than a deliberate
            // one. See PublicBookingApiGate's own remarks for the full reasoning.
            "booking.public_api_disabled" => StatusCodes.Status403Forbidden,
            // `22-11`. Provisioning's own vocabulary - this route sits behind
            // IModuleProvisioningAuthenticator rather than an operator identity (ModuleRegistrationEndpoints'
            // own remarks), so there is no enumeration concern shaping these toward vagueness.
            "chat_module_registration.tenant_not_found" or "chat_module_registration.not_found" =>
                StatusCodes.Status404NotFound,
            // 409, not 400: the request was well-formed and the tenant exists - a second registration
            // for an already-registered tenant is a caller mistake with its own remedy (rotate), the
            // same "the world moved" reasoning booking.invalid_state's own comment gives.
            "chat_module_registration.already_registered" => StatusCodes.Status409Conflict,
            "chat_module_registration.invalid_credential"
                // `22-17`: RegisterChatModuleHandler's own tenant-auto-provisioning step failed on
                // its input - the same caller-mistake shape as invalid_credential right above.
                or "chat_module_registration.tenant_provisioning_failed" => StatusCodes.Status400BadRequest,
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
