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
    [InlineData("", "Europe/Moscow", 10)]
    [InlineData("Main", "+03:00", 10)]
    [InlineData("Main", "Europe/Moscow", -1)]
    [InlineData("Main", "Europe/Moscow", 100_000)]
    public async Task CreatingACalendar_TurnsADomainRefusalIntoAnOrdinaryRejection(
        string name, string zone, int buffer)
    {
        // A tenant typing "-5" into a buffer field is a caller mistake. Letting the aggregate's
        // ArgumentOutOfRangeException escape would make a 400 look like a 500 in every log.
        var world = new World();

        var result = await world.CreateCalendarAsync(name: name, timeZone: zone, bufferMinutes: buffer);

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
            string name = "Main", string timeZone = "Europe/Moscow", int bufferMinutes = 10, bool publish = true) =>
            new CreateCalendarHandler(
                    new FakeTenantRepository(Tenant),
                    Calendars,
                    Permissions,
                    new SequentialIdGenerator(),
                    new FakeClock(BookingFixtures.Now))
                .HandleAsync(
                    new CreateCalendar(Actor, BookingFixtures.TenantId, name, timeZone, bufferMinutes, publish),
                    CancellationToken.None);

        public Task<Result<WorkerId>> CreateWorkerAsync(
            Guid? calendarId = null, IReadOnlyList<Guid>? serviceIds = null) =>
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
                        "Alex",
                        new CalendarId(calendarId ?? BookingFixtures.CalendarId.Value),
                        serviceIds ?? []),
                    CancellationToken.None);

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
}
