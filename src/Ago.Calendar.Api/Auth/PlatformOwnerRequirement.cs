using Microsoft.AspNetCore.Authorization;

namespace Ago.Calendar.Api.Auth;

/// <summary>
/// `24-17`: this product's own copy of `ago-chat`'s `Ago.Chat.Api.Auth.PlatformOwnerRequirement` -
/// the platform owner, satisfied by the same Keycloak realm role on the same shared realm
/// (`PlatformOwnerRealmRole`'s own remarks explain why copying rather than sharing is the right shape,
/// not merely the expedient one).
/// </summary>
internal sealed class PlatformOwnerRequirement : IAuthorizationRequirement
{
    /// <summary>Must match `ago-chat`'s own `PlatformOwnerRequirement.RealmRoleName` and the
    /// `platform-owner` realm role defined in `ago-deploy`'s shared Keycloak realm import - one role,
    /// recognised identically by both products' own copies of this mechanism (`adr/0027`: "one
    /// Keycloak login").</summary>
    public const string RealmRoleName = "platform-owner";
}
