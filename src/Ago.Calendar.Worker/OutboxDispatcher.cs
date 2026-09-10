using System.Threading.Channels;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Calendar.Worker;

/// <summary>
/// `25-44`: this product's first outbox dispatcher - `Ago.Calendar.Worker` had none at all until this
/// item, so every row it ever staged (`BookingConfirmed` since `20-04`, `ModuleQuantityImpactComputed`
/// since `23-88`/`adr/0165`) committed correctly and then sat in Postgres forever, undelivered
/// (messaging.md's own honest gap notes, both corrected in the same change that adds this class).
///
/// <para><b>A port, not a new design.</b> Claims unpublished rows with <c>FOR UPDATE SKIP LOCKED</c>,
/// publishes each via <see cref="IEventPublisher"/>, marks <c>published_at</c> on success - the
/// identical mechanism <c>Ago.Chat.Worker.OutboxDispatcher</c> already proves in production, restated
/// here for this product's own <c>outbox</c> table (the identical shared-platform schema -
/// <c>Ago.Platform.Persistence.Postgres.EfOutboxWriter{TDbContext}</c> is the one writer both products
/// use to stage a row in the first place). Safe with multiple <see cref="OutboxDispatcher"/> instances
/// against the same Postgres, since <c>SKIP LOCKED</c> means two instances claiming concurrently
/// simply split the unpublished rows between them rather than racing for the same one (adr/0005). Raw
/// Npgsql, not EF: <c>SELECT ... FOR UPDATE SKIP LOCKED</c> has no LINQ shape, and this dispatcher
/// never needs change tracking.</para>
///
/// <para><b>Deliberately carries no metrics or tracing instrumentation</b>, unlike its `Ago.Chat.Worker`
/// precedent (which reports through `ChatMetrics`/`ChatTracing`) - `Ago.Platform.Hosting.
/// AddPlatformObservability` is never called anywhere in this repository today (confirmed by grep),
/// so `Ago.Calendar` has no `Meter`/`ActivitySource` wiring for any instrument, on any class, to reach
/// an exporter through. Building a `CalendarMetrics`/`CalendarTracing` pair with nothing to export
/// them would be new, unused surface - the opposite of "port a working design" - not a faithful port
/// of the one this dispatcher's own precedent actually needs. Wiring this product's observability is
/// a distinct, product-wide gap for its own item, not something this port should invent piecemeal for
/// one class.</para>
/// </summary>
public sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    IEventPublisher publisher,
    IClock clock,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const string NotifyChannel = "outbox_new_row";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded to 1 and DropWrite: this is a wake signal, not a queue of individual
        // notifications - many inserts before the dispatcher gets to look just collapse into "there
        // is more work", which is all a batch claim needs to know.
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        await using var listenConnection = await dataSource.OpenConnectionAsync(stoppingToken);
        listenConnection.Notification += (_, _) => wake.Writer.TryWrite(true);
        await using (var listenCommand = new NpgsqlCommand($"LISTEN {NotifyChannel};", listenConnection))
        {
            await listenCommand.ExecuteNonQueryAsync(stoppingToken);
        }

        var notificationPump = PumpNotificationsAsync(listenConnection, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DispatchBatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // concurrency.md: a BackgroundService catches and continues - an unobserved
                    // exception here would silently kill the whole dispatch loop.
                    logger.LogError(ex, "Outbox dispatch cycle failed; retrying next cycle.");
                }

                // A fresh Task.Delay every iteration, not a shared PeriodicTimer - see
                // `Ago.Chat.Worker.OutboxDispatcher`'s own remarks for why racing a shared timer
                // inside Task.WhenAny against the wake signal permanently stalls the loop after the
                // first notification-driven wakeup.
                var wakeTask = wake.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var pollTask = Task.Delay(options.Value.PollInterval, stoppingToken);
                await Task.WhenAny(wakeTask, pollTask);
                while (wake.Reader.TryRead(out _))
                {
                    // Drain any notifications that arrived while a batch was already in flight.
                }
            }
        }
        finally
        {
            // concurrency.md's shutdown sequence: stop accepting new work, let this cycle finish
            // (already awaited above), then close cleanly - the pump task observes the same
            // stoppingToken and exits on its own.
            await notificationPump;
        }
    }

    private static async Task PumpNotificationsAsync(NpgsqlConnection connection, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Npgsql only raises Notification while something is actively waiting on the
                // connection - this loop is that "actively waiting".
                await connection.WaitAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    internal async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var rows = await ClaimBatchAsync(connection, transaction, options.Value.BatchSize, cancellationToken);

        foreach (var row in rows)
        {
            using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publishCts.CancelAfter(options.Value.PublishTimeout);

            try
            {
                await publisher.PublishAsync(row.ToEnvelope(), publishCts.Token);
                await MarkPublishedAsync(connection, transaction, row.Id, clock.UtcNow, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // publishCts fired on its own timeout, not because the dispatcher is shutting down
                // (the outer token is still fine) - an unresponsive broker, treated exactly like any
                // other publish failure: increment attempts, move on to the next row.
                logger.LogWarning("Publishing outbox row {OutboxId} timed out after {Timeout} (attempt {Attempt})",
                    row.Id, options.Value.PublishTimeout, row.Attempts + 1);
                await IncrementAttemptsAsync(connection, transaction, row.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A lost optimistic-concurrency race is Debug; a broker publish failure is a real,
                // if expected, degraded condition (resilience.md: "outbox accumulates while it is
                // down") - Warning, not Error, since the row is not lost, only delayed.
                logger.LogWarning(ex, "Failed to publish outbox row {OutboxId} (attempt {Attempt})", row.Id, row.Attempts + 1);
                await IncrementAttemptsAsync(connection, transaction, row.Id, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, occurred_at, type, version, payload, partition_key, correlation_id, attempts, trace_context
            FROM outbox
            WHERE published_at IS NULL
            ORDER BY occurred_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var rows = new List<ClaimedOutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClaimedOutboxRow(
                reader.GetGuid(0),
                reader.GetDateTime(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetGuid(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    }

    private static async Task MarkPublishedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE outbox SET published_at = @publishedAt, attempts = attempts + 1 WHERE id = @id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("publishedAt", publishedAt);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task IncrementAttemptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE outbox SET attempts = attempts + 1 WHERE id = @id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
