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
/// `26-268`/`adr/0188`: the manual-entry write's own transaction, against a real Postgres - a fresh
/// person insert and a raw atomic claim straight into <c>Booked</c>, together or not at all. The
/// two statements and their rollback are SQL/transaction behaviour whose guarantee a fake would prove
/// nothing about, the identical reason <see cref="BookingStoreTests"/> and
/// <see cref="BookingRescheduleStoreTests"/> both test their own stores against a real database.
///
/// <para>Phone numbers here are invented <c>+7999...</c> values belonging to nobody
/// (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ManualBookingStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASuccessfulEntry_ClaimsTheSlotIntoBooked_UpsertsThePerson_AndStagesBothEvents()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);
        var personId = CalendarSeed.NewId();

        var confirmation = await EnterAsync(seed, slot.Id, personId, "+79990000060");

        Assert.NotNull(confirmation);
        Assert.Equal(slot.Id, confirmation.Value.BookingId);
        Assert.Equal([slot.Id], confirmation.Value.EventIds);
        Assert.Equal(personId, confirmation.Value.PersonId);
        Assert.Equal(seed.Worker.Id, confirmation.Value.WorkerId);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == slot.Id);

        // Straight into Booked, no deadline, no chat origin - the whole point of a manual entry.
        Assert.Equal(EventStatus.Booked, stored.Status);
        Assert.Equal(personId, stored.PersonId);
        Assert.Equal(seed.Service.Id, stored.ServiceId);
        Assert.Null(stored.ConfirmationDeadline);
        Assert.Equal(slot.Id, stored.BookingId);
        Assert.Null(stored.OriginConversationId);

        // The freshly minted person: phone_verified_at stays null (no SMS proof), but the operator's
        // own "I called and it is them" fact is recorded (`23-12`/§3.3).
        var record = await db.PersonRecords.SingleAsync(p => p.PersonId == personId);
        Assert.Equal("+79990000060", record.Phone.Value);
        Assert.Null(record.PhoneVerifiedAt);
        Assert.Equal(Now, record.PhoneConfirmedByOperatorAt);

        // Exactly the two events `adr/0188`/§3.5 names - never BookingPendingStateChanged, since this
        // booking was never pending.
        var registeredRows = await OutboxRowsOfTypeAsync(nameof(PersonRegistered));
        var registeredRow = Assert.Single(registeredRows, r => r.PartitionKey == personId.ToString());
        var registered = JsonSerializer.Deserialize<PersonRegistered>(registeredRow.Payload)!;
        Assert.Equal(personId, registered.PersonId);
        Assert.Equal(seed.Tenant.Id.Value, registered.AccountId);
        Assert.Equal("+79990000060", registered.Phone);
        Assert.Equal("Anna", registered.Name);

        var confirmedRows = await OutboxRowsOfTypeAsync(nameof(BookingConfirmed));
        var confirmedRow = Assert.Single(confirmedRows, r => r.PartitionKey == slot.Id.Value.ToString());
        var confirmedContract = JsonSerializer.Deserialize<BookingConfirmed>(confirmedRow.Payload)!;
        Assert.Equal(slot.Id.Value, confirmedContract.EventId);
        Assert.Equal(personId, confirmedContract.PersonId);

        Assert.Empty(await OutboxRowsOfTypeAsync(nameof(BookingPendingStateChanged), slot.Id.Value));
    }

    [Fact]
    public async Task WhenTheSlotWasTakenInTheRace_TheWholeTransactionRollsBack_AndStagesNothing()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);

        // Somebody else claims it first - the ordinary race.
        await using (var db = fixture.CreateDbContext())
        {
            var winner = await new BookingStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator())
                .TryBookAsync(
                    new BookingAttempt(
                        seed.Tenant.Id, seed.Calendar.Id, [slot.Id], seed.Service.Id,
                        new PhoneNumber("+79990000061"), new PersonRegistration(null),
                        Now, Now.AddMinutes(15), Now, CalendarSeed.NewId(), null),
                    CancellationToken.None);
            Assert.NotNull(winner);
        }

        var personId = CalendarSeed.NewId();
        var confirmation = await EnterAsync(seed, slot.Id, personId, "+79990000062");

        Assert.Null(confirmation);

        await using var verify = fixture.CreateDbContext();

        // No person record for the person this manual entry would have minted - the identical
        // data-minimisation property IBookingStore's own remarks state: an entry that did not happen
        // must leave no trace.
        Assert.False(await verify.PersonRecords.AnyAsync(p => p.PersonId == personId));

        Assert.Empty(await OutboxRowsOfTypeAsync(nameof(PersonRegistered), personId));
    }

    private async Task<Event> AnAvailableSlotAsync(SeededTenant seed)
    {
        var slot = CalendarSeed.Slot(seed, Now.AddHours(2));
        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        return slot;
    }

    private async Task<BookingConfirmation?> EnterAsync(
        SeededTenant seed, EventId slotId, Guid personId, string phone)
    {
        var idGenerator = new UuidV7Generator();
        var phoneNumber = new PhoneNumber(phone);

        var personRegistered = PersonRegisteredMapper.ToEnvelope(
            personId, seed.Tenant.Id, phoneNumber, "Anna", Now, idGenerator);
        var bookingConfirmed = BookingConfirmedMapper.ToEnvelope(
            eventId: slotId,
            tenantId: seed.Tenant.Id,
            calendarId: seed.Calendar.Id,
            personId: personId,
            startsAt: Now.AddHours(2),
            endsAt: Now.AddHours(2).AddMinutes(45),
            localDate: DateOnly.FromDateTime(Now.AddHours(2).UtcDateTime),
            occurredAt: Now,
            idGenerator);

        var attempt = new ManualBookingAttempt(
            seed.Tenant.Id, seed.Calendar.Id, [slotId], seed.Service.Id, phoneNumber, personId, Now,
            personRegistered, bookingConfirmed);

        await using var db = fixture.CreateDbContext();
        return await new ManualBookingStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db))
            .TryEnterAsync(attempt, CancellationToken.None);
    }

    /// <summary>The outbox rows of one type - scoped to this test's own partition key when given,
    /// because the shared `outbox` table also holds other tests' rows in this same collection.</summary>
    private async Task<IReadOnlyList<(string PartitionKey, string Payload)>> OutboxRowsOfTypeAsync(
        string type, Guid? partitionKey = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var sql = partitionKey is null
            ? "select partition_key, payload from outbox where type = @type order by occurred_at"
            : "select partition_key, payload from outbox where type = @type and partition_key = @partitionKey order by occurred_at";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("type", type);
        if (partitionKey is { } key)
        {
            command.Parameters.AddWithValue("partitionKey", key.ToString());
        }

        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
