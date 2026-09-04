using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-06`'s two console Done-when clauses, end to end over real HTTP against a real Postgres:
/// a tenant configuring itself, and an operator working the shared pending queue.
///
/// <para><b>Everything except the Keycloak signature is real</b> - see <see cref="ConsoleApiFactory"/>
/// for exactly what is stood in for and why. In particular the claims transformation, the
/// <c>calendar-operator</c> policy and <c>PermissionChecker</c> all run against real rows, so
/// "an unknown subject is refused" and "an operator without the permission is refused" are properties
/// of the shipped code rather than of a fake.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConsoleEndpointTests(PostgresFixture fixture) : IAsyncLifetime
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
    public async Task ATenant_CanCreateACalendarAWorkerAServiceAndAWorkingHoursRule()
    {
        // `20-06`'s first Done-when, in one test because it is one claim: the four objects only mean
        // anything together. A worker on no calendar offering nothing is invisible to every other
        // part of the product, and a working-hours rule for such a worker is refused by the aggregate.
        var seed = await ProvisionAsync();

        var calendarId = await CreatedIdAsync(
            "/api/v1/console/calendars",
            new CreateCalendarRequest("Second chair", "Europe/Moscow", Publish: true),
            seed,
            "calendarId");

        var serviceId = await CreatedIdAsync(
            "/api/v1/console/services", new CreateServiceRequest("Beard trim", 30), seed, "serviceId");

        var workerId = await CreatedIdAsync(
            "/api/v1/console/workers",
            new CreateWorkerRequest("Fox", "Robin", null, null, calendarId, [serviceId]),
            seed,
            "workerId");

        var ruleResponse = await PostAsync(
            "/api/v1/console/working-hours",
            new AddWorkingHoursRuleRequest(
                calendarId, workerId, (int)DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            seed);
        Assert.Equal(HttpStatusCode.Created, ruleResponse.StatusCode);

        // Read back through the console's own configuration screen, not through the database: the
        // point of the clause is that a tenant can *see* what they configured.
        var configuration = await GetConfigurationAsync(seed);

        Assert.Equal(seed.Tenant.PublicKey.Value, configuration.PublicKey);
        var created = Assert.Single(configuration.Calendars, calendar => calendar.CalendarId == calendarId);
        Assert.True(created.IsPublished);
        Assert.Contains(workerId, created.WorkerIds);
        Assert.Equal(workerId, Assert.Single(created.WorkingHours).WorkerId);
        Assert.Contains(configuration.Services, service => service.ServiceId == serviceId);
        Assert.Contains(
            configuration.Workers,
            worker => worker.WorkerId == workerId && worker.ServiceIds.Contains(serviceId));
    }

    [Fact]
    public async Task AnOperator_SeesThePendingQueueAcrossEveryCalendarAndCanRejectFromIt()
    {
        // `20-06`'s second Done-when, including its own parenthesis: "two calendars, confirming the
        // queue is not scoped to one".
        var seed = await ProvisionAsync();
        var secondCalendarId = await CreatedIdAsync(
            "/api/v1/console/calendars",
            new CreateCalendarRequest("Second chair", "Europe/Moscow", Publish: true),
            seed,
            "calendarId");

        var first = await APendingBookingAsync(seed, seed.Calendar.Id, seed.Worker.Id);
        var secondWorkerId = await CreatedIdAsync(
            "/api/v1/console/workers",
            new CreateWorkerRequest("Fox", "Robin", null, null, secondCalendarId, [seed.Service.Id.Value]),
            seed,
            "workerId");
        var second = await APendingBookingAsync(seed, new CalendarId(secondCalendarId), new WorkerId(secondWorkerId));

        var queue = await GetQueueAsync(seed);

        Assert.Equal(2, queue.Length);
        Assert.Contains(queue, row => row.BookingId == first.Id.Value && row.CalendarId == seed.Calendar.Id.Value);
        Assert.Contains(queue, row => row.BookingId == second.Id.Value && row.CalendarId == secondCalendarId);

        var rejected = await PostAsync($"/api/v1/console/bookings/{second.Id.Value}/reject", content: null, seed);
        Assert.Equal(HttpStatusCode.NoContent, rejected.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Cancelled, (await db.Events.FindAsync(second.Id))!.Status);

        // The rejected row leaves the queue; the other one is untouched.
        var afterwards = await GetQueueAsync(seed);
        Assert.Equal(first.Id.Value, Assert.Single(afterwards).BookingId);
    }

    [Fact]
    public async Task AnOperator_CannotRejectAnotherTenantsBooking()
    {
        // The permission check passed - this operator really does hold booking:reject in their own
        // tenant. What stops them is the second check, against the tenant on the *row*, and it is
        // reported as absent rather than as forbidden.
        var mine = await ProvisionAsync();
        var theirs = await ProvisionAsync();
        var theirBooking = await APendingBookingAsync(theirs, theirs.Calendar.Id, theirs.Worker.Id);

        var response = await PostAsync($"/api/v1/console/bookings/{theirBooking.Id.Value}/reject", null, mine);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(EventStatus.PendingConfirmation, (await db.Events.FindAsync(theirBooking.Id))!.Status);
    }

    [Fact]
    public async Task AnUnknownKeycloakSubject_IsRefusedByThePolicyRatherThanReachingAHandler()
    {
        // A real person who signed in to the realm and is not an operator of this product. adr/0022
        // chose this explicitly: no operator_id claim, so the policy refuses, rather than a
        // downstream accessor throwing on a missing claim and producing a 500.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/configuration");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, "kc-nobody-at-all");

        var response = await _client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Expected the policy to refuse an unresolvable subject; got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_IsRefused()
    {
        var response = await _client.GetAsync("/api/v1/console/configuration");

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"The console must not be anonymous; got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task AnOperatorWithoutCalendarConfigure_CannotConfigure()
    {
        // Proved through a real role row rather than a fake checker: the operator is given a role
        // that grants only the queue permissions, which is the "dispatcher" shape adr/0016's
        // granularity argument exists for.
        var seed = await ProvisionAsync();
        var dispatcher = await ADispatcherAsync(seed);

        var response = await PostAsync(
            "/api/v1/console/services", new CreateServiceRequest("Beard trim", 30), dispatcher);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "configuration.forbidden", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATenant_CanApproveAnOriginAndTheEmbedSurfaceImmediatelyServesIt()
    {
        // The editor `5-01` deferred, and the reason `10-04`'s stale-negative question does not
        // arise here: layer 1 has no cache at all, so an origin approved a moment ago is approved
        // now. Proved by doing it in one test with no wait.
        var seed = await ProvisionAsync();

        var before = await EmbedAsync(seed.Tenant.PublicKey.Value, "https://newly-added.example");
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        var set = await PutAsync(
            "/api/v1/console/configuration/allowed-origins",
            new SetAllowedOriginsRequest(["https://newly-added.example"]),
            seed);
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var after = await EmbedAsync(seed.Tenant.PublicKey.Value, "https://newly-added.example");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ADayOff_RequiresTheConfigurePermission_AndActsOnlyOnTheCallersOwnTenant()
    {
        // `20-02`'s manual edits had no actor until this item gave them an HTTP surface. This is the
        // check that arrived with it.
        var mine = await ProvisionAsync();
        var theirs = await ProvisionAsync();

        var response = await PostAsync(
            "/api/v1/console/availability/day-off",
            new DayOffRequest(theirs.Calendar.Id.Value, theirs.Worker.Id.Value, new DateOnly(2026, 5, 5)),
            mine);

        // Another tenant's calendar, reported as absent - never "you may not touch that one", which
        // would confirm it exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // **The code, not just the status**, and that distinction is not pedantry: it is what makes
        // this test able to fail. Deleting the cross-tenant check outright still produces a 404 -
        // the handler simply walks on and finds no materialised day - so a test asserting only the
        // status passes against a handler with no tenant boundary at all. Found by deleting the
        // check and watching the test go green.
        Assert.Contains(
            "availability.calendar_not_found",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecutPreview_WithoutTheConfigurePermission_Returns403NotAServerError()
    {
        // `22-20`'s own first Done-when, proven over real HTTP rather than at the switch: an
        // operator who holds no `Permission.CalendarConfigure` asking to preview a re-cut is an
        // ordinary permission refusal - before this item, `ErrorExtensions` had no arm for
        // `recut.forbidden` at all, so this same request reached the operator as a 500.
        var seed = await ProvisionAsync();
        var dispatcher = await ADispatcherAsync(seed);

        var response = await PostAsync(
            $"/api/v1/console/workers/{seed.Worker.Id.Value}/schedule/recut/preview",
            new RecutPreviewRequest(new DateOnly(2026, 5, 5)),
            dispatcher);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The code, not just the status - the same reasoning `ADayOff_RequiresTheConfigurePermission`'s
        // own comment gives: a status alone cannot distinguish this from a handler that refuses for a
        // different, wrongly-mapped reason.
        Assert.Contains(
            "recut.forbidden", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecutPreview_ForAWorkerThatDoesNotExist_Returns404NotAServerError()
    {
        // `22-20`'s own second Done-when, over real HTTP: a worker id that resolves to nothing in
        // this tenant is reported as absent, not as a crash - `recut.worker_not_found` had no arm
        // before this item either.
        var seed = await ProvisionAsync();

        var response = await PostAsync(
            $"/api/v1/console/workers/{Guid.NewGuid()}/schedule/recut/preview",
            new RecutPreviewRequest(new DateOnly(2026, 5, 5)),
            seed);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(
            "recut.worker_not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATenant_CanSaveAndReadBackAWorkersSchedule()
    {
        // `20-14`'s own console-level Done-when, over real HTTP: create-or-replace, then read the
        // same shape back - the schedule section of `20-13`'s worker card needs both in one round
        // trip to prefill an edit form without a second, different-shaped request.
        var seed = await ProvisionAsync();

        var saveResponse = await PutAsync(
            $"/api/v1/console/workers/{seed.Worker.Id.Value}/schedule",
            new SaveWorkerScheduleRequest(
                "Weekly", null, null, null, null, null,
                SlotMinutes: 45, BufferMinutes: 10, HorizonDays: 30, MaterializeFrom: new DateOnly(2026, 3, 2)),
            seed);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        var saved = await saveResponse.Content.ReadFromJsonAsync<WorkerScheduleResponse>();
        Assert.Equal("Weekly", saved!.Kind);
        Assert.Equal(45, saved.SlotMinutes);

        using var getRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/console/workers/{seed.Worker.Id.Value}/schedule");
        getRequest.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);
        var getResponse = await _client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<WorkerScheduleResponse>();
        Assert.Equal(saved.ScheduleId, fetched!.ScheduleId);
        Assert.Equal(10, fetched.BufferMinutes);
    }

    [Fact]
    public async Task AHorizonAboveOneEightyDays_IsRefusedByTheApiItself()
    {
        // CLAUDE.md's own instruction: the 180-day cap is enforced in the handler (which delegates to
        // WorkerSchedule's own validation), not only in the console's form - so a direct API call
        // cannot bypass it. This is that direct call.
        var seed = await ProvisionAsync();

        var response = await PutAsync(
            $"/api/v1/console/workers/{seed.Worker.Id.Value}/schedule",
            new SaveWorkerScheduleRequest(
                "Weekly", null, null, null, null, null,
                SlotMinutes: 45, BufferMinutes: 10, HorizonDays: 181, MaterializeFrom: new DateOnly(2026, 3, 2)),
            seed);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("configuration.invalid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingASchedule_RefusesToMoveMaterializeFromBackwards()
    {
        var seed = await ProvisionAsync();
        await PutAsync(
            $"/api/v1/console/workers/{seed.Worker.Id.Value}/schedule",
            new SaveWorkerScheduleRequest(
                "Weekly", null, null, null, null, null,
                SlotMinutes: 45, BufferMinutes: 10, HorizonDays: 30, MaterializeFrom: new DateOnly(2026, 3, 13)),
            seed);

        var response = await PutAsync(
            $"/api/v1/console/workers/{seed.Worker.Id.Value}/schedule",
            new SaveWorkerScheduleRequest(
                "Weekly", null, null, null, null, null,
                SlotMinutes: 45, BufferMinutes: 10, HorizonDays: 30, MaterializeFrom: new DateOnly(2026, 3, 2)),
            seed);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("configuration.invalid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<SeededTenant> ProvisionAsync() => await CalendarSeed.WriteAsync(fixture);

    /// <summary>An operator of the same tenant holding a role that grants the queue permissions and
    /// not <see cref="Permission.CalendarConfigure"/>.</summary>
    private async Task<SeededTenant> ADispatcherAsync(SeededTenant seed)
    {
        var subject = $"kc-{CalendarSeed.NewId():N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);
        string[] permissions = [Permission.BookingReject.Value, Permission.BookingCancel.Value];

        await using var db = fixture.CreateDbContext();
        var projections = new RoleAssignmentProjectionStore(db);
        await projections.StageAsync(operatorId, seed.Tenant.Id, subject, permissions, CalendarSeed.Now, CancellationToken.None);
        await db.SaveChangesAsync();

        return seed with { OperatorId = operatorId, ExternalSubjectId = subject };
    }

    private async Task<Event> APendingBookingAsync(SeededTenant seed, CalendarId calendarId, WorkerId workerId)
    {
        var startsAt = DateTimeOffset.UtcNow.AddDays(11).AddMinutes(Random.Shared.Next(0, 600));
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()),
            seed.Tenant.Id,
            calendarId,
            workerId,
            new TimeSlot(startsAt, startsAt.AddMinutes(45)),
            DateOnly.FromDateTime(startsAt.UtcDateTime),
            DateTimeOffset.UtcNow);

        slot.Claim(seed.Customer.Id, seed.Service.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2));
        slot.ClearDomainEvents();

        await using var db = fixture.CreateDbContext();
        db.Events.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    private async Task<TenantConfigurationResponse> GetConfigurationAsync(SeededTenant seed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/configuration");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TenantConfigurationResponse>())!;
    }

    private async Task<PendingBookingResponse[]> GetQueueAsync(SeededTenant seed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PendingBookingResponse[]>())!;
    }

    private async Task<HttpResponseMessage> EmbedAsync(string publicKey, string origin)
    {
        // `await` inside the `using`, never `return _client.SendAsync(request)`: disposing the
        // request before the task completes disposes its content stream underneath TestHost, which
        // surfaces as an ObjectDisposedException from deep inside the pipeline rather than as
        // anything resembling the real mistake. Found by writing it the other way first.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/embed/{publicKey}");
        request.Headers.Add("Origin", origin);
        return await _client.SendAsync(request);
    }

    private async Task<Guid> CreatedIdAsync(string url, object content, SeededTenant seed, string field)
    {
        var response = await PostAsync(url, content, seed);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>();
        return payload![field];
    }

    private Task<HttpResponseMessage> PostAsync(string url, object? content, SeededTenant seed) =>
        SendAsync(HttpMethod.Post, url, content, seed);

    private Task<HttpResponseMessage> PutAsync(string url, object content, SeededTenant seed) =>
        SendAsync(HttpMethod.Put, url, content, seed);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? content, SeededTenant seed)
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
