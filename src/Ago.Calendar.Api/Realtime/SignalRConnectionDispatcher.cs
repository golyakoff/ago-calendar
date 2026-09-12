using System.Text.Json;
using Ago.Calendar.Api.Hubs;
using Ago.Platform.Abstractions;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Calendar.Api.Realtime;

/// <summary>
/// `25-63`: the host-supplied half of `Ago.Platform.Realtime`'s own <see cref="ILocalConnectionDispatcher"/>
/// port - the one place that actually knows SignalR. Simpler than `Ago.Chat.Api.Realtime.SignalRConnectionDispatcher`,
/// and genuinely so rather than trimmed for looks: that class routes a connection id to one of *two*
/// hubs (visitor or operator), decided by a principal-key prefix
/// (`Ago.Chat.Application.Realtime.PrincipalKeys.KindOf`), because `ago-chat` runs two hubs on one
/// host. This product runs exactly one (`CalendarOperatorHub` - `Ago.Calendar.Api.Auth.AuthenticationSetup`'s
/// own "one scheme, not two" remarks give the identical reason this product has no second hub to
/// route to), so there is no kind to distinguish and nothing for
/// <c>Ago.Calendar.Application.Realtime.PrincipalKeys</c> to expose for this purpose.
///
/// <para>Deserializes the payload into <see cref="JsonElement"/>, not a concrete DTO type, for the
/// identical reason `ago-chat`'s own dispatcher does: this class does not need to know what a
/// <c>BookingPendingStateChanged</c> is, only that it can hand the client a structurally faithful JSON
/// value under whatever method name the publisher chose.</para>
///
/// <para>A connection id this process no longer tracks (already disconnected) is not an error - the
/// registry's own "advice, not truth" contract (`realtime.md`) working exactly as designed for a
/// stale entry, reported through <see cref="DispatchOutcome.ConnectionNotLocal"/> rather than thrown.</para>
/// </summary>
public sealed class SignalRConnectionDispatcher(
    IHubContext<CalendarOperatorHub> hub, LocalConnectionTracker tracker) : ILocalConnectionDispatcher
{
    public async Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
    {
        if (tracker.TryGet(connectionId) is null)
        {
            return DispatchOutcome.ConnectionNotLocal;
        }

        var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
        await hub.Clients.Client(connectionId.Value).SendAsync(method, payload, cancellationToken);

        // "Handed to SignalR for a connection this process holds" - deliberately not a claim that the
        // client received it, which no transport here can promise synchronously (the identical
        // caveat `Ago.Chat.Api.Realtime.SignalRConnectionDispatcher`'s own remarks state).
        return DispatchOutcome.Delivered;
    }
}
