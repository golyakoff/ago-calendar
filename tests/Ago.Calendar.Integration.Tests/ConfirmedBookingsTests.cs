using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.ConfirmedBookings;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `23-34`'s own new screen, against a real Postgres - the same shape `ContactsReportTests` already
/// established for a full personal-data listing (a Dapper read store, tenant-isolated, gated by a
/// permission the handler checks once), adapted for one that also groups by day and by master.
///
/// <para>Tenant isolation is proved the exact way `18-08`'s own Outcome describes and
/// `ContactsReportTests.TheReadStore_IsTenantIsolated` already proves for the contacts report: this
/// suite's own version lives in <see cref="TheReadStore_IsTenantIsolated"/>. The managing session
/// re-verified it independently by mutating the read store's own <c>where</c> clause (dropping the
/// <c>e.tenant_id = @TenantId</c> predicate) and watching this test fail with the other tenant's
/// booking leaking into the result, then reverting - recorded in the item's own report rather than
/// left as a claim nothing exercised.</para>
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConfirmedBookingsTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    [Fact]
    public async Task TheReadStore_IsTenantIsolated()
    {
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        await ABookedBookingAsync(mine, Now.AddHours(1));
        await ABookedBookingAsync(theirs, Now.AddHours(1));

        var store = new ConfirmedBookingReadStore(fixture.DataSource);
        var rows = await store.GetConfirmedForTenantAsync(
            mine.Tenant.Id, Today, Today.AddDays(1), mask: false, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(mine.Customer.Id, row.CustomerId);
    }

    [Fact]
    public async Task TheHandler_ReturnsTheMasterAndTheService_ForACallerHoldingCustomerRead()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddHours(2));

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id);

        var row = Assert.Single(rows);
        Assert.Equal(seed.Worker.Id, row.WorkerId);
        Assert.Equal(seed.Worker.DisplayName, row.WorkerDisplayName);
        Assert.Equal(seed.Service.Id, row.ServiceId);
        Assert.Equal(seed.Service.Name, row.ServiceName);
        Assert.Equal(seed.Customer.Id, row.CustomerId);
        Assert.Equal(seed.Customer.Phone.Value, row.Phone);
        Assert.False(row.Masked);
    }

    [Fact]
    public async Task TheHandler_WithoutCustomerRead_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddHours(1));

        // A real operator of this tenant, holding the same booking-action permissions as the queue's
        // own stranger - the refusal must come from the permission model resolving real rows, not
        // from the operator being unknown, the identical shape
        // `SharedPendingQueueTests.AnOperatorWithoutRejectAsync` uses for its own stranger.
        var strangerSubject = $"kc-{CalendarSeed.NewId():N}";
        var strangerId = OperatorId.FromExternalSubjectId(strangerSubject);
        string[] strangerPermissions = [Permission.BookingReject.Value, Permission.BookingCancel.Value];

        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            await projections.StageAsync(
                strangerId, seed.Tenant.Id, strangerSubject, strangerPermissions, CalendarSeed.Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var reader = fixture.CreateDbContext();
        var result = await new GetConfirmedBookingsForTenantHandler(
                new ConfirmedBookingReadStore(fixture.DataSource),
                new PermissionChecker(new RoleAssignmentProjectionStore(reader)),
                new ContactVisibilityProjectionStore(reader))
            .HandleAsync(
                new GetConfirmedBookingsForTenant(strangerId, seed.Tenant.Id, Today, Today.AddDays(1)),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("confirmed_bookings.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task APendingBooking_NeverAppearsInTheList()
    {
        // `23-34`'s own scope: "it is a read, and it stays one" - and the read is of what is
        // *confirmed*, never of what is still waiting on the queue. A booking claimed but not yet
        // confirmed must not leak into this screen just because it has a customer and a service.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(Now.AddHours(1), Now.AddHours(1).AddMinutes(45)),
            DateOnly.FromDateTime(Now.UtcDateTime), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var booking = await db.Events.SingleAsync(e => e.Id == slot.Id);
            booking.Claim(seed.Customer.Id, seed.Service.Id, Now, Now.AddMinutes(30), slot.Id);
            booking.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ADateOutsideTheRange_IsExcluded()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddDays(10));

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, Today, Today.AddDays(1));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ACallerHoldingCustomerRead_SeesTheMaskedPhone_WhenTheTenantsRungCallsForIt()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            var visibility = new ContactVisibilityProjectionStore(db);
            await visibility.StageAsync(seed.Tenant.Id, ContactVisibility.MaskedWithReveal, Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id);

        var row = Assert.Single(rows);
        Assert.True(row.Masked);
        Assert.NotEqual(seed.Customer.Phone.Value, row.Phone);
    }

    /// <summary>`20-18`'s own shape, restated for a confirmed booking rather than a pending one: a
    /// three-slot run must render as one row, not three - proven against a real Postgres so
    /// <c>ConfirmedBookingReadStore</c>'s own <c>group by booking_id</c> is what is actually
    /// exercised.</summary>
    [Fact]
    public async Task AThreeSlotBooking_ShowsAsOneRowInTheList()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slots = new List<Event>(3);
        var start = Now.AddHours(1);
        for (var i = 0; i < 3; i++)
        {
            slots.Add(Event.Materialize(
                new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
                new TimeSlot(start, start.AddMinutes(30)), DateOnly.FromDateTime(start.UtcDateTime), Now));
            start = start.AddMinutes(40);
        }

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.AddRange(slots);
            await db.SaveChangesAsync();
        }

        var anchorId = slots[0].Id;
        await using (var db = fixture.CreateDbContext())
        {
            foreach (var id in slots.Select(s => s.Id))
            {
                var eventRow = await db.Events.SingleAsync(e => e.Id == id);
                eventRow.Claim(seed.Customer.Id, seed.Service.Id, Now, Now.AddMinutes(30), anchorId);
                eventRow.ClearDomainEvents();
            }

            await db.SaveChangesAsync();
        }

        // `ExpiredBookingConfirmer.ConfirmExpiredAsync`'s own shape: the state machine runs on
        // *every* row of the run, not only the anchor - a multi-slot booking's own slots each carry
        // their own status, and only the anchor's own id doubles as the group's `booking_id`.
        await using (var db = fixture.CreateDbContext())
        {
            foreach (var id in slots.Select(s => s.Id))
            {
                var eventRow = await db.Events.SingleAsync(e => e.Id == id);
                eventRow.Confirm(Now.AddMinutes(31));
                eventRow.ClearDomainEvents();
            }

            await db.SaveChangesAsync();
        }

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id);

        var row = Assert.Single(rows);
        Assert.Equal(anchorId, row.BookingId);
        Assert.Equal(slots[0].StartsAt, row.StartsAt);
        Assert.Equal(slots[^1].EndsAt, row.EndsAt);
    }

    [Fact]
    public async Task TheConsoleEndpoint_ReturnsTheList_OverRealHttp()
    {
        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddHours(1));

        var from = Today.ToString("yyyy-MM-dd");
        var to = Today.AddDays(1).ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/console/confirmed-bookings?from={from}&to={to}");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookings = await response.Content.ReadFromJsonAsync<ConfirmedBookingResponse[]>();
        Assert.Equal(seed.Customer.Id.Value, Assert.Single(bookings!).CustomerId);
    }

    private async Task<Event> ABookedBookingAsync(SeededTenant seed, DateTimeOffset startsAt, int minutes = 45)
    {
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(startsAt, startsAt.AddMinutes(minutes)), DateOnly.FromDateTime(startsAt.UtcDateTime), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Events.SingleAsync(e => e.Id == slot.Id);
            row.Claim(seed.Customer.Id, seed.Service.Id, Now, Now.AddMinutes(30), slot.Id);
            row.Confirm(Now.AddMinutes(31));
            row.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        return slot;
    }

    private async Task<IReadOnlyList<ConfirmedBookingRow>> ListAsync(
        OperatorId operatorId, TenantId tenantId, DateOnly? from = null, DateOnly? to = null)
    {
        await using var db = fixture.CreateDbContext();
        var result = await new GetConfirmedBookingsForTenantHandler(
                new ConfirmedBookingReadStore(fixture.DataSource),
                new PermissionChecker(new RoleAssignmentProjectionStore(db)),
                new ContactVisibilityProjectionStore(db))
            .HandleAsync(
                new GetConfirmedBookingsForTenant(
                    operatorId, tenantId, from ?? Today, to ?? Today.AddDays(1)),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }
}
