using System.Security.Claims;
using System.Text.Json;

namespace Ago.Calendar.Api.Auth;

/// <summary>
/// `24-17`: this product's own copy of `ago-chat`'s `Ago.Chat.Api.Auth.PlatformOwnerRealmRole` -
/// "does this validated token carry the `platform-owner` realm role", read the identical way: the
/// raw `realm_access` claim Keycloak signs, parsed for a `roles` array containing the exact string
/// <see cref="PlatformOwnerRequirement.RealmRoleName"/>.
///
/// <para><b>Why copying rather than sharing is right here, and not merely expedient.</b> `adr/0027`
/// already decided this product carries its own copy of Keycloak-token-derived mechanisms rather than
/// depending on a package shared with `ago-chat` across the repository boundary - `AuthenticationSetup.cs`
/// says so of the token flow itself ("this product's own copy"), and the platform-owner realm role is
/// the identical kind of fact: read off the same Keycloak realm (`adr/0027`: "one Keycloak login, two
/// local `operators` rows, one per product"), by a claim Keycloak signs and neither product's database
/// can influence, so there is nothing product-specific about the *reading* for a shared package to
/// earn its keep over - only the discipline of not letting the two copies drift, which is what keeping
/// this file's shape identical to its `ago-chat` original is for.</para>
///
/// <para><b>Why this is new here at all.</b> `ago-calendar` has never had a platform-owner concept
/// before this item - `adr/0032` introduced it in `ago-chat` alone. `24-17` is the first thing in this
/// product that needs an owner-only surface (this file's own caller, `OwnerTenantScopeEndpoints`), so
/// this is that concept's first crossing into this repository, not a pre-existing mechanism being
/// wired up.</para>
/// </summary>
internal static class PlatformOwnerRealmRole
{
    internal const string RealmAccessClaimType = "realm_access";

    private const string RolesPropertyName = "roles";

    public static bool IsHeldBy(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        foreach (var claim in user.FindAll(RealmAccessClaimType))
        {
            if (ContainsOwnerRole(claim.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsOwnerRole(string realmAccessJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(realmAccessJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty(RolesPropertyName, out var roles)
                || roles.ValueKind is not JsonValueKind.Array)
            {
                return false;
            }

            foreach (var role in roles.EnumerateArray())
            {
                if (role.ValueKind is JsonValueKind.String
                    && string.Equals(role.GetString(), PlatformOwnerRequirement.RealmRoleName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
