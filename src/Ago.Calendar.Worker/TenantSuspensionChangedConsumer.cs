using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `22-08`/`adr/0149` rule 1: this product's fourth broker consumer - projects `ago-chat`'s own
/// `TenantSuspensionChanged` lease instant onto the local tenancy row, the identical shape
/// <see cref="ModuleQuantityGrantedConsumer"/> already establishes for the granted worker quota.
///
/// <para><b>No module-key filter, unlike <see cref="ModuleQuantityGrantedConsumer"/>.</b> A quantity
/// grant is one fact among many modules' own quantities riding a single shared topic; a suspension is
/// account-wide by design (`docs/backlog/22-08-*.md`'s own Scope) and the wire contract carries no
/// module key at all to filter on - every module subscribed to this topic applies the identical fact
/// to its own tenancy row, unconditionally.</para>
///
/// <para><b>Two commits, not one</b> - the identical shape <see cref="ModuleQuantityGrantedConsumer"/>'s
/// own remarks state: <see cref="ISuspensionLeaseStore.ApplyAsync"/> commits its own single-aggregate
/// write, and the inbox record is a second, separate save, safe only because the lease application is
/// naturally idempotent under a redelivery (a snapshot, never a delta -
/// <see cref="Tenant.ApplySuspensionLease"/>'s own remarks).</para>
/// </summary>
public sealed class TenantSuspensionChangedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<TenantSuspensionChangedConsumerOptions> options,
    ILogger<TenantSuspensionChangedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "calendar-worker-suspension-lease";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        // A literal, not `nameof(...)`: the topic name is `ago-chat`'s own
        // `nameof(Ago.Chat.Contracts.TenantSuspensionChanged)`, a type this project cannot reference -
        // the same reasoning `ModuleQuantityGrantedConsumer`'s own topic constant gives.
        const string topic = "TenantSuspensionChanged";

        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<TenantSuspensionChangedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize TenantSuspensionChanged payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var leases = scope.ServiceProvider.GetRequiredService<ISuspensionLeaseStore>();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxChecker>();

            var tenantId = new TenantId(contract.SiteId);

            // Commits on its own - see ISuspensionLeaseStore's own remarks on why this is not staged
            // for the inbox call below to combine into one save.
            await leases.ApplyAsync(tenantId, contract.SuspendedUntil, cancellationToken);

            // A second, separate commit. A duplicate message_id here means the lease above was
            // (harmlessly) re-applied - ApplySuspensionLease's own snapshot semantics make a
            // redelivery a no-op by construction.
            await inbox.TryRecordAndSaveAsync(envelope.MessageId, ConsumerName, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to apply TenantSuspensionChanged for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
