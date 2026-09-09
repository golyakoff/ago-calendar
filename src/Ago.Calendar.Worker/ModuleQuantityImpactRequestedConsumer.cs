using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `23-88`/`adr/0165`: the calendar's own half of the async worker-quota impact preview - reacts to
/// `ago-chat`'s own <c>ModuleQuantityImpactRequested</c> and answers it on this product's own outbox,
/// the first calendar-to-chat crossing (`adr/0165`'s own remarks) built on the identical mechanism
/// every prior chat-to-calendar crossing already uses.
///
/// <para><b>Filtered by <see cref="ModuleQuantityImpactRequestedWireContract.ModuleKey"/>, not by
/// topic</b> - the identical "one topic serves every module's own question, so each module's own
/// consumer knows its own key" shape <see cref="ModuleQuantityGrantedConsumer"/>'s own remarks
/// establish for the opposite direction. A question for a different module is acknowledged and
/// otherwise ignored: nothing about it is this consumer's to answer.</para>
///
/// <para><b>No idempotency ledger - a deliberate divergence from
/// <see cref="ModuleQuantityGrantedConsumer"/>'s own inbox row, not an oversight.</b> That consumer
/// writes a real state change (<see cref="IWorkerQuotaGrantStore.ApplyAsync"/> deactivates workers and
/// overwrites the tenant's own granted quota) and records an inbox entry as the fast-path companion to
/// that write's own natural idempotency. This consumer changes no local state at all - it only reads
/// (no lock, <see cref="IWorkerQuotaImpactAnswerer"/>'s own remarks) and republishes. A redelivered
/// question is answered twice, with two distinct <c>MessageId</c>s but the identical values (or a
/// freshly correct, possibly different count, if a worker was created or deactivated between the two
/// deliveries - which is a *more* correct answer, never a wrong one): either way,
/// <c>Ago.Chat.Application.Abstractions.IModuleQuantityImpactPreviewStore.AnswerAsync</c> on the
/// receiving side is a naturally idempotent snapshot overwrite (this item's own report, `adr/0165`'s
/// own Decision). An inbox row here would protect against nothing an ordinary re-answer does not
/// already handle correctly - the identical reasoning <c>Ago.Chat.Worker.ModuleQuantityImpactComputedConsumer</c>'s
/// own remarks give for skipping one on the receiving end of this same round trip.</para>
/// </summary>
public sealed class ModuleQuantityImpactRequestedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ModuleQuantityImpactRequestedConsumerOptions> options,
    ILogger<ModuleQuantityImpactRequestedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "calendar-worker-quota-impact-answer";

    /// <summary>Calendar's own module key - see <see cref="ModuleQuantityGrantedConsumer"/>'s own
    /// identical constant for why this is a literal this project is allowed to carry.</summary>
    private const string CalendarModuleKey = "calendar";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        // A literal, not `nameof(...)`: the topic name is `ago-chat`'s own
        // `nameof(Ago.Chat.Contracts.ModuleQuantityImpactRequested)`, a type this project cannot
        // reference - the same reasoning every other consumer's own topic constant gives.
        const string topic = "ModuleQuantityImpactRequested";

        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ModuleQuantityImpactRequestedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize ModuleQuantityImpactRequested payload for outbox message {envelope.MessageId}.");

            if (!string.Equals(contract.ModuleKey, CalendarModuleKey, StringComparison.Ordinal))
            {
                await context.AckAsync(cancellationToken);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var answerer = scope.ServiceProvider.GetRequiredService<IWorkerQuotaImpactAnswerer>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            var tenantId = new TenantId(contract.SiteId);

            // Commits on its own - IWorkerQuotaImpactAnswerer.AnswerAsync's own remarks: the outbox
            // row it stages is the entire write, so there is nothing else here for it to be combined
            // with (and, per this class's own remarks, no inbox record follows it either).
            await answerer.AnswerAsync(
                tenantId, contract.RequestedQuantity, contract.CorrelationId, clock.UtcNow, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to answer ModuleQuantityImpactRequested for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
