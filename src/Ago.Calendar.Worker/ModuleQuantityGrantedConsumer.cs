using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `22-07`/`adr/0093`: the calendar's own half of the crossing - "chat grants; the calendar holds and
/// applies; propagation rides the outbox" (this item's own Scope). Reacts to `ago-chat`'s own
/// <c>ModuleQuantityGranted</c>, a fact chat publishes without ever learning what a "calendar" or a
/// "master" is (<see cref="ModuleQuantityGrantedWireContract"/>'s own remarks) - the identical
/// module-opacity <see cref="RoleAssignmentsChangedConsumer"/> already relies on for
/// <c>RoleAssignmentsChanged</c>, applied here to a second, unrelated fact riding the same mechanism.
///
/// <para><b>Filtered by <see cref="ModuleQuantityGrantedWireContract.ModuleKey"/>, not by topic.</b>
/// One topic serves every module's own quantity grant - chat has no registry of modules to fan out by,
/// and does not need one - so this consumer subscribes to all of it and discards what is not its own,
/// the same "each module's own consumer knows its own key" shape
/// <c>tests/Ago.Chat.Architecture.Tests/KnownModuleKeys</c> already establishes on the publishing
/// side. A grant for a different module is acknowledged and otherwise ignored: nothing about it is
/// this consumer's to record, and no inbox row is written for a message this consumer never actually
/// acted on.</para>
///
/// <para><b>Two commits, not one - see <see cref="IWorkerQuotaGrantStore"/>'s own remarks for why.</b>
/// <see cref="IWorkerQuotaGrantStore.ApplyAsync"/> opens, uses and commits its own transaction (it
/// needs a <c>SELECT ... FOR UPDATE</c> lock held across a read-decide-write sequence, which a
/// "stage and let the caller combine the save" shape cannot express); the inbox record is a second,
/// separate save, safe only because the grant itself is naturally idempotent under a redelivery.</para>
/// </summary>
public sealed class ModuleQuantityGrantedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ModuleQuantityGrantedConsumerOptions> options,
    ILogger<ModuleQuantityGrantedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "calendar-worker-quota-grant";

    /// <summary>Calendar's own module key, `Ago.Chat.Domain.ModuleKey`'s wire value for this
    /// product - a literal this project is allowed to carry, unlike <c>Ago.Chat.*</c>
    /// (<see cref="ModuleQuantityGrantedWireContract"/>'s own remarks).</summary>
    private const string CalendarModuleKey = "calendar";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        // A literal, not `nameof(...)`: the topic name is `ago-chat`'s own
        // `nameof(Ago.Chat.Contracts.ModuleQuantityGranted)`, a type this project cannot reference -
        // the same reasoning RoleAssignmentsChangedConsumer's own topic constant gives.
        const string topic = "ModuleQuantityGranted";

        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ModuleQuantityGrantedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize ModuleQuantityGranted payload for outbox message {envelope.MessageId}.");

            if (!string.Equals(contract.ModuleKey, CalendarModuleKey, StringComparison.Ordinal))
            {
                await context.AckAsync(cancellationToken);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var quotaGrants = scope.ServiceProvider.GetRequiredService<IWorkerQuotaGrantStore>();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxChecker>();

            var tenantId = new TenantId(contract.SiteId);

            // Commits on its own - see IWorkerQuotaGrantStore's own remarks on why this is not staged
            // for the inbox call below to combine into one save.
            await quotaGrants.ApplyAsync(tenantId, contract.Quantity, contract.OccurredAt, cancellationToken);

            // A second, separate commit. A duplicate message_id here means the grant above was
            // (harmlessly) re-applied - it does not mean anything was left unwritten, since
            // ApplyAsync's own snapshot semantics make a redelivery a no-op by construction.
            await inbox.TryRecordAndSaveAsync(envelope.MessageId, ConsumerName, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to apply ModuleQuantityGranted for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
