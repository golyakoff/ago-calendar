using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `23-12`/`adr/0123`: reacts to `ago-chat`'s own <c>ContactVisibilityChanged</c> - this product's
/// third broker consumer, and the third instance of the identical shape
/// <see cref="RoleAssignmentsChangedConsumer"/> established first: an event this product did not
/// publish itself, staged into a local projection and committed together with the inbox record.
///
/// <para><b>One save, not two - unlike <see cref="ModuleQuantityGrantedConsumer"/>.</b> That consumer
/// needs a lock held across a read-decide-write (its own remarks explain why), because a worker quota
/// grant reads its own current value to decide what to deactivate. A rung has nothing to decide from
/// its own prior value - <see cref="IContactVisibilityProjectionStore.StageAsync"/>'s own remarks: it
/// is displayed, never counted against or compared - so this consumer takes the identical one-save
/// shape <see cref="RoleAssignmentsChangedConsumer"/> already uses instead: stage, then let the
/// inbox's own <see cref="IInboxChecker.TryRecordAndSaveAsync"/> call commit both writes
/// together.</para>
///
/// <para><b>Idempotent by construction, not merely by the inbox ledger.</b> <see cref="IContactVisibilityProjectionStore.StageAsync"/>
/// is a full replace to whatever the event's own <see cref="ContactVisibilityChangedWireContract.Rung"/>
/// says is current, never a delta - so redelivering the identical message twice stages the identical
/// value twice, and the second delivery's own inbox call additionally refuses to commit at all (a
/// duplicate <c>message_id</c> for this consumer's own name), rolling that redundant stage back with
/// it.</para>
///
/// <para><see cref="SubscriptionMode.Competing"/>, the default every per-item consumer in this system
/// uses (`messaging.md`).</para>
/// </summary>
public sealed class ContactVisibilityChangedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ContactVisibilityChangedConsumerOptions> options,
    ILogger<ContactVisibilityChangedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "calendar-contact-visibility-projection";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        // A literal, not `nameof(...)`: the topic name is `ago-chat`'s own
        // `nameof(Ago.Chat.Contracts.ContactVisibilityChanged)`, a type this project cannot reference
        // - the same reasoning RoleAssignmentsChangedConsumer's own topic constant gives.
        const string topic = "ContactVisibilityChanged";

        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ContactVisibilityChangedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize ContactVisibilityChanged payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var visibility = scope.ServiceProvider.GetRequiredService<IContactVisibilityProjectionStore>();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxChecker>();

            var tenantId = new TenantId(contract.SiteId);
            var rung = Enum.Parse<ContactVisibility>(contract.Rung);

            await visibility.StageAsync(tenantId, rung, contract.OccurredAt, cancellationToken);

            // IInboxChecker's own contract: this call commits the stage above together with the
            // inbox row, or - on a genuine duplicate message_id - commits neither
            // (RoleAssignmentsChangedConsumer's own remarks on why that is still correct rather than
            // merely harmless).
            await inbox.TryRecordAndSaveAsync(envelope.MessageId, ConsumerName, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to project ContactVisibilityChanged for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
