using System.Text.Json;
using Ago.Calendar.Application.UseCases.ResolveBookingPendingDelivery;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `25-63`: reacts to this product's own <see cref="BookingPendingStateChanged"/> by handing off to
/// the platform's fan-out path (<see cref="ResolveBookingPendingDeliveryTargetsHandler"/>) - the
/// identical shape `ago-chat`'s own <c>ConnectionFanoutConsumer</c>/<c>TeamChatFanoutConsumer</c>
/// establish for the same job in that product: a domain event this product staged itself (unlike
/// <see cref="ContactVisibilityChangedConsumer"/> and its siblings, which project a *different*
/// product's event and therefore need <see cref="IInboxChecker"/>'s own durable idempotency ledger),
/// so <see cref="SubscriptionMode.Competing"/> - exactly one <c>Worker</c> replica resolves-and-publishes
/// per staged fact, not every replica.
///
/// <para><b>No inbox row, on purpose - the same reasoning those siblings' own remarks give for the
/// opposite conclusion, restated here for why it does not apply.</b> Resolving "who is connected to
/// this tenant right now" and re-publishing that fan-out has no ledger to violate: the fan-out itself
/// is ephemeral (a Redis-backed connection registry snapshot, `realtime.md`), and redelivering this
/// message re-asks the identical question against whatever is connected *now* - re-publishing it is
/// exactly as harmless as the first publish (`adr/0020`, and `ConnectionFanoutConsumer`'s own remarks
/// verbatim).</para>
/// </summary>
public sealed class BookingPendingFanoutConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<BookingPendingFanoutConsumerOptions> options,
    ILogger<BookingPendingFanoutConsumer> logger) : BackgroundService
{
    /// <summary>This consumer's own stable identity. `ago-chat`'s equivalent
    /// (`ConnectionFanoutConsumer.ConsumerName`) is `internal`, granted to its own integration-test
    /// assembly via `InternalsVisibleTo` so a real end-to-end test can compute this exact `Competing`
    /// subscription's own queue name (<c>{topic}.{ConsumerName}</c>) rather than guessing at a fixed
    /// sleep. This project's own equivalent tests (<c>ModuleQuantityGrantedWireTests</c>,
    /// <c>ModuleQuantityImpactRequestedWireTests</c>) instead hardcode the identical literal as their
    /// own local constant - the convention this file follows too, kept `private` - because
    /// `Ago.Calendar.Worker` and `Ago.Calendar.Api` both use bare top-level-statement `Program`
    /// classes in the global namespace, and `InternalsVisibleTo` from `Ago.Calendar.Worker` to
    /// `Ago.Calendar.Integration.Tests` (which already references both hosts) makes the two
    /// ambiguous (CS0433) with no clean fix short of an `extern alias` - not worth taking on for one
    /// string constant.</summary>
    private const string ConsumerName = "booking-pending-fanout";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(BookingPendingStateChanged), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<BookingPendingStateChanged>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(BookingPendingStateChanged)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ResolveBookingPendingDeliveryTargetsHandler>();

            var command = new ResolveBookingPendingDeliveryTargets(
                contract.EventId, new TenantId(contract.TenantId), contract.Status, contract.OccurredAt, contract.CorrelationId);

            await handler.HandleAsync(command, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve booking-pending delivery for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
