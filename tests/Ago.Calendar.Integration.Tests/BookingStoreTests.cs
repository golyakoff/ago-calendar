using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// The two statements `20-03` turns on, against a real Postgres: the compare-and-set claim and the
/// lead-card upsert. Both are SQL whose exact text is the guarantee, so a fake would prove nothing
/// about either.
///
/// <para>Phone numbers here are invented <c>+7999...</c> values belonging to nobody. A public
/// repository must not carry a real person's contact details, and a phone number is this product's
/// most directly identifying field (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class BookingStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASuccessfulClaim_TransitionsTheSlotAndCreatesTheLeadCard()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);

        var confirmation = await BookAsync(seed, slot.Id, "+79990000010", "Anna");

        Assert.NotNull(confirmation);
        Assert.Equal(slot.Id, confirmation.Value.EventId);
        Assert.Equal(seed.Worker.Id, confirmation.Value.WorkerId);

        // RETURNING gave the caller the slot the statement itself wrote, not a re-read.
        Assert.Equal(slot.StartsAt, confirmation.Value.Slot.StartsAt);
        Assert.Equal(slot.LocalDate, confirmation.Value.LocalDate);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == slot.Id);

        Assert.Equal(EventStatus.PendingConfirmation, stored.Status);
        Assert.Equal(confirmation.Value.CustomerId, stored.CustomerId);
        Assert.Equal(seed.Service.Id, stored.ServiceId);
        Assert.Equal(Now.AddMinutes(15), stored.ConfirmationDeadline);

        var card = await db.Customers.SingleAsync(c => c.Id == confirmation.Value.CustomerId);
        Assert.Equal("+79990000010", card.Phone.Value);
        Assert.Equal("Anna", card.DisplayName);
        Assert.Equal(0, card.NoShowCount);
    }

    [Fact]
    public async Task ASecondClaimAgainstTheSameSlot_LosesAndChangesNothing()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);

        var first = await BookAsync(seed, slot.Id, "+79990000011");
        var second = await BookAsync(seed, slot.Id, "+79990000012");

        Assert.NotNull(first);

        // Null, not an exception: the loser of a race is an ordinary outcome (`4-01`).
        Assert.Null(second);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == slot.Id);
        Assert.Equal(first.Value.CustomerId, stored.CustomerId);

        // And the loser's lead card was rolled back with its claim - no personal data written for a
        // booking that did not happen. This is the assertion the single transaction exists for.
        Assert.False(await db.Customers.AnyAsync(c => c.Phone == new PhoneNumber("+79990000012")));
    }

    [Fact]
    public async Task ARepeatedBookingFromTheSamePhone_UpdatesTheOneLeadCard()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));

        var one = await BookAsync(seed, first.Id, "+79990000013", "Anna");
        var two = await BookAsync(seed, second.Id, "+79990000013", "Anna B", at: Now.AddMinutes(5));

        Assert.NotNull(one);
        Assert.NotNull(two);

        // The Done-when in one line: the second booking reused the first booking's card rather than
        // minting a second one. The id the handler generated for the "if it inserts" case was
        // discarded by ON CONFLICT, which is exactly what DO UPDATE ... RETURNING is for - the caller
        // learns the winning row's id without a second query.
        Assert.Equal(one.Value.CustomerId, two.Value.CustomerId);

        await using var db = fixture.CreateDbContext();
        var cards = await db.Customers
            .Where(c => c.TenantId == seed.Tenant.Id && c.Phone == new PhoneNumber("+79990000013"))
            .ToListAsync();

        var card = Assert.Single(cards);
        Assert.Equal(Now.AddMinutes(5), card.LastSeenAt);
        Assert.Equal(Now, card.FirstSeenAt);

        // "Anna", not "Anna B": a name already on the card is not overwritten by whatever a public
        // form was typed into next time. An operator who corrected it must not be undone.
        Assert.Equal("Anna", card.DisplayName);
    }

    [Fact]
    public async Task ALateBooking_NeverRewindsTheLastSeenWatermark()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));

        await BookAsync(seed, first.Id, "+79990000014", at: Now.AddMinutes(30));

        // A request that was slow in flight arrives with an older instant. GREATEST is what stops it
        // rewinding the watermark - the same rule Customer.Touch enforces in memory, restated in SQL
        // because this statement never goes through that method.
        await BookAsync(seed, second.Id, "+79990000014", at: Now);

        await using var db = fixture.CreateDbContext();
        var card = await db.Customers.SingleAsync(
            c => c.TenantId == seed.Tenant.Id && c.Phone == new PhoneNumber("+79990000014"));

        Assert.Equal(Now.AddMinutes(30), card.LastSeenAt);
    }

    [Fact]
    public async Task ABlankNameOnASecondBooking_DoesNotEraseTheNameAlreadyOnTheCard()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));

        await BookAsync(seed, first.Id, "+79990000015", "Anna");
        await BookAsync(seed, second.Id, "+79990000015", displayName: null);

        await using var db = fixture.CreateDbContext();
        var card = await db.Customers.SingleAsync(
            c => c.TenantId == seed.Tenant.Id && c.Phone == new PhoneNumber("+79990000015"));

        Assert.Equal("Anna", card.DisplayName);
    }

    [Fact]
    public async Task TheSamePhoneAtTwoTenants_IsTwoLeadCards()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);
        var mySlot = await AnAvailableSlotAsync(mine);
        var theirSlot = await AnAvailableSlotAsync(theirs);

        var one = await BookAsync(mine, mySlot.Id, "+79990000016");
        var two = await BookAsync(theirs, theirSlot.Id, "+79990000016");

        // (tenant_id, phone), never phone alone. One tenant's notes must never reach another's
        // console, and the upsert's conflict target is what enforces that at the storage level.
        Assert.NotEqual(one!.Value.CustomerId, two!.Value.CustomerId);
    }

    [Fact]
    public async Task AnEventOnAnotherCalendar_IsNotClaimable()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);
        var theirSlot = await AnAvailableSlotAsync(theirs);

        // My calendar id in the route, their event id in the path. The claim's WHERE clause carries
        // both, so the mismatch makes the row unclaimable rather than merely un-validated - which
        // matters because this endpoint is unauthenticated and the calendar id is the only thing
        // tying a request to a tenant.
        var confirmation = await BookAsync(mine, theirSlot.Id, "+79990000017");

        Assert.Null(confirmation);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Available, (await db.Events.SingleAsync(e => e.Id == theirSlot.Id)).Status);
    }

    [Fact]
    public async Task ASlotThatHasAlreadyStarted_IsNotClaimable()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);

        // Event.Claim's own precondition, restated in the WHERE clause where it cannot go stale.
        var confirmation = await BookAsync(seed, slot.Id, "+79990000018", at: slot.StartsAt.AddMinutes(1));

        Assert.Null(confirmation);
    }

    [Fact]
    public async Task ABlockedSlot_IsNotClaimable()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var blocked = Event.BlockOut(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(Now.AddHours(6), Now.AddHours(6).AddMinutes(45)),
            DateOnly.FromDateTime(Now.UtcDateTime), Now);

        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([blocked], CancellationToken.None);
        }

        // A day off (`20-02`) is a Blocked row, and the claim's status predicate names Available
        // exactly - so a tenant's closure cannot be booked through.
        Assert.Null(await BookAsync(seed, blocked.Id, "+79990000019"));
    }

    private async Task<Event> AnAvailableSlotAsync(SeededTenant seed, DateTimeOffset? startsAt = null)
    {
        var slot = CalendarSeed.Slot(seed, startsAt ?? Now.AddHours(2));
        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        return slot;
    }

    private async Task<BookingConfirmation?> BookAsync(
        SeededTenant seed,
        EventId eventId,
        string phone,
        string? displayName = null,
        DateTimeOffset? at = null)
    {
        var now = at ?? Now;
        await using var db = fixture.CreateDbContext();
        return await new BookingStore(db).TryBookAsync(
            new BookingAttempt(
                seed.Tenant.Id,
                seed.Calendar.Id,
                eventId,
                seed.Service.Id,
                new PhoneNumber(phone),
                displayName,
                new CustomerId(CalendarSeed.NewId()),
                now,
                now.AddMinutes(15)),
            CancellationToken.None);
    }
}
