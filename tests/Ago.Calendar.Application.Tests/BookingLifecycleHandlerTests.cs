using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// The three operator-facing transitions, with every port faked: which failure each reports, and -
/// the assertions that matter most - which writes each declines to attempt at all.
/// </summary>
public class BookingLifecycleHandlerTests
{
    private static readonly OperatorId Operator = new(new Guid("66666666-6666-6666-6666-666666666666"));

    [Fact]
    public async Task Reject_TransitionsAPendingBookingToCancelled()
    {
        var world = new World(BookingFixtures.PendingBooking());

        var result = await world.RejectAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(EventStatus.Cancelled, Assert.Single(world.Events.Saved).Status);
    }

    [Fact]
    public async Task Reject_WithoutThePermission_IsRefusedAndWritesNothing()
    {
        var world = new World(BookingFixtures.PendingBooking());
        world.Permissions.Deny(Permission.BookingReject);

        var result = await world.RejectAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("booking.forbidden", result.Error!.Value.Code);

        // Never loaded, never saved: the check is first, so a caller with no right does not even
        // learn whether the id exists.
        Assert.Empty(world.Events.Saved);
        Assert.Empty(world.Events.Loaded);
    }

    [Fact]
    public async Task Cancel_TransitionsAConfirmedBookingToCancelled()
    {
        var world = new World(BookingFixtures.ConfirmedBooking());

        var result = await world.CancelAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(EventStatus.Cancelled, Assert.Single(world.Events.Saved).Status);
    }

    [Fact]
    public async Task Cancel_WithoutThePermission_IsRefusedAndWritesNothing()
    {
        var world = new World(BookingFixtures.ConfirmedBooking());
        world.Permissions.Deny(Permission.BookingCancel);

        var result = await world.CancelAsync();

        Assert.Equal("booking.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Events.Saved);
    }

    [Fact]
    public async Task Cancel_RequiresItsOwnPermission_NotTheRejectOne()
    {
        // adr/0016's granularity argument, held as a test: rejecting inside the veto window and
        // cancelling a confirmed visit are different acts with different costs to the customer, and a
        // tenant may well grant one and not the other.
        var world = new World(BookingFixtures.ConfirmedBooking());
        world.Permissions.Deny(Permission.BookingCancel);
        world.Permissions.Allow(Permission.BookingReject);

        Assert.True((await world.CancelAsync()).IsFailure);
    }

    [Fact]
    public async Task MarkNoShow_FlagsAVisitThatHasEnded()
    {
        var world = new World(BookingFixtures.ConfirmedBooking(), at: BookingFixtures.Slot.EndsAt.AddMinutes(5));

        var result = await world.MarkNoShowAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(EventStatus.NoShow, Assert.Single(world.Events.Saved).Status);
    }

    [Fact]
    public async Task MarkNoShow_BeforeTheVisitHasEnded_IsRefused()
    {
        // Enforced by the aggregate, not by this handler: a no-show is a statement about something
        // that did not happen and cannot be made about a visit that has not had its chance. The
        // handler's job is to turn that into an ordinary failure instead of an exception.
        var world = new World(BookingFixtures.ConfirmedBooking(), at: BookingFixtures.Slot.StartsAt.AddMinutes(-1));

        var result = await world.MarkNoShowAsync();

        Assert.Equal("booking.invalid_state", result.Error!.Value.Code);
        Assert.Empty(world.Events.Saved);
    }

    [Fact]
    public async Task MarkNoShow_WithoutThePermission_IsRefusedAndWritesNothing()
    {
        var world = new World(BookingFixtures.ConfirmedBooking(), at: BookingFixtures.Slot.EndsAt.AddMinutes(5));
        world.Permissions.Deny(Permission.BookingMarkNoShow);

        Assert.Equal("booking.forbidden", (await world.MarkNoShowAsync()).Error!.Value.Code);
        Assert.Empty(world.Events.Saved);
    }

    [Fact]
    public async Task AnotherTenantsBooking_IsReportedAsAbsentRatherThanForbidden()
    {
        // The one place these errors stay vague. An operator of tenant A learning that an id exists
        // in tenant B is a cross-tenant leak however politely it is worded - so the answer is the
        // same one they would get for an id nobody owns.
        var world = new World(BookingFixtures.PendingBooking(tenantId: BookingFixtures.OtherTenantId));

        var result = await world.RejectAsync();

        Assert.Equal("booking.not_found", result.Error!.Value.Code);
        Assert.Empty(world.Events.Saved);
    }

    [Fact]
    public async Task AMissingBooking_IsReportedAsAbsent()
    {
        var world = new World(booking: null);

        Assert.Equal("booking.not_found", (await world.RejectAsync()).Error!.Value.Code);
    }

    [Fact]
    public async Task Reject_OnABookingTheSweepAlreadyConfirmed_IsAnOrdinaryInvalidState()
    {
        // The operator lost the race by a second. Not an error to log, not a 500 - a message they can
        // act on, and the state machine is what produced it.
        var world = new World(BookingFixtures.ConfirmedBooking());

        var result = await world.RejectAsync();

        Assert.Equal("booking.invalid_state", result.Error!.Value.Code);
        Assert.Empty(world.Events.Saved);
    }

    [Fact]
    public async Task AConcurrentWriterThatWinsFirst_SurfacesAsAConflictNotAnOrmException()
    {
        var world = new World(BookingFixtures.PendingBooking());
        world.Events.FailNextSaveWithConflict = true;

        var result = await world.RejectAsync();

        Assert.Equal("booking.concurrency_conflict", result.Error!.Value.Code);
    }

    /// <summary>`20-18`'s own Done-when, proven on a three-slot booking: cancel releases every slot of
    /// the run, not only the one the route named - and the route may name *any* member, not just the
    /// anchor.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cancel_OnAThreeSlotBooking_CancelsEveryRow(int routeSlotIndex)
    {
        var group = BookingFixtures.ConfirmedBookingGroup(3);
        var world = new World(group, routeEventId: group[routeSlotIndex].Id);

        var result = await world.CancelAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, world.Events.Saved.Count);
        Assert.All(world.Events.Saved, e => Assert.Equal(EventStatus.Cancelled, e.Status));
        Assert.Equal(group.Select(e => e.Id).ToHashSet(), world.Events.Saved.Select(e => e.Id).ToHashSet());
    }

    [Fact]
    public async Task Reject_OnAThreeSlotBooking_RejectsEveryRow()
    {
        var group = BookingFixtures.PendingBookingGroup(3);
        var world = new World(group, routeEventId: group[0].Id);

        var result = await world.RejectAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, world.Events.Saved.Count);
        Assert.All(world.Events.Saved, e => Assert.Equal(EventStatus.Cancelled, e.Status));
    }

    [Fact]
    public async Task MarkNoShow_OnAThreeSlotBooking_FlagsEveryRow_OnlyAfterTheWholeRunHasEnded()
    {
        var group = BookingFixtures.ConfirmedBookingGroup(3);
        var lastSlotEndsAt = group[^1].EndsAt;

        // Before the run's own last slot ends: refused, and nothing saved - even though the run's
        // first slot ended a while ago. MarkNoShow's per-row check on the last row is what makes this
        // correct without any extra group-level arithmetic - see MarkNoShowHandler's own remarks.
        var tooEarly = new World(BookingFixtures.ConfirmedBookingGroup(3), at: lastSlotEndsAt.AddMinutes(-1));
        Assert.Equal("booking.invalid_state", (await tooEarly.MarkNoShowAsync()).Error!.Value.Code);
        Assert.Empty(tooEarly.Events.Saved);

        var world = new World(group, at: lastSlotEndsAt, routeEventId: group[1].Id);
        var result = await world.MarkNoShowAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, world.Events.Saved.Count);
        Assert.All(world.Events.Saved, e => Assert.Equal(EventStatus.NoShow, e.Status));
    }

    /// <summary>The three handlers plus their fakes. One world, one booking, one thing different per
    /// test.</summary>
    private sealed class World
    {
        private readonly RejectBookingHandler _reject;
        private readonly CancelBookingHandler _cancel;
        private readonly MarkNoShowHandler _noShow;

        private readonly EventId _routeEventId;

        public World(Event? booking, DateTimeOffset? at = null)
            : this(booking is null ? [] : (IReadOnlyList<Event>)[booking], at, BookingFixtures.EventId)
        {
        }

        /// <summary>`20-18`: the group overload, for a test proving the three handlers act on a
        /// whole multi-slot run. <paramref name="routeEventId"/> is whichever member of the run the
        /// route named - any one of them, not necessarily the anchor.</summary>
        public World(IReadOnlyList<Event> group, DateTimeOffset? at = null, EventId? routeEventId = null)
        {
            Events = new FakeEventRepositoryWithSaves(group);
            _routeEventId = routeEventId ?? BookingFixtures.EventId;
            var clock = new FakeClock(at ?? BookingFixtures.Now);

            _reject = new RejectBookingHandler(Events, Permissions, clock);
            _cancel = new CancelBookingHandler(Events, Permissions, clock);
            _noShow = new MarkNoShowHandler(Events, Permissions, clock);
        }

        public FakeEventRepositoryWithSaves Events { get; }

        public FakePermissionChecker Permissions { get; } = new();

        public Task<Ago.Platform.Kernel.Result> RejectAsync() =>
            _reject.HandleAsync(
                new RejectBooking(Operator, BookingFixtures.TenantId, _routeEventId), CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result> CancelAsync() =>
            _cancel.HandleAsync(
                new CancelBooking(Operator, BookingFixtures.TenantId, _routeEventId), CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result> MarkNoShowAsync() =>
            _noShow.HandleAsync(
                new MarkNoShow(Operator, BookingFixtures.TenantId, _routeEventId), CancellationToken.None);
    }
}
