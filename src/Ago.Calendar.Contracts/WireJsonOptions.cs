using System.Text.Json;

namespace Ago.Calendar.Contracts;

/// <summary>
/// `25-63`: this product's own copy of `Ago.Chat.Contracts.WireJsonOptions` - see that type's own
/// remarks for the exact bug this exists to avoid (`5-11`, found live there): a handler that
/// pre-serializes a <c>Contracts</c> DTO into a JSON *string* for <c>INodeFanoutPublisher.PublishAsync</c>
/// must use the hub protocol's own naming policy (`camelCase`, SignalR's default, unchanged by
/// `Ago.Calendar.Api/Program.cs`) itself - <c>Ago.Calendar.Api.Realtime.SignalRConnectionDispatcher</c>
/// re-parses that string into a <c>JsonElement</c> before handing it to SignalR, and a
/// <c>JsonElement</c> re-emits whatever property names were already baked into it, never consulting
/// the hub protocol's naming policy again. Not copied for `BookingConfirmedMapper`'s own envelope:
/// that payload never reaches a hub, only `20-05`'s future SMS consumer, so PascalCase round-trips
/// through <c>JsonSerializer.Deserialize&lt;BookingConfirmed&gt;</c> correctly as it already does.
/// </summary>
public static class WireJsonOptions
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
