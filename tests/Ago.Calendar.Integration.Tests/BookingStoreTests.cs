using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
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
    public async Task ASuccessfulClaim_TransitionsTheSlotAndCreatesThePersonRecord()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);

        var confirmation = await BookAsync(seed, slot.Id, "+79990000010", "Anna");

        Assert.NotNull(confirmation);
        Assert.Equal(slot.Id, confirmation.Value.BookingId);
        Assert.Equal([slot.Id], confirmation.Value.EventIds);
        Assert.Equal(seed.Worker.Id, confirmation.Value.WorkerId);

        // RETURNING gave the caller the slot the statement itself wrote, not a re-read.
        Assert.Equal(slot.StartsAt, confirmation.Value.Slot.StartsAt);
        Assert.Equal(slot.LocalDate, confirmation.Value.LocalDate);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == slot.Id);

        Assert.Equal(EventStatus.PendingConfirmation, stored.Status);
        Assert.Equal(confirmation.Value.PersonId, stored.PersonId);
        Assert.Equal(seed.Service.Id, stored.ServiceId);
        Assert.Equal(Now.AddMinutes(15), stored.ConfirmationDeadline);

        // `20-18`: a single-slot booking is its own anchor - the claim wrote its own id into its own
        // booking_id, not null.
        Assert.Equal(slot.Id, stored.BookingId);

        var record = await db.PersonRecords.SingleAsync(p => p.PersonId == confirmation.Value.PersonId);
        Assert.Equal("+79990000010", record.Phone.Value);
        Assert.Equal(0, record.NoShowCount);
    }

    /// <summary>`adr/0184` decision 2: a booking whose person id this product minted (no chat origin)
    /// announces that person to chat through the outbox, in the claim's own transaction - and a
    /// chat-origin booking, whose person chat already knows, stages nothing of the kind.</summary>
    [Fact]
    public async Task ALocallyMintedPerson_IsAnnouncedOnTheOutbox_ButAChatKnownPersonIsNot()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));

        var minted = await BookAsync(seed, first.Id, "+79990000040", "Anna");
        var chatKnown = await BookAsync(seed, second.Id, "+79990000041", "Boris", registerPerson: false);

        Assert.NotNull(minted);
        Assert.NotNull(chatKnown);

        var rows = await OutboxRowsOfTypeAsync("PersonRegistered");
        var row = Assert.Single(rows, r => r.PartitionKey == minted.Value.PersonId.ToString());
        Assert.DoesNotContain(rows, r => r.PartitionKey == chatKnown.Value.PersonId.ToString());

        var payload = JsonSerializer.Deserialize<PersonRegistered>(row.Payload)!;
        Assert.Equal(minted.Value.PersonId, payload.PersonId);
        Assert.Equal(seed.Tenant.Id.Value, payload.AccountId);
        Assert.Equal("+79990000040", payload.Phone);
        Assert.Equal("Anna", payload.Name);
    }

    /// <summary>The announcement rides the claim's transaction: a lost race leaves no PersonRegistered
    /// row behind, exactly as it leaves no person record - chat must never learn of a person whose
    /// booking never happened.</summary>
    [Fact]
    public async Task ALostRace_StagesNoPersonRegistered()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);

        var winner = await BookAsync(seed, slot.Id, "+79990000042");
        var loser = await BookAsync(seed, slot.Id, "+79990000043");

        Assert.NotNull(winner);
        Assert.Null(loser);

        var rows = await OutboxRowsOfTypeAsync("PersonRegistered");
        Assert.Single(rows, r => r.PartitionKey == winner.Value.PersonId.ToString());
        Assert.DoesNotContain(rows, r => r.Payload.Contains("+79990000043", StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<(string PartitionKey, string Payload)>> OutboxRowsOfTypeAsync(string type)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select partition_key, payload from outbox where type = @type order by occurred_at", connection);
        command.Parameters.AddWithValue("type", type);
        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>
    /// `25-63`: the claim's own half of this item's one push, staged onto the real `outbox` table in
    /// the same transaction as the claim itself - see BookingStore.TryBookAsync's own remarks for why
    /// that transaction needed a real SaveChangesAsync added to it (this raw-SQL claim never called
    /// one before). `BookingPendingFanoutEndToEndTests` (this same test project) proves the row this
    /// test finds is actually delivered to a connected operator, over a real broker; this test proves
    /// only that the row lands correctly, the identical split `ModuleQuantityImpactRequestedWireTests`'
    /// own remarks draw between staging and delivery.
    /// </summary>
    [Fact]
    public async Task ASuccessfulClaim_StagesABookingPendingStateChangedRow()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);

        var confirmation = await BookAsync(seed, slot.Id, "+79990000011", "Boris");
        Assert.NotNull(confirmation);

        // Queried by partition_key, not id: unlike BookingConfirmedMapper (which reuses the booking's
        // own id as the outbox row's id, since a booking can only ever be confirmed once),
        // BookingPendingStateChangedMapper mints a fresh MessageId every time - this fact can be
        // staged twice for the same booking (entering, then later leaving) - so the booking's own id
        // lives only in partition_key, this mapper's own remarks explain why.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select type, partition_key, payload from outbox where partition_key = @partitionKey and type = @type",
            connection);
        command.Parameters.AddWithValue("partitionKey", slot.Id.Value.ToString());
        command.Parameters.AddWithValue("type", nameof(BookingPendingStateChanged));
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "No outbox row was staged for the claim.");
        Assert.Equal(nameof(BookingPendingStateChanged), reader.GetString(0));
        Assert.Equal(slot.Id.Value.ToString(), reader.GetString(1));

        var contract = System.Text.Json.JsonSerializer.Deserialize<BookingPendingStateChanged>(reader.GetString(2))!;
        Assert.Equal(seed.Tenant.Id.Value, contract.TenantId);
        Assert.Equal("PendingConfirmation", contract.Status);

        Assert.False(await reader.ReadAsync(), "Exactly one row per claim, not one per attempt.");
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
        Assert.Equal(first.Value.PersonId, stored.PersonId);

        // And the loser's person record was rolled back with its claim - no personal data written for a
        // booking that did not happen. This is the assertion the single transaction exists for.
        Assert.False(await db.PersonRecords.AnyAsync(p => p.Phone == new PhoneNumber("+79990000012")));
    }

    /// <summary>`adr/0184`: the record is keyed by the person id, so a returning person - the same
    /// id, chat's own visitor id on a chat-origin booking - lands on the one row, whatever number they
    /// typed this time.</summary>
    [Fact]
    public async Task ARepeatedBookingByTheSamePerson_UpdatesTheOneRecord()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));
        var personId = CalendarSeed.NewId();

        var one = await BookAsync(seed, first.Id, "+79990000013", personId: personId);
        var two = await BookAsync(seed, second.Id, "+79990000013", at: Now.AddMinutes(5), personId: personId);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal(personId, one.Value.PersonId);
        Assert.Equal(personId, two.Value.PersonId);

        await using var db = fixture.CreateDbContext();
        var records = await db.PersonRecords.Where(p => p.TenantId == seed.Tenant.Id).ToListAsync();

        var record = Assert.Single(records, p => p.PersonId == personId);
        Assert.Equal(Now.AddMinutes(5), record.LastSeenAt);
        Assert.Equal(Now, record.FirstSeenAt);
    }

    /// <summary>`adr/0147`'s "a phone is a hint, not proof", kept by `adr/0184`: two bookings with the
    /// same number under two person ids are two records, never silently one.</summary>
    [Fact]
    public async Task TheSamePhoneUnderTwoPersonIds_IsTwoRecords()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));

        var one = await BookAsync(seed, first.Id, "+79990000044");
        var two = await BookAsync(seed, second.Id, "+79990000044");

        Assert.NotEqual(one!.Value.PersonId, two!.Value.PersonId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.PersonRecords.CountAsync(
            p => p.TenantId == seed.Tenant.Id && p.Phone == new PhoneNumber("+79990000044")));
    }

    [Fact]
    public async Task ALateBooking_NeverRewindsTheLastSeenWatermark()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));
        var personId = CalendarSeed.NewId();

        await BookAsync(seed, first.Id, "+79990000014", at: Now.AddMinutes(30), personId: personId);

        // A request that was slow in flight arrives with an older instant. GREATEST is what stops it
        // rewinding the watermark - the same rule PersonRecord.Touch enforces in memory, restated in SQL
        // because this statement never goes through that method.
        await BookAsync(seed, second.Id, "+79990000014", at: Now, personId: personId);

        await using var db = fixture.CreateDbContext();
        var record = await db.PersonRecords.SingleAsync(p => p.PersonId == personId);

        Assert.Equal(Now.AddMinutes(30), record.LastSeenAt);
    }

    /// <summary>`adr/0184` (author decision O1): the phone follows the booking, and the verification
    /// follows the phone - a person who books again with a different number gets that number written
    /// and its own verification mark (this attempt's, not the old number's earlier one), because nobody
    /// ever proved the new number back then.</summary>
    [Fact]
    public async Task ABookingWithADifferentPhone_ByTheSamePerson_ReplacesThePhoneAndItsVerification()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));
        var personId = CalendarSeed.NewId();
        var oldNumberVerifiedAt = Now.AddDays(-1);
        var newNumberVerifiedAt = Now.AddMinutes(5);

        await BookAsync(seed, first.Id, "+79990000015", phoneVerifiedAt: oldNumberVerifiedAt, personId: personId);
        await BookAsync(seed, second.Id, "+79990000016", at: Now.AddMinutes(5), phoneVerifiedAt: newNumberVerifiedAt,
            personId: personId, registerPerson: false);

        await using var db = fixture.CreateDbContext();
        var record = await db.PersonRecords.SingleAsync(p => p.PersonId == personId);

        Assert.Equal("+79990000016", record.Phone.Value);
        // Not the old number's earlier instant - the earliest-wins rule applies only while the number is
        // the same one it was proven for.
        Assert.Equal(newNumberVerifiedAt, record.PhoneVerifiedAt);
    }

    /// <summary>`20-09`'s own Done-when: the claim writes the caller's asserted verification timestamp
    /// onto the person record, in the same transaction as the claim itself.</summary>
    [Fact]
    public async Task AVerifiedClaim_SnapshotsThePhoneVerifiedAtTimestampOntoThePersonRecord()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await AnAvailableSlotAsync(seed);
        var verifiedAt = Now.AddDays(-2);

        var confirmation = await BookAsync(seed, slot.Id, "+79990000020", phoneVerifiedAt: verifiedAt);

        Assert.NotNull(confirmation);
        await using var db = fixture.CreateDbContext();
        var record = await db.PersonRecords.SingleAsync(p => p.PersonId == confirmation!.Value.PersonId);
        Assert.Equal(verifiedAt, record.PhoneVerifiedAt);
    }

    /// <summary>The "keep what's already there" rule on <c>phone_verified_at</c> (`UpsertPersonRecordSql`'s
    /// own remarks): a phone verified once for an earlier booking by this person must not need
    /// re-proving for a later one from the same number, and the *first* verification is the honest
    /// "since when" answer - a later, different assertion must never silently replace it.</summary>
    [Fact]
    public async Task ARepeatedBookingFromAnAlreadyVerifiedPhone_KeepsTheEarlierVerificationTimestamp()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var first = await AnAvailableSlotAsync(seed);
        var second = await AnAvailableSlotAsync(seed, startsAt: Now.AddHours(4));
        var firstVerifiedAt = Now.AddDays(-5);
        var laterVerifiedAt = Now.AddMinutes(5);
        var personId = CalendarSeed.NewId();

        await BookAsync(seed, first.Id, "+79990000021", phoneVerifiedAt: firstVerifiedAt, personId: personId);
        await BookAsync(seed, second.Id, "+79990000021", at: Now.AddMinutes(5), phoneVerifiedAt: laterVerifiedAt, personId: personId);

        await using var db = fixture.CreateDbContext();
        var record = await db.PersonRecords.SingleAsync(p => p.PersonId == personId);

        Assert.Equal(firstVerifiedAt, record.PhoneVerifiedAt);
    }

    [Fact]
    public async Task TheSamePhoneAtTwoTenants_IsTwoRecords()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);
        var mySlot = await AnAvailableSlotAsync(mine);
        var theirSlot = await AnAvailableSlotAsync(theirs);

        var one = await BookAsync(mine, mySlot.Id, "+79990000016");
        var two = await BookAsync(theirs, theirSlot.Id, "+79990000016");

        // Two tenants, two person ids, two records: one tenant's operational facts never reach
        // another's console.
        Assert.NotEqual(one!.Value.PersonId, two!.Value.PersonId);
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

    /// <summary>`20-18`: the multi-row claim's own ordinary case - three consecutive slots, all
    /// Available, claimed together in one statement.</summary>
    [Fact]
    public async Task AMultiSlotRun_IsClaimedWhole_WithOneSharedBookingIdOnEveryRow()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var run = await ConsecutiveSlotsAsync(seed, count: 3, slotMinutes: 30, bufferMinutes: 10);

        var confirmation = await BookAsync(seed, [run[0].Id, run[1].Id, run[2].Id], "+79990000030");

        Assert.NotNull(confirmation);
        Assert.Equal(run[0].Id, confirmation.Value.BookingId);
        Assert.Equal([run[0].Id, run[1].Id, run[2].Id], confirmation.Value.EventIds);

        // The run's own whole span - first slot's start to last slot's end, buffers included - not
        // just the first slot's own 30 minutes.
        Assert.Equal(run[0].StartsAt, confirmation.Value.Slot.StartsAt);
        Assert.Equal(run[2].EndsAt, confirmation.Value.Slot.EndsAt);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events
            .Where(e => e.Id == run[0].Id || e.Id == run[1].Id || e.Id == run[2].Id)
            .ToListAsync();

        Assert.All(stored, e => Assert.Equal(EventStatus.PendingConfirmation, e.Status));
        Assert.All(stored, e => Assert.Equal(run[0].Id, e.BookingId));
        Assert.All(stored, e => Assert.Equal(confirmation.Value.PersonId, e.PersonId));
        Assert.All(stored, e => Assert.Equal(Now.AddMinutes(15), e.ConfirmationDeadline));
    }

    /// <summary>The item's own fails-before, proven directly: a run whose middle slot is already
    /// gone is not claimable as a whole, and the rows-affected count falling short of the run's own
    /// length is what rolls the entire attempt back - not just the missing slot's own absence, but the
    /// two slots that *were* still Available a moment before this statement ran.</summary>
    [Fact]
    public async Task ARunWithOneSlotAlreadyTaken_ClaimsNothingAtAll()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var run = await ConsecutiveSlotsAsync(seed, count: 3, slotMinutes: 30, bufferMinutes: 10);

        // Somebody else already has the middle slot.
        await BookAsync(seed, run[1].Id, "+79990000031");

        var confirmation = await BookAsync(seed, [run[0].Id, run[1].Id, run[2].Id], "+79990000032");

        Assert.Null(confirmation);

        await using var db = fixture.CreateDbContext();
        var first = await db.Events.SingleAsync(e => e.Id == run[0].Id);
        var last = await db.Events.SingleAsync(e => e.Id == run[2].Id);

        // The two slots this second attempt could have taken are untouched - no torn claim survives
        // a rolled-back transaction.
        Assert.Equal(EventStatus.Available, first.Status);
        Assert.Null(first.BookingId);
        Assert.Equal(EventStatus.Available, last.Status);
        Assert.Null(last.BookingId);

        // And no second person record for the attempt that lost - the same data-minimisation property
        // the single-slot case already proves.
        Assert.False(await db.PersonRecords.AnyAsync(p => p.Phone == new PhoneNumber("+79990000032")));
    }

    private async Task<IReadOnlyList<Event>> ConsecutiveSlotsAsync(
        SeededTenant seed, int count, int slotMinutes, int bufferMinutes)
    {
        var slots = new List<Event>(count);
        var start = Now.AddHours(2);
        for (var i = 0; i < count; i++)
        {
            slots.Add(CalendarSeed.Slot(seed, start, slotMinutes));
            start = start.AddMinutes(slotMinutes + bufferMinutes);
        }

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync(slots, CancellationToken.None);
        return slots;
    }

    private async Task<Event> AnAvailableSlotAsync(SeededTenant seed, DateTimeOffset? startsAt = null)
    {
        var slot = CalendarSeed.Slot(seed, startsAt ?? Now.AddHours(2));
        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        return slot;
    }

    /// <param name="personId">`adr/0184`: the person this booking is for - a fresh, "locally minted" id
    /// by default, or a caller-supplied one to model the same person booking again.</param>
    /// <param name="registerPerson">Whether the attempt models a locally minted person id (a booking
    /// with no chat origin, which announces the person on the outbox) - the default - or a chat-known
    /// one, which stages nothing.</param>
    private Task<BookingConfirmation?> BookAsync(
        SeededTenant seed,
        EventId eventId,
        string phone,
        string? displayName = null,
        DateTimeOffset? at = null,
        DateTimeOffset? phoneVerifiedAt = null,
        Guid? personId = null,
        bool registerPerson = true) =>
        BookAsync(seed, [eventId], phone, displayName, at, phoneVerifiedAt, personId, registerPerson);

    private async Task<BookingConfirmation?> BookAsync(
        SeededTenant seed,
        IReadOnlyList<EventId> eventIds,
        string phone,
        string? displayName = null,
        DateTimeOffset? at = null,
        DateTimeOffset? phoneVerifiedAt = null,
        Guid? personId = null,
        bool registerPerson = true)
    {
        var now = at ?? Now;
        await using var db = fixture.CreateDbContext();
        return await new BookingStore(db, new EfOutboxWriter<AgoCalendarDbContext>(db), new UuidV7Generator()).TryBookAsync(
            new BookingAttempt(
                seed.Tenant.Id,
                seed.Calendar.Id,
                eventIds,
                seed.Service.Id,
                new PhoneNumber(phone),
                registerPerson ? new PersonRegistration(displayName) : null,
                now,
                now.AddMinutes(15),
                phoneVerifiedAt ?? now,
                personId ?? CalendarSeed.NewId(),
                null),
            CancellationToken.None);
    }

    /// <summary>
    /// `22-08`/`CLAUDE.md` rule 8: the load-bearing fix - a suspended tenant's own lease, read live
    /// inside <c>ClaimSlotSql</c>'s own <c>WHERE</c> clause, refuses the claim. Against real Postgres,
    /// not a fake: the guarantee is the SQL text's own subquery, exactly the reason this whole file's
    /// class remarks give for testing the two statements against a real database at all.
    /// </summary>
    [Fact]
    public async Task ASuspendedTenant_CannotClaimANewBooking()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await SuspendTenantAsync(seed.Tenant.Id, Now.AddMinutes(5));
        var slot = await AnAvailableSlotAsync(seed);

        var confirmation = await BookAsync(seed, slot.Id, "+79990000020", "Suspended Test");

        Assert.Null(confirmation);
        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == slot.Id);
        Assert.Equal(EventStatus.Available, stored.Status);
    }

    /// <summary>The mirror of the test above - once the lease instant itself has passed, the identical
    /// tenant is bookable again with no manual step, the same "expiry is checked live, never swept"
    /// property <c>Tenant.SuspensionValidUntil</c>'s own remarks state.</summary>
    [Fact]
    public async Task ATenantWhoseLeaseHasAlreadyExpired_CanClaimANewBooking()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await SuspendTenantAsync(seed.Tenant.Id, Now.AddMinutes(-1));
        var slot = await AnAvailableSlotAsync(seed);

        var confirmation = await BookAsync(seed, slot.Id, "+79990000021", "Expired Lease Test");

        Assert.NotNull(confirmation);
    }

    /// <summary>Lifting a suspension (a <see langword="null"/> lease) restores bookings immediately -
    /// no re-provisioning, no new credential, the identical fact a real claim against real Postgres is
    /// the only honest way to prove.</summary>
    [Fact]
    public async Task ATenantWhoseSuspensionWasLifted_CanClaimANewBooking()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await SuspendTenantAsync(seed.Tenant.Id, Now.AddMinutes(30));
        await SuspendTenantAsync(seed.Tenant.Id, null);
        var slot = await AnAvailableSlotAsync(seed);

        var confirmation = await BookAsync(seed, slot.Id, "+79990000022", "Lifted Test");

        Assert.NotNull(confirmation);
    }

    private async Task SuspendTenantAsync(TenantId tenantId, DateTimeOffset? validUntil)
    {
        await using var db = fixture.CreateDbContext();
        await new SuspensionLeaseStore(new TenantRepository(db)).ApplyAsync(tenantId, validUntil, CancellationToken.None);
    }
}
