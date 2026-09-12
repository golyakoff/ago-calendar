using Ago.Calendar.Api.Auth;
using Ago.Calendar.Api.Realtime;
using Ago.Calendar.Application.Realtime;
using Ago.Platform.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Calendar.Api.Hubs;

/// <summary>
/// `25-63`: this product's first SignalR hub - authenticated by the operator's JWT, tenant-scoped,
/// built on `Ago.Platform.Realtime` the same way `Ago.Chat.Api.Hubs.OperatorHub` already is (the
/// shared platform package, not a second implementation of the same idea -
/// docs/architecture/repositories.md). Push-only: it defines no hub method at all, because it has no
/// client-to-server call to make. `OperatorHub` earns its many methods (join, send, history, presence)
/// because a conversation is a two-way channel an operator actively drives; the pending-bookings queue
/// this hub exists for is a plain, already-permission-checked REST read
/// (<c>GetPendingBookingsForTenantHandler</c>) that the console re-calls on its own - this hub's only
/// job is to tell it *when* to call again, which needs nothing from the client but the connection
/// itself.
///
/// <para><b>Tenant-scoped, not operator-scoped - the one deliberate departure from `OperatorHub`'s own
/// shape, and why.</b> `OperatorHub` registers a connection under the operator's own principal
/// (`Ago.Chat.Application.Realtime.PrincipalKeys.ForOperator`) because its pushes name a specific
/// recipient (the operator a conversation was assigned to). This hub's one push has no such recipient
/// - "the pending queue changed" is true for every operator of the tenant equally - so it registers
/// under <see cref="PrincipalKeys.ForTenant"/> instead. See that class's own remarks for why this also
/// avoids needing a per-tenant operator directory this product does not otherwise carry.</para>
///
/// <para><b>Authenticated by <see cref="CalendarClaims.OperatorPolicy"/>, the single scheme this
/// product already has.</b> `Ago.Calendar.Api.Auth.AuthenticationSetup`'s own "one scheme, not two"
/// remarks explain why there is no `[Authorize(AuthenticationSchemes: ...)]` to name here the way
/// `OperatorHub` names `JwtSchemes.Operator` - every authenticated caller in this product is an
/// operator, so the policy alone is the whole authorization surface.</para>
///
/// <para><b>No drain check, unlike `OperatorHub`'s own `3-06` one - deliberately, for now.</b>
/// `Ago.Platform.Realtime.ConnectionDrainCoordinator` is not registered in this host's own
/// <c>Program.cs</c>; see that registration site's own remarks for the real, found-live reason
/// (a `WebApplicationFactory`-based test-host teardown incompatibility, not a design decision about
/// what this hub should do). Without a coordinator ever calling `DrainState.MarkDraining()`, a check
/// here would read a flag that can never become true - dead code, not a safety net.</para>
/// </summary>
[Authorize(Policy = CalendarClaims.OperatorPolicy)]
public sealed class CalendarOperatorHub(
    CalendarHubConnectionRegistration connectionRegistration,
    CalendarConsoleOriginValidator consoleOrigin) : Hub
{
    /// <summary>Same origin-check wiring as `Ago.Chat.Api.Hubs.OperatorHub.OnConnectedAsync` - see
    /// its own comment for the `5-18` incident this exists to prevent (a CORS-refused origin
    /// succeeding a SignalR handshake anyway, because a WebSocket upgrade never runs a CORS
    /// preflight).</summary>
    public override async Task OnConnectedAsync()
    {
        if (!consoleOrigin.IsAllowed(Context))
        {
            Context.Abort();
            return;
        }

        var tenantId = Context.User!.GetTenantId();
        var principal = PrincipalKeys.ForTenant(tenantId);
        await connectionRegistration.OnConnectedAsync(new ConnectionId(Context.ConnectionId), principal, Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = Context.User!.GetTenantId();
        var principal = PrincipalKeys.ForTenant(tenantId);
        await connectionRegistration.OnDisconnectedAsync(new ConnectionId(Context.ConnectionId), principal);

        await base.OnDisconnectedAsync(exception);
    }
}
