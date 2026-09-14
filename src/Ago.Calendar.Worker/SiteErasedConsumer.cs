using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.TenantErasure;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `25-82`: reacts to `ago-chat`'s own `SiteErased` - the fact that a tenant is now completely gone
/// from that database - by draining every row this product still holds for it, the identical shape
/// <see cref="RoleAssignmentsChangedConsumer"/> already establishes for the fact this product consumes
/// but does not publish. Not this product's outbox: there is nothing here to stage or dispatch, only a
/// subscription against the broker `ago-chat`'s own outbox dispatcher already publishes to.
///
/// <para><b>Reuses <see cref="EraseTenantDataHandler"/> wholesale, rather than draining
/// `role_assignment_projections` alone.</b> That handler already exists, already erases everything a
/// tenant erasure needs to reach - `role_assignment_projections`, `contact_visibility_projections`, and
/// the `tenants` row itself with everything that cascades from it - and is already exhaustively proven
/// idempotent against a real Postgres (<c>TenantErasureEndpointTests</c>). A demo tenant is chat-only
/// and typically has no row in this product's own `tenants` table at all (the calendar module was
/// never registered for it), so for the case this item measured, that branch is simply a no-op; when a
/// demo tenant *does* happen to carry an auto-provisioned tenant row, draining it too is the same class
/// of fix this item makes, not scope creep - a narrower, hand-written delete limited to one table would
/// have had to duplicate logic this port already gets right.</para>
///
/// <para><b>Two commits, not one</b> - the identical shape <see cref="TenantSuspensionChangedConsumer"/>'s
/// own remarks state: <see cref="ITenantErasureRepository.EraseAsync"/> (reached through
/// <see cref="EraseTenantDataHandler"/>) commits its own transaction, and the inbox record is a second,
/// separate save, safe only because erasure is naturally idempotent under a redelivery - that port's
/// own remarks ("a tenant that no longer exists is not an error... a second call must land on the
/// identical answer rather than throw").</para>
///
/// <para><b><see cref="SubscriptionMode.Competing"/></b>, the default every per-item consumer in this
/// system uses (`messaging.md`): exactly one replica of this consumer processes a given message.</para>
/// </summary>
public sealed class SiteErasedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<SiteErasedConsumerOptions> options,
    ILogger<SiteErasedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "calendar-tenant-erasure";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        // A literal, not `nameof(...)`: the topic name is `ago-chat`'s own
        // `nameof(Ago.Chat.Contracts.SiteErased)`, a type this project cannot reference - the same
        // reasoning every other topic constant in this file family gives.
        const string topic = "SiteErased";

        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<SiteErasedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize SiteErased payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var erasure = scope.ServiceProvider.GetRequiredService<EraseTenantDataHandler>();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxChecker>();

            var tenantId = new TenantId(contract.SiteId);

            // Commits on its own - see this class's own remarks on why this is not staged for the
            // inbox call below to combine into one save.
            var result = await erasure.HandleAsync(new EraseTenantData(tenantId), cancellationToken);

            // A second, separate commit. A duplicate message_id here means the erasure above was
            // (harmlessly) re-attempted - ITenantErasureRepository.EraseAsync's own idempotent-by-construction
            // semantics make a redelivery a no-op by construction.
            await inbox.TryRecordAndSaveAsync(envelope.MessageId, ConsumerName, cancellationToken);

            await context.AckAsync(cancellationToken);

            logger.LogInformation(
                "Tenant {TenantId} erasure driven by SiteErased (outbox message {MessageId}): existed={TenantExisted}, confirmed={Confirmed}.",
                tenantId.Value, envelope.MessageId, result.TenantExisted, result.Confirmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to erase tenant data for SiteErased outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}

/// <summary>
/// `25-82`: this product's own local copy of `ago-chat`'s `SiteErased` wire shape - `adr/0027`'s "each
/// product declares its own local copy, never a shared Contracts assembly" discipline, the identical
/// pattern <see cref="RoleAssignmentsChangedWireContract"/> already establishes. Only the one field this
/// consumer actually reads.
/// </summary>
internal sealed record SiteErasedWireContract(Guid SiteId, DateTimeOffset OccurredAt, Guid CorrelationId);
