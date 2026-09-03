using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `22-05`/`adr/0093`: reacts to `ago-chat`'s own `RoleAssignmentsChanged` - the first time this
/// product has ever consumed an event it did not publish itself. Not this product's outbox: there is
/// nothing here to stage or dispatch, only a subscription against the broker `ago-chat`'s own outbox
/// dispatcher already publishes to (a shared connection - `Messaging:RabbitMq:*`, deploy-configured to
/// name the same broker/vhost `ago-chat`'s own Worker uses; see this item's own report for the
/// manifest side of that, which is out of this repository's lane).
///
/// <para><b>Idempotent by construction, not merely by the inbox ledger.</b>
/// <see cref="IRoleAssignmentProjectionStore.StageAsync"/> is a full replace to whatever the event's
/// own <see cref="RoleAssignmentsChangedWireContract.Permissions"/> says is current, never a delta -
/// so redelivering the identical message twice stages the identical values twice, and the second
/// delivery's own <see cref="IInboxChecker.TryRecordAndSaveAsync"/> call additionally refuses to
/// commit at all (a duplicate <c>message_id</c> for this consumer's own name), rolling that redundant
/// stage back with it. Either defence alone would already be enough; both exist because
/// `messaging.md` asks for the ledger as the fast path and a naturally idempotent write as the real
/// one.</para>
///
/// <para><b><see cref="Ago.Calendar.Domain.OperatorId.FromExternalSubjectId"/> is what a "delivered
/// twice" test actually has to prove nothing doubles</b> - the same subject always derives the same
/// id, so two deliveries of one event write to the exact same projection row rather than two rows
/// that happen to agree.</para>
///
/// <para><b><see cref="SubscriptionMode.Competing"/></b>, the default every per-item consumer in this
/// system uses (`messaging.md`): exactly one replica of this consumer processes a given message, and
/// every replica of it shares one queue under the same <see cref="ConsumerName"/>.</para>
/// </summary>
public sealed class RoleAssignmentsChangedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<RoleAssignmentsChangedConsumerOptions> options,
    ILogger<RoleAssignmentsChangedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "calendar-role-assignment-projection";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        // A literal, not `nameof(...)`: the topic name is `ago-chat`'s own
        // `nameof(Ago.Chat.Contracts.RoleAssignmentsChanged)`, a type this project cannot reference
        // (RoleAssignmentsChangedWireContract's own remarks) - the literal has to match that type's
        // bare name exactly, which is why it is spelled out here rather than derived from this
        // project's differently-named local copy.
        const string topic = "RoleAssignmentsChanged";

        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<RoleAssignmentsChangedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize RoleAssignmentsChanged payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var projections = scope.ServiceProvider.GetRequiredService<IRoleAssignmentProjectionStore>();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxChecker>();

            var operatorId = OperatorId.FromExternalSubjectId(contract.ExternalSubjectId);
            var tenantId = new TenantId(contract.SiteId);

            await projections.StageAsync(
                operatorId, tenantId, contract.ExternalSubjectId, contract.Permissions, contract.OccurredAt,
                cancellationToken);

            // IInboxChecker's own contract: this call commits the stage above together with the
            // inbox row, or - on a genuine duplicate message_id - commits neither (RoleAssignmentsChangedConsumer's
            // own remarks on why that is still correct rather than merely harmless).
            await inbox.TryRecordAndSaveAsync(envelope.MessageId, ConsumerName, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to project RoleAssignmentsChanged for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
