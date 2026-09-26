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
/// `26-208`/`adr/0187`: the reschedule's own transaction, against a real Postgres - cancel the old run
/// and claim a new one straight into <c>Booked</c>, together or not at all. The two statements
/// (a raw atomic claim and an EF cancel on one connection) and their rollback are SQL/transaction
/// behaviour whose guarantee a fake would prove nothing about, exactly the reason
/// <see cref="BookingStoreTests"/> tests <c>BookingStore</c> against a real database.
///
/// <para>Phone numbers here are invented <c>+7999...</c> values belonging to nobody - a public
/// repository must not carry a real person's contact details (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class BookingRescheduleStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASuccessfulReschedule_CancelsTheOldRun_ClaimsTheNewOneIntoBooked_AndStagesOneBookingRescheduled()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var oldRun = await ABookedBookingAsync(seed, Now.AddHours(2));
        var target = await AnAvailableSlotAsync(seed, Now.AddHours(5));

        var ok = await RescheduleAsync(seed, oldRun, target);

        Assert.True(ok);

        await using var db = fixture.CreateDbContext();

        // The old run is cancelled - and NOT re-offered as Available (`adr/0187`: it inherits plain
        // cancel's behaviour). Its person and booking_id are untouched history.
        var oldStored = await db.Events.SingleAsync(e => e.Id == oldRun.Id);
        Assert.Equal(EventStatus.Cancelled, oldStored.Status);
        Assert.Equal(oldRun.Id, oldStored.BookingId);
        Assert.Equal(oldRun.PersonId, oldStored.PersonId);

        // The new run landed straight in Booked (no veto window), carrying the old booking's person and
        // service, its own id as the new anchor, and a null deadline.
        var newStored = await db.Events.SingleAsync(e => e.Id == target.Id);
        Assert.Equal(EventStatus.Booked, newStored.Status);
        Assert.Equal(target.Id, newStored.BookingId);
        Assert.Equal(oldRun.PersonId, newStored.PersonId);
        Assert.Equal(seed.Service.Id, newStored.ServiceId);
        Assert.Null(newStored.ConfirmationDeadline);

        // Exactly one BookingRescheduled for this move (scoped by the new anchor's partition key, since
        // the shared outbox table also holds other tests' rows), carrying the mandatory old->new link -
        // and no BookingConfirmed / BookingPendingStateChanged for this move (`adr/0187`: one clean
        // event). Both the new-anchor and old-anchor partition keys are checked for the absent pair.
        var rescheduled = await OutboxRowsOfAsync(nameof(BookingRescheduled), target.Id);
        var row = Assert.Single(rescheduled);
        var contract = JsonSerializer.Deserialize<BookingRescheduled>(row)!;
        Assert.Equal(target.Id.Value, contract.EventId);
        Assert.Equal(oldRun.Id.Value, contract.PreviousEventId);
        Assert.Equal(oldRun.StartsAt, contract.PreviousStartsAt);
        Assert.Equal(target.StartsAt, contract.NewStartsAt);
        Assert.Equal(target.EndsAt, contract.NewEndsAt);
        Assert.Equal(seed.Tenant.Id.Value, contract.TenantId);

        Assert.Empty(await OutboxRowsOfAsync(nameof(BookingConfirmed), target.Id));
        Assert.Empty(await OutboxRowsOfAsync(nameof(BookingConfirmed), oldRun.Id));
        Assert.Empty(await OutboxRowsOfAsync(nameof(BookingPendingStateChanged), target.Id));
        Assert.Empty(await OutboxRowsOfAsync(nameof(BookingPendingStateChanged), oldRun.Id));
    }

    [Fact]
    public async Task WhenTheTargetWasTakenInTheRace_TheWholeTransactionRollsBack_AndTheOldBookingStaysBooked()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var oldRun = await ABookedBookingAsync(seed, Now.AddHours(2));
        var target = await AnAvailableSlotAsync(seed, Now.AddHours(5));

        // Somebody else claims the target between the handler's courtesy read and this reschedule's own
        // claim - the ordinary race. It is now PendingConfirmation, not Available.
        await using (var db = fixture.CreateDbContext())
        {
            var winner = await new BookingStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator())
                .TryBookAsync(
                    new BookingAttempt(
                        seed.Tenant.Id, seed.Calendar.Id, [target.Id], seed.Service.Id,
                        new PhoneNumber("+79990000051"), new PersonRegistration(null),
                        Now, Now.AddMinutes(15), Now, CalendarSeed.NewId(), null),
                    CancellationToken.None);
            Assert.NotNull(winner);
        }

        var ok = await RescheduleAsync(seed, oldRun, target);

        Assert.False(ok);

        await using var verify = fixture.CreateDbContext();

        // The whole reschedule rolled back: the old booking stays Booked, untouched - the property the
        // single transaction exists for.
        var oldStored = await verify.Events.SingleAsync(e => e.Id == oldRun.Id);
        Assert.Equal(EventStatus.Booked, oldStored.Status);
        Assert.Equal(oldRun.Id, oldStored.BookingId);

        // The target belongs to the winner - still PendingConfirmation (the winner's veto window),
        // never the Booked our reschedule would have set, and carrying the winner's person, not the
        // rescheduled booking's.
        var targetStored = await verify.Events.SingleAsync(e => e.Id == target.Id);
        Assert.Equal(EventStatus.PendingConfirmation, targetStored.Status);
        Assert.NotEqual(oldRun.PersonId, targetStored.PersonId);

        // And no BookingRescheduled was staged for an attempt that lost the race (scoped by this
        // reschedule's own new-anchor partition key - the shared outbox holds other tests' rows).
        Assert.Empty(await OutboxRowsOfAsync(nameof(BookingRescheduled), target.Id));
    }

    /// <summary>Inserts one confirmed, single-slot booking directly - claimed and confirmed through the
    /// aggregate, then persisted - so the outbox starts empty for this test (unlike going through
    /// <c>BookingStore</c>, which would stage a claim row). The person id is the seed's own record, so
    /// the <c>person_id</c> foreign key is satisfied.</summary>
    private async Task<Event> ABookedBookingAsync(SeededTenant seed, DateTimeOffset startsAt)
    {
        var slot = CalendarSeed.Slot(seed, startsAt);
        slot.Claim(seed.Person.PersonId, seed.Service.Id, Now, Now.AddMinutes(15));
        slot.Confirm(Now.AddMinutes(15));
        slot.ClearDomainEvents();

        await using var db = fixture.CreateDbContext();
        db.Events.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    private async Task<Event> AnAvailableSlotAsync(SeededTenant seed, DateTimeOffset startsAt)
    {
        var slot = CalendarSeed.Slot(seed, startsAt);
        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        return slot;
    }

    private async Task<bool> RescheduleAsync(SeededTenant seed, Event oldRun, Event target)
    {
        var envelope = BookingRescheduledMapper.ToEnvelope(
            newBookingId: target.Id,
            previousBookingId: oldRun.Id,
            tenantId: seed.Tenant.Id,
            calendarId: seed.Calendar.Id,
            personId: oldRun.PersonId!.Value,
            previousStartsAt: oldRun.StartsAt,
            newStartsAt: target.StartsAt,
            newEndsAt: target.EndsAt,
            localDate: target.LocalDate,
            occurredAt: Now,
            new UuidV7Generator());

        var request = new BookingRescheduleRequest(
            PreviousBookingId: oldRun.Id,
            CalendarId: seed.Calendar.Id,
            NewEventIds: [target.Id],
            PersonId: oldRun.PersonId!.Value,
            ServiceId: seed.Service.Id,
            OriginConversationId: null,
            Now: Now,
            RescheduledEvent: envelope);

        await using var db = fixture.CreateDbContext();
        return await new BookingRescheduleStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db))
            .TryRescheduleAsync(request, CancellationToken.None);
    }

    /// <summary>The outbox rows of one type for one booking anchor - scoped by partition key because
    /// the <c>outbox</c> table is shared across every test in this collection, so an unscoped query
    /// would see other tests' rows (BookingStoreTests stages BookingPendingStateChanged, for one).</summary>
    private async Task<IReadOnlyList<string>> OutboxRowsOfAsync(string type, EventId partitionKey)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select payload from outbox where type = @type and partition_key = @partitionKey order by occurred_at",
            connection);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("partitionKey", partitionKey.Value.ToString());
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
