using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `25-145`: a fixed-offset stand-in for <see cref="IWallClockResolver"/>, for
/// <c>ChatModuleTaskHandlerTests</c> - the first <c>Ago.Calendar.Application.Tests</c> caller of this
/// port. Deliberately not the real <c>SystemWallClockResolver</c>: that type lives in
/// <c>Ago.Calendar.Infrastructure.Time</c>, a project this test assembly does not and should not
/// reference (testing.md's "application units with fakes" - a handler test proves the handler's own
/// decisions, not the tz database's), and every zone this suite's own <c>BookingFixtures.Calendar</c>
/// configures (<c>Europe/Moscow</c>) has kept one fixed offset year-round since Russia's 2014 return
/// to permanent standard time, so a single constant offset is a faithful stand-in rather than a
/// simplification that happens to pass. Real <c>TimeZoneInfo</c> resolution against the eleven
/// canonical Russian zones this item's own labels can render is proven separately, against the host's
/// real tz database, by <c>Ago.Calendar.Integration.Tests.WallClockResolverTests</c>.
/// </summary>
internal sealed class FakeWallClockResolver(TimeSpan offset) : IWallClockResolver
{
    public TimeSlot? ToInstantWindow(CalendarTimeZone zone, DateOnly localDate, TimeOnly opensAt, TimeOnly closesAt)
    {
        var startsAt = new DateTimeOffset(localDate, opensAt, offset);
        var endsAt = new DateTimeOffset(localDate, closesAt, offset);
        return new TimeSlot(startsAt, endsAt);
    }

    public DateOnly ToLocalDate(CalendarTimeZone zone, DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(offset).DateTime);

    public DateTimeOffset ToLocal(CalendarTimeZone zone, DateTimeOffset instant) => instant.ToOffset(offset);
}

/// <summary>Plain in-memory load/save, matching what the real adapter does - see
/// <see cref="IChatBookingTaskStore"/>'s own remarks for why there is no compare-and-set to fake
/// here, unlike <see cref="FakeBookingStore"/>.</summary>
internal sealed class FakeChatBookingTaskStore : IChatBookingTaskStore
{
    private readonly Dictionary<ChatBookingTaskId, ChatBookingTask> _tasks = [];

    public Task<ChatBookingTask?> GetByIdAsync(ChatBookingTaskId id, CancellationToken cancellationToken) =>
        Task.FromResult(_tasks.GetValueOrDefault(id));

    public Task AddAsync(ChatBookingTask task, CancellationToken cancellationToken)
    {
        _tasks[task.Id] = task;
        return Task.CompletedTask;
    }

    public Task SaveAsync(ChatBookingTask task, CancellationToken cancellationToken)
    {
        _tasks[task.Id] = task;
        return Task.CompletedTask;
    }
}
