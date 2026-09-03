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
/// <c>PhoneVerificationProofTokenGenerator</c>: this adapter's only real dependency is
/// <c>System.Security.Cryptography</c> (BCL) plus configuration, not Postgres, but a whole project for
/// one class with no external dependency of its own would be the premature separation
/// `clean-architecture.md` warns against - the existing infrastructure project already referenced from
/// the composition root is where this codebase puts a small crypto adapter, not a project per
/// concept.</para>
/// </summary>
public interface IModuleCallCredentialValidator
{
    /// <summary>Validates the raw <c>X-Ago-Module-Credential</c> header value (may be <see
    /// langword="null"/> or empty when the header was not sent). Never throws - every outcome, including
    /// "no header, and this deployment does not yet require one", is a value in
    /// <see cref="ModuleCallCredentialResult"/>.</summary>
    ModuleCallCredentialResult Validate(string? headerValue, DateTimeOffset now);
}

/// <param name="IsAuthenticated">
/// <see langword="false"/> means "refuse this call with 401" - covering both a required credential that
/// is missing and one that is present but wrong (bad signature, malformed, or expired). A credential
/// that is missing while this deployment's rollout policy has not yet made one mandatory is the one
/// case that is *not* a refusal - see the implementation's own remarks on the accepting-but-warning
/// rollout window.
/// </param>
/// <param name="SiteId">
/// The site id the credential proved, when <paramref name="IsAuthenticated"/> is <see langword="true"/>
/// and a credential was actually presented and verified. <see langword="null"/> in the one case a call
/// is authenticated with no site id to check: the accepting-but-warning window, where no header was
/// sent at all and this deployment has not yet made one mandatory. A caller that receives
/// <see langword="null"/> here skips the site cross-check rather than treating it as a match - see
/// <c>ChatModuleTaskEndpoints</c>'s own remarks for exactly where that check happens.
/// </param>
public readonly record struct ModuleCallCredentialResult(bool IsAuthenticated, Guid? SiteId);
