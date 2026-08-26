using Ago.Calendar.Application.UseCases.DeleteDayOff;
using Ago.Calendar.Application.UseCases.EditDayBoundary;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
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

    public async Task<AvailabilityMaterialized> MaterializeAsync(CalendarId calendarId, int horizonDays)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new MaterializeAvailabilityHandler(
            new BookingCalendarRepository(db),
            new WorkerRepository(db),
            new WorkingHoursRuleRepository(db),
            new ServiceRepository(db),
            new EventRepository(db),
            Resolver,
            new UuidV7Generator(),
            Clock);

        return await handler.HandleAsync(new MaterializeAvailability(calendarId, horizonDays), CancellationToken.None);
    }

    public async Task<Result> DeleteDayOffAsync(CalendarId calendarId, WorkerId workerId, DateOnly localDate)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new DeleteDayOffHandler(new EventRepository(db), new UuidV7Generator(), Clock);
        return await handler.HandleAsync(new DeleteDayOff(calendarId, workerId, localDate), CancellationToken.None);
    }

    public async Task<Result> EditDayBoundaryAsync(
        CalendarId calendarId, WorkerId workerId, DateOnly localDate, TimeOnly opensAt, TimeOnly closesAt)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new EditDayBoundaryHandler(
            new BookingCalendarRepository(db),
            new WorkerRepository(db),
            new ServiceRepository(db),
            new EventRepository(db),
            Resolver,
            new UuidV7Generator(),
            Clock);

        return await handler.HandleAsync(
            new EditDayBoundary(calendarId, workerId, localDate, opensAt, closesAt), CancellationToken.None);
    }
}
