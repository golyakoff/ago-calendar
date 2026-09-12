using Ago.Calendar.Api.Cors;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Calendar.Api.Realtime;

/// <summary>
/// `25-63`: the same question `Ago.Chat.Api.Cors.ConsoleOriginValidator` asks for its own operator
/// hub ("may this browser open this hub connection"), answered here against this product's own
/// <see cref="ConsoleOrigins"/> - the one origin list this product already has (unlike `ago-chat`,
/// which keeps a tenant's own widget origins and the console's own configuration apart;
/// <see cref="ConsoleOrigins"/> is that second list already, and there is no first one here for a hub
/// connection to be confused with).
///
/// <para><b>Why a hub needs this at all when <see cref="Cors.TenantOriginCorsPolicyProvider"/>
/// already exists.</b> `Ago.Chat.Api.Hubs.OperatorHub`'s own doc comment states the live incident this
/// guards against: a browser's CORS preflight never runs for a SignalR WebSocket upgrade, so a CORS
/// policy answering "no" produces nothing here to check - the handshake simply succeeds against an
/// origin CORS would have refused. This is the same check, run inside <c>OnConnectedAsync</c> instead
/// of the HTTP pipeline, for the one surface CORS cannot reach.</para>
/// </summary>
public sealed class CalendarConsoleOriginValidator(ConsoleOrigins consoleOrigins)
{
    public bool IsAllowed(HubCallerContext context)
    {
        var origin = context.GetHttpContext()?.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // No cross-origin claim to verify - a same-origin caller or a non-browser client sends
            // none, and a browser cannot omit it (Ago.Chat.Api.Cors.ConsoleOriginValidator's own
            // identical reasoning).
            return true;
        }

        return consoleOrigins.Contains(origin);
    }
}
