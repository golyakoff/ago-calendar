using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `23-59`/`adr/0147`: reacts to `ago-chat`'s own `ContactCollected` - this product's fourth broker
/// consumer, and the second (after <see cref="ModuleQuantityGrantedConsumer"/>) whose own write commits
/// itself rather than being staged for the inbox call to combine - see
/// <see cref="IContactCollectedCustomerStore"/>'s own remarks for why an `ON CONFLICT` upsert cannot be
/// expressed as a "stage, then let the caller save" shape.
///
/// <para><b>The module-granted gate, done locally, never by asking chat.</b> "for a tenant that has
/// it" (the item's own Done-when) means: does a row for this <see cref="TenantId"/> exist in this
/// product's own `tenants` table. That is exactly the fact `adr/0093`'s domains-stay-apart rule leaves
/// this product free to check on its own - a plain local read, no cross-database call, and the reason
/// this consumer can ack and move on immediately for the (very common) case of a contact from a site
/// that has never had the calendar module: nothing here reads or even knows that
/// <c>ago_chat.enabled_modules</c> exists.</para>
///
/// <para><b>Only <c>Kind == "Phone"</c> becomes anything.</b> `ContactCollected`'s own remarks: chat
/// publishes every kind, ignorant of what a module needs. <see cref="Customer"/> has no field for an
/// e-mail address or a free-text "Other" note - its whole identity is a phone number - so a non-Phone
/// contact has nowhere to go here, the same "discard what is not mine" shape
/// <see cref="ModuleQuantityGrantedConsumer"/> already uses for a grant naming a different module.
/// Acked, not dead-lettered: there is nothing wrong with the message, only nothing here to do with
/// it.</para>
///
/// <para><b>A value that will never parse as a <see cref="PhoneNumber"/> is also acked, not
/// retried.</b> Chat's own validation (<c>VisitorContactDetail.ValidateAndTrim</c>) only refuses empty
/// or oversized text - it has no notion of E.164 shape, so a visitor who typed "call me on Tuesday"
/// into the phone field is a real, reachable state this consumer must not loop forever retrying. This
/// is a permanent, not a transient, failure - the identical distinction the calendar's own console
/// input validation draws - so it is caught here and logged, never rethrown into this consumer's own
/// retry policy.</para>
/// </summary>
public sealed class ContactCollectedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ContactCollectedConsumerOptions> options,
    ILogger<ContactCollectedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "calendar-contact-customer-projection";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        // A literal, not `nameof(...)`: the topic name is `ago-chat`'s own
        // `nameof(Ago.Chat.Contracts.ContactCollected)`, a type this project cannot reference - the
        // same reasoning every other consumer's own topic constant gives.
        const string topic = "ContactCollected";

        return consumer.SubscribeAsync(
            topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ContactCollectedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize ContactCollected payload for outbox message {envelope.MessageId}.");

            if (!string.Equals(contract.Kind, "Phone", StringComparison.Ordinal))
            {
                await context.AckAsync(cancellationToken);
                return;
            }

            PhoneNumber phone;
            try
            {
                phone = new PhoneNumber(contract.Value);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(
                    ex, "ContactCollected {ContactDetailId} for site {SiteId} does not parse as a phone number; skipped.",
                    contract.ContactDetailId, contract.SiteId);
                await context.AckAsync(cancellationToken);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
            var customers = scope.ServiceProvider.GetRequiredService<IContactCollectedCustomerStore>();
            var inbox = scope.ServiceProvider.GetRequiredService<IInboxChecker>();

            var tenantId = new TenantId(contract.SiteId);
            var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken);
            if (tenant is null)
            {
                // The module has never been granted to this site (or was granted after this contact
                // was collected and has not carried it over yet) - not an error. `ago-chat`'s own
                // ContactCarryoverBackfill is what reaches this contact again once a `tenants` row for
                // this site exists.
                await context.AckAsync(cancellationToken);
                return;
            }

            // Commits on its own - see IContactCollectedCustomerStore's own remarks on why this is not
            // staged for the inbox call below to combine into one save.
            await customers.UpsertAsync(tenantId, contract.ContactDetailId, phone, contract.RecordedAt, cancellationToken);

            // A second, separate commit. A duplicate message_id here means the upsert above was
            // (harmlessly) re-applied - the upsert's own ON CONFLICT target is what makes a redelivery
            // idempotent, not this ledger; this ledger is the fast path (messaging.md).
            await inbox.TryRecordAndSaveAsync(envelope.MessageId, ConsumerName, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to project ContactCollected for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
