using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Application.UseCases.Cors;

/// <summary>Is this <c>Origin</c> header value listed by <i>any</i> tenant?</summary>
/// <param name="Origin">Verbatim from the request. Never trimmed or lowercased here - the
/// normalisation belongs to <c>Tenant</c>, which owns what a stored origin looks like.</param>
public readonly record struct CheckTenantOrigin(string Origin);

/// <summary>
/// <b>Layer 1 of `5-01`'s two-layer CORS model, adapted from "site" to "tenant".</b> Its entire job is
/// to let a legitimate embed's preflight succeed; it is <i>not</i> the tenant boundary, and
/// <see cref="ITenantRepository.AnyAllowsOriginAsync"/> says why in detail.
///
/// <para><b>The timing constraint that forces this shape, found live in `5-01` and true again here.</b>
/// A browser's <c>OPTIONS</c> preflight carries the method, the URL and the <c>Origin</c> header, and
/// never the request body or any other header's <i>value</i>. So an <c>ICorsPolicyProvider</c> cannot
/// know which tenant a request is for. `20-06` answers that by putting the tenant's public key in the
/// <b>path</b> of every public read - which means a preflight could in principle resolve the tenant
/// for those routes. It still does not, deliberately: the one route that cannot do it is `20-03`'s
/// booking <c>POST</c>, which names a calendar rather than a tenant, and a CORS policy that were
/// precise on three routes and coarse on the fourth would invite the belief that the precise ones are
/// a boundary. One rule, coarse everywhere, with the real check in the app - that is what `5-01`
/// concluded, and splitting it would be re-deriving the same mistake at a smaller scale.</para>
///
/// <para><b>No cache, and that is a decision rather than an omission.</b> `5-01`'s equivalent is
/// cached because AGO Chat has an <c>ICache</c> wired; this product deliberately does not (`20-03`
/// registers the rate limiter alone, for the reason its own <c>AddCalendarRateLimiting</c> records).
/// The read is one <c>EXISTS</c> against a GIN index, browsers cache a preflight for as long as
/// <c>Access-Control-Max-Age</c> says, and adding a cache here would buy an unmeasured saving while
/// importing `5-01`/`10-04`'s stale-negative problem - a newly approved origin stranded behind a
/// cached "no". CLAUDE.md: measure or stay silent.</para>
/// </summary>
public sealed class CheckTenantOriginHandler(ITenantRepository tenants)
{
    public async Task<bool> HandleAsync(CheckTenantOrigin query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Origin))
        {
            return false;
        }

        return await tenants.AnyAllowsOriginAsync(
            query.Origin.Trim().TrimEnd('/').ToLowerInvariant(), cancellationToken);
    }
}
