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

    public Task AddAsync(Worker worker, CancellationToken cancellationToken)
    {
        Added.Add(worker);
        return Task.CompletedTask;
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
