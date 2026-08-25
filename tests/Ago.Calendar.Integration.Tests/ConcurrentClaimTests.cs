using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// The product's central race, run for real: two customers reaching for the same free slot at the
/// same moment.
///
/// <para>The domain unit tests prove that a second <c>Claim</c> on a copy already known to be
/// <c>PendingConfirmation</c> throws. They cannot prove this, because here both writers load the row
/// while it is genuinely <c>Available</c> and both pass the in-memory check - which is exactly the
/// interleaving that matters and exactly the one a mocked repository would hide. What separates them
/// is the row's <c>xmin</c>, and only a real Postgres has one.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConcurrentClaimTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset SlotStart = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwoCustomersClaimingTheSameSlot_LeaveExactlyOneWinner()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = CalendarSeed.Slot(seed, SlotStart);
        var second = Customer.Register(
            new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, new PhoneNumber("+79995550000"), CalendarSeed.Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(second);
            await db.SaveChangesAsync();
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        // A DbContext per claimant, both loading before either writes - a shared context would
        // resolve the second load from the identity map and quietly never race at all.
        var outcomes = await Task.WhenAll(
            ClaimAsync(slot.Id, seed.Customer.Id, seed.Service.Id),
            ClaimAsync(slot.Id, second.Id, seed.Service.Id));

        Assert.Equal(1, outcomes.Count(outcome => outcome.Won));
        Assert.Equal(1, outcomes.Count(outcome => !outcome.Won));

        await using var reader = fixture.CreateDbContext();
        var stored = await reader.Events.FirstOrDefaultAsync(e => e.Id == slot.Id);
        Assert.NotNull(stored);
        Assert.Equal(EventStatus.PendingConfirmation, stored.Status);

        // The winner's customer is on the row - not the loser's, and not a mixture of the two.
        var winner = outcomes.Single(outcome => outcome.Won);
        Assert.Equal(winner.CustomerId, stored.CustomerId!.Value);
    }

    [Fact]
    public async Task TheLoser_GetsTheConflictType_NotARawOrmException()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = CalendarSeed.Slot(seed, SlotStart);

        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        // Both load an Available copy...
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var mine = await new EventRepository(firstDb).GetByIdAsync(slot.Id, CancellationToken.None);
        var theirs = await new EventRepository(secondDb).GetByIdAsync(slot.Id, CancellationToken.None);
        Assert.Equal(EventStatus.Available, mine!.Status);
        Assert.Equal(EventStatus.Available, theirs!.Status);

        // ...and both pass the aggregate's own state check, in memory, with no error. That is the
        // point: the aggregate is the first line of defence, never the guarantee.
        mine.Claim(seed.Customer.Id, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));
        theirs.Claim(seed.Customer.Id, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));

        await new EventRepository(firstDb).SaveAsync(mine, CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<EventConcurrencyConflictException>(
            () => new EventRepository(secondDb).SaveAsync(theirs, CancellationToken.None));
        Assert.Equal(slot.Id, conflict.EventId);
    }

    [Fact]
    public async Task AfterALostRace_ReloadingSeesTheWinnersRow_NotAStaleCopy()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = CalendarSeed.Slot(seed, SlotStart);

        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var loser = await new EventRepository(secondDb).GetByIdAsync(slot.Id, CancellationToken.None);

        var winner = await new EventRepository(firstDb).GetByIdAsync(slot.Id, CancellationToken.None);
        winner!.Claim(seed.Customer.Id, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));
        await new EventRepository(firstDb).SaveAsync(winner, CancellationToken.None);

        loser!.Claim(seed.Customer.Id, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));
        await Assert.ThrowsAsync<EventConcurrencyConflictException>(
            () => new EventRepository(secondDb).SaveAsync(loser, CancellationToken.None));

        // The adapter clears the change tracker on conflict precisely so this reload is real. Without
        // it, the identity map would hand back the loser's own uncommitted copy and a retry would
        // re-decide against a fantasy.
        var reloaded = await new EventRepository(secondDb).GetByIdAsync(slot.Id, CancellationToken.None);
        Assert.NotSame(loser, reloaded);
        Assert.Equal(EventStatus.PendingConfirmation, reloaded!.Status);

        // And a retry on the fresh copy now fails for the honest reason - the slot is gone.
        Assert.Throws<InvalidEventStateException>(() => reloaded.Claim(
            seed.Customer.Id, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15)));
    }

    private async Task<(bool Won, CustomerId CustomerId)> ClaimAsync(
        EventId eventId, CustomerId customerId, ServiceId serviceId)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new EventRepository(db);
        var slot = await repository.GetByIdAsync(eventId, CancellationToken.None);

        try
        {
            slot!.Claim(customerId, serviceId, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(15));
            await repository.SaveAsync(slot, CancellationToken.None);
            return (true, customerId);
        }
        catch (EventConcurrencyConflictException)
        {
            return (false, customerId);
        }
        catch (InvalidEventStateException)
        {
            // The other side committed before this one even loaded - the same loss, seen one step
            // earlier. Both outcomes are "somebody else got the slot", which is why the caller only
            // asks whether it won.
            return (false, customerId);
        }
    }
}
