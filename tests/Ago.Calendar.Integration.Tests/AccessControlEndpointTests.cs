using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-12`'s own console surface, over real HTTP against a real Postgres: a tenant provisions a second
/// role, moves a real operator onto it, and the account-owner invariant refuses to be bypassed through
/// the API the same way <c>OperatorAccountOwnerTests</c> already proved the aggregate refuses it
/// directly.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccessControlEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ConsoleApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ConsoleApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task ATenant_CanProvisionASecondRole_AndMoveARealOperatorOntoIt()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var roleId = await CreatedIdAsync(
            "/api/v1/console/roles",
            new CreateRoleRequest("Dispatcher", [Permission.BookingReject.Value, Permission.BookingCancel.Value]),
            seed,
            "roleId");

        var junior = await ANewOperatorAsync(seed.Tenant.Id, "Junior");

        var granted = await PostAsync($"/api/v1/console/operators/{junior.Value}/roles/{roleId}", null, seed);
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);

        var operators = await GetAsync<OperatorResponse[]>("/api/v1/console/operators", seed);
        var juniorRow = Assert.Single(operators, o => o.OperatorId == junior.Value);
        Assert.Contains(roleId, juniorRow.RoleIds);
        Assert.False(juniorRow.IsAccountOwner);

        var owner = Assert.Single(operators, o => o.OperatorId == seed.Operator.Id.Value);
        Assert.True(owner.IsAccountOwner);

        // An operator can be moved off a role, too - the item's own "onto/off it" wording.
        var revoked = await DeleteAsync($"/api/v1/console/operators/{junior.Value}/roles/{roleId}", seed);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        var afterwards = await GetAsync<OperatorResponse[]>("/api/v1/console/operators", seed);
        Assert.Empty(Assert.Single(afterwards, o => o.OperatorId == junior.Value).RoleIds);
    }

    [Fact]
    public async Task AnOperatorWithoutCustomerRead_CannotReadAContactsPhone_AndOneWithItCan()
    {
        // The item's own Done-when, over HTTP: two operators of the same tenant, one holding
        // CustomerRead and one not, get different answers about the same customer's phone in the
        // queue - proved end to end rather than only against the fakes/read store directly.
        var seed = await CalendarSeed.WriteAsync(fixture);

        var dispatcherRoleId = await CreatedIdAsync(
            "/api/v1/console/roles",
            new CreateRoleRequest("Dispatcher-no-contacts", [Permission.BookingReject.Value, Permission.BookingCancel.Value]),
            seed,
            "roleId");
        var dispatcherOperatorId = await ANewOperatorAsync(seed.Tenant.Id, "Dispatcher");
        await PostAsync($"/api/v1/console/operators/{dispatcherOperatorId.Value}/roles/{dispatcherRoleId}", null, seed);

        var dispatcher = await LinkedExternalSubjectAsync(dispatcherOperatorId);

        var booking = await APendingBookingAsync(seed);

        using var ownerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        ownerRequest.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.Operator.ExternalSubjectId);
        var ownerResponse = await _client.SendAsync(ownerRequest);
        var ownerQueue = (await ownerResponse.Content.ReadFromJsonAsync<PendingBookingResponse[]>())!;
        Assert.NotNull(Assert.Single(ownerQueue, r => r.BookingId == booking.Id.Value).Phone);

        using var dispatcherRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        dispatcherRequest.Headers.Add(ConsoleApiFactory.SubjectHeader, dispatcher);
        var dispatcherResponse = await _client.SendAsync(dispatcherRequest);
        var dispatcherQueue = (await dispatcherResponse.Content.ReadFromJsonAsync<PendingBookingResponse[]>())!;
        Assert.Null(Assert.Single(dispatcherQueue, r => r.BookingId == booking.Id.Value).Phone);
    }

    [Fact]
    public async Task RevokingTheAccountOwnersOnlyContactRole_IsRefused_OverHttp()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var response = await DeleteAsync(
            $"/api/v1/console/operators/{seed.Operator.Id.Value}/roles/{seed.Role.Id.Value}", seed);

        // 409: the caller does hold calendar:configure - what refuses this specific request is the
        // account-owner invariant, a state the aggregate itself protects, not a permission they lack.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "access.account_owner_requires_contact_access",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // The database, not just the HTTP status - the role is still granted.
        await using var db = fixture.CreateDbContext();
        var stillHeld = await db.Set<RoleAssignment>()
            .AnyAsync(a => a.OperatorId == seed.Operator.Id && a.RoleId == seed.Role.Id);
        Assert.True(stillHeld);
    }

    [Fact]
    public async Task RolesAndOperators_AreTenantIsolated()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        var roles = await GetAsync<RoleResponse[]>("/api/v1/console/roles", mine);
        Assert.DoesNotContain(roles, r => r.RoleId == theirs.Role.Id.Value);

        var operators = await GetAsync<OperatorResponse[]>("/api/v1/console/operators", mine);
        Assert.DoesNotContain(operators, o => o.OperatorId == theirs.Operator.Id.Value);
    }

    private async Task<OperatorId> ANewOperatorAsync(TenantId tenantId, string displayName)
    {
        var @operator = Operator.Create(new OperatorId(CalendarSeed.NewId()), tenantId, displayName);
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(@operator);
        await db.SaveChangesAsync();
        return @operator.Id;
    }

    /// <summary>Gives an operator created outside <c>CalendarSeed</c> a Keycloak subject it did not
    /// have yet, so a second request can authenticate as them.</summary>
    private async Task<string> LinkedExternalSubjectAsync(OperatorId operatorId)
    {
        var subject = $"kc-{CalendarSeed.NewId():N}";
        await using var db = fixture.CreateDbContext();
        var @operator = await db.Operators.Include("_roles").FirstAsync(o => o.Id == operatorId);
        @operator.LinkExternalIdentity(subject);
        await db.SaveChangesAsync();
        return subject;
    }

    private async Task<Event> APendingBookingAsync(SeededTenant seed)
    {
        var startsAt = DateTimeOffset.UtcNow.AddDays(11);
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(startsAt, startsAt.AddMinutes(45)), DateOnly.FromDateTime(startsAt.UtcDateTime),
            DateTimeOffset.UtcNow);
        slot.Claim(seed.Customer.Id, seed.Service.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2));
        slot.ClearDomainEvents();

        await using var db = fixture.CreateDbContext();
        db.Events.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    private async Task<Guid> CreatedIdAsync(string url, object content, SeededTenant seed, string field)
    {
        var response = await PostAsync(url, content, seed);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>();
        return payload![field];
    }

    private async Task<T> GetAsync<T>(string url, SeededTenant seed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.Operator.ExternalSubjectId);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<HttpResponseMessage> PostAsync(string url, object? content, SeededTenant seed) =>
        SendAsync(HttpMethod.Post, url, content, seed);

    private Task<HttpResponseMessage> DeleteAsync(string url, SeededTenant seed) =>
        SendAsync(HttpMethod.Delete, url, null, seed);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? content, SeededTenant seed)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.Operator.ExternalSubjectId);
        if (content is not null)
        {
            request.Content = JsonContent.Create(content, content.GetType());
        }

        return await _client.SendAsync(request);
    }
}
