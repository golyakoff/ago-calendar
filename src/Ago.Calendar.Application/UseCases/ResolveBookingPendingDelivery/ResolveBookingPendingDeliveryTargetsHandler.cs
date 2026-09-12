using System.Text.Json;
using Ago.Calendar.Application.Realtime;
using Ago.Calendar.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ResolveBookingPendingDelivery;

/// <summary>
/// `25-63`: the product-specific half of `Ago.Chat`'s own realtime.md Fan-out path, this product's
/// first use of it - resolve *who*, decide *what payload*, hand off to
/// <see cref="INodeFanoutPublisher"/> for the generic *where*. Called by
/// <c>Ago.Calendar.Worker.BookingPendingFanoutConsumer</c> reacting to a staged
/// <see cref="BookingPendingStateChanged"/>.
///
/// <para><b>One recipient: the tenant itself, not a list of operators.</b> Unlike
/// <c>ResolveMessageDeliveryTargetsHandler</c>/<c>ResolveTeamMessageDeliveryTargetsHandler</c> in
/// `ago-chat`, both of which resolve a set of individual operator principals, this pushes to
/// <see cref="PrincipalKeys.ForTenant"/> directly - see that class's own remarks for why "every
/// operator of the tenant" has no directory to resolve here, and why keying the connection registry
/// by tenant sidesteps needing one. No permission filter either: every operator who can open
/// <c>CalendarOperatorHub</c> at all already carries `calendar-operator`'s two claims
/// (<c>CalendarClaims.OperatorPolicy</c>), and this push carries no data of its own for a permission
/// to protect - it is a bare "re-read the queue" signal, and the queue's own read
/// (<c>GetPendingBookingsForTenantHandler</c>) is where <c>Permission.BookingReject</c> is actually
/// checked, on every re-read, for the caller making it. `ago-chat`'s own team-chat broadcast sets the
/// precedent for "every operator, unconditionally, no per-recipient filter" - see
/// <c>ResolveTeamMessageDeliveryTargetsHandler</c>'s own remarks for the identical reasoning applied
/// to an actual room membership rather than a tenant-wide connection key.</para>
/// </summary>
public sealed class ResolveBookingPendingDeliveryTargetsHandler(INodeFanoutPublisher fanout)
{
    private const string Method = "PendingBookingsChanged";

    public async Task<Result> HandleAsync(ResolveBookingPendingDeliveryTargets command, CancellationToken cancellationToken)
    {
        var recipients = new[] { PrincipalKeys.ForTenant(command.TenantId) };

        var dto = new BookingPendingStateChanged(
            command.EventId, command.TenantId.Value, command.Status, command.OccurredAt, command.CorrelationId);

        // WireJsonOptions.Options, not a plain JsonSerializer.Serialize(dto) - see that type's own
        // remarks (`5-11`'s bug, restated for this product) for why a pre-serialized string handed to
        // INodeFanoutPublisher must already be camelCase.
        await fanout.PublishAsync(
            recipients, Method, JsonSerializer.Serialize(dto, WireJsonOptions.Options), command.CorrelationId, cancellationToken);

        return Result.Success();
    }
}
