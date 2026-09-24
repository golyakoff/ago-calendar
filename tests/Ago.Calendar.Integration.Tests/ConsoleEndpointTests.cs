using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
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
        // `22-07`: quota 2 - the seeded worker plus the one this test creates through the endpoint.
        var seed = await ProvisionAsync(workerQuota: 2);

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
        // `22-07`: the granted quota is visible on the same screen that is refusing to exceed it.
        Assert.Equal(2, configuration.WorkerQuota);
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
    public async Task AWorkingHoursRule_CanBeCorrectedAndRemoved_AndBookingReadinessFollows()
    {
        // `26-97`'s first Done-when, end to end: until this item a mistyped 09:00-for-19:00 was
        // permanent and deleting the worker was the only remedy in the product. Read back through the
        // console's own configuration screen *and* through `booking-readiness`, because precondition 4
        // is the one a removed rule has to stop satisfying - a delete that left the tenant "bookable"
        // with no hours would be a worse defect than the one this item fixes.
        var seed = await ProvisionAsync();

        var ruleId = await CreatedIdAsync(
            "/api/v1/console/working-hours",
            new AddWorkingHoursRuleRequest(
                seed.Calendar.Id.Value, seed.Worker.Id.Value, (int)DayOfWeek.Tuesday,
                new TimeOnly(9, 0), new TimeOnly(9, 30)),
            seed,
            "ruleId");

        Assert.True(await WorkingHoursConfiguredAsync(seed));

        var corrected = await PutAsync(
            $"/api/v1/console/working-hours/{ruleId}",
            new UpdateWorkingHoursRuleRequest((int)DayOfWeek.Wednesday, new TimeOnly(10, 0), new TimeOnly(19, 0)),
            seed);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        var body = (await corrected.Content.ReadFromJsonAsync<WorkingHoursRuleChangeResponse>())!;
        Assert.Equal(ruleId, body.Rule!.RuleId);
        Assert.Equal((int)DayOfWeek.Wednesday, body.Rule.DayOfWeek);
        Assert.Equal(new TimeOnly(19, 0), body.Rule.EndsAt);

        // The tenant's own screen agrees, through a real read of a real row - not the echo above.
        var configuration = await GetConfigurationAsync(seed);
        var stored = Assert.Single(
            Assert.Single(configuration.Calendars, c => c.CalendarId == seed.Calendar.Id.Value).WorkingHours);
        Assert.Equal((int)DayOfWeek.Wednesday, stored.DayOfWeek);
        Assert.Equal(new TimeOnly(10, 0), stored.StartsAt);

        var removed = await DeleteAsync($"/api/v1/console/working-hours/{ruleId}", seed);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        Assert.Empty(
            Assert.Single(
                (await GetConfigurationAsync(seed)).Calendars, c => c.CalendarId == seed.Calendar.Id.Value)
            .WorkingHours);
        Assert.False(await WorkingHoursConfiguredAsync(seed));
    }

    [Fact]
    public async Task AWorkingHoursRuleOfAnotherTenant_CanBeNeitherCorrectedNorRemoved()
    {
        // `26-97`'s tenant Done-when. The permission check passes - this operator holds
        // calendar:configure in their own tenant - and what stops them is the check against the tenant
        // on the rule's own calendar, which is exactly the boundary `WorkingHoursRule.For` already
        // guards on the way in (TenantMismatchException) expressed as an HTTP refusal.
        var mine = await ProvisionAsync();
        var theirs = await ProvisionAsync();

        var theirRuleId = await CreatedIdAsync(
            "/api/v1/console/working-hours",
            new AddWorkingHoursRuleRequest(
                theirs.Calendar.Id.Value, theirs.Worker.Id.Value, (int)DayOfWeek.Tuesday,
                new TimeOnly(9, 0), new TimeOnly(18, 0)),
            theirs,
            "ruleId");

        var edit = await PutAsync(
            $"/api/v1/console/working-hours/{theirRuleId}",
            new UpdateWorkingHoursRuleRequest((int)DayOfWeek.Sunday, new TimeOnly(0, 1), new TimeOnly(23, 59)),
            mine);
        var delete = await DeleteAsync($"/api/v1/console/working-hours/{theirRuleId}", mine);

        // Reported as absent, never as "you may not touch that one", which would confirm it exists.
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // **The code, not just the status** - the same reasoning the day-off test below gives for
        // asserting on it: a handler with no tenant boundary at all would still 404 on some other
        // path, and a test asserting only the status would pass against one.
        Assert.Contains(
            "configuration.not_found", await edit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(
            "configuration.not_found", await delete.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // And the row is untouched, read straight out of the database rather than inferred.
        await using var db = fixture.CreateDbContext();
        var row = await db.WorkingHoursRules.SingleAsync(rule => rule.Id == new WorkingHoursRuleId(theirRuleId));
        Assert.Equal(DayOfWeek.Tuesday, row.DayOfWeek);
        Assert.Equal(new TimeOnly(18, 0), row.EndsAt);
    }

    [Fact]
    public async Task CorrectingARuleWithDaysAlreadyCut_ReportsThemAndTheLiveBookingOnThem()
    {
        // `26-97`'s materialisation Done-when, and the whole reason the answer is "always allow, never
        // stay silent" rather than "refuse while a booking exists" - see `WorkingHoursReconciler` for
        // the full decision. The edit succeeds; what comes back with it is the days already cut from
        // the old hours, the live booking sitting on one of them, and the exact date to hand
        // POST /workers/{id}/schedule/recut/preview.
        var seed = await ProvisionAsync();
        var today = TodayInSeededZone();

        var ruleId = await CreatedIdAsync(
            "/api/v1/console/working-hours",
            new AddWorkingHoursRuleRequest(
                seed.Calendar.Id.Value, seed.Worker.Id.Value, (int)today.DayOfWeek,
                new TimeOnly(9, 0), new TimeOnly(9, 30)),
            seed,
            "ruleId");

        // Two weeks already cut: the cursor sits past them, which is precisely what the materialiser
        // will not go back below on its own (MaterializeAvailabilityHandler's forward-only rule).
        await CalendarSeed.AddWeeklyScheduleAsync(
            fixture, seed, horizonDays: 30, materializeFrom: today.AddDays(14));
        await ABookingOnAsync(seed, today.AddDays(7));

        var response = await PutAsync(
            $"/api/v1/console/working-hours/{ruleId}",
            new UpdateWorkingHoursRuleRequest((int)today.DayOfWeek, new TimeOnly(9, 0), new TimeOnly(19, 0)),
            seed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reconciliation = (await response.Content.ReadFromJsonAsync<WorkingHoursRuleChangeResponse>())!
            .Reconciliation;

        Assert.Equal(today, reconciliation.RecutFrom);
        Assert.Equal([today, today.AddDays(7)], reconciliation.AlreadyCutDays);
        Assert.Equal(1, reconciliation.LiveBookingCount);

        // The date it hands back is one the re-cut flow actually accepts - the point of pre-filling it
        // rather than asking an operator to derive it from a cursor they cannot see.
        var preview = await PostAsync(
            $"/api/v1/console/workers/{seed.Worker.Id.Value}/schedule/recut/preview",
            new RecutPreviewRequest(reconciliation.RecutFrom!.Value),
            seed);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    [Fact]
    public async Task AServiceCreatedWithAPriceAndADescription_ReadsBackBothThroughTheRealStore()
    {
        // `23-35`'s own demonstration: not asserted, proved end to end - a real POST, a real Postgres
        // row (EF Core's complex-property mapping for Money?, exercised for the first time against a
        // real database rather than only an in-memory model), and a real GET reading it back through
        // the console's own configuration screen.
        var seed = await ProvisionAsync();

        var serviceId = await CreatedIdAsync(
            "/api/v1/console/services",
            new CreateServiceRequest(
                "Colour", 90, PriceMinorUnits: 350000, PriceIsFrom: true, Description: "Full colour and toner."),
            seed,
            "serviceId");

        var configuration = await GetConfigurationAsync(seed);

        var created = Assert.Single(configuration.Services, service => service.ServiceId == serviceId);
        Assert.Equal(350000, created.PriceMinorUnits);
        Assert.Equal("RUB", created.PriceCurrencyCode);
        Assert.True(created.PriceIsFrom);
        Assert.Equal("Full colour and toner.", created.Description);
    }

    [Fact]
    public async Task AServiceCreatedWithNeitherAPriceNorADescription_ReadsBackNeither()
    {
        // The honest-absence half of the same claim: null is not "not yet supported", it survives the
        // round trip through EF Core's nullable complex property as null, not as a zero or an empty
        // string that a renderer could mistake for a stated fact.
        var seed = await ProvisionAsync();

        var serviceId = await CreatedIdAsync(
            "/api/v1/console/services", new CreateServiceRequest("Consultation", 15), seed, "serviceId");

        var configuration = await GetConfigurationAsync(seed);

        var created = Assert.Single(configuration.Services, service => service.ServiceId == serviceId);
        Assert.Null(created.PriceMinorUnits);
        Assert.Null(created.PriceCurrencyCode);
        Assert.False(created.PriceIsFrom);
        Assert.Null(created.Description);
    }

    [Fact]
    public async Task AService_CanBeCorrectedAfterTheFact()
    {
        // `26-96`'s first Done-when, end to end: the typo in a visitor-facing duration and price that
        // was permanent in this product until this item. A real PUT, a real row, and the correction
        // read back through the same GET both clients use.
        var seed = await ProvisionAsync();
        var serviceId = await CreatedIdAsync(
            "/api/v1/console/services",
            new CreateServiceRequest("Haicut", 4, PriceMinorUnits: 90000),
            seed,
            "serviceId");

        var response = await PutAsync(
            $"/api/v1/console/services/{serviceId}",
            new UpdateServiceRequest(
                "Haircut", 45, PriceMinorUnits: 150000, PriceIsFrom: true,
                Description: "Wash and cut.", IsActive: true),
            seed);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var configuration = await GetConfigurationAsync(seed);
        var corrected = Assert.Single(configuration.Services, service => service.ServiceId == serviceId);
        Assert.Equal("Haircut", corrected.Name);
        Assert.Equal(45, corrected.DurationMinutes);
        Assert.Equal(150000, corrected.PriceMinorUnits);
        Assert.True(corrected.PriceIsFrom);
        Assert.Equal("Wash and cut.", corrected.Description);
        Assert.True(corrected.IsActive);
    }

    [Fact]
    public async Task EditingAService_RefusesExactlyWhereCreatingOneAlreadyRefuses()
    {
        // The API itself, not only the console's form - the same direct-call check
        // AHorizonAboveOneEightyDays_IsRefusedByTheApiItself makes for a schedule.
        var seed = await ProvisionAsync();
        var serviceId = await CreatedIdAsync(
            "/api/v1/console/services", new CreateServiceRequest("Haircut", 45), seed, "serviceId");

        var response = await PutAsync(
            $"/api/v1/console/services/{serviceId}",
            new UpdateServiceRequest("Haircut", 0, null, false, null, IsActive: true),
            seed);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("configuration.invalid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchivedService_DisappearsFromTheBookingSurfaceAndStaysOnTheConsoleRead()
    {
        // `26-96`'s third Done-when, and the whole argument for option (a) over a DELETE, proved
        // against real SQL rather than asserted: the seeded worker performs the seeded service, so
        // archiving it has to leave the worker's own service list (and every booking that named it)
        // able to resolve the name, while the visitor-facing surface stops offering it.
        var seed = await ProvisionAsync();

        var beforeArchiving = await BookableServicesAsync(seed);
        Assert.Contains(beforeArchiving, service => service.ServiceId == seed.Service.Id);

        var response = await PutAsync(
            $"/api/v1/console/services/{seed.Service.Id.Value}",
            new UpdateServiceRequest(seed.Service.Name, 45, null, false, null, IsActive: false),
            seed);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var afterArchiving = await BookableServicesAsync(seed);
        Assert.DoesNotContain(afterArchiving, service => service.ServiceId == seed.Service.Id);

        var configuration = await GetConfigurationAsync(seed);
        var archived = Assert.Single(
            configuration.Services, service => service.ServiceId == seed.Service.Id.Value);
        Assert.False(archived.IsActive);
        Assert.Equal(seed.Service.Name, archived.Name);
        Assert.Contains(
            configuration.Workers,
            worker => worker.WorkerId == seed.Worker.Id.Value
                      && worker.ServiceIds.Contains(seed.Service.Id.Value));
    }

    [Fact]
    public async Task AnOperator_SeesThePendingQueueAcrossEveryCalendarAndCanRejectFromIt()
    {
        // `20-06`'s second Done-when, including its own parenthesis: "two calendars, confirming the
        // queue is not scoped to one".
        // `22-07`: quota 2 - the seeded worker plus the one this test creates through the endpoint.
        var seed = await ProvisionAsync(workerQuota: 2);
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

    /// <param name="workerQuota">`22-07`: most callers here never create a worker beyond the one
    /// <see cref="CalendarSeed.WriteAsync"/> already seeds directly, so the default of zero (no add-on
    /// grant) is fine for them. The two tests that go on to create a further worker through the real
    /// <c>POST /api/v1/console/workers</c> endpoint pass a quota that accounts for both.</param>
    private async Task<SeededTenant> ProvisionAsync(int workerQuota = 0) =>
        await CalendarSeed.WriteAsync(fixture, workerQuota: workerQuota);

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

    /// <summary>`26-97`: precondition 4 of the six `booking-readiness` checks, for the seeded
    /// calendar - the one a working-hours rule is the whole evidence for.</summary>
    private async Task<bool> WorkingHoursConfiguredAsync(SeededTenant seed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/booking-readiness");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var calendars = (await response.Content.ReadFromJsonAsync<CalendarReadinessResponse[]>())!;
        var calendar = Assert.Single(calendars, c => c.CalendarId == seed.Calendar.Id.Value);
        return Assert.Single(calendar.Preconditions, p => p.Precondition == "WorkingHoursConfigured").IsMet;
    }

    /// <summary>`26-97`: today as the seeded calendar's own zone sees it, which is the only "today"
    /// the reconciliation is computed against - a UTC date would be a day out for part of every day
    /// in Europe/Moscow.</summary>
    private static DateOnly TodayInSeededZone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
    }

    /// <summary>A claimed slot on one business-local day - the row the reconciliation has to notice,
    /// written through the real aggregate and the real mappings rather than as raw SQL.</summary>
    private async Task ABookingOnAsync(SeededTenant seed, DateOnly localDate)
    {
        // Stored as UTC, exactly as `timestamptz` demands (date-and-time.md) - the wall-clock 09:00
        // is the calendar's own, and the conversion happens here rather than being written as an
        // offset Npgsql refuses.
        var startsAt = new DateTimeOffset(localDate, new TimeOnly(9, 0), TimeSpan.FromHours(3))
            .ToUniversalTime();
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()),
            seed.Tenant.Id,
            seed.Calendar.Id,
            seed.Worker.Id,
            new TimeSlot(startsAt, startsAt.AddMinutes(45)),
            localDate,
            DateTimeOffset.UtcNow);

        slot.Claim(seed.Customer.Id, seed.Service.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2));
        slot.ClearDomainEvents();

        await using var db = fixture.CreateDbContext();
        db.Events.Add(slot);
        await db.SaveChangesAsync();
    }

    private async Task<TenantConfigurationResponse> GetConfigurationAsync(SeededTenant seed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/configuration");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TenantConfigurationResponse>())!;
    }

    /// <summary>`26-96`: what a visitor would be offered on this tenant's own calendar, read through
    /// the real <see cref="BookingSurfaceReadStore"/> against the real database rather than through
    /// the public HTTP surface, which would need an allowed origin and an embed scope this test has
    /// no opinion about. The SQL is the thing under test here - the <c>s.is_active</c> predicate -
    /// and this is the shortest path to it that still runs the shipped query.</summary>
    private async Task<IReadOnlyList<BookableServiceRow>> BookableServicesAsync(SeededTenant seed) =>
        await new BookingSurfaceReadStore(fixture.DataSource)
            .ListServicesAsync(seed.Calendar.Id, CancellationToken.None);

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

    private Task<HttpResponseMessage> DeleteAsync(string url, SeededTenant seed) =>
        SendAsync(HttpMethod.Delete, url, null, seed);

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
