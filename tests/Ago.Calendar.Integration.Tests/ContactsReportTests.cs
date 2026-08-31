using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;

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
        var rows = await store.ListForTenantAsync(mine.Tenant.Id, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.CustomerId == mine.Customer.Id);
        Assert.Contains(rows, r => r.CustomerId == extraOfMine.Id);
        Assert.DoesNotContain(rows, r => r.CustomerId == theirs.Customer.Id);
    }

    [Fact]
    public async Task TheHandler_ReturnsEveryField_ForACallerHoldingCustomerRead()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var customer = await db.Customers.FindAsync(seed.Customer.Id);
        customer!.Describe("Anna", "Prefers afternoons");
        await db.SaveChangesAsync();

        var result = await new GetTenantContactsHandler(new ContactsReadStore(fixture.DataSource), new PermissionChecker(db))
            .HandleAsync(new GetTenantContacts(seed.Operator.Id, seed.Tenant.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var row = Assert.Single(result.Value);
        Assert.Equal("Anna", row.DisplayName);
        Assert.Equal("Prefers afternoons", row.Notes);
        Assert.Equal(0, row.NoShowCount);
        Assert.Equal(seed.Customer.Phone, row.Phone);
    }

    [Fact]
    public async Task TheHandler_WithoutCustomerRead_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var role = Role.Create(
            new RoleId(CalendarSeed.NewId()), seed.Tenant.Id, "No contacts",
            [Permission.BookingReject, Permission.BookingCancel]);
        var stranger = Operator.Create(new OperatorId(CalendarSeed.NewId()), seed.Tenant.Id, "Casey");
        stranger.Grant(role);

        await using (var db = fixture.CreateDbContext())
        {
            db.Roles.Add(role);
            db.Operators.Add(stranger);
            await db.SaveChangesAsync();
        }

        await using var reader = fixture.CreateDbContext();
        var result = await new GetTenantContactsHandler(
                new ContactsReadStore(fixture.DataSource), new PermissionChecker(reader))
            .HandleAsync(new GetTenantContacts(stranger.Id, seed.Tenant.Id), CancellationToken.None);

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
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.Operator.ExternalSubjectId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contacts = await response.Content.ReadFromJsonAsync<ContactResponse[]>();
        Assert.Equal(seed.Customer.Id.Value, Assert.Single(contacts!).CustomerId);
    }
}
