using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-60`/`adr/0147`: the other half of that ADR's own choice, against a real Postgres. Every port
/// real except the clock and id generator, the same discipline <see cref="ContactPhoneRevealTests"/>
/// already applies for a different consequential act.
///
/// <para><b>What this file demonstrates rather than asserts.</b> Not "the merge succeeded" - the
/// concrete before/after booking ownership: which <c>events</c> rows moved, which stayed, and that a
/// tenant-mismatched request cannot move a row even when the store's own bulk statement is called
/// directly, bypassing the handler's own tenant check entirely.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CustomerMergeTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = CalendarSeed.Now;

    [Fact]
    public async Task MergingTwoCustomers_MovesEveryBookingFromTheAbsorbedRowOntoTheSurvivor_AndRecordsTheMerge()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // The absorbed row: a second, chat-sourced customer sharing the seeded (booking-sourced)
        // customer's own phone - `23-59`'s own scenario, the duplicate this item exists to let a
        // human resolve.
        var absorbed = Customer.RegisterFromChat(
            new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Customer.Phone, Guid.NewGuid(), Now);
        absorbed.RecordNoShow(Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(absorbed);
            // One booking on the survivor, two on the row about to be absorbed - including one that
            // is not `Booked`, so the reassignment is proven for every status, not only the
            // confirmed one.
            db.Events.Add(BookedEventFor(seed, seed.Customer.Id, Now.AddDays(1)));
            var absorbedBooked = BookedEventFor(seed, absorbed.Id, Now.AddDays(2));
            var absorbedCancelled = BookedEventFor(seed, absorbed.Id, Now.AddDays(3));
            absorbedCancelled.Cancel(Now);
            db.Events.AddRange(absorbedBooked, absorbedCancelled);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new MergeCustomersHandler(
                new CustomerRepository(db), new CustomerMergeStore(db),
                new PermissionChecker(new RoleAssignmentProjectionStore(db)),
                new UuidV7Generator(), new FixedClock(Now.AddHours(1)));

            var result = await handler.HandleAsync(
                new MergeCustomers(seed.OperatorId, seed.Tenant.Id, seed.Customer.Id, absorbed.Id), CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(seed.Customer.Id, result.Value.SurvivorCustomerId);
            Assert.Equal(absorbed.Id, result.Value.AbsorbedCustomerId);
            Assert.Equal(2, result.Value.BookingsMoved);
        }

        // The concrete before/after: every event that named the absorbed customer now names the
        // survivor, and nothing that already named the survivor changed.
        await using (var reader = fixture.CreateDbContext())
        {
            var survivorsEvents = await reader.Events
                .Where(e => e.TenantId == seed.Tenant.Id && e.CustomerId == seed.Customer.Id)
                .Select(e => e.Id)
                .ToListAsync();
            Assert.Equal(3, survivorsEvents.Count);

            var stillOnAbsorbed = await reader.Events.CountAsync(e => e.CustomerId == absorbed.Id);
            Assert.Equal(0, stillOnAbsorbed);

            var survivor = await reader.Customers.SingleAsync(c => c.Id == seed.Customer.Id);
            // The absorbed row's own single no-show, added onto the survivor's own zero.
            Assert.Equal(1, survivor.NoShowCount);
            Assert.Null(survivor.MergedIntoCustomerId);

            var tombstoned = await reader.Customers.SingleAsync(c => c.Id == absorbed.Id);
            Assert.Equal(seed.Customer.Id, tombstoned.MergedIntoCustomerId);
            Assert.NotNull(tombstoned.MergedAt);
        }

        // The audit row `adr/0147`'s own "a merge that cannot be explained afterwards is a merge
        // nobody will trust" calls for - who, when, which record absorbed which.
        var mergeStore = new CustomerMergeReadStore(fixture.DataSource);
        var page = await mergeStore.ListForTenantAsync(seed.Tenant.Id, null, 10, CancellationToken.None);
        var record = Assert.Single(page.Items);
        Assert.Equal(seed.Customer.Id.Value, record.SurvivorCustomerId);
        Assert.Equal(absorbed.Id.Value, record.AbsorbedCustomerId);
        Assert.Equal(seed.OperatorId.Value, record.OperatorId);
        Assert.Equal(2, record.BookingsMoved);

        // The absorbed row is tombstoned, not deleted - `20-12`'s own ordinary contacts report no
        // longer lists it, but the audit row above still resolves it.
        var contacts = await new ContactsReadStore(fixture.DataSource)
            .ListForTenantAsync(seed.Tenant.Id, mask: false, CancellationToken.None);
        var visibleRow = Assert.Single(contacts);
        Assert.Equal(seed.Customer.Id, visibleRow.CustomerId);
    }

    /// <summary>`23-60`'s own "never" scope line - merging across tenants - proven at the handler,
    /// the ordinary caller's own path: a customer id from a different tenant reads like no such
    /// customer, the identical "wrong tenant reads like not found" shape
    /// <see cref="ContactPhoneRevealTests.ACallerFromAnotherTenant_CannotRevealACustomerOfADifferentTenant"/>
    /// already proves for a reveal.</summary>
    [Fact]
    public async Task ACallerCannotMergeACustomerFromAnotherTenant()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var handler = new MergeCustomersHandler(
            new CustomerRepository(db), new CustomerMergeStore(db),
            new PermissionChecker(new RoleAssignmentProjectionStore(db)),
            new UuidV7Generator(), new FixedClock(Now));

        var result = await handler.HandleAsync(
            new MergeCustomers(mine.OperatorId, mine.Tenant.Id, mine.Customer.Id, theirs.Customer.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("contacts.customer_not_found", result.Error!.Value.Code);

        await using var reader = fixture.CreateDbContext();
        var theirsCustomer = await reader.Customers.SingleAsync(c => c.Id == theirs.Customer.Id);
        Assert.Null(theirsCustomer.MergedIntoCustomerId);
        Assert.Empty(await new CustomerMergeReadStore(fixture.DataSource)
            .ListForTenantAsync(mine.Tenant.Id, null, 10, CancellationToken.None)
            .ContinueWith(t => t.Result.Items, TaskScheduler.Default));
    }

    /// <summary>
    /// `23-60`: the "structurally impossible, not just checked" claim, demonstrated at the store
    /// itself rather than trusted from <see cref="MergeCustomersHandler"/>'s own tenant check - this
    /// calls <see cref="CustomerMergeStore.MergeAsync"/> directly with a <see cref="TenantId"/> that
    /// does not match the tenant the absorbed customer's own bookings actually belong to, the way a
    /// hypothetical future bug upstream of this store might. The bulk reassignment's own <c>WHERE</c>
    /// clause requires both the absorbed customer's id <em>and</em> the given tenant id - so a
    /// mismatched tenant id moves nothing, proven by the row count rather than assumed from the SQL
    /// text.
    /// </summary>
    [Fact]
    public async Task TheStoresBulkReassignment_MovesNothing_WhenGivenTheWrongTenant()
    {
        var owner = await CalendarSeed.WriteAsync(fixture);
        var stranger = await CalendarSeed.WriteAsync(fixture);

        var absorbed = Customer.RegisterFromChat(
            new CustomerId(CalendarSeed.NewId()), owner.Tenant.Id, owner.Customer.Phone, Guid.NewGuid(), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(absorbed);
            db.Events.Add(BookedEventFor(owner, absorbed.Id, Now.AddDays(1)));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var survivor = await db.Customers.SingleAsync(c => c.Id == owner.Customer.Id);
            var absorbedTracked = await db.Customers.SingleAsync(c => c.Id == absorbed.Id);
            survivor.AbsorbHistoryFrom(absorbedTracked, Now.AddHours(1));
            absorbedTracked.MarkMergedInto(survivor.Id, Now.AddHours(1));

            var store = new CustomerMergeStore(db);
            // stranger.Tenant.Id, not owner.Tenant.Id - the mismatch this test exists to prove is
            // harmless.
            var result = await store.MergeAsync(
                stranger.Tenant.Id, survivor, absorbedTracked, owner.OperatorId, Guid.NewGuid(), Now.AddHours(1),
                CancellationToken.None);

            Assert.Equal(0, result.BookingsMoved);
        }

        await using var reader = fixture.CreateDbContext();
        var stillOnAbsorbed = await reader.Events.CountAsync(e => e.CustomerId == absorbed.Id);
        Assert.Equal(1, stillOnAbsorbed);
    }

    private static Event BookedEventFor(SeededTenant seed, CustomerId customerId, DateTimeOffset startsAt)
    {
        var slot = new TimeSlot(startsAt, startsAt.AddMinutes(45));
        var @event = Event.Materialize(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id, slot,
            DateOnly.FromDateTime(startsAt.UtcDateTime), Now);
        @event.Claim(customerId, seed.Service.Id, Now, startsAt.AddMinutes(-5));
        @event.Confirm(Now);
        return @event;
    }
}
