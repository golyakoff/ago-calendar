using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `26-208`/`adr/0187`: the operator reschedule, with every port faked. Which failure each refusal
/// reports, which refusals never reach the store at all, and - the assertions that matter most - that
/// a successful move stages exactly one <c>BookingRescheduled</c> (carrying the old booking's person,
/// service and origin) and nothing else, while a lost claim-race leaves the old booking
/// <see cref="EventStatus.Booked"/>.
/// </summary>
public class RescheduleBookingHandlerTests
{
    private static readonly OperatorId Operator = new(new Guid("66666666-6666-6666-6666-666666666666"));

    private static readonly EventId TargetSlotId = new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    // Two hours after the old booking's own slot, on the same business-local day - a genuine move.
    private static readonly TimeSlot TargetSlot =
        new(BookingFixtures.Now.AddHours(4), BookingFixtures.Now.AddHours(4).AddMinutes(45));

    [Fact]
    public async Task Reschedule_MovesTheBooking_StagingExactlyOneBookingRescheduled()
    {
        var origin = Guid.NewGuid();
        var world = new World(BookedBookingWithOrigin(origin), AvailableTarget());

        var result = await world.RescheduleAsync();

        Assert.True(result.IsSuccess);

        // The store was asked to claim exactly the target run, carrying the old booking's own person,
        // service and origin onto the new run (`adr/0187`: same worker, same service, time-only).
        var request = Assert.Single(world.Store.Requests);
        Assert.Equal([TargetSlotId], request.NewEventIds);
        Assert.Equal(BookingFixtures.PersonId, request.PersonId);
        Assert.Equal(BookingFixtures.ServiceId, request.ServiceId);
        Assert.Equal(origin, request.OriginConversationId);
        Assert.Equal(BookingFixtures.EventId, request.PreviousBookingId);

        // Exactly one event staged, and it is BookingRescheduled - never the BookingConfirmed /
        // BookingPendingStateChanged pair the ordinary confirm path stages, and never a Cancelled
        // signal for the old run (`adr/0187`).
        var staged = Assert.Single(world.Outbox.Enqueued);
        Assert.Equal(nameof(Ago.Calendar.Contracts.BookingRescheduled), staged.Type);
    }

    [Fact]
    public async Task Reschedule_WithoutThePermission_IsRefusedAndTouchesNothing()
    {
        var world = new World(BookingFixtures.ConfirmedBooking(), AvailableTarget());
        world.Permissions.Deny(Permission.BookingReschedule);

        var result = await world.RescheduleAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("booking.forbidden", result.Error!.Value.Code);

        // The check is first: a caller with no right never learns whether the booking exists, and the
        // store is never called.
        Assert.Empty(world.Events.Loaded);
        Assert.False(world.Store.Called);
    }

    [Fact]
    public async Task Reschedule_RequiresItsOwnPermission_NotTheCancelOne()
    {
        // adr/0016's granularity argument, held as a test (`adr/0187` §Permission): a tenant may grant
        // cancel without reschedule, so holding cancel is not enough.
        var world = new World(BookingFixtures.ConfirmedBooking(), AvailableTarget());
        world.Permissions.Deny(Permission.BookingReschedule);
        world.Permissions.Allow(Permission.BookingCancel);

        Assert.True((await world.RescheduleAsync()).IsFailure);
        Assert.False(world.Store.Called);
    }

    [Fact]
    public async Task Reschedule_OnAnotherTenantsBooking_IsReportedAsAbsentRatherThanForbidden()
    {
        var world = new World(
            BookingFixtures.ConfirmedBooking(tenantId: BookingFixtures.OtherTenantId), AvailableTarget());

        var result = await world.RescheduleAsync();

        Assert.Equal("booking.not_found", result.Error!.Value.Code);
        Assert.False(world.Store.Called);
    }

    [Fact]
    public async Task Reschedule_OnAMissingBooking_IsReportedAsAbsent()
    {
        var world = new World(oldBooking: null, AvailableTarget());

        Assert.Equal("booking.not_found", (await world.RescheduleAsync()).Error!.Value.Code);
        Assert.False(world.Store.Called);
    }

    [Fact]
    public async Task Reschedule_OfAStillPendingBooking_IsRefused()
    {
        // `adr/0187`: v1 is Booked-only. A still-pending booking is more naturally rejected + rebooked.
        var world = new World(BookingFixtures.PendingBooking(), AvailableTarget());

        var result = await world.RescheduleAsync();

        Assert.Equal("booking.invalid_state", result.Error!.Value.Code);
        Assert.False(world.Store.Called);
    }

    [Fact]
    public async Task Reschedule_ToADifferentWorker_IsRefused()
    {
        // `adr/0187`: same worker only for v1. The target slot belongs to another worker.
        var otherWorkerTarget = Event.Materialize(
            TargetSlotId, BookingFixtures.TenantId, BookingFixtures.CalendarId,
            new WorkerId(Guid.NewGuid()), TargetSlot, BookingFixtures.LocalDate, BookingFixtures.Now);
        var world = new World(BookingFixtures.ConfirmedBooking(), otherWorkerTarget);

        var result = await world.RescheduleAsync();

        Assert.Equal("booking.invalid_state", result.Error!.Value.Code);
        Assert.False(world.Store.Called);
    }

    [Fact]
    public async Task Reschedule_ToATargetThatIsNotAvailable_SaysSlotUnavailable()
    {
        // The picked slot is blocked, so ConsecutiveRunFinder finds no legal run starting there - the
        // ordinary "that time will not work" outcome, decided by the courtesy read before the store is
        // ever asked (rule 8: a stale read costs only this rejection).
        var blockedTarget = Event.BlockOut(
            TargetSlotId, BookingFixtures.TenantId, BookingFixtures.CalendarId,
            BookingFixtures.WorkerId, TargetSlot, BookingFixtures.LocalDate, BookingFixtures.Now);
        var world = new World(BookingFixtures.ConfirmedBooking(), blockedTarget);

        var result = await world.RescheduleAsync();

        Assert.Equal("booking.slot_unavailable", result.Error!.Value.Code);
        Assert.False(world.Store.Called);
    }

    [Fact]
    public async Task Reschedule_WhenTheClaimLosesTheRace_LeavesTheOldBookingBooked_AndSaysSlotUnavailable()
    {
        var oldBooking = BookingFixtures.ConfirmedBooking();
        var world = new World(oldBooking, AvailableTarget());
        world.Store.SlotIsClaimable = false;

        var result = await world.RescheduleAsync();

        Assert.Equal("booking.slot_unavailable", result.Error!.Value.Code);

        // The store was asked (the race is real, decided inside its transaction) but the whole thing
        // rolled back - and the handler never touched the old booking, so it stays Booked. The fake
        // outbox is empty: a lost race stages nothing.
        Assert.True(world.Store.Called);
        Assert.Equal(EventStatus.Booked, oldBooking.Status);
        Assert.Empty(world.Outbox.Enqueued);
    }

    [Fact]
    public async Task Reschedule_WhenTheOldRunRacedAnotherWriter_SurfacesAsAConflict()
    {
        // The cancel-half's own race: another writer changed the old run between this handler's read
        // and the store's transaction. Surfaced as a conflict, not an ORM exception.
        var world = new World(BookingFixtures.ConfirmedBooking(), AvailableTarget());
        world.Store.ThrowOnReschedule = new EventConcurrencyConflictException(BookingFixtures.EventId);

        var result = await world.RescheduleAsync();

        Assert.Equal("booking.concurrency_conflict", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Reschedule_WhenTheOldRunStoppedBeingBooked_IsAnOrdinaryInvalidState()
    {
        var world = new World(BookingFixtures.ConfirmedBooking(), AvailableTarget());
        world.Store.ThrowOnReschedule = new InvalidEventStateException("already cancelled");

        var result = await world.RescheduleAsync();

        Assert.Equal("booking.invalid_state", result.Error!.Value.Code);
    }

    private static Event AvailableTarget() =>
        Event.Materialize(
            TargetSlotId, BookingFixtures.TenantId, BookingFixtures.CalendarId,
            BookingFixtures.WorkerId, TargetSlot, BookingFixtures.LocalDate, BookingFixtures.Now);

    private static Event BookedBookingWithOrigin(Guid originConversationId)
    {
        var booking = Event.Materialize(
            BookingFixtures.EventId, BookingFixtures.TenantId, BookingFixtures.CalendarId,
            BookingFixtures.WorkerId, BookingFixtures.Slot, BookingFixtures.LocalDate, BookingFixtures.Now);
        booking.Claim(
            BookingFixtures.PersonId, BookingFixtures.ServiceId, BookingFixtures.Now,
            BookingFixtures.Now.AddMinutes(15), bookingId: null, originConversationId: originConversationId);
        booking.Confirm(BookingFixtures.Now.AddMinutes(15));
        booking.ClearDomainEvents();
        return booking;
    }

    /// <summary>One world: the handler under test with every port faked, an old booking and a target
    /// slot, one thing different per test.</summary>
    private sealed class World
    {
        private readonly RescheduleBookingHandler _handler;

        public World(Event? oldBooking, Event target)
        {
            var rows = oldBooking is null ? new List<Event> { target } : [oldBooking, target];
            Events = new FakeRescheduleEventRepository(rows);
            Store = new FakeBookingRescheduleStore(Outbox);

            _handler = new RescheduleBookingHandler(
                Events,
                new FakeServiceRepository(BookingFixtures.HaircutService()),
                new FakeWorkerScheduleRepository(BookingFixtures.Schedule(slotMinutes: 45, bufferMinutes: 0)),
                Store,
                Permissions,
                new FakeIdGenerator(),
                new FakeClock(BookingFixtures.Now));
        }

        public FakeRescheduleEventRepository Events { get; }

        public FakeBookingRescheduleStore Store { get; }

        public FakePermissionChecker Permissions { get; } = new();

        public FakeOutboxWriter Outbox { get; } = new();

        public Task<Ago.Platform.Kernel.Result> RescheduleAsync() =>
            _handler.HandleAsync(
                new RescheduleBooking(Operator, BookingFixtures.TenantId, BookingFixtures.EventId, TargetSlotId),
                CancellationToken.None);
    }
}
