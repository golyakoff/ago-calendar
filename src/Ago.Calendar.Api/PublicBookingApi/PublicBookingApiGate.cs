using Ago.Calendar.Api.Http;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Api.PublicBookingApi;

/// <summary>
/// The lockdown itself, as an <see cref="IEndpointFilter"/> attached to every route `20-10`'s public
/// booking surface exposes - <c>BookingEndpoints</c>'s <c>POST .../book</c> and
/// <c>PhoneVerificationEndpoints</c>'s initiate/confirm group. See <see cref="PublicBookingApiOptions"/>
/// for why the surface is closed at all.
///
/// <para><b>An endpoint filter, not a change inside each handler.</b> The filter runs before model
/// binding resolves a handler's own parameters and before any of the handler's own code - including
/// <c>BookEventHandler</c>'s database reads and the phone-verification handlers' own rate limiting -
/// ever executes. That is the point: closing this surface must not depend on every handler
/// independently remembering to check a flag first, the same "one place enforces it" reasoning that
/// keeps a security check from being a convention several call sites could each get slightly wrong.</para>
///
/// <para><b>No exception for any caller, including AGO's own platform-owner role
/// (adr/0032's cross-tenant staff concept) - considered and explicitly rejected.</b> This filter never
/// inspects <see cref="HttpContext.User"/> at all, which is not an oversight: these routes are
/// unauthenticated by design (<c>BookingEndpoints</c>'s own remarks - there is no token to check and
/// nothing to check it against), so a role-based bypass here would have to be built from nothing, and
/// building it would reopen exactly the question this lockdown exists to leave unopened - what
/// "publishing the booking API" should even look like. A caller who is staff reaches this filter
/// exactly like a stranger does.</para>
///
/// <para><b>403, not 404.</b> This codebase already uses 404 for one class of public-surface failure -
/// <c>booking.surface_not_found</c>/<c>booking.origin_not_allowed</c> in <see cref="ErrorExtensions"/> -
/// but that 404 is specifically an information-hiding move: it stops a stranger from learning whether a
/// given tenant or origin exists. Nothing here is tenant-specific or caller-specific; every request to
/// this surface gets the identical refusal regardless of which calendar, which origin, or which phone
/// number it names, so there is no fact a 404 would be protecting by pretending the route is absent.
/// What a 404 would risk instead is exactly what this item's own brief warns against: a bare 404 from a
/// route that plainly exists in this file reads as a stale link or a typo to anyone who goes looking,
/// which is a worse outcome for a deliberate, reversible decision than a 403 that says, in a named
/// error code, that the route exists and is refusing on purpose. 403 is also the status this codebase
/// already reserves for exactly that shape - "you may not," stated plainly - for
/// <c>booking.forbidden</c>/<c>availability.forbidden</c>/<c>configuration.forbidden</c> in
/// <see cref="ErrorExtensions"/>, even though every one of those is an authenticated-operator case and
/// this one is not: the caller here holds no permission to be denied, but the shape of the message - a
/// route that exists and refuses - is identical, and reusing the established status keeps that
/// vocabulary consistent rather than inventing a fourth meaning for a rarely used code.</para>
/// </summary>
public sealed class PublicBookingApiGate(PublicBookingApiOptions options) : IEndpointFilter
{
    private static readonly Error Disabled = new(
        "booking.public_api_disabled",
        "This booking API is not open to the public right now.");

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);

        return options.Enabled
            ? next(context)
            : ValueTask.FromResult<object?>(Disabled.ToProblem(context.HttpContext));
    }
}
