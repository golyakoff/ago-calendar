using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-04`'s shared queue, against a real Postgres: <b>one queue per tenant, spanning every calendar,
/// visible and actionable by every operator who holds the permission.</b>
///
/// <para>The Done-when asks for two calendars and two operators specifically, and the reason is that
/// the failure this guards against is silent: a queue accidentally scoped to "the operator's own
/// calendar" would look completely correct in a single-calendar fixture. So the test seeds two of
/// each and asserts that neither operator's view is narrower than the other's.</para>
///
/// <para>Also the first exercise of this product's <c>IPermissionChecker</c> against real
/// <c>roles</c>/<c>operator_roles</c> rows - which is worth doing here rather than only against a
/// fake, because a permission model that resolves nothing would make every one of these reads return
/// "forbidden" and every fake-backed test still pass.</para>
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class SharedPendingQueueTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EitherOperatorSeesBothCalendarsPendingBookings()
    {
        var world = await ATenantWithTwoCalendarsAsync();

        var first = await QueueAsync(world.FirstOperator, world.TenantId);
        var second = await QueueAsync(world.SecondOperator, world.TenantId);

        // Both calendars, for both operators, and the same set - there is no "mine".
        Assert.Equal(2, first.Count);
        Assert.Equal(
            new[] { world.FirstCalendar, world.SecondCalendar }.OrderBy(c => c.Value),
            first.Select(row => row.CalendarId).OrderBy(c => c.Value));

        Assert.Equal(
            first.Select(row => row.EventId).OrderBy(e => e.Value),
            second.Select(row => row.EventId).OrderBy(e => e.Value));
    }

    [Fact]
    public async Task EitherOperatorCanActOnEitherCalendarsBooking()
    {
        // Seeing is not acting, and a queue that showed everything while only letting an operator
        // reject "their own" would be the same bug one layer down.
        var world = await ATenantWithTwoCalendarsAsync();
        var rows = await QueueAsync(world.FirstOperator, world.TenantId);

        var onTheOtherCalendar = rows.Single(row => row.CalendarId == world.SecondCalendar);

        await using var db = fixture.CreateDbContext();
        var result = await new RejectBookingHandler(
                new EventRepository(db), new PermissionChecker(db), new FixedClock(Now))
            .HandleAsync(
                new RejectBooking(world.FirstOperator, world.TenantId, onTheOtherCalendar.EventId),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task AnotherTenantsPendingBookingsAreNotInTheQueue()
    {
        var mine = await ATenantWithTwoCalendarsAsync();
        var theirs = await ATenantWithTwoCalendarsAsync();

        var rows = await QueueAsync(mine.FirstOperator, mine.TenantId);

        Assert.DoesNotContain(rows, row => row.CalendarId == theirs.FirstCalendar);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task AnOperatorWithoutThePermission_GetsNoQueueAtAll()
    {
        var world = await ATenantWithTwoCalendarsAsync();

        // A real operator of this tenant, with a role that grants everything except the one this read
        // is gated on - so the refusal comes from the permission model resolving real rows, not from
        // the operator being unknown.
        var stranger = await AnOperatorWithoutRejectAsync(world.TenantId);

        await using var db = fixture.CreateDbContext();
        var result = await new GetPendingBookingsForTenantHandler(
                new PendingBookingReadStore(fixture.DataSource), new PermissionChecker(db), new FixedClock(Now))
            .HandleAsync(new GetPendingBookingsForTenant(stranger, world.TenantId, 100), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("booking.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task TheQueueIsOrderedByDeadline_AndFlagsWhatIsAlreadyOverdue()
    {
        var world = await ATenantWithTwoCalendarsAsync();

        // Read at an instant between the two deadlines: the earlier one is overdue, the later is not.
        var rows = await QueueAsync(world.FirstOperator, world.TenantId, at: Now.AddMinutes(20));

        Assert.Equal(rows.OrderBy(row => row.ConfirmationDeadline).Select(r => r.EventId), rows.Select(r => r.EventId));

        // The overdue flag is the sweep's health made visible on the one screen a human already looks
        // at - a row that shows it means the sweep has not run, while the customer has already been
        // told they are booked.
        Assert.True(rows[0].IsOverdue);
        Assert.False(rows[1].IsOverdue);
    }

    [Fact]
    public async Task AConfirmedBookingLeavesTheQueue()
    {
        var world = await ATenantWithTwoCalendarsAsync();
        var rows = await QueueAsync(world.FirstOperator, world.TenantId);
        var target = rows[0];

        await using (var db = fixture.CreateDbContext())
        {
            var booking = await db.Events.SingleAsync(e => e.Id == target.EventId);
            booking.Confirm(Now.AddMinutes(30));
            booking.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        var after = await QueueAsync(world.FirstOperator, world.TenantId);

        Assert.DoesNotContain(after, row => row.EventId == target.EventId);
        Assert.Single(after);
    }

    private async Task<IReadOnlyList<PendingBookingRow>> QueueAsync(
        OperatorId operatorId, TenantId tenantId, DateTimeOffset? at = null)
    {
        await using var db = fixture.CreateDbContext();
        var result = await new GetPendingBookingsForTenantHandler(
                new PendingBookingReadStore(fixture.DataSource),
                new PermissionChecker(db),
                new FixedClock(at ?? Now))
            .HandleAsync(new GetPendingBookingsForTenant(operatorId, tenantId, 100), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private async Task<OperatorId> AnOperatorWithoutRejectAsync(TenantId tenantId)
    {
        var role = Role.Create(
            new RoleId(CalendarSeed.NewId()), tenantId, "Receptionist",
            [Permission.CustomerRead, Permission.CustomerEdit]);
        var @operator = Operator.Create(new OperatorId(CalendarSeed.NewId()), tenantId, "Sam");
        @operator.Grant(role);

        await using var db = fixture.CreateDbContext();
        db.Roles.Add(role);
        db.Operators.Add(@operator);
        await db.SaveChangesAsync();

        return @operator.Id;
    }

    /// <summary>One tenant, two calendars each with its own worker, two operators both holding the
    /// seeded role, and one pending booking per calendar with different deadlines.</summary>
    private async Task<SeededQueue> ATenantWithTwoCalendarsAsync()
    {
        var first = await CalendarSeed.WriteAsync(fixture);

        // A second calendar under the *same* tenant, with its own worker - the exclusion constraint
        // is per worker, so two calendars sharing one would constrain the fixture for no reason.
        var secondCalendar = BookingCalendar.Create(
            new CalendarId(CalendarSeed.NewId()), first.Tenant.Id, "Second",
            new CalendarTimeZone("Europe/Moscow"), 10, Now);
        secondCalendar.Publish();
        var secondWorker = Worker.Create(new WorkerId(CalendarSeed.NewId()), first.Tenant.Id, "Bo");
        secondWorker.JoinCalendar(secondCalendar);
        secondWorker.Offer(first.Service);

        var role = Role.SeedOperatorRole(new RoleId(CalendarSeed.NewId()), first.Tenant.Id);
        var operatorOne = Operator.Create(new OperatorId(CalendarSeed.NewId()), first.Tenant.Id, "Ann");
        var operatorTwo = Operator.Create(new OperatorId(CalendarSeed.NewId()), first.Tenant.Id, "Ben");
        operatorOne.Grant(role);
        operatorTwo.Grant(role);

        await using (var db = fixture.CreateDbContext())
        {
            db.Calendars.Add(secondCalendar);
            db.Workers.Add(secondWorker);
            db.Roles.Add(role);
            db.Operators.Add(operatorOne);
            db.Operators.Add(operatorTwo);
            await db.SaveChangesAsync();
        }

        await APendingBookingAsync(first.Tenant.Id, first.Calendar.Id, first.Worker.Id, first.Service.Id,
            "+79998000001", Now.AddMinutes(15), Now.AddDays(3));
        await APendingBookingAsync(first.Tenant.Id, secondCalendar.Id, secondWorker.Id, first.Service.Id,
            "+79998000002", Now.AddMinutes(25), Now.AddDays(3).AddHours(2));

        return new SeededQueue(
            first.Tenant.Id, first.Calendar.Id, secondCalendar.Id, operatorOne.Id, operatorTwo.Id);
    }

    private async Task APendingBookingAsync(
        TenantId tenantId, CalendarId calendarId, WorkerId workerId, ServiceId serviceId,
        string phone, DateTimeOffset deadline, DateTimeOffset startsAt)
    {
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()), tenantId, calendarId, workerId,
            new TimeSlot(startsAt, startsAt.AddMinutes(45)), DateOnly.FromDateTime(startsAt.UtcDateTime), Now);
        var customer = Customer.Register(
            new CustomerId(CalendarSeed.NewId()), tenantId, new PhoneNumber(phone), Now);

        await using var db = fixture.CreateDbContext();
        db.Customers.Add(customer);
        db.Events.Add(slot);
        await db.SaveChangesAsync();

        slot.Claim(customer.Id, serviceId, Now, deadline);
        slot.ClearDomainEvents();
        await db.SaveChangesAsync();
    }

    private sealed record SeededQueue(
        TenantId TenantId,
        CalendarId FirstCalendar,
        CalendarId SecondCalendar,
        OperatorId FirstOperator,
        OperatorId SecondOperator);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
