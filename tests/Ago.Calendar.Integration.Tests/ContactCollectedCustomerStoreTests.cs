using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-59`/`adr/0147`: "the calendar consumes and creates a customer, idempotently, for tenants where
/// the module is granted" - <see cref="IContactCollectedCustomerStore"/>'s own write, proven against
/// real Postgres. `ContactCollectedConsumer`'s own decode/kind-filter/tenant-existence-check steps are
/// this consumer's own concern and are not re-proven here (`ContactCollectedConsumer`'s own remarks);
/// this file proves what happens once this store is actually called.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactCollectedCustomerStoreTests(PostgresFixture fixture)
{
    /// <summary>`ago_calendar`'s own half of Done-when #5 ("neither product queries the other's
    /// schema, asserted by a test") - the mirror of `RoleAssignmentProjectionDemonstrationTests`'s own
    /// check, applied to the tables this item's own item is about: this schema holds no
    /// `visitor_contact_details` and no `visitors` table at all, not merely empty ones - there is
    /// nothing here for a cross-product read to reach even if a future change tried.</summary>
    [Fact]
    public async Task CalendarsOwnSchema_HoldsNoChatContactTables()
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await TableExistsAsync(db, "visitor_contact_details"));
        Assert.False(await TableExistsAsync(db, "visitors"));
        Assert.True(await TableExistsAsync(db, "customers"));
    }

    [Fact]
    public async Task AContactCollected_ForATenantThatHasTheModule_CreatesAChatSourcedCustomer()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var sourceContactId = Guid.NewGuid();
        var phone = new PhoneNumber("+15550101");

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ContactCollectedCustomerStore(db, new UuidV7Generator());
            await store.UpsertAsync(seed.Tenant.Id, sourceContactId, phone, CalendarSeed.Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var customer = await verify.Customers.SingleAsync(
            c => c.TenantId == seed.Tenant.Id && c.SourceContactId == sourceContactId, CancellationToken.None);
        Assert.Equal(CustomerSource.Chat, customer.Source);
        Assert.Equal(phone, customer.Phone);
    }

    /// <summary>Done-when: "the same contact arriving twice produces one customer." Two deliveries of
    /// the identical <c>sourceContactId</c> - the real shape a broker redelivery or the ordinary-versus-
    /// carry-over double-publish takes (<c>Ago.Chat.Contracts.ContactCollected</c>'s own remarks on why
    /// both publishers can safely name the same id).</summary>
    [Fact]
    public async Task TheSameSourceContactId_DeliveredTwice_UpsertsOntoOneRow()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var sourceContactId = Guid.NewGuid();
        var phone = new PhoneNumber("+15550102");

        await using (var db1 = fixture.CreateDbContext())
        {
            var store = new ContactCollectedCustomerStore(db1, new UuidV7Generator());
            await store.UpsertAsync(seed.Tenant.Id, sourceContactId, phone, CalendarSeed.Now, CancellationToken.None);
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            var store = new ContactCollectedCustomerStore(db2, new UuidV7Generator());
            await store.UpsertAsync(
                seed.Tenant.Id, sourceContactId, phone, CalendarSeed.Now.AddMinutes(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var matches = await verify.Customers
            .Where(c => c.TenantId == seed.Tenant.Id && c.SourceContactId == sourceContactId)
            .ToListAsync(CancellationToken.None);
        var only = Assert.Single(matches);
        // last_seen_at moved forward to the second delivery's own timestamp - GREATEST, not overwrite.
        Assert.Equal(CalendarSeed.Now.AddMinutes(1), only.LastSeenAt);
    }

    /// <summary>
    /// The item's own sharpest rule, and `adr/0147`'s own rejected-alternative: "a phone that matches
    /// an existing customer creates a separate row." <see cref="CalendarSeed.WriteAsync"/> seeds a
    /// <see cref="CustomerSource.Booking"/>-sourced customer at <c>+79991234567</c> - this test lands a
    /// chat contact on the identical number and proves two rows exist afterward, not one merged row.
    /// </summary>
    [Fact]
    public async Task APhoneMatchingAnExistingBookingSourcedCustomer_CreatesASeparateChatSourcedRow()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var existingPhone = seed.Customer.Phone;
        var sourceContactId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ContactCollectedCustomerStore(db, new UuidV7Generator());
            await store.UpsertAsync(seed.Tenant.Id, sourceContactId, existingPhone, CalendarSeed.Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var rowsForThatPhone = await verify.Customers
            .Where(c => c.TenantId == seed.Tenant.Id && c.Phone == existingPhone)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(2, rowsForThatPhone.Count);
        Assert.Contains(rowsForThatPhone, c => c.Source == CustomerSource.Booking && c.SourceContactId == null);
        Assert.Contains(rowsForThatPhone, c => c.Source == CustomerSource.Chat && c.SourceContactId == sourceContactId);

        // Neither row was mutated into the other - both still hold their own distinct id.
        Assert.NotEqual(seed.Customer.Id, rowsForThatPhone.Single(c => c.Source == CustomerSource.Chat).Id);
    }

    /// <summary>The mirror image of the previous test, for the reasoning `adr/0147` itself gives for
    /// never merging on a phone: two different people who happen to share a number must not become one
    /// chat-sourced row either.</summary>
    [Fact]
    public async Task TwoChatContacts_WithDifferentSourceContactIds_ButTheSamePhone_AreTwoRows()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var phone = new PhoneNumber("+15550103");
        var firstContactId = Guid.NewGuid();
        var secondContactId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ContactCollectedCustomerStore(db, new UuidV7Generator());
            await store.UpsertAsync(seed.Tenant.Id, firstContactId, phone, CalendarSeed.Now, CancellationToken.None);
            await store.UpsertAsync(seed.Tenant.Id, secondContactId, phone, CalendarSeed.Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Customers
            .Where(c => c.TenantId == seed.Tenant.Id && c.Phone == phone && c.Source == CustomerSource.Chat)
            .ToListAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count);
    }

    private static async Task<bool> TableExistsAsync(AgoCalendarDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table) IS NOT NULL";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = $"public.{table}";
        command.Parameters.Add(parameter);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
