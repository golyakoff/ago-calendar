using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `25-12`: pins the precondition order the console renders verbatim
/// (`ago-console/src/calendar/BookingReadiness.tsx` does no sorting of its own). The assertion is on
/// the actual response the handler returns, not on the private <c>Order</c> array by reflection -
/// what a future reviewer can silently get wrong is the array itself, and a test that reads through
/// it via reflection would not notice its own mistargeting if the array were renamed or removed.
/// </summary>
public class GetBookingReadinessHandlerTests
{
    private static readonly OperatorId Actor = new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Fact]
    public async Task Preconditions_AreOrderedWithCalendarPublishedLast()
    {
        // `25-12`: CalendarPublished depends on nothing and nothing depends on it, so it is
        // structurally free to sit anywhere - but it is the "go live" toggle a tenant flips last,
        // once every other fact already holds, not the first thing to fix. The other five keep the
        // dependency-chained order they already had (worker, service, hours, schedule, slots).
        var world = new World();
        world.ReadStore.Rows.Add(FullyMetRow());

        var result = await world.GetReadinessAsync();

        Assert.True(result.IsSuccess);
        var order = result.Value!.Single().Preconditions.Select(state => state.Precondition).ToList();
        Assert.Equal(
            [
                BookingPrecondition.WorkerOnCalendar,
                BookingPrecondition.ServiceOffered,
                BookingPrecondition.WorkingHoursConfigured,
                BookingPrecondition.ScheduleSaved,
                BookingPrecondition.SlotsMaterialized,
                BookingPrecondition.CalendarPublished,
            ],
            order);
    }

    [Fact]
    public async Task NothingConfigured_UsesTheSameOrderAsAConfiguredCalendar()
    {
        // The synthetic all-unmet placeholder for a tenant with no calendar at all is built from the
        // same `Order` array - this test would fail if that placeholder and the real row-mapping path
        // ever drifted onto two different orders.
        var world = new World();

        var result = await world.GetReadinessAsync();

        Assert.True(result.IsSuccess);
        var order = result.Value!.Single().Preconditions.Select(state => state.Precondition).ToList();
        Assert.Equal(BookingPrecondition.CalendarPublished, order[^1]);
        Assert.Equal(BookingPrecondition.WorkerOnCalendar, order[0]);
    }

    private static CalendarReadinessRow FullyMetRow() => new(
        BookingFixtures.CalendarId,
        "Main",
        IsPublished: true,
        HasWorker: true,
        HasWorkerWithService: true,
        HasWorkingHours: true,
        HasSchedule: true,
        HasFutureSlots: true);

    private sealed class World
    {
        public FakePermissionChecker Permissions { get; } = new();

        public FakeBookingReadinessReadStore ReadStore { get; } = new();

        public Task<Result<IReadOnlyList<CalendarReadiness>>> GetReadinessAsync() =>
            new GetBookingReadinessHandler(ReadStore, Permissions, new FakeClock(BookingFixtures.Now))
                .HandleAsync(new GetBookingReadiness(Actor, BookingFixtures.TenantId), CancellationToken.None);
    }
}

internal sealed class FakeBookingReadinessReadStore : IBookingReadinessReadStore
{
    public List<CalendarReadinessRow> Rows { get; } = [];

    public Task<IReadOnlyList<CalendarReadinessRow>> GetForTenantAsync(
        TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CalendarReadinessRow>>([.. Rows]);
}
