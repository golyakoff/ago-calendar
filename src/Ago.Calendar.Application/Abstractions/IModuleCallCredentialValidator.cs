namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-02`: proves a call to `/api/v1/module-tasks*` is really from `Ago.Chat.*`, for the site it
/// claims - the port `ChatModuleTaskEndpoints`'s own remarks named as genuinely missing ("no
/// service-to-service authentication exists in either direction yet"). A port here, not a static
/// method or an inline check in the endpoint delegate: the dependency rule says nothing about crypto
/// specifically, but the *mechanism* (an HMAC-signed, short-lived assertion today) is exactly the kind
/// of decision this codebase always puts behind an interface - it could become mTLS or a different
/// signing scheme later without <c>Ago.Calendar.Api</c> changing a line, the identical reasoning
/// <see cref="IPhoneVerificationProofTokenGenerator"/> already applies to its own bearer credential.
///
/// <para><b>Implemented in <c>Ago.Calendar.Infrastructure.Postgres</c></b>, not a new project of its
/// own - the same call this codebase already made for
/// <c>PhoneVerificationProofTokenGenerator</c>: this adapter's real dependencies are
/// <c>System.Security.Cryptography</c> (BCL) and, since `22-04`, the per-tenant registry stored in
/// this product's own database - not a whole project for one class.</para>
///
/// <para><b>`22-04`: async, not synchronous.</b> Validating a credential now means resolving which
/// tenant's own secret to check it against - <see cref="Ago.Calendar.Domain.ChatModuleRegistration"/>,
/// a database read - before the signature itself can be verified. Before this item, the whole
/// deployment checked against one secret from configuration and the method never needed to await
/// anything; that shortcut disappears with the deployment-wide secret it depended on.</para>
/// </summary>
public interface IModuleCallCredentialValidator
{
    /// <summary>Validates the raw <c>X-Ago-Module-Credential</c> header value (may be <see
    /// langword="null"/> or empty when the header was not sent). Never throws - every outcome is a
    /// value in <see cref="ModuleCallCredentialResult"/>.</summary>
    Task<ModuleCallCredentialResult> ValidateAsync(string? headerValue, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <param name="IsAuthenticated">
/// <see langword="false"/> means "refuse this call with 401" - a missing header, a malformed one, a bad
/// signature, an expired one, or one naming a tenant with no <see cref="Ago.Calendar.Domain.ChatModuleRegistration"/>
/// of its own are all this outcome. `22-04` removes the one case that used to be exempt (an absent
/// header treated as pre-migration traffic while a statically configured tenant answered regardless):
/// per-site resolution has no deployment-wide tenant left to fall back to, so a call with nothing to
/// authenticate has nothing to resolve into either, and always refuses.
/// </param>
/// <param name="SiteId">
/// The site id the credential proved, whenever <paramref name="IsAuthenticated"/> is
/// <see langword="true"/> - always present in that case now that there is no header-optional rollout
/// window left (see <see cref="IsAuthenticated"/>'s own remarks). This is the tenant's own id -
/// see <see cref="Ago.Calendar.Domain.ChatModuleRegistration"/>'s own remarks on why a chat site id and
/// a <c>TenantId</c> are the same value once a tenant is registered this way.
/// </param>
public readonly record struct ModuleCallCredentialResult(bool IsAuthenticated, Guid? SiteId);
