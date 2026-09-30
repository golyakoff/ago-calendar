using System.Text.Json;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Worker;

/// <summary>
/// `26-315`: reacts to this product's own <see cref="WorkerScheduleSaved"/> - staged by
/// <c>SaveWorkerScheduleHandler</c> on every create-or-reconfigure - by materialising that calendar
/// right away, through the identical <see cref="MaterializeAvailabilityHandler"/>
/// <see cref="AvailabilityMaterializationJob"/> itself calls. This is this item's own preferred fix:
/// before it, a schedule saved after the daily job's last tick produced zero materialised slots for up
/// to 24h, and no operator action could shorten that - see <see cref="WorkerScheduleSaved"/>'s own
/// remarks, and <c>Ago.Calendar.Application.UseCases.RecutSchedule.RecutConfirmHandler</c>'s own
/// bootstrap branch for the second, human-triggered path to the identical outcome.
///
/// <para><b>The same shape <see cref="BookingPendingFanoutConsumer"/> already establishes: a domain
/// fact this product staged itself, so <see cref="SubscriptionMode.Competing"/> - exactly one
/// <c>Worker</c> replica materialises per staged save, not every replica - and no
/// <see cref="IInboxChecker"/> ledger, for the identical reason that consumer's own remarks give for
/// skipping one: <see cref="MaterializeAvailabilityHandler"/> is idempotent by construction (its own
/// remarks - a day already materialised is never regenerated, and the cursor only ever advances), so
/// redelivering this message re-asks the identical "materialise whatever is still missing" question
/// and finds nothing left to do, exactly as harmless as the first delivery.</b></para>
///
/// <para><b>Materialises the whole calendar, not only the worker that was saved.</b>
/// <see cref="MaterializeAvailability"/> only exists at calendar granularity - the daily job's own unit
/// of work - and there is no narrower port to call instead; inventing one for this one consumer would be
/// new, speculative port surface for a fix this item's own scope asks to keep contained. A calendar with
/// more than one worker simply gets every one of them that independently needs materialising, the
/// identical side effect <see cref="Ago.Calendar.Application.UseCases.RecutSchedule.RecutConfirmHandler"/>'s
/// own bootstrap branch already accepts for the same reason.</para>
/// </summary>
public sealed class WorkerScheduleSavedMaterializationConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerScheduleSavedMaterializationConsumerOptions> options,
    ILogger<WorkerScheduleSavedMaterializationConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "worker-schedule-saved-materialization";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(WorkerScheduleSaved), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<WorkerScheduleSaved>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(WorkerScheduleSaved)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<MaterializeAvailabilityHandler>();

            await handler.HandleAsync(
                new MaterializeAvailability(new CalendarId(contract.CalendarId)), cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to materialise for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
