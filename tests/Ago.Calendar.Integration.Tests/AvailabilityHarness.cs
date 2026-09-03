using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Application.UseCases.DeleteDayOff;
using Ago.Calendar.Application.UseCases.EditDayBoundary;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Application.UseCases.RecutSchedule;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Calendar.Infrastructure.Time;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Integration.Tests;

/// <summary>A clock the test owns. adr/0011's whole point: a rule about time that cannot be
/// controlled in a test is a rule that is never actually tested.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>
/// Builds the three `20-02` handlers over real repositories on a real Postgres, with a controllable
/// clock and the real tz-database resolver.
///
/// <para>No fakes for the repositories, deliberately. Every claim this item makes - re-running
/// inserts nothing, a booking survives, an edited day is not resurrected - is a claim about what the
/// database does with a statement, and a fake repository would prove only that the fake agrees with
/// the fake's author. testing.md: a mocked database proves the test compiles.</para>
///
/// <para>A fresh <see cref="AgoCalendarDbContext"/> per handler, matching the scoped registration in
/// <c>CalendarModule</c>: a run that reused one context would answer its own reads from the identity
/// map and hide exactly the staleness these tests exist to provoke.</para>
/// </summary>
internal sealed class AvailabilityHarness(PostgresFixture fixture, FixedClock clock)
{
    /// <summary>One resolver for the whole suite, matching its singleton registration - the zone
    /// cache is the reason it is a singleton in the first place, and a per-call instance would
    /// re-read the host's tz database for every day of every horizon.</summary>
    private static readonly SystemWallClockResolver Resolver = new();

    public FixedClock Clock { get; } = clock;

    public async Task<AvailabilityMaterialized> MaterializeAsync(CalendarId calendarId)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new MaterializeAvailabilityHandler(
            new BookingCalendarRepository(db),
            new WorkerRepository(db),
            new WorkingHoursRuleRepository(db),
            new WorkerScheduleRepository(db),
            new EventRepository(db),
            Resolver,
            new UuidV7Generator(),
            Clock);

        return await handler.HandleAsync(new MaterializeAvailability(calendarId), CancellationToken.None);
    }

    /// <summary>
    /// Takes the whole seed rather than two ids, because `20-06` gave this use case an actor: the
    /// handler now checks a real permission against the seed's own operator and role rows through
    /// the real <c>PermissionChecker</c>. Passing ids would have meant inventing an operator per call
    /// site.
    /// </summary>
    public async Task<Result> DeleteDayOffAsync(SeededTenant seed, DateOnly localDate)
    {
        ArgumentNullException.ThrowIfNull(seed);

        await using var db = fixture.CreateDbContext();
        var handler = new DeleteDayOffHandler(
            new BookingCalendarRepository(db),
            new PermissionChecker(new RoleAssignmentProjectionStore(db)),
            new EventRepository(db),
            new UuidV7Generator(),
            Clock);

        return await handler.HandleAsync(
            new DeleteDayOff(seed.OperatorId, seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id, localDate),
            CancellationToken.None);
    }

    public async Task<Result> EditDayBoundaryAsync(
        SeededTenant seed, DateOnly localDate, TimeOnly opensAt, TimeOnly closesAt)
    {
        ArgumentNullException.ThrowIfNull(seed);

        await using var db = fixture.CreateDbContext();
        var handler = new EditDayBoundaryHandler(
            new BookingCalendarRepository(db),
            new PermissionChecker(new RoleAssignmentProjectionStore(db)),
            new WorkerRepository(db),
            new WorkerScheduleRepository(db),
            new EventRepository(db),
            Resolver,
            new UuidV7Generator(),
            Clock);

        return await handler.HandleAsync(
            new EditDayBoundary(
                seed.OperatorId, seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id, localDate, opensAt, closesAt),
            CancellationToken.None);
    }

    /// <summary>`20-16`'s own preview - read-only, so a fresh short-lived context and the shared
    /// <see cref="PostgresFixture.DataSource"/> for the read store, the identical shape
    /// `WorkerSlotsTests` already uses for `GetWorkerSlotsHandler`.</summary>
    public async Task<Result<RecutPreviewResult>> RecutPreviewAsync(SeededTenant seed, DateOnly from)
    {
        ArgumentNullException.ThrowIfNull(seed);

        await using var db = fixture.CreateDbContext();
        var handler = new RecutPreviewHandler(
            new BookingCalendarRepository(db),
            new WorkerRepository(db),
            new WorkerScheduleRepository(db),
            new WorkerSlotReadStore(fixture.DataSource),
            Resolver,
            new PermissionChecker(new RoleAssignmentProjectionStore(db)),
            Clock);

        return await handler.HandleAsync(
            new RecutPreview(seed.OperatorId, seed.Tenant.Id, seed.Worker.Id, from), CancellationToken.None);
    }

    /// <summary>
    /// `20-16`'s own confirm. One <see cref="AgoCalendarDbContext"/> for every dependency, including
    /// the <see cref="CancelBookingHandler"/> passed to <see cref="RecutConfirmHandler"/>'s own
    /// constructor - matching the production shape exactly: both are scoped in <c>CalendarModule</c>,
    /// so both resolve against the same context within one request, and a test that gave them two
    /// contexts would not be testing what production actually does.
    /// </summary>
    public async Task<Result<RecutConfirmResult>> RecutConfirmAsync(
        SeededTenant seed, DateOnly from, string fingerprint, params RecutBookingDecision[] decisions)
    {
        ArgumentNullException.ThrowIfNull(seed);

        await using var db = fixture.CreateDbContext();
        var cancelHandler = new CancelBookingHandler(new EventRepository(db), new PermissionChecker(new RoleAssignmentProjectionStore(db)), Clock);
        var handler = new RecutConfirmHandler(
            new BookingCalendarRepository(db),
            new WorkerRepository(db),
            new WorkerScheduleRepository(db),
            new WorkingHoursRuleRepository(db),
            new EventRepository(db),
            Resolver,
            new UuidV7Generator(),
            new PermissionChecker(new RoleAssignmentProjectionStore(db)),
            Clock,
            cancelHandler);

        return await handler.HandleAsync(
            new RecutConfirm(seed.OperatorId, seed.Tenant.Id, seed.Worker.Id, from, fingerprint, decisions),
            CancellationToken.None);
    }
}
