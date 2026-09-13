using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-13`'s own four endpoints - <c>GET /workers</c>, <c>GET /workers/{id}</c>,
/// <c>PUT /workers/{id}</c>, <c>DELETE /workers/{id}</c> - end to end over real HTTP against a real
/// Postgres, the same way <see cref="ConsoleEndpointTests"/> proves `20-06`'s surface. Everything
/// except the Keycloak signature is real (<see cref="ConsoleApiFactory"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
public class WorkerEndpointTests(PostgresFixture fixture) : IAsyncLifetime
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
    public async Task ATenant_CanListGetUpdateAndReadBackAWorker()
    {
        // `22-23`: this test's own later step deactivates the seeded worker and then reactivates him
        // - a quota of 1 is what makes that reactivation of the tenant's only worker legal under the
        // gate this item adds, rather than a scenario this test never meant to exercise.
        var seed = await CalendarSeed.WriteAsync(fixture, workerQuota: 1);

        var listed = await GetAsync<WorkerResponse[]>("/api/v1/console/workers", seed);
        var row = Assert.Single(listed, w => w.WorkerId == seed.Worker.Id.Value);
        Assert.Equal("Doe", row.LastName);
        Assert.Equal("Alex", row.FirstName);
        Assert.Equal("Alex Doe", row.DisplayName);
        Assert.False(row.DisplayNameIsCustom);
        Assert.True(row.IsActive);

        var single = await GetAsync<WorkerResponse>($"/api/v1/console/workers/{seed.Worker.Id.Value}", seed);
        // `25-74`: not `Assert.Equal` - `WorkerResponse.ServiceIds` is a `List<Guid>` once round-tripped
        // through JSON, and `List<T>` has no value equality, so two structurally-identical rows read
        // through the list endpoint and the single-worker endpoint would never compare equal by
        // reference. `Assert.Equivalent` compares every member structurally instead, which is what this
        // assertion was always actually trying to prove.
        Assert.Equivalent(row, single);

        // Rename before any custom display name is set - it keeps deriving.
        var afterRename = await SendAsync(
            HttpMethod.Put, $"/api/v1/console/workers/{seed.Worker.Id.Value}",
            new UpdateWorkerRequest("Doe", "Alexandra", null, null, true, [seed.Service.Id.Value]), seed);
        Assert.Equal(HttpStatusCode.NoContent, afterRename.StatusCode);
        var renamed = await GetAsync<WorkerResponse>($"/api/v1/console/workers/{seed.Worker.Id.Value}", seed);
        Assert.Equal("Alexandra Doe", renamed.DisplayName);
        Assert.False(renamed.DisplayNameIsCustom);

        // Now a human types a custom display name and, in the same request, renames again - the
        // custom value must win and freeze.
        var afterCustom = await SendAsync(
            HttpMethod.Put, $"/api/v1/console/workers/{seed.Worker.Id.Value}",
            new UpdateWorkerRequest("Doeson", "Alexandra", "Petrovna", "Alexandra the Barber", false, [seed.Service.Id.Value]), seed);
        Assert.Equal(HttpStatusCode.NoContent, afterCustom.StatusCode);
        var custom = await GetAsync<WorkerResponse>($"/api/v1/console/workers/{seed.Worker.Id.Value}", seed);
        Assert.Equal("Doeson", custom.LastName);
        Assert.Equal("Petrovna", custom.MiddleName);
        Assert.Equal("Alexandra the Barber", custom.DisplayName);
        Assert.True(custom.DisplayNameIsCustom);
        Assert.False(custom.IsActive);
        Assert.True(custom.UpdatedAt > custom.CreatedAt);

        // A further rename with no explicit display name must not un-freeze it.
        var afterFurtherRename = await SendAsync(
            HttpMethod.Put, $"/api/v1/console/workers/{seed.Worker.Id.Value}",
            new UpdateWorkerRequest("Doeson", "Alexandra", "Petrovna", null, true, [seed.Service.Id.Value]), seed);
        Assert.Equal(HttpStatusCode.NoContent, afterFurtherRename.StatusCode);
        var stillCustom = await GetAsync<WorkerResponse>($"/api/v1/console/workers/{seed.Worker.Id.Value}", seed);
        Assert.Equal("Alexandra the Barber", stillCustom.DisplayName);
        Assert.True(stillCustom.IsActive);
    }

    /// <summary>
    /// `25-74`'s own Done-when: "an operator can add a second service to an existing worker through
    /// the console, and it is immediately offered at booking - proven end to end, not only that the
    /// API call succeeds." The API call alone proves nothing about whether the booking surface
    /// actually changed - <see cref="BookingSurfaceReadStore.ListWorkersAsync"/>, the same read the
    /// public widget calls before offering a worker for a chosen service, is what this test checks
    /// after the `PUT`, not the response status code.
    /// </summary>
    [Fact]
    public async Task AddingASecondServiceThroughTheConsole_MakesTheWorkerBookableForIt()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var secondService = Service.Create(
            new ServiceId(CalendarSeed.NewId()), seed.Tenant.Id, "Manicure", TimeSpan.FromMinutes(30));
        await using (var db = fixture.CreateDbContext())
        {
            db.Services.Add(secondService);
            await db.SaveChangesAsync();
        }

        var surface = new BookingSurfaceReadStore(fixture.DataSource);
        Assert.Empty(await surface.ListWorkersAsync(seed.Calendar.Id, secondService.Id, CancellationToken.None));

        var response = await SendAsync(
            HttpMethod.Put, $"/api/v1/console/workers/{seed.Worker.Id.Value}",
            new UpdateWorkerRequest(
                "Doe", "Alex", null, null, true, [seed.Service.Id.Value, secondService.Id.Value]),
            seed);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var bookable = await surface.ListWorkersAsync(seed.Calendar.Id, secondService.Id, CancellationToken.None);
        var offering = Assert.Single(bookable);
        Assert.Equal(seed.Worker.Id, offering.WorkerId);

        // The original service is still offered too - this was an addition, not a replacement.
        var stillOffersFirst = await surface.ListWorkersAsync(seed.Calendar.Id, seed.Service.Id, CancellationToken.None);
        Assert.Single(stillOffersFirst, w => w.WorkerId == seed.Worker.Id);
    }

    [Fact]
    public async Task AWorkerWithOnlyAvailableEvents_CanBeDeleted_TakingHisSlotsRulesAndJoinsWithHim()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeek.Monday);

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(CalendarSeed.Slot(seed, DateTimeOffset.UtcNow.AddDays(3)));
            await db.SaveChangesAsync();
        }

        var response = await SendAsync(
            HttpMethod.Delete, $"/api/v1/console/workers/{seed.Worker.Id.Value}", null, seed);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        Assert.Null(await verify.Workers.FindAsync(seed.Worker.Id));
        Assert.False(await verify.Events.AnyAsync(e => e.WorkerId == seed.Worker.Id));
        Assert.False(await verify.WorkingHoursRules.AnyAsync(r => r.WorkerId == seed.Worker.Id));
        Assert.False(await verify.Set<CalendarMembership>().AnyAsync(m => m.WorkerId == seed.Worker.Id));
        Assert.False(await verify.Set<ServiceOffering>().AnyAsync(o => o.WorkerId == seed.Worker.Id));
    }

    [Fact]
    public async Task AWorkerWithABookedEvent_CannotBeDeleted_AndTheApiSaysWhy()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = await ABookedEventAsync(seed);

        var response = await SendAsync(
            HttpMethod.Delete, $"/api/v1/console/workers/{seed.Worker.Id.Value}", null, seed);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "configuration.worker_has_booking_history",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using var verify = fixture.CreateDbContext();
        Assert.NotNull(await verify.Workers.FindAsync(seed.Worker.Id));
        Assert.NotNull(await verify.Events.FindAsync(slot.Id));
    }

    [Fact]
    public async Task AWorkerWithANoShow_CannotBeDeletedEither()
    {
        // Past history, not a live booking - the item's own done-when is explicit that
        // PendingConfirmation, Booked *and* NoShow all count, past or future.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var start = DateTimeOffset.UtcNow.AddDays(-10);
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(start, start.AddMinutes(45)), DateOnly.FromDateTime(start.UtcDateTime), CalendarSeed.Now);
        slot.Claim(seed.Customer.Id, seed.Service.Id, start.AddMinutes(-30), start.AddMinutes(-15));
        slot.Confirm(start.AddMinutes(-15));
        slot.MarkNoShow(start.AddMinutes(50));
        slot.ClearDomainEvents();

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        var response = await SendAsync(
            HttpMethod.Delete, $"/api/v1/console/workers/{seed.Worker.Id.Value}", null, seed);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantsWorker_IsInvisibleToListing()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        var listed = await GetAsync<WorkerResponse[]>("/api/v1/console/workers", mine);

        Assert.DoesNotContain(listed, w => w.WorkerId == theirs.Worker.Id.Value);
    }

    [Fact]
    public async Task AnotherTenantsWorker_IsNotFoundByGet()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        var response = await SendAsync(HttpMethod.Get, $"/api/v1/console/workers/{theirs.Worker.Id.Value}", null, mine);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantsWorker_CannotBeUpdated()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        var response = await SendAsync(
            HttpMethod.Put, $"/api/v1/console/workers/{theirs.Worker.Id.Value}",
            new UpdateWorkerRequest("Hacked", "Hacked", null, null, false, []), mine);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var untouched = await verify.Workers.FindAsync(theirs.Worker.Id);
        Assert.Equal("Doe", untouched!.LastName);
        Assert.True(untouched.IsActive);
    }

    [Fact]
    public async Task AnotherTenantsWorker_CannotBeDeleted()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        var response = await SendAsync(HttpMethod.Delete, $"/api/v1/console/workers/{theirs.Worker.Id.Value}", null, mine);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        Assert.NotNull(await verify.Workers.FindAsync(theirs.Worker.Id));
    }

    private async Task<Event> ABookedEventAsync(SeededTenant seed)
    {
        var startsAt = DateTimeOffset.UtcNow.AddDays(5);
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

    private async Task<T> GetAsync<T>(string url, SeededTenant seed)
    {
        var response = await SendAsync(HttpMethod.Get, url, null, seed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? content, SeededTenant seed)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);
        if (content is not null)
        {
            request.Content = JsonContent.Create(content, content.GetType());
        }

        return await _client.SendAsync(request);
    }
}
