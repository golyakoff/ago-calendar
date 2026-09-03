namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-11`: proves a call to `/api/v1/module-registrations*` is really from `Ago.Chat.*`'s own
/// deployment - the provisioning-side counterpart to <see cref="IModuleCallCredentialValidator"/>,
/// deliberately a <b>different</b> mechanism rather than a "purpose" field smuggled into that one's
/// wire format. The two protect different things: <see cref="IModuleCallCredentialValidator"/> proves
/// a call is for a specific, already-registered site, by looking up that site's own secret - which is
/// exactly the fact that does not exist yet the first time a site is ever registered. Provisioning has
/// to be provable before any per-site row exists, so it is checked against one secret shared by the two
/// deployments' own operators - the same person configuring `Ago.Chat.*`'s `EnableModuleForSite` and
/// this deployment's own environment, out of band, the identical manual-coordination shape
/// <see cref="Ago.Calendar.Domain.ChatModuleRegistration.Credential"/>'s own remarks already accept for
/// <c>EntryPoint</c>/<c>Credential</c>.
///
/// <para><b>A plain shared-secret compare, not a signed assertion.</b> <see cref="IModuleCallCredentialValidator"/>'s
/// own HMAC scheme earns its complexity from a real threat this one does not share: a per-message,
/// per-conversation channel where the caller (a chat operator's own site) must be provably one of many
/// sites, cross-checked against a request body. Provisioning is a low-volume, admin-to-admin channel
/// with exactly one legitimate caller (the one deployment of `Ago.Chat.*` this product's own operator
/// configured an entry point for) - a raw shared secret, compared in constant time over TLS, is the
/// same honest, minimal-machinery choice `adr/0094` made for the deployment-wide secret it accepted as
/// this product's *first* answer to authentication, scoped down here to a channel that never grew past
/// the threat model that answer was actually adequate for. Reusing the signed-assertion format instead
/// would be exactly the "fourth hand-kept copy" `22-11`'s own backlog item warns against - a header
/// carrying a raw secret with one comparison rule is a second wire agreement, not a variant of the
/// first.</para>
///
/// <para><b>Synchronous, unlike <see cref="IModuleCallCredentialValidator.ValidateAsync"/>.</b> No
/// database read is needed - the secret this compares against is deployment-wide configuration
/// (<c>ModuleProvisioningOptions</c>), read once at startup, not a per-tenant row.</para>
/// </summary>
public interface IModuleProvisioningAuthenticator
{
    /// <summary>Validates the raw <c>X-Ago-Module-Provisioning-Secret</c> header value (may be
    /// <see langword="null"/> or empty when the header was not sent). Never throws.</summary>
    bool Authenticate(string? headerValue);
}
