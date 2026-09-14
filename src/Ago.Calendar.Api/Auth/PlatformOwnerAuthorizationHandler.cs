using Microsoft.AspNetCore.Authorization;

namespace Ago.Calendar.Api.Auth;

/// <summary>
/// `24-17`: this product's own copy of `ago-chat`'s `Ago.Chat.Api.Auth.PlatformOwnerAuthorizationHandler`
/// - decides <see cref="PlatformOwnerRequirement"/> by reading the validated token's `realm_access.roles`
/// claim, and nothing else. Never queries this product's own `operators`/tenancy tables and never
/// consults `IPermissionChecker` - the identical structural boundary `adr/0032` argues for in
/// `ago-chat`, unchanged by which product's database happens to be behind the request.
/// </summary>
internal sealed class PlatformOwnerAuthorizationHandler : AuthorizationHandler<PlatformOwnerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PlatformOwnerRequirement requirement)
    {
        if (PlatformOwnerRealmRole.IsHeldBy(context.User))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(
                this, $"The token carries no '{PlatformOwnerRequirement.RealmRoleName}' realm role."));
        }

        return Task.CompletedTask;
    }
}
