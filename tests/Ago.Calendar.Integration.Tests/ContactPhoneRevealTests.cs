using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-12`/`decisions.md` §5: masked, revealed on demand, and the reveal is recorded - against a real
/// Postgres, every port real except the clock and id generator.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactPhoneRevealTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = CalendarSeed.Now;

    [Fact]
    public async Task RevealingAPhone_ReturnsTheRealNumberAndWritesExactlyOneRecord()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await StageRungAsync(seed.Tenant.Id, ContactVisibility.MaskedWithReveal);

        var (result, reveals) = await RevealAsync(seed.OperatorId, seed.Tenant.Id, seed.Customer.Id, "ConsoleContacts");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(seed.Customer.Phone.Value, result.Value);

        var page = await reveals.ListForTenantAsync(seed.Tenant.Id, null, 10, CancellationToken.None);
        var item = Assert.Single(page.Items);
        Assert.Equal(seed.Customer.Id.Value, item.CustomerId);
        Assert.Equal(seed.OperatorId.Value, item.OperatorId);
        Assert.Equal("ConsoleContacts", item.Surface);
    }

    [Fact]
    public async Task ACallerWithoutCustomerRead_CannotReveal_AndWritesNoRecord()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var stranger = await AnOperatorWithoutCustomerReadAsync(seed.Tenant.Id);

        var (result, reveals) = await RevealAsync(stranger, seed.Tenant.Id, seed.Customer.Id, "ConsoleContacts");

        Assert.True(result.IsFailure);
        Assert.Equal("contacts.forbidden", result.Error!.Value.Code);

        var page = await reveals.ListForTenantAsync(seed.Tenant.Id, null, 10, CancellationToken.None);
        Assert.Empty(page.Items);
    }

    /// <summary>`23-12`'s own Done-when: "a caller of another tenant cannot reveal." An operator who
    /// holds <c>CustomerRead</c> in their own tenant, asked to reveal a customer id that happens to
    /// belong to a different one, is refused - the customer lookup is scoped to the caller's own
    /// tenant, and a customer belonging to someone else's simply does not match it.</summary>
    [Fact]
    public async Task ACallerFromAnotherTenant_CannotRevealACustomerOfADifferentTenant()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        var (result, reveals) = await RevealAsync(mine.OperatorId, mine.Tenant.Id, theirs.Customer.Id, "ConsoleContacts");

        Assert.True(result.IsFailure);
        Assert.Equal("contacts.customer_not_found", result.Error!.Value.Code);

        var page = await reveals.ListForTenantAsync(mine.Tenant.Id, null, 10, CancellationToken.None);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ConfirmingAPhone_IsDistinctFromCodeVerification()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        await using (var db = fixture.CreateDbContext())
        {
            var customer = await db.Customers.FindAsync(seed.Customer.Id);
            customer!.RecordVerifiedPhone(Now);
            await db.SaveChangesAsync();
        }

        var confirmedAt = Now.AddMinutes(5);
        var result = await ConfirmAsync(seed.OperatorId, seed.Tenant.Id, seed.Customer.Id, confirmedAt);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(confirmedAt, result.Value);

        await using var reader = fixture.CreateDbContext();
        var contacts = await new ContactsReadStore(fixture.DataSource)
            .ListForTenantAsync(seed.Tenant.Id, mask: false, CancellationToken.None);
        var row = Assert.Single(contacts);
        Assert.Equal(Now, row.PhoneVerifiedAt);
        Assert.Equal(confirmedAt, row.PhoneConfirmedByOperatorAt);
        Assert.NotEqual(row.PhoneVerifiedAt, row.PhoneConfirmedByOperatorAt);
    }

    [Fact]
    public async Task ACallerWithoutCustomerRead_CannotConfirm()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var stranger = await AnOperatorWithoutCustomerReadAsync(seed.Tenant.Id);

        var result = await ConfirmAsync(stranger, seed.Tenant.Id, seed.Customer.Id, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("contacts.forbidden", result.Error!.Value.Code);

        await using var reader = fixture.CreateDbContext();
        var customer = await reader.Customers.FindAsync(seed.Customer.Id);
        Assert.Null(customer!.PhoneConfirmedByOperatorAt);
    }

    [Fact]
    public async Task TheTenantCanReadItsOwnRevealRecords()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await StageRungAsync(seed.Tenant.Id, ContactVisibility.MaskedWithReveal);
        await RevealAsync(seed.OperatorId, seed.Tenant.Id, seed.Customer.Id, "ConsoleContacts");

        await using var db = fixture.CreateDbContext();
        var handler = new GetPhoneRevealsForTenantHandler(
            new ContactPhoneRevealRepository(fixture.DataSource), new PermissionChecker(new RoleAssignmentProjectionStore(db)));

        var result = await handler.HandleAsync(
            new GetPhoneRevealsForTenant(seed.OperatorId, seed.Tenant.Id, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(result.Value.Items);
    }

    /// <summary>
    /// `23-12`'s own central claim, demonstrated end to end rather than asserted from the design: on
    /// the masked rung, the real digits of a customer's phone number never reach the wire at all - the
    /// whole serialised response is searched, not the one field a careless edit might mask while
    /// leaving another unmasked.
    /// </summary>
    [Fact]
    public async Task OnTheMaskedRung_TheContactsResponse_NeverContainsTheRealNumberAnywhere()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await StageRungAsync(seed.Tenant.Id, ContactVisibility.MaskedWithReveal);

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/contacts");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(seed.Customer.Phone.Value, body, StringComparison.Ordinal);

        var contacts = await response.Content.ReadFromJsonAsync<ContactResponse[]>();
        var row = Assert.Single(contacts!);
        Assert.True(row.Masked);
        Assert.NotEqual(seed.Customer.Phone.Value, row.Phone);
        Assert.StartsWith(seed.Customer.Phone.Value[..2], row.Phone, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnTheVisibleRung_TheContactsResponse_CarriesTheRealNumberUnmasked()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        // No StageRungAsync call at all - proves the "no projected value behaves as Visible" default,
        // not merely the explicit-Visible case.

        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/contacts");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);
        var response = await client.SendAsync(request);

        var contacts = await response.Content.ReadFromJsonAsync<ContactResponse[]>();
        var row = Assert.Single(contacts!);
        Assert.False(row.Masked);
        Assert.Equal(seed.Customer.Phone.Value, row.Phone);
    }

    private async Task StageRungAsync(TenantId tenantId, ContactVisibility rung)
    {
        await using var db = fixture.CreateDbContext();
        var store = new ContactVisibilityProjectionStore(db);
        await store.StageAsync(tenantId, rung, Now, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private async Task<(Result<string> Result, ContactPhoneRevealRepository Reveals)> RevealAsync(
        OperatorId operatorId, TenantId tenantId, CustomerId customerId, string surface)
    {
        await using var db = fixture.CreateDbContext();
        var reveals = new ContactPhoneRevealRepository(fixture.DataSource);
        var handler = new RevealCustomerPhoneHandler(
            new CustomerRepository(db), new PermissionChecker(new RoleAssignmentProjectionStore(db)), reveals,
            new UuidV7Generator(), new FixedClock(Now));

        var result = await handler.HandleAsync(
            new RevealCustomerPhone(operatorId, tenantId, customerId, surface), CancellationToken.None);
        return (result, reveals);
    }

    private async Task<Result<DateTimeOffset>> ConfirmAsync(
        OperatorId operatorId, TenantId tenantId, CustomerId customerId, DateTimeOffset confirmedAt)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new ConfirmOperatorVerifiedPhoneHandler(
            new CustomerRepository(db), new PermissionChecker(new RoleAssignmentProjectionStore(db)), new FixedClock(confirmedAt));

        return await handler.HandleAsync(
            new ConfirmOperatorVerifiedPhone(operatorId, tenantId, customerId), CancellationToken.None);
    }

    /// <summary>`22-05`/`adr/0093`: a second operator, narrower than <see cref="CalendarSeed"/>'s own
    /// seed - a projection row written directly, the same way the seed itself writes one.</summary>
    private async Task<OperatorId> AnOperatorWithoutCustomerReadAsync(TenantId tenantId)
    {
        var subject = $"kc-{CalendarSeed.NewId():N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        await using var db = fixture.CreateDbContext();
        var projections = new RoleAssignmentProjectionStore(db);
        await projections.StageAsync(
            operatorId, tenantId, subject,
            [Permission.BookingReject.Value, Permission.CalendarConfigure.Value], Now, CancellationToken.None);
        await db.SaveChangesAsync();

        return operatorId;
    }
}
