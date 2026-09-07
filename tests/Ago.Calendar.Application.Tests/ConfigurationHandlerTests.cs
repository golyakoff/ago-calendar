using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-06`'s tenant setup, at the level where its two rules live: <b>a permission is held</b> and
/// <b>the thing being configured belongs to the tenant that holds it</b>. Both are checked in the
/// handler rather than at the endpoint, for the reason `20-04` established - an authorization
/// decision made in a host is a decision one route can silently skip.
/// </summary>
public class ConfigurationHandlerTests
{
    private static readonly OperatorId Actor = new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Fact]
    public async Task CreatingACalendar_RequiresCalendarConfigure()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.CreateCalendarAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Calendars.Added);
    }

    [Fact]
    public async Task CreatingACalendar_ChecksThePermissionAgainstTheTenantTheActionNames()
    {
        // Not against the operator's own row. PermissionChecker filters roles by the tenant the
        // *action* names precisely so a token claiming another tenant resolves to no roles at all;
        // a handler that passed the operator's own tenant instead would defeat that.
        var world = new World();

        await world.CreateCalendarAsync();

        Assert.Equal(
            (Permission.CalendarConfigure, BookingFixtures.TenantId), Assert.Single(world.Permissions.Checked));
    }

    [Fact]
    public async Task CreatingACalendar_PublishesItOnlyWhenAsked()
    {
        var world = new World();

        Assert.True((await world.CreateCalendarAsync(publish: false)).IsSuccess);
        Assert.False(Assert.Single(world.Calendars.Added).IsPublished);

        var published = new World();
        Assert.True((await published.CreateCalendarAsync(publish: true)).IsSuccess);
        Assert.True(Assert.Single(published.Calendars.Added).IsPublished);
    }

    [Theory]
    [InlineData("", "Europe/Moscow")]
    [InlineData("Main", "+03:00")]
    public async Task CreatingACalendar_TurnsADomainRefusalIntoAnOrdinaryRejection(string name, string zone)
    {
        // A tenant typing an unresolvable zone id is a caller mistake. Letting the aggregate's
        // ArgumentException escape would make a 400 look like a 500 in every log.
        var world = new World();

        var result = await world.CreateCalendarAsync(name: name, timeZone: zone);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.invalid", result.Error!.Value.Code);
        Assert.Empty(world.Calendars.Added);
    }

    [Fact]
    public async Task CreatingAWorker_JoinsTheCalendarAndRecordsTheServices_InOneAggregate()
    {
        var world = new World();

        var result = await world.CreateWorkerAsync(serviceIds: [BookingFixtures.ServiceId.Value]);

        Assert.True(result.IsSuccess);
        var worker = Assert.Single(world.Workers.Added);
        Assert.True(worker.WorksIn(BookingFixtures.CalendarId));
        Assert.True(worker.Offers(BookingFixtures.ServiceId));
    }

    [Fact]
    public async Task CreatingAWorker_OnAnotherTenantsCalendar_IsNotFound()
    {
        // The id exists and belongs to somebody else. Reported as absent, never as forbidden: an
        // operator of tenant A learning that an id exists in tenant B is a cross-tenant leak however
        // politely it is worded.
        var world = new World();

        var result = await world.CreateWorkerAsync(calendarId: Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.not_found", result.Error!.Value.Code);
        Assert.Empty(world.Workers.Added);
    }

    [Fact]
    public async Task CreatingAWorker_WithAServiceThatIsNotThisTenants_IsNotFound()
    {
        var world = new World();

        var result = await world.CreateWorkerAsync(serviceIds: [Guid.NewGuid()]);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.not_found", result.Error!.Value.Code);
        Assert.Empty(world.Workers.Added);
    }

    /// <summary>`22-07`'s own Done-when: "the (N+1)-th worker is refused... proven by trying." N
    /// itself is created without incident here - see the next test for the refusal.</summary>
    [Fact]
    public async Task CreatingAWorker_UpToTheGrantedQuota_Succeeds()
    {
        var world = new World { Workers = { Quota = 2 } };

        var first = await world.CreateWorkerAsync(lastName: "One", firstName: "A");
        var second = await world.CreateWorkerAsync(lastName: "Two", firstName: "B");

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, world.Workers.Added.Count);
    }

    [Fact]
    public async Task CreatingTheNPlusFirstWorker_IsRefused_AndWritesNothing()
    {
        var world = new World { Workers = { Quota = 1 } };
        Assert.True((await world.CreateWorkerAsync(lastName: "One", firstName: "A")).IsSuccess);

        var result = await world.CreateWorkerAsync(lastName: "Two", firstName: "B");

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", result.Error!.Value.Code);
        Assert.Single(world.Workers.Added);
    }

    [Fact]
    public async Task CreatingAWorker_WithNoGrantAtAll_IsRefused()
    {
        // `22-07`: zero is Tenant.Register's own default - a tenant who has not bought the add-on
        // (or whose grant has not yet arrived over the outbox) cannot create a first worker, and
        // that is the intended refusal, not a bug in this fake.
        var world = new World { Workers = { Quota = 0 } };

        var result = await world.CreateWorkerAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", result.Error!.Value.Code);
        Assert.Empty(world.Workers.Added);
    }

    [Fact]
    public async Task CreatingAWorker_WithAnExplicitDisplayName_MarksItCustomFromTheStart()
    {
        var world = new World();

        var result = await world.CreateWorkerAsync(lastName: "Fox", firstName: "Robin", displayName: "Foxy");

        Assert.True(result.IsSuccess);
        var worker = Assert.Single(world.Workers.Added);
        Assert.Equal("Foxy", worker.DisplayName);
        Assert.True(worker.DisplayNameIsCustom);
    }

    [Fact]
    public async Task CreatingAWorker_WithNoExplicitDisplayName_DerivesIt()
    {
        var world = new World();

        var result = await world.CreateWorkerAsync(lastName: "Fox", firstName: "Robin");

        Assert.True(result.IsSuccess);
        var worker = Assert.Single(world.Workers.Added);
        Assert.Equal("Robin Fox", worker.DisplayName);
        Assert.False(worker.DisplayNameIsCustom);
    }

    [Fact]
    public async Task UpdatingAWorker_RequiresCalendarConfigure()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync()).Value;
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.UpdateWorkerAsync(workerId);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task UpdatingAnotherTenantsWorker_IsNotFound()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync()).Value;

        var result = await world.UpdateWorkerAsync(workerId, tenantId: new TenantId(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task UpdatingAWorker_WithNoExplicitDisplayName_KeepsDerivingIt()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync(lastName: "Fox", firstName: "Robin")).Value;

        var result = await world.UpdateWorkerAsync(workerId, lastName: "Sparrow", firstName: "Robin");

        Assert.True(result.IsSuccess);
        var worker = Assert.Single(world.Workers.Added);
        Assert.Equal("Robin Sparrow", worker.DisplayName);
        Assert.False(worker.DisplayNameIsCustom);
    }

    [Fact]
    public async Task UpdatingAWorker_WithAnExplicitDisplayName_FreezesItAgainstLaterRenames()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync(lastName: "Fox", firstName: "Robin")).Value;

        Assert.True((await world.UpdateWorkerAsync(workerId, lastName: "Fox", firstName: "Robin", displayName: "Foxy")).IsSuccess);
        Assert.True(Assert.Single(world.Workers.Added).DisplayNameIsCustom);

        // A second update, with no explicit display name this time, must not silently recompute over
        // the custom one - the exact rule `WorkerTests` proves at the aggregate's own level, exercised
        // here through the handler that actually calls Rename then (conditionally) SetDisplayName.
        Assert.True((await world.UpdateWorkerAsync(workerId, lastName: "Sparrow", firstName: "Robin")).IsSuccess);
        Assert.Equal("Foxy", Assert.Single(world.Workers.Added).DisplayName);
    }

    [Fact]
    public async Task UpdatingAWorker_TogglesActivity()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync()).Value;

        Assert.True((await world.UpdateWorkerAsync(workerId, isActive: false)).IsSuccess);
        Assert.False(Assert.Single(world.Workers.Added).IsActive);

        Assert.True((await world.UpdateWorkerAsync(workerId, isActive: true)).IsSuccess);
        Assert.True(Assert.Single(world.Workers.Added).IsActive);
    }

    [Fact]
    public async Task ReactivatingAWorker_PastTheQuota_IsRefused()
    {
        // 22-23: a downgrade deactivates the excess (`adr/0125`) rather than deleting it, and this is
        // the bypass that leaves - reactivating one of those rows through the ordinary edit endpoint
        // with no gate at all.
        var world = new World();
        world.Workers.Quota = 2;
        await world.CreateWorkerAsync(lastName: "Kept", firstName: "One");

        // Added directly rather than through a second CreateWorkerAsync call - World's own
        // SequentialIdGenerator is instantiated fresh per call, so two calls in the same test would
        // mint the identical id and collide in Added, a test-fixture quirk unrelated to what this
        // test proves. The downgrade itself is 22-07's own concern (out of scope here) - only its
        // aftermath, a deactivated worker sitting at/over quota, matters to this test.
        var excess = Worker.Create(
            new WorkerId(Guid.NewGuid()), BookingFixtures.TenantId, "Excess", "Two", null, BookingFixtures.Now);
        world.Workers.Added.Add(excess);
        excess.Deactivate(BookingFixtures.Now);
        world.Workers.Quota = 1;

        var result = await world.UpdateWorkerAsync(excess.Id, isActive: true);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.worker_quota_exceeded", result.Error!.Value.Code);
    }

    [Fact]
    public async Task ReactivatingAWorker_WithinTheQuota_Succeeds()
    {
        // Quota of exactly one, with no other active worker - this is what forces the count to
        // exclude the worker being reactivated by its own id (see
        // IWorkerRepository.TryReactivateWithinQuotaAsync's own remarks): the fake's Added list holds
        // the same reference the handler already flipped IsActive on before calling in, so without
        // the exclusion this worker would be counted against its own reactivation.
        var world = new World();
        world.Workers.Quota = 1;
        var workerId = (await world.CreateWorkerAsync()).Value;
        Assert.Single(world.Workers.Added, w => w.Id == workerId).Deactivate(BookingFixtures.Now);

        var result = await world.UpdateWorkerAsync(workerId, isActive: true);

        Assert.True(result.IsSuccess);
        Assert.True(Assert.Single(world.Workers.Added).IsActive);
    }

    [Fact]
    public async Task UpdatingAnAlreadyActiveWorker_AtTheQuotaLimit_IsNotGatedByQuota()
    {
        // 22-23's own regression case: gating on "command.IsActive == true" instead of on the
        // false-to-true transition would refuse this ordinary rename the instant the tenant's *other*
        // workers alone already fill the quota - even though this worker was already active and
        // nothing about his own activity is changing. A single-worker tenant would not expose this
        // (excluding the worker being saved from his own count leaves nobody else to hit the limit),
        // so a second, already-active worker is what makes the naive gate actually bite.
        var world = new World();
        world.Workers.Quota = 2;
        var workerId = (await world.CreateWorkerAsync(lastName: "Doe")).Value;
        var other = Worker.Create(
            new WorkerId(Guid.NewGuid()), BookingFixtures.TenantId, "Other", "Person", null, BookingFixtures.Now);
        world.Workers.Added.Add(other);
        world.Workers.Quota = 1;

        var result = await world.UpdateWorkerAsync(workerId, lastName: "Renamed", isActive: true);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", Assert.Single(world.Workers.Added, w => w.Id == workerId).LastName);
    }

    [Fact]
    public async Task DeletingAWorker_RequiresCalendarConfigure()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync()).Value;
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.DeleteWorkerAsync(workerId);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.forbidden", result.Error!.Value.Code);
        Assert.Single(world.Workers.Added);
    }

    [Fact]
    public async Task DeletingAWorker_WithNoBookingHistory_Succeeds()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync()).Value;

        var result = await world.DeleteWorkerAsync(workerId);

        Assert.True(result.IsSuccess);
        Assert.Empty(world.Workers.Added);
    }

    [Fact]
    public async Task DeletingAWorker_WithBookingHistory_RefusesAndSaysWhy()
    {
        var world = new World();
        var workerId = (await world.CreateWorkerAsync()).Value;
        world.Workers.Undeletable.Add(workerId);

        var result = await world.DeleteWorkerAsync(workerId);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.worker_has_booking_history", result.Error!.Value.Code);
        Assert.Single(world.Workers.Added);
    }

    // `23-35`: a service has a price and a description, or it deliberately does not.

    [Fact]
    public async Task CreatingAService_RequiresCalendarConfigure()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.CreateServiceAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Services.Added);
    }

    [Fact]
    public async Task CreatingAService_WithAPriceAndADescription_StoresBoth()
    {
        var world = new World();

        var result = await world.CreateServiceAsync(
            priceMinorUnits: 250000, priceIsFrom: true, description: "Colour and cut.");

        Assert.True(result.IsSuccess);
        var created = Assert.Single(world.Services.Added);
        Assert.Equal(Money.Rubles(250000), created.Price);
        Assert.True(created.PriceIsFrom);
        Assert.Equal("Colour and cut.", created.Description);
    }

    [Fact]
    public async Task CreatingAService_WithNeitherAPriceNorADescription_IsValid()
    {
        // A nullable field that is sometimes wrong is worse than an honest absence - see the
        // backlog item's own Goal. A shop still setting itself up leaves both blank.
        var world = new World();

        var result = await world.CreateServiceAsync();

        Assert.True(result.IsSuccess);
        var created = Assert.Single(world.Services.Added);
        Assert.Null(created.Price);
        Assert.Null(created.Description);
    }

    [Fact]
    public async Task CreatingAService_WithANegativePrice_TurnsADomainRefusalIntoAnOrdinaryRejection()
    {
        var world = new World();

        var result = await world.CreateServiceAsync(priceMinorUnits: -1);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.invalid", result.Error!.Value.Code);
        Assert.Empty(world.Services.Added);
    }

    [Fact]
    public async Task SettingAllowedOrigins_RequiresCalendarConfigure()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.SetOriginsAsync(["https://shop.example"]);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task SettingAllowedOrigins_RefusesAnOriginWithAPath()
    {
        var world = new World();

        var result = await world.SetOriginsAsync(["https://shop.example/booking"]);

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task SettingAllowedOrigins_ReplacesTheTenantsList()
    {
        var world = new World();

        Assert.True((await world.SetOriginsAsync(["https://a.example", "https://b.example"])).IsSuccess);

        Assert.Equal(["https://a.example", "https://b.example"], world.Tenant.AllowedOrigins);
    }

    private sealed class World
    {
        public World()
        {
            Tenant = BookingFixtures.Tenant([]);
            Calendars = new RecordingCalendarRepository(BookingFixtures.Calendar());
        }

        public Tenant Tenant { get; }

        public FakePermissionChecker Permissions { get; } = new();

        public RecordingCalendarRepository Calendars { get; }

        public RecordingWorkerRepository Workers { get; } = new();

        public RecordingServiceRepository Services { get; } = new();

        public Task<Result<CalendarId>> CreateCalendarAsync(
            string name = "Main", string timeZone = "Europe/Moscow", bool publish = true) =>
            new CreateCalendarHandler(
                    new FakeTenantRepository(Tenant),
                    Calendars,
                    Permissions,
                    new SequentialIdGenerator(),
                    new FakeClock(BookingFixtures.Now))
                .HandleAsync(
                    new CreateCalendar(Actor, BookingFixtures.TenantId, name, timeZone, publish),
                    CancellationToken.None);

        public Task<Result<WorkerId>> CreateWorkerAsync(
            Guid? calendarId = null,
            IReadOnlyList<Guid>? serviceIds = null,
            string lastName = "Doe",
            string firstName = "Alex",
            string? middleName = null,
            string? displayName = null) =>
            new CreateWorkerHandler(
                    Calendars,
                    new FakeServiceRepository(BookingFixtures.HaircutService()),
                    Workers,
                    Permissions,
                    new SequentialIdGenerator(),
                    new FakeClock(BookingFixtures.Now))
                .HandleAsync(
                    new CreateWorker(
                        Actor,
                        BookingFixtures.TenantId,
                        lastName,
                        firstName,
                        middleName,
                        displayName,
                        new CalendarId(calendarId ?? BookingFixtures.CalendarId.Value),
                        serviceIds ?? []),
                    CancellationToken.None);

        public Task<Result<ServiceId>> CreateServiceAsync(
            string name = "Haircut",
            int durationMinutes = 45,
            int? priceMinorUnits = null,
            bool priceIsFrom = false,
            string? description = null) =>
            new CreateServiceHandler(
                    new FakeTenantRepository(Tenant),
                    Services,
                    Permissions,
                    new SequentialIdGenerator(),
                    new FakeClock(BookingFixtures.Now))
                .HandleAsync(
                    new CreateService(
                        Actor, BookingFixtures.TenantId, name, durationMinutes,
                        priceMinorUnits, priceIsFrom, description),
                    CancellationToken.None);

        public Task<Result> UpdateWorkerAsync(
            WorkerId workerId,
            string lastName = "Doe",
            string firstName = "Alex",
            string? middleName = null,
            string? displayName = null,
            bool isActive = true,
            TenantId? tenantId = null,
            DateTimeOffset? now = null) =>
            new UpdateWorkerHandler(Workers, Permissions, new FakeClock(now ?? BookingFixtures.Now))
                .HandleAsync(
                    new UpdateWorker(
                        Actor, tenantId ?? BookingFixtures.TenantId, workerId,
                        lastName, firstName, middleName, displayName, isActive),
                    CancellationToken.None);

        public Task<Result> DeleteWorkerAsync(WorkerId workerId, TenantId? tenantId = null) =>
            new DeleteWorkerHandler(Workers, Permissions)
                .HandleAsync(
                    new DeleteWorker(Actor, tenantId ?? BookingFixtures.TenantId, workerId), CancellationToken.None);

        public Task<Result<IReadOnlyList<WorkerDetail>>> ListWorkersAsync() =>
            new ListWorkersForTenantHandler(Workers, Permissions)
                .HandleAsync(new ListWorkersForTenant(Actor, BookingFixtures.TenantId), CancellationToken.None);

        public Task<Result> SetOriginsAsync(IReadOnlyList<string> origins) =>
            new SetAllowedOriginsHandler(new FakeTenantRepository(Tenant), Permissions)
                .HandleAsync(new SetAllowedOrigins(Actor, BookingFixtures.TenantId, origins), CancellationToken.None);
    }
}

/// <summary>Records what was written, so "the refused call wrote nothing" is an assertion rather than
/// an inference.</summary>
internal sealed class RecordingCalendarRepository(BookingCalendar? existing) : IBookingCalendarRepository
{
    public List<BookingCalendar> Added { get; } = [];

    public List<BookingCalendar> Saved { get; } = [];

    public Task<BookingCalendar?> GetByIdAsync(CalendarId id, CancellationToken cancellationToken) =>
        Task.FromResult(existing is not null && existing.Id == id ? existing : null);

    public Task<IReadOnlyList<BookingCalendar>> ListPublishedAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookingCalendar>>(existing is { IsPublished: true } ? [existing] : []);

    public Task<IReadOnlyList<BookingCalendar>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookingCalendar>>(existing is not null ? [existing] : []);

    public Task AddAsync(BookingCalendar calendar, CancellationToken cancellationToken)
    {
        Added.Add(calendar);
        return Task.CompletedTask;
    }

    public Task SaveAsync(BookingCalendar calendar, CancellationToken cancellationToken)
    {
        Saved.Add(calendar);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingWorkerRepository : IWorkerRepository
{
    /// <summary>`22-07`: unlimited unless a test says otherwise - existing tests seed no grant at
    /// all, and the fake must not start refusing calls nobody asked it to gate.</summary>
    public int Quota { get; set; } = int.MaxValue;

    public List<Worker> Added { get; } = [];

    /// <summary>Workers this fake refuses to delete - the handler-level stand-in for "has booking
    /// history", which only <see cref="Ago.Calendar.Infrastructure.Postgres.WorkerRepository"/>'s own
    /// real SQL can prove for real (see the integration tests for that proof).</summary>
    public HashSet<WorkerId> Undeletable { get; } = [];

    public List<WorkerId> Deleted { get; } = [];

    public Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken cancellationToken) =>
        Task.FromResult<Worker?>(Added.Find(worker => worker.Id == id));

    public Task<IReadOnlyList<Worker>> ListActiveForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Worker>>([.. Added.Where(worker => worker.IsActive && worker.WorksIn(calendarId))]);

    public Task<IReadOnlyList<Worker>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Worker>>([.. Added.Where(worker => worker.TenantId == tenantId)]);

    public Task<bool> TryAddWithinQuotaAsync(Worker worker, CancellationToken cancellationToken)
    {
        var activeCount = Added.Count(w => w.TenantId == worker.TenantId && w.IsActive);
        if (activeCount >= Quota)
        {
            return Task.FromResult(false);
        }

        Added.Add(worker);
        return Task.FromResult(true);
    }

    /// <summary>`22-23`: the same <c>Quota</c> the create-side fake above already gates, applied to
    /// the reactivation door - excludes <paramref name="worker"/> from its own count by id, matching
    /// the real repository's reasoning: the answer must not depend on the caller having already
    /// flipped <see cref="Worker.IsActive"/> in memory before calling in, which it always has by the
    /// time this fake sees it (<see cref="Added"/> holds the same reference).</summary>
    public Task<bool> TryReactivateWithinQuotaAsync(Worker worker, CancellationToken cancellationToken)
    {
        var activeCount = Added.Count(w => w.TenantId == worker.TenantId && w.IsActive && w.Id != worker.Id);
        return Task.FromResult(activeCount < Quota);
    }

    public Task SaveAsync(Worker worker, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> DeleteIfNeverBookedAsync(WorkerId id, TenantId tenantId, CancellationToken cancellationToken)
    {
        var worker = Added.Find(w => w.Id == id && w.TenantId == tenantId);
        if (worker is null || Undeletable.Contains(id))
        {
            return Task.FromResult(false);
        }

        Added.Remove(worker);
        Deleted.Add(id);
        return Task.FromResult(true);
    }
}

/// <summary>`23-35`'s own version of <see cref="RecordingCalendarRepository"/> - records what
/// <see cref="CreateServiceHandler"/> actually wrote, so a refused call having written nothing is an
/// assertion rather than an inference.</summary>
internal sealed class RecordingServiceRepository : IServiceRepository
{
    public List<Service> Added { get; } = [];

    public Task<Service?> GetByIdAsync(ServiceId id, CancellationToken cancellationToken) =>
        Task.FromResult(Added.Find(service => service.Id == id));

    public Task<IReadOnlyList<Service>> ListForTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Service>>([.. Added.Where(service => service.TenantId == tenantId)]);

    public Task AddAsync(Service service, CancellationToken cancellationToken)
    {
        Added.Add(service);
        return Task.CompletedTask;
    }
}
