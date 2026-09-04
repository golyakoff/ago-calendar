using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-23`'s own Done-when, end to end over real HTTP against a real Postgres: a tenant with nothing
/// set up sees every precondition unmet, a tenant one fact short of bookable is told exactly which
/// one, and a tenant this endpoint calls bookable really can take a booking - proved in the same test
/// rather than trusted on the read's own word (the item's own "the two must agree, or the read is
/// decoration").
///
/// <para>Everything except the Keycloak signature is real, the same shape
/// <see cref="ConsoleEndpointTests"/> already establishes for this permission and this claims
/// transformation.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class BookingReadinessTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ConsoleAndPublicBookingApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ConsoleAndPublicBookingApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task ATenantWithNothingSetUp_GetsEveryPreconditionUnmetInAStableOrder()
    {
        var seed = await ABareTenantAsync();

        var readiness = await GetReadinessAsync(seed);

        var calendar = Assert.Single(readiness);
        Assert.Null(calendar.CalendarId);
        Assert.False(calendar.IsBookable);
        Assert.Equal(
            ["CalendarPublished", "WorkerOnCalendar", "ServiceOffered", "WorkingHoursConfigured", "ScheduleSaved", "SlotsMaterialized"],
            calendar.Preconditions.Select(p => p.Precondition));
        Assert.All(calendar.Preconditions, p => Assert.False(p.IsMet));
    }

    [Fact]
    public async Task AConfiguredTenantWithNoMaterializedSlots_IsReportedNotBookableOnTheSlotsPrecondition()
    {
        // Every fact but the last one, deliberately: a calendar, an active worker on it who offers a
        // service, working hours for that worker, and a saved schedule - and, on purpose, no
        // materialised Event row. `flows.md` 3.1's own named failure: a setup that looks finished.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeek.Monday);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 30);

        var readiness = await GetReadinessAsync(seed);

        var calendar = Assert.Single(readiness);
        Assert.Equal(seed.Calendar.Id.Value, calendar.CalendarId);
        Assert.False(calendar.IsBookable);

        var byName = calendar.Preconditions.ToDictionary(p => p.Precondition, p => p.IsMet);
        Assert.True(byName["CalendarPublished"]);
        Assert.True(byName["WorkerOnCalendar"]);
        Assert.True(byName["ServiceOffered"]);
        Assert.True(byName["WorkingHoursConfigured"]);
        Assert.True(byName["ScheduleSaved"]);
        Assert.False(byName["SlotsMaterialized"]);
    }

    [Fact]
    public async Task ACycleScheduledTenant_NeedsNoWorkingHoursRuleOfItsOwn()
    {
        // adr/0084: a Cycle schedule carries its own wall-clock window and never reads
        // working_hours_rules at all - reporting "working hours" unmet for a worker who chose Cycle
        // would be exactly the false negative this item exists to avoid.
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddCycleScheduleAsync(
            fixture, seed, anchor: new DateOnly(2026, 3, 2), workingDays: 1, restDays: 3,
            startsAt: new TimeOnly(9, 0), endsAt: new TimeOnly(18, 0), horizonDays: 30);

        var readiness = await GetReadinessAsync(seed);

        var calendar = Assert.Single(readiness);
        var byName = calendar.Preconditions.ToDictionary(p => p.Precondition, p => p.IsMet);
        Assert.True(byName["WorkingHoursConfigured"]);
        Assert.True(byName["ScheduleSaved"]);
        // Still not bookable - nothing materialised the slots this cycle schedule would eventually
        // produce, and this endpoint never infers a slot from configuration alone.
        Assert.False(byName["SlotsMaterialized"]);
        Assert.False(calendar.IsBookable);
    }

    [Fact]
    public async Task AFullySetUpTenant_IsReportedBookable_AndAPublicBookingAgainstItSucceeds()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), CalendarSeed.EveryDay);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 30);

        var startsAt = DateTimeOffset.UtcNow.AddDays(7);
        var slot = CalendarSeed.Slot(seed, startsAt);
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        var readiness = await GetReadinessAsync(seed);

        var calendar = Assert.Single(readiness);
        Assert.Equal(seed.Calendar.Id.Value, calendar.CalendarId);
        Assert.True(calendar.IsBookable, $"expected every precondition met; got {Describe(calendar)}");
        Assert.All(calendar.Preconditions, p => Assert.True(p.IsMet, $"{p.Precondition} was reported unmet"));

        // The two must agree, or the read is decoration (the item's own words): the same slot this
        // endpoint just called bookable really can be booked, over real HTTP, through the real public
        // booking path.
        await SeedVerifiedCustomerAsync(seed, "+79996001234");
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{slot.Id.Value}/book",
            new BookEventRequest(seed.Service.Id.Value, "+79996001234", "Anna"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantsReadiness_CannotBeRead()
    {
        var mine = await ABareTenantAsync();
        var theirs = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, theirs, new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeek.Monday);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, theirs, horizonDays: 30);

        var readiness = await GetReadinessAsync(mine);

        // `mine` has no calendar of its own - the fully-unmet placeholder, and never a glimpse of
        // `theirs`'s calendar id, name, or readiness.
        var calendar = Assert.Single(readiness);
        Assert.Null(calendar.CalendarId);
        Assert.DoesNotContain(readiness, c => c.CalendarId == theirs.Calendar.Id.Value);
    }

    private static string Describe(CalendarReadinessResponse calendar) =>
        string.Join(", ", calendar.Preconditions.Select(p => $"{p.Precondition}={p.IsMet}"));

    /// <summary>A tenant and an operator who can reach <c>booking-readiness</c>, and nothing else -
    /// no calendar, no worker, no service. `22-05`/`adr/0093`: an operator is a
    /// <c>role_assignment_projections</c> row, not an aggregate this test creates and grants a role
    /// to - <see cref="CalendarSeed.WriteAsync"/>'s own remarks explain why this is written through
    /// the real <see cref="RoleAssignmentProjectionStore"/> adapter rather than a fake.</summary>
    private async Task<SeededTenant> ABareTenantAsync()
    {
        var tenant = Tenant.Register(
            new TenantId(CalendarSeed.NewId()), "Empty shop",
            new TenantPublicKey($"empty-{CalendarSeed.NewId():N}"[..24]), CalendarSeed.Now, []);
        var subject = $"kc-{CalendarSeed.NewId():N}";
        var operatorId = OperatorId.FromExternalSubjectId(subject);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        var projections = new RoleAssignmentProjectionStore(db);
        await projections.StageAsync(
            operatorId, tenant.Id, subject, CalendarSeed.AllPermissions, CalendarSeed.Now, CancellationToken.None);
        await db.SaveChangesAsync();

        // The rest of SeededTenant is never touched by a "nothing set up" test - null!s are fine for
        // a record this test never reads past Tenant/OperatorId/ExternalSubjectId.
        return new SeededTenant(tenant, null!, null!, null!, null!, operatorId, subject);
    }

    private async Task SeedVerifiedCustomerAsync(SeededTenant seed, string phone)
    {
        var parsed = new PhoneNumber(phone);
        await using var db = fixture.CreateDbContext();
        var customer = Customer.Register(new CustomerId(CalendarSeed.NewId()), seed.Tenant.Id, parsed, CalendarSeed.Now);
        customer.RecordVerifiedPhone(CalendarSeed.Now);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
    }

    private async Task<CalendarReadinessResponse[]> GetReadinessAsync(SeededTenant seed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/booking-readiness");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarReadinessResponse[]>())!;
    }
}

/// <summary>
/// <see cref="ConsoleApiFactory"/> cannot be subclassed (it is sealed), so this replicates its own
/// <c>X-Test-Subject</c> wiring verbatim and adds <see cref="BookingApiFactory"/>'s own
/// <c>PublicBookingApi:Enabled</c> override - this suite is the one place a test needs both an
/// authenticated console operator and an open public booking endpoint in the same host, which neither
/// existing factory offers alone.
/// </summary>
internal sealed class ConsoleAndPublicBookingApiFactory(PostgresFixture fixture) : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("PublicBookingApi:Enabled", "true");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, HeaderSubjectAuthenticationHandler>(
                    HeaderSubjectAuthenticationHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = HeaderSubjectAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = HeaderSubjectAuthenticationHandler.SchemeName;
            });
        });
    }
}
