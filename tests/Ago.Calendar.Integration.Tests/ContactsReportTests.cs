using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-12`'s own new kind of screen, against a real Postgres: `18-08`'s own shape precedent (a Dapper
/// read store, tenant-isolated, gated by a permission the handler checks once) adapted for a full
/// personal-data listing. Tenant isolation is proved the exact way `18-08`'s own Outcome describes -
/// this suite's own version of that check lives in <see cref="TheReadStore_IsTenantIsolated"/>, and the
/// managing session re-verified it independently by mutating the read store's own <c>WHERE</c> clause
/// and watching this test fail, then reverting - see the item's own report for that record.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ContactsReportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TheReadStore_IsTenantIsolated()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        var extraOfMine = Customer.Register(
            new CustomerId(CalendarSeed.NewId()), mine.Tenant.Id, new PhoneNumber("+79998887766"), CalendarSeed.Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(extraOfMine);
            await db.SaveChangesAsync();
        }

        var store = new ContactsReadStore(fixture.DataSource);
        var rows = await store.ListForTenantAsync(mine.Tenant.Id, mask: false, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.CustomerId == mine.Customer.Id);
        Assert.Contains(rows, r => r.CustomerId == extraOfMine.Id);
        Assert.DoesNotContain(rows, r => r.CustomerId == theirs.Customer.Id);
    }

    /// <summary>`23-60`/`adr/0147`'s own first Done-when: "a tenant can see that two customer records
    /// share a phone." Against a real Postgres, not asserted from the SQL alone - two rows sharing a
    /// phone, one a chat-sourced duplicate the way `23-59` actually produces one, each naming the
    /// other back and nobody else.</summary>
    [Fact]
    public async Task TheReadStore_NamesEveryOtherLiveCustomerSharingTheSamePhone()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var chatDuplicate = Customer.RegisterFromChat(
            new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Customer.Phone, Guid.NewGuid(), CalendarSeed.Now);
        var unrelated = Customer.Register(
            new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, new PhoneNumber("+79990009999"), CalendarSeed.Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.AddRange(chatDuplicate, unrelated);
            await db.SaveChangesAsync();
        }

        var rows = await new ContactsReadStore(fixture.DataSource)
            .ListForTenantAsync(seed.Tenant.Id, mask: false, CancellationToken.None);

        var seededRow = Assert.Single(rows, r => r.CustomerId == seed.Customer.Id);
        Assert.Equal([chatDuplicate.Id], seededRow.DuplicatePhoneCustomerIds);

        var chatRow = Assert.Single(rows, r => r.CustomerId == chatDuplicate.Id);
        Assert.Equal([seed.Customer.Id], chatRow.DuplicatePhoneCustomerIds);

        var unrelatedRow = Assert.Single(rows, r => r.CustomerId == unrelated.Id);
        Assert.Empty(unrelatedRow.DuplicatePhoneCustomerIds);
    }

    /// <summary>Once one side of a duplicate pair is merged away, the survivor's own
    /// `DuplicatePhoneCustomerIds` empties out - the hint disappears because the tombstoned row is
    /// excluded from this store's own query entirely (`IContactsReadStore`'s own remarks), not
    /// because anything re-checks whether a phone is still shared.</summary>
    [Fact]
    public async Task AfterAMerge_TheSurvivorNoLongerShowsADuplicateHint()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var chatDuplicate = Customer.RegisterFromChat(
            new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Customer.Phone, Guid.NewGuid(), CalendarSeed.Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(chatDuplicate);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var survivor = await db.Customers.SingleAsync(c => c.Id == seed.Customer.Id);
            var absorbed = await db.Customers.SingleAsync(c => c.Id == chatDuplicate.Id);
            survivor.AbsorbHistoryFrom(absorbed, CalendarSeed.Now.AddHours(1));
            absorbed.MarkMergedInto(survivor.Id, CalendarSeed.Now.AddHours(1));
            await new CustomerMergeStore(db).MergeAsync(
                seed.Tenant.Id, survivor, absorbed, seed.OperatorId, Guid.NewGuid(), CalendarSeed.Now.AddHours(1),
                CancellationToken.None);
        }

        var rows = await new ContactsReadStore(fixture.DataSource)
            .ListForTenantAsync(seed.Tenant.Id, mask: false, CancellationToken.None);

        var survivorRow = Assert.Single(rows);
        Assert.Equal(seed.Customer.Id, survivorRow.CustomerId);
        Assert.Empty(survivorRow.DuplicatePhoneCustomerIds);
    }

    [Fact]
    public async Task TheHandler_ReturnsEveryField_ForACallerHoldingCustomerRead()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var customer = await db.Customers.FindAsync(seed.Customer.Id);
        customer!.Describe("Anna", "Prefers afternoons");
        await db.SaveChangesAsync();

        var result = await new GetTenantContactsHandler(
                new ContactsReadStore(fixture.DataSource), new PermissionChecker(new RoleAssignmentProjectionStore(db)),
                new ContactVisibilityProjectionStore(db))
            .HandleAsync(new GetTenantContacts(seed.OperatorId, seed.Tenant.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var row = Assert.Single(result.Value);
        Assert.Equal("Anna", row.DisplayName);
        Assert.Equal("Prefers afternoons", row.Notes);
        Assert.Equal(0, row.NoShowCount);
        Assert.Equal(seed.Customer.Phone.Value, row.Phone);
    }

    [Fact]
    public async Task TheHandler_WithoutCustomerRead_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var strangerSubject = $"kc-{CalendarSeed.NewId():N}";
        var strangerId = OperatorId.FromExternalSubjectId(strangerSubject);
        string[] strangerPermissions = [Permission.BookingReject.Value, Permission.BookingCancel.Value];

        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            await projections.StageAsync(
                strangerId, seed.Tenant.Id, strangerSubject, strangerPermissions, CalendarSeed.Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var reader = fixture.CreateDbContext();
        var result = await new GetTenantContactsHandler(
                new ContactsReadStore(fixture.DataSource), new PermissionChecker(new RoleAssignmentProjectionStore(reader)),
                new ContactVisibilityProjectionStore(reader))
            .HandleAsync(new GetTenantContacts(strangerId, seed.Tenant.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("contacts.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task TheConsoleEndpoint_ReturnsTheReport_OverRealHttp()
    {
        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();
        var seed = await CalendarSeed.WriteAsync(fixture);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/contacts");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contacts = await response.Content.ReadFromJsonAsync<ContactResponse[]>();
        Assert.Equal(seed.Customer.Id.Value, Assert.Single(contacts!).CustomerId);
    }
}
