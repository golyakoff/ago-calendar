using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Concurrency.Tests;

/// <summary>
/// N customers reaching for the same slot at the same instant. This is the item's whole point, so
/// the tests are built to make the collision unavoidable rather than likely.
///
/// <para><b>How contention is actually forced, not merely hoped for.</b> Three things together:
/// every caller gets its own <c>DbContext</c> on its own pooled connection, so the writers are
/// genuinely simultaneous rather than sequential awaits wearing a <c>Task.WhenAll</c>; every caller
/// is parked on one shared <see cref="TaskCompletionSource"/> gate and released together, so they
/// arrive inside the same few milliseconds instead of whenever the scheduler got to them; and the
/// connections are opened *before* the gate, so the first thing after release is the statement
/// itself rather than a connection handshake that would spread the arrivals back out. The last one
/// is the part that is easy to leave out and quietly turns a race test into a sequence test.</para>
///
/// <para>`4-01`'s <c>OperatorCapacityStoreTests</c> is the bar this is held to: the assertion is on
/// the rows afterwards, never on "no exception was thrown".</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public class ConcurrentBookingTests(ConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(24)]
    public async Task ManyCustomersReachingForOneSlot_ProduceExactlyOneBooking(int customers)
    {
        var seed = await SeedAsync();
        var slot = await AnAvailableSlotAsync(seed);

        var results = await RaceAsync(customers, phoneOffset: 0, seed, slot.Id);

        var winners = results.Where(result => result is not null).ToList();

        // Exactly one, at every degree of contention. Two would be two customers in one chair, which
        // is the defect this product does not survive; zero would mean the mechanism rejects
        // everybody under load, which is the failure a naive retry loop produces.
        Assert.Single(winners);
        Assert.Equal(customers - 1, results.Count(result => result is null));

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == slot.Id);

        // Not a torn state: one status, one customer, one deadline, and the customer is the winner's.
        Assert.Equal(EventStatus.PendingConfirmation, stored.Status);
        Assert.Equal(winners[0]!.Value.CustomerId, stored.CustomerId);
        Assert.NotNull(stored.ConfirmationDeadline);
        Assert.Equal(seed.ServiceId, stored.ServiceId);

        // And every loser rolled back whole: exactly one lead card exists for this burst, the
        // winner's. The losers' phone numbers were never written, which is the data-minimisation
        // property the single transaction is there for.
        var cards = await db.Customers.Where(c => c.TenantId == seed.TenantId).ToListAsync();
        Assert.Single(cards);
        Assert.Equal(winners[0]!.Value.CustomerId, cards[0].Id);
    }

    [Fact]
    public async Task TheLosersLearnTheyLostWithoutAnException()
    {
        var seed = await SeedAsync();
        var slot = await AnAvailableSlotAsync(seed);

        // RaceAsync does not catch anything: a thrown exception fails the test rather than being
        // counted as a loss. That is the assertion - a lost race is a null, never a throw, so no
        // caller is ever tempted to use catch as control flow (`4-01`, concurrency.md).
        var results = await RaceAsync(16, phoneOffset: 100, seed, slot.Id);

        Assert.Single(results, result => result is not null);
    }

    [Fact]
    public async Task ManyCustomersReachingForDifferentSlots_AllSucceed()
    {
        // The other half of the claim's correctness, and the one a too-eager lock would break: the
        // statement must not serialise bookings that are not actually competing. Sixteen customers,
        // sixteen slots, all at once.
        var seed = await SeedAsync();
        var slots = new List<Event>();
        for (var i = 0; i < 16; i++)
        {
            slots.Add(await AnAvailableSlotAsync(seed, Now.AddHours(2 + i)));
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = slots.Select((slot, index) =>
            Task.Run(() => AttemptAsync(seed, slot.Id, $"+7999100{index:D4}", gate))).ToList();

        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(16, results.Select(result => result!.Value.CustomerId).Distinct().Count());
    }

    [Fact]
    public async Task OnePhoneBookingSeveralSlotsAtOnce_EndsWithExactlyOneLeadCard()
    {
        // The upsert's own race, which the slot claim's race hides: every one of these succeeds at
        // claiming a different slot, so all sixteen reach ON CONFLICT (tenant_id, phone) against the
        // same key at the same instant. Postgres arbitrates on the unique index inside the statement;
        // a read-then-insert would produce duplicate cards here, or a unique-violation storm.
        var seed = await SeedAsync();
        var slots = new List<Event>();
        for (var i = 0; i < 16; i++)
        {
            slots.Add(await AnAvailableSlotAsync(seed, Now.AddHours(2 + i)));
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = slots.Select(slot =>
            Task.Run(() => AttemptAsync(seed, slot.Id, "+79992000001", gate))).ToList();

        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, result => Assert.NotNull(result));

        await using var db = fixture.CreateDbContext();
        var cards = await db.Customers.Where(c => c.TenantId == seed.TenantId).ToListAsync();

        Assert.Single(cards);

        // Every booking points at that one card.
        Assert.All(results, result => Assert.Equal(cards[0].Id, result!.Value.CustomerId));
    }

    /// <summary>
    /// `20-18`'s own highest-stakes guarantee, and the item's own required proof: two customers racing
    /// for overlapping <b>runs</b> - not the same single slot, but two multi-slot bookings that share
    /// exactly one slot - can never both win, because that shared row can only be updated once. Three
    /// consecutive slots; one customer wants the first two, the other wants the last two, so slot 1 is
    /// the one row both attempts genuinely contend for.
    /// </summary>
    [Fact]
    public async Task TwoCustomersRacingForOverlappingRuns_ExactlyOneWins_AndNoPartialClaimSurvives()
    {
        var seed = await SeedAsync();
        var slots = await ConsecutiveSlotsAsync(seed, count: 3, slotMinutes: 30, bufferMinutes: 10);
        var runOne = new List<EventId> { slots[0].Id, slots[1].Id };
        var runTwo = new List<EventId> { slots[1].Id, slots[2].Id };

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptOne = Task.Run(() => AttemptAsync(seed, runOne, "+79993100001", gate));
        var attemptTwo = Task.Run(() => AttemptAsync(seed, runTwo, "+79993100002", gate));

        await Task.Delay(50);
        gate.SetResult();
        var results = await Task.WhenAll(attemptOne, attemptTwo);

        // Exactly one run wins, whole - never both, and never a torn claim where one attempt got
        // some of its slots and not others (BookingStore rolls the whole transaction back the moment
        // its own rows-affected count falls short of the run's own length).
        var winners = results.Where(result => result is not null).ToList();
        Assert.Single(winners);
        var winner = winners[0]!.Value;

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events
            .Where(e => e.Id == slots[0].Id || e.Id == slots[1].Id || e.Id == slots[2].Id)
            .ToDictionaryAsync(e => e.Id);

        // Every slot the winner's own run named is claimed, sharing that run's own booking id.
        foreach (var claimedId in winner.EventIds)
        {
            Assert.Equal(EventStatus.PendingConfirmation, stored[claimedId].Status);
            Assert.Equal(winner.BookingId, stored[claimedId].BookingId);
        }

        // Slot 1 - the shared row both runs actually contended for - went to whichever run won, and
        // is claimed either way.
        Assert.Equal(EventStatus.PendingConfirmation, stored[slots[1].Id].Status);

        // The loser's own slot - the one not shared with the winner's run - is untouched: still
        // Available, no booking id, no customer, no deadline. That is the whole assertion this test
        // exists for: the loser did not get a partial claim, it got nothing at all.
        var loserOnlySlotId = winner.EventIds.Contains(slots[0].Id) ? slots[2].Id : slots[0].Id;
        var loserSlot = stored[loserOnlySlotId];
        Assert.Equal(EventStatus.Available, loserSlot.Status);
        Assert.Null(loserSlot.BookingId);
        Assert.Null(loserSlot.CustomerId);
        Assert.Null(loserSlot.ConfirmationDeadline);

        // And the loser's own lead card was never written - the same data-minimisation property the
        // single-slot race already proves, restated for a run: a failed multi-slot attempt leaves
        // exactly as little trace as a failed single-slot one.
        var cards = await db.Customers.Where(c => c.TenantId == seed.TenantId).ToListAsync();
        Assert.Single(cards);
        Assert.Equal(winner.CustomerId, cards[0].Id);
    }

    private async Task<IReadOnlyList<BookingConfirmation?>> RaceAsync(
        int callers, int phoneOffset, SeededCalendar seed, EventId eventId)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, callers)
            .Select(index => Task.Run(() => AttemptAsync(seed, eventId, $"+7999{phoneOffset + index:D7}", gate)))
            .ToList();

        // Give every caller a moment to reach the gate before opening it. Without this the first
        // task can finish before the last one starts, and the test degrades into a sequence.
        await Task.Delay(50);
        gate.SetResult();

        return await Task.WhenAll(attempts);
    }

    private Task<BookingConfirmation?> AttemptAsync(
        SeededCalendar seed, EventId eventId, string phone, TaskCompletionSource gate) =>
        AttemptAsync(seed, [eventId], phone, gate);

    private async Task<BookingConfirmation?> AttemptAsync(
        SeededCalendar seed, IReadOnlyList<EventId> eventIds, string phone, TaskCompletionSource gate)
    {
        await using var db = fixture.CreateDbContext();

        // Open the connection before waiting on the gate: a handshake after release would stagger
        // the arrivals by however long each one took to connect, which is exactly the interval a
        // compare-and-set is supposed to be tested across.
        await db.Database.OpenConnectionAsync();

        var store = new BookingStore(db);
        await gate.Task;

        return await store.TryBookAsync(
            new BookingAttempt(
                seed.TenantId,
                seed.CalendarId,
                eventIds,
                seed.ServiceId,
                new PhoneNumber(phone),
                "Anna",
                new CustomerId(NewId()),
                Now,
                Now.AddMinutes(15),
                Now),
            CancellationToken.None);
    }

    private async Task<Event> AnAvailableSlotAsync(SeededCalendar seed, DateTimeOffset? startsAt = null)
    {
        var start = startsAt ?? Now.AddHours(2);
        var slot = Event.Materialize(
            new EventId(NewId()), seed.TenantId, seed.CalendarId, seed.WorkerId,
            new TimeSlot(start, start.AddMinutes(45)), DateOnly.FromDateTime(start.UtcDateTime), Now);

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        return slot;
    }

    /// <summary>`20-18`: <see cref="AnAvailableSlotAsync"/>'s own multi-slot generalisation - a real
    /// run of consecutive <see cref="EventStatus.Available"/> rows, inserted the same way (through
    /// <see cref="EventRepository.AddRangeAsync"/>, so the exclusion constraint is exercised exactly
    /// as it is in production).</summary>
    private async Task<IReadOnlyList<Event>> ConsecutiveSlotsAsync(
        SeededCalendar seed, int count, int slotMinutes, int bufferMinutes)
    {
        var slots = new List<Event>(count);
        var start = Now.AddHours(2);
        for (var i = 0; i < count; i++)
        {
            slots.Add(Event.Materialize(
                new EventId(NewId()), seed.TenantId, seed.CalendarId, seed.WorkerId,
                new TimeSlot(start, start.AddMinutes(slotMinutes)), DateOnly.FromDateTime(start.UtcDateTime), Now));
            start = start.AddMinutes(slotMinutes + bufferMinutes);
        }

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync(slots, CancellationToken.None);
        return slots;
    }

    private async Task<SeededCalendar> SeedAsync()
    {
        var tenant = Tenant.Register(new TenantId(NewId()), "Barbershop", new TenantPublicKey("shop-" + NewId().ToString("N")), Now);
        var calendar = BookingCalendar.Create(
            new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), Now);
        var worker = Worker.Create(new WorkerId(NewId()), tenant.Id, "Doe", "Alex", null, Now);
        var service = Service.Create(new ServiceId(NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45));

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);
        await db.SaveChangesAsync();

        return new SeededCalendar(tenant.Id, calendar.Id, worker.Id) { ServiceId = service.Id };
    }

    private static Guid NewId() => Guid.CreateVersion7(DateTimeOffset.UtcNow);
}
