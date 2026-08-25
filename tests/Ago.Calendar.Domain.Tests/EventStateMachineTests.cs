namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// The <see cref="Event"/> state machine. Every one of these runs in microseconds with no database,
/// which is the point: these are rules about one row, and they are the half of "no double booking"
/// that an aggregate genuinely owns. The other half - two rows overlapping - is proven against a
/// real Postgres in <c>Ago.Calendar.Integration.Tests</c>, because it cannot be proven here.
/// </summary>
public class EventStateMachineTests
{
    private static readonly DateTimeOffset Now = CalendarFixtures.Now;

    [Fact]
    public void Claim_FromAvailable_MovesToPendingConfirmation_AndRaisesEventClaimed()
    {
        var world = new World();

        world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(15));

        Assert.Equal(EventStatus.PendingConfirmation, world.Slot.Status);
        Assert.Equal(world.Customer.Id, world.Slot.CustomerId);
        Assert.Equal(world.Service.Id, world.Slot.ServiceId);
        Assert.Equal(Now.AddMinutes(15), world.Slot.ConfirmationDeadline);
        var claimed = Assert.IsType<EventClaimed>(Assert.Single(world.Slot.DomainEvents));
        Assert.Equal(world.Slot.Id, claimed.EventId);
    }

    [Fact]
    public void Claim_WhenAlreadyPendingConfirmation_Throws()
    {
        var world = new World();
        world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(15));

        var second = CalendarFixtures.Customer(world.Tenant, "+79995550000");

        Assert.Throws<InvalidEventStateException>(() =>
            world.Slot.Claim(second.Id, world.Service.Id, Now, Now.AddMinutes(15)));
    }

    [Fact]
    public void Claim_WhenAlreadyBooked_Throws_AndThereIsNoRouteBackToAvailable()
    {
        var world = new World();
        world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(15));
        world.Slot.Confirm(Now.AddMinutes(15));

        Assert.Throws<InvalidEventStateException>(() =>
            world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(30)));
        Assert.Equal(EventStatus.Booked, world.Slot.Status);
    }

    [Fact]
    public void Claim_WhenTheSlotHasAlreadyStarted_Throws()
    {
        var world = new World();

        // Time is a parameter, so "the slot is in the past" is testable at any wall-clock instant on
        // any machine - the whole reason the aggregate never reads a clock.
        var afterTheSlotStarted = world.Slot.StartsAt.AddMinutes(1);

        Assert.Throws<InvalidEventStateException>(() => world.Slot.Claim(
            world.Customer.Id, world.Service.Id, afterTheSlotStarted, afterTheSlotStarted.AddMinutes(15)));
    }

    [Fact]
    public void Claim_WithAConfirmationWindowThatHasAlreadyClosed_Throws()
    {
        var world = new World();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(-1)));
    }

    [Fact]
    public void Confirm_FromPendingConfirmation_ClearsTheDeadline_AndRaisesEventConfirmed()
    {
        var world = new World();
        world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(15));
        world.Slot.ClearDomainEvents();

        world.Slot.Confirm(Now.AddMinutes(15));

        Assert.Equal(EventStatus.Booked, world.Slot.Status);
        Assert.Null(world.Slot.ConfirmationDeadline);
        Assert.IsType<EventConfirmed>(Assert.Single(world.Slot.DomainEvents));
    }

    [Fact]
    public void Confirm_FromAvailable_Throws()
    {
        var world = new World();

        Assert.Throws<InvalidEventStateException>(() => world.Slot.Confirm(Now));
    }

    [Fact]
    public void Reject_FromPendingConfirmation_CancelsWithTheRejectionReason()
    {
        var world = new World();
        world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(15));
        world.Slot.ClearDomainEvents();

        world.Slot.Reject(Now.AddMinutes(3));

        Assert.Equal(EventStatus.Cancelled, world.Slot.Status);
        var cancelled = Assert.IsType<EventCancelled>(Assert.Single(world.Slot.DomainEvents));
        Assert.Equal(CancellationReason.RejectedByOperator, cancelled.Reason);

        // The customer is deliberately kept: who cancelled on whom is the history the lead card is
        // for.
        Assert.Equal(world.Customer.Id, world.Slot.CustomerId);
    }

    [Fact]
    public void Reject_AfterConfirmation_Throws_BecauseTheWindowIsOver()
    {
        var world = new World();
        world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(15));
        world.Slot.Confirm(Now.AddMinutes(15));

        Assert.Throws<InvalidEventStateException>(() => world.Slot.Reject(Now.AddMinutes(16)));
    }

    [Fact]
    public void Cancel_FromBooked_CancelsWithTheCancellationReason()
    {
        var world = new World();
        var booked = CalendarFixtures.BookedSlot(world.Tenant, world.Calendar, world.Worker, world.Customer, world.Service);
        booked.ClearDomainEvents();

        booked.Cancel(Now.AddHours(1));

        Assert.Equal(EventStatus.Cancelled, booked.Status);
        var cancelled = Assert.IsType<EventCancelled>(Assert.Single(booked.DomainEvents));
        Assert.Equal(CancellationReason.CancelledByOperator, cancelled.Reason);
    }

    [Fact]
    public void Cancel_Twice_Throws()
    {
        var world = new World();
        var booked = CalendarFixtures.BookedSlot(world.Tenant, world.Calendar, world.Worker, world.Customer, world.Service);
        booked.Cancel(Now.AddHours(1));

        Assert.Throws<InvalidEventStateException>(() => booked.Cancel(Now.AddHours(2)));
    }

    [Fact]
    public void MarkNoShow_BeforeTheSlotEnds_Throws()
    {
        var world = new World();
        var booked = CalendarFixtures.BookedSlot(world.Tenant, world.Calendar, world.Worker, world.Customer, world.Service);

        // One second before the visit is over is still not a no-show.
        Assert.Throws<InvalidEventStateException>(() => booked.MarkNoShow(booked.EndsAt.AddSeconds(-1)));
    }

    [Fact]
    public void MarkNoShow_OnceTheSlotHasEnded_Records()
    {
        var world = new World();
        var booked = CalendarFixtures.BookedSlot(world.Tenant, world.Calendar, world.Worker, world.Customer, world.Service);
        booked.ClearDomainEvents();

        booked.MarkNoShow(booked.EndsAt);

        Assert.Equal(EventStatus.NoShow, booked.Status);
        Assert.IsType<EventNoShowRecorded>(Assert.Single(booked.DomainEvents));
    }

    [Fact]
    public void MarkNoShow_OnAnAvailableSlot_Throws()
    {
        var world = new World();

        Assert.Throws<InvalidEventStateException>(() => world.Slot.MarkNoShow(world.Slot.EndsAt));
    }

    [Fact]
    public void Block_FromAvailable_Succeeds_AndRaisesNothing()
    {
        var world = new World();

        world.Slot.Block();

        Assert.Equal(EventStatus.Blocked, world.Slot.Status);
        Assert.Empty(world.Slot.DomainEvents);
    }

    [Fact]
    public void Block_OnAClaimedSlot_Throws_BecauseACustomerIsWaitingOnIt()
    {
        var world = new World();
        world.Slot.Claim(world.Customer.Id, world.Service.Id, Now, Now.AddMinutes(15));

        Assert.Throws<InvalidEventStateException>(world.Slot.Block);
    }

    [Fact]
    public void Materialize_RaisesNoDomainEvent()
    {
        var world = new World();

        // A horizon's worth of slots would otherwise be a horizon's worth of outbox rows with no
        // consumer. See Event.Materialize's own remarks.
        Assert.Empty(world.Slot.DomainEvents);
        Assert.Equal(EventStatus.Available, world.Slot.Status);
    }

    private sealed class World
    {
        public World()
        {
            Tenant = CalendarFixtures.Tenant();
            Calendar = CalendarFixtures.Calendar(Tenant);
            Worker = CalendarFixtures.Worker(Tenant);
            Service = CalendarFixtures.Service(Tenant);
            Customer = CalendarFixtures.Customer(Tenant);
            Worker.JoinCalendar(Calendar);
            Worker.Offer(Service);
            Slot = CalendarFixtures.AvailableSlot(Tenant, Calendar, Worker);
        }

        public Tenant Tenant { get; }

        public BookingCalendar Calendar { get; }

        public Worker Worker { get; }

        public Service Service { get; }

        public Customer Customer { get; }

        public Event Slot { get; }
    }
}
