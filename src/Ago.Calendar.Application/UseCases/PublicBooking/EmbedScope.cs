using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PublicBooking;

/// <summary>
/// What a public request turned out to be about, once its public key and (optionally) its calendar id
/// have been resolved and its origin has been checked against <i>that</i> tenant.
/// </summary>
public readonly record struct EmbedScope(Tenant Tenant, BookingCalendar? Calendar);

/// <summary>
/// The preamble every unauthenticated read on the booking surface performs, in one place.
///
/// <para><b>This is where layer 2 lives for the read side</b> - the in-app, per-tenant origin check
/// `5-01` proved is the real multi-tenant boundary, because layer 1 can only say that <i>some</i>
/// tenant approved the origin. Three handlers call it, and having them share it is the point: a
/// per-handler copy is three chances for the fourth handler to forget, which is precisely how
/// `5-01`'s own note describes the shape of every cross-tenant bug.</para>
///
/// <para><b>Not named <c>*Handler</c>, and not a use case.</b> It orchestrates nothing a caller asked
/// for; it resolves the subject of somebody else's use case. Naming it <c>Handler</c> would put it
/// under a convention test that exists to describe entry points (<c>UseCaseConventionTests</c>), and
/// a reviewer scanning for "what can this product be asked to do" would find a row that answers
/// nothing.</para>
/// </summary>
public sealed class EmbedScopeResolver(ITenantRepository tenants, IBookingCalendarRepository calendars)
{
    public async Task<Result<EmbedScope>> ResolveAsync(
        string publicKey, Guid? calendarId, string? origin, CancellationToken cancellationToken)
    {
        TenantPublicKey key;
        try
        {
            key = new TenantPublicKey(publicKey ?? string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            // A malformed key is a caller typing badly, not a bug. Reported as "no such surface" for
            // the same reason every other failure here is - see PublicBookingErrors.
            return PublicBookingErrors.NotFound();
        }

        var tenant = await tenants.FindByPublicKeyAsync(key, cancellationToken);
        if (tenant is null)
        {
            return PublicBookingErrors.NotFound();
        }

        // Layer 2. See the type's own remarks, and OriginPolicy for why a *missing* Origin is not a
        // rejection.
        if (!OriginPolicy.IsAcceptable(tenant, origin))
        {
            return PublicBookingErrors.OriginNotAllowed();
        }

        if (calendarId is not { } id)
        {
            return Result<EmbedScope>.Success(new EmbedScope(tenant, null));
        }

        var calendar = await calendars.GetByIdAsync(new CalendarId(id), cancellationToken);

        // Unpublished is reported exactly like absent, and a calendar belonging to another tenant is
        // reported exactly like absent - the same collapse `BookEventHandler` already performs, for
        // the same reason.
        if (calendar is null || !calendar.IsPublished || calendar.TenantId != tenant.Id)
        {
            return PublicBookingErrors.NotFound();
        }

        return Result<EmbedScope>.Success(new EmbedScope(tenant, calendar));
    }
}

/// <summary>
/// The one statement of what layer 2 does with an <c>Origin</c> header, so that the read side and
/// `20-03`'s booking write cannot drift into two different answers.
/// </summary>
public static class OriginPolicy
{
    /// <summary>
    /// <b>A present-but-wrong origin is rejected; an absent origin is not.</b> That asymmetry is the
    /// decision, and it differs from `5-01`'s chat case on purpose.
    ///
    /// <para>AGO Chat's visitor-session endpoint has exactly one legitimate caller - a browser on a
    /// customer's page - so a request with no <c>Origin</c> is already anomalous there. This product's
    /// booking surface is deliberately unauthenticated and deliberately not widget-only: `21-01` will
    /// reach it from a channel adapter with no browser anywhere in the path, and the boundary review
    /// that settled `20-06`'s embed question did so precisely because booking must work where there is
    /// no widget at all. Requiring an <c>Origin</c> would therefore ban the product's own second
    /// channel to gain nothing, because <c>Origin</c> is trivially forgeable by any non-browser
    /// caller - `5-01` says so in as many words.</para>
    ///
    /// <para><b>So state the property honestly rather than overclaiming it.</b> What layer 2 stops is
    /// the attack a browser can actually mount: a page at an origin approved for tenant A using it
    /// against tenant B. The browser attaches the real <c>Origin</c> itself and a page cannot remove
    /// it, so within a browser this is a real boundary. It is not, and cannot be, a limit on who may
    /// book from a script - that limit is `20-03`'s rate limiters and the fact that a published
    /// calendar is public by definition.</para>
    /// </summary>
    public static bool IsAcceptable(Tenant tenant, string? origin)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return string.IsNullOrWhiteSpace(origin) || tenant.Allows(origin);
    }
}
