using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;

namespace Ago.Calendar.Api.Realtime;

/// <summary>
/// `25-63`: the register/unregister half of <c>CalendarOperatorHub</c>'s own wiring, factored out the
/// same way `Ago.Chat.Api.Realtime.HubConnectionRegistration` is - directly testable with plain
/// <see cref="ConnectionId"/>/<see cref="PrincipalKey"/> values, no <c>HubCallerContext</c> needed.
///
/// <para><b>Simpler than its `ago-chat` counterpart, and that is a real difference, not a
/// trim.</b> <c>HubConnectionRegistration.OnDisconnectedAsync</c> there returns whether the
/// disconnecting principal has zero connections left anywhere, because `4-04`'s presence sweep needs
/// to know. This hub tracks no presence at all - its one principal
/// (<c>Ago.Calendar.Application.Realtime.PrincipalKeys.ForTenant</c>) names a tenant's whole
/// connection set, not one operator's, and nothing downstream ever asks "is this tenant now fully
/// disconnected" - so there is no caller for that answer here, and adding it back would be exactly
/// the premature generalisation CLAUDE.md warns a platform-adjacent layer against.</para>
/// </summary>
public sealed class CalendarHubConnectionRegistration(
    IConnectionRegistry connectionRegistry, LocalConnectionTracker connectionTracker, NodeId currentNode)
{
    public Task OnConnectedAsync(ConnectionId connectionId, PrincipalKey principal, CancellationToken cancellationToken)
    {
        connectionTracker.Add(connectionId, principal);
        return connectionRegistry.RegisterAsync(connectionId, currentNode, principal, cancellationToken);
    }

    public Task OnDisconnectedAsync(ConnectionId connectionId, PrincipalKey principal)
    {
        connectionTracker.Remove(connectionId);
        // Deliberately CancellationToken.None, not the caller's token - Ago.Chat.Api's own
        // HubConnectionRegistration.OnDisconnectedAsync gives the identical reason: by the time a hub
        // calls this from OnDisconnectedAsync, Context.ConnectionAborted may already be signalled, and
        // this cleanup must still complete rather than being cancelled by the very disconnect that
        // triggered it.
        return connectionRegistry.UnregisterAsync(connectionId, currentNode, principal, CancellationToken.None);
    }
}
