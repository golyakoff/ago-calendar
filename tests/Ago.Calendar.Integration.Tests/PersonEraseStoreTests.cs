using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `26-275`/`adr/0189`: the person-erasure write's own transaction, against a real Postgres - the
/// guard, the anonymising update and the delete, together or not at all. All three are SQL/transaction
/// behaviour whose guarantee a fake would prove nothing about, the identical reason
/// <see cref="ManualBookingStoreTests"/> and <see cref="BookingRescheduleStoreTests"/> both already
/// test their own stores this way.
///
/// <para>Phone numbers here are invented <c>+7999...</c> values belonging to nobody
/// (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PersonEraseStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset EraseNow = CalendarSeed.Now.AddDays(30);

    [Fact]
    public async Task NoFutureBooking_ErasesThePersonRecord_AnonymisesEveryOtherEvent_AndStagesPersonErased()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // A past, held-status booking - the shape the STATS constraint (issue 1815) exists for: it
        // must survive, with its person link cleared, so an aggregate-by-service/worker is unchanged.
        var pastBooked = await SeedEventAsync(
            seed, CalendarSeed.Now.AddHours(2), seed.Person.PersonId, EventStatus.Booked);

        // A future event that is NOT a live booking (it was cancelled) - the guard must let this one
        // through, and the anonymising update must still reach it: nothing about "future" alone
        // blocks erasure, only a *live* future booking does.
        var futureCancelled = await SeedEventAsync(
            seed, EraseNow.AddDays(1), seed.Person.PersonId, EventStatus.Cancelled);

        // A past no-show - the third held status IPersonBookingReadStore's own remarks name.
        var pastNoShow = await SeedEventAsync(
            seed, CalendarSeed.Now.AddHours(4), seed.Person.PersonId, EventStatus.NoShow);

        var erased = await EraseAsync(seed, EraseNow);

        Assert.True(erased);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.PersonRecords.AnyAsync(p => p.PersonId == seed.Person.PersonId));

        var reread = await db.Events
            .Where(e => e.Id == pastBooked.Id || e.Id == futureCancelled.Id || e.Id == pastNoShow.Id)
            .ToDictionaryAsync(e => e.Id);

        // Every one of this person's own events keeps its status and every other column - only the
        // one column that named this person is cleared.
        Assert.Null(reread[pastBooked.Id].PersonId);
        Assert.Equal(EventStatus.Booked, reread[pastBooked.Id].Status);
        Assert.Null(reread[futureCancelled.Id].PersonId);
        Assert.Equal(EventStatus.Cancelled, reread[futureCancelled.Id].Status);
        Assert.Null(reread[pastNoShow.Id].PersonId);
        Assert.Equal(EventStatus.NoShow, reread[pastNoShow.Id].Status);

        var rows = await OutboxRowsOfTypeAsync(nameof(PersonErased), seed.Person.PersonId);
        var row = Assert.Single(rows);
        var contract = JsonSerializer.Deserialize<PersonErased>(row.Payload)!;
        Assert.Equal(seed.Person.PersonId, contract.PersonId);
        Assert.Equal(seed.Tenant.Id.Value, contract.AccountId);
    }

    [Fact]
    public async Task ALiveFutureBookedEvent_RefusesTheWholeTransaction_ErasesNothing_AnonymisesNothing_StagesNothing()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var pastBooked = await SeedEventAsync(
            seed, CalendarSeed.Now.AddHours(2), seed.Person.PersonId, EventStatus.Booked);
        var futureBooked = await SeedEventAsync(
            seed, EraseNow.AddDays(1), seed.Person.PersonId, EventStatus.Booked);

        var erased = await EraseAsync(seed, EraseNow);

        Assert.False(erased);

        await using var db = fixture.CreateDbContext();
        // Rolled back whole - the person record is still there...
        Assert.True(await db.PersonRecords.AnyAsync(p => p.PersonId == seed.Person.PersonId));

        // ...and so is the *past* event's own person link. A refused attempt must leave the
        // anonymising update's own work undone too, not just skip the delete - CLAUDE.md's own
        // "all in one transaction" promise, restated for this guard.
        var rereadPast = await db.Events.SingleAsync(e => e.Id == pastBooked.Id);
        Assert.Equal(seed.Person.PersonId, rereadPast.PersonId);

        var rereadFuture = await db.Events.SingleAsync(e => e.Id == futureBooked.Id);
        Assert.Equal(seed.Person.PersonId, rereadFuture.PersonId);
        Assert.Equal(EventStatus.Booked, rereadFuture.Status);

        Assert.Empty(await OutboxRowsOfTypeAsync(nameof(PersonErased), seed.Person.PersonId));
    }

    [Fact]
    public async Task ALiveFuturePendingConfirmationEvent_RefusesTheWholeTransaction_TheIdenticalGuard()
    {
        // `26-275`/`adr/0189`'s guard names both held-and-not-yet-safe statuses - PendingConfirmation
        // is still a live claim an operator may yet veto, so it blocks erasure exactly like Booked
        // does.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var futurePending = await SeedEventAsync(
            seed, EraseNow.AddDays(1), seed.Person.PersonId, EventStatus.PendingConfirmation);

        var erased = await EraseAsync(seed, EraseNow);

        Assert.False(erased);

        await using var db = fixture.CreateDbContext();
        Assert.True(await db.PersonRecords.AnyAsync(p => p.PersonId == seed.Person.PersonId));
        var reread = await db.Events.SingleAsync(e => e.Id == futurePending.Id);
        Assert.Equal(seed.Person.PersonId, reread.PersonId);
    }

    [Fact]
    public async Task APersonWithNoEventsAtAll_ErasesCleanly()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var erased = await EraseAsync(seed, EraseNow);

        Assert.True(erased);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.PersonRecords.AnyAsync(p => p.PersonId == seed.Person.PersonId));
    }

    private async Task<bool> EraseAsync(SeededTenant seed, DateTimeOffset now)
    {
        var idGenerator = new UuidV7Generator();
        var personErased = PersonErasedMapper.ToEnvelope(seed.Person.PersonId, seed.Tenant.Id, now, idGenerator);
        var attempt = new PersonEraseAttempt(seed.Tenant.Id, seed.Person.PersonId, now, personErased);

        await using var db = fixture.CreateDbContext();
        return await new PersonEraseStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db))
            .TryEraseAsync(attempt, CancellationToken.None);
    }

    /// <summary>Writes one event straight into its final state through the real aggregate's own
    /// transitions (<c>Materialize</c> then <c>Claim</c>, and <c>Confirm</c>/<c>Cancel</c>/
    /// <c>MarkNoShow</c> as needed) rather than a hand-built row - the same "through the real mappings"
    /// discipline <see cref="CalendarSeed"/> itself follows. <paramref name="startsAt"/> is this row's
    /// own slot; every transition below runs at <see cref="CalendarSeed.Now"/> or later, which is
    /// always safely before <see cref="EraseNow"/>, so a "past" or "future" row here means past/future
    /// relative to the erasure instant a test names, never relative to when the transition itself
    /// happened.</summary>
    private async Task<Event> SeedEventAsync(
        SeededTenant seed, DateTimeOffset startsAt, Guid personId, EventStatus finalStatus)
    {
        var @event = CalendarSeed.Slot(seed, startsAt);
        @event.Claim(personId, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));

        switch (finalStatus)
        {
            case EventStatus.PendingConfirmation:
                break;
            case EventStatus.Booked:
                @event.Confirm(CalendarSeed.Now.AddMinutes(20));
                break;
            case EventStatus.Cancelled:
                @event.Cancel(CalendarSeed.Now.AddMinutes(20));
                break;
            case EventStatus.NoShow:
                @event.Confirm(CalendarSeed.Now.AddMinutes(20));
                @event.MarkNoShow(@event.EndsAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(finalStatus), finalStatus, "Not a held status this seed builds.");
        }

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([@event], CancellationToken.None);
        return @event;
    }

    /// <summary>The outbox rows of one type, scoped to this test's own partition key - the identical
    /// helper <see cref="ManualBookingStoreTests"/> already establishes, restated here because the
    /// shared <c>outbox</c> table also holds other tests' rows in this same collection.</summary>
    private async Task<IReadOnlyList<(string PartitionKey, string Payload)>> OutboxRowsOfTypeAsync(
        string type, Guid partitionKey)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        const string sql =
            "select partition_key, payload from outbox where type = @type and partition_key = @partitionKey order by occurred_at";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("partitionKey", partitionKey.ToString());

        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
