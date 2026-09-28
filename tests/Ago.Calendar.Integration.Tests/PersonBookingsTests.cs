using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PersonBookings;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `26-269`'s own new read, against a real Postgres - the same shape `ConfirmedBookingsTests` already
/// established for its tenant-wide sibling (a Dapper read store, tenant-isolated, gated by a permission
/// the handler checks once), adapted for one filtered by person instead of by date, and widened to the
/// three held statuses a person's own history is made of.
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PersonBookingsTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    [Fact]
    public async Task TheReadStore_IsTenantIsolated()
    {
        // Deliberately the *same* person id in two different tenants - a scenario the events table's
        // own schema does not rule out - so this proves the query is gated on `tenant_id`, not on
        // `person_id` alone. Mirrors `ConfirmedBookingsTests.TheReadStore_IsTenantIsolated`'s own intent.
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);

        await ABookedBookingAsync(mine, Now.AddHours(1), personId: mine.Person.PersonId);
        await ABookedBookingAsync(theirs, Now.AddHours(1), personId: mine.Person.PersonId);

        var store = new PersonBookingReadStore(fixture.DataSource);
        var rows = await store.GetForPersonAsync(mine.Tenant.Id, mine.Person.PersonId, mask: false, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(mine.Worker.Id, row.WorkerId);
    }

    [Fact]
    public async Task APersonWithNoBookings_ReturnsAnEmptyList_NeverAnError()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task AnUnknownPersonId_ReturnsAnEmptyList_NeverAnError()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddHours(1));

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, CalendarSeed.NewId());

        Assert.Empty(rows);
    }

    [Fact]
    public async Task TheHandler_ReturnsThePastAndTheUpcomingBooking_ForACallerHoldingCustomerRead()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddDays(-3));
        await ABookedBookingAsync(seed, Now.AddDays(3));

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(seed.Person.PersonId, row.PersonId));
    }

    [Fact]
    public async Task APendingConfirmationBooking_IsIncluded()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = CalendarSeed.Slot(seed, Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var booking = await db.Events.SingleAsync(e => e.Id == slot.Id);
            booking.Claim(seed.Person.PersonId, seed.Service.Id, Now, Now.AddMinutes(30), slot.Id);
            booking.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        var row = Assert.Single(rows);
        Assert.Equal(EventStatus.PendingConfirmation, row.Status);
    }

    [Fact]
    public async Task ANoShowBooking_IsIncluded_AndCarriesTheNoShowStatus()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var startsAt = Now.AddDays(-1);
        var slot = CalendarSeed.Slot(seed, startsAt);

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var booking = await db.Events.SingleAsync(e => e.Id == slot.Id);
            booking.Claim(seed.Person.PersonId, seed.Service.Id, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(30), slot.Id);
            booking.Confirm(Now.AddDays(-2).AddMinutes(31));
            booking.MarkNoShow(startsAt.AddHours(1));
            booking.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        var row = Assert.Single(rows);
        Assert.Equal(EventStatus.NoShow, row.Status);
    }

    [Fact]
    public async Task ACancelledBooking_NeverAppearsInTheList()
    {
        // `26-269`'s own scope, `IPersonBookingReadStore`'s own remarks: a withdrawn booking is not part
        // of the held history the client-detail hub's Предстоящие/Прошедшие split exists to show.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = CalendarSeed.Slot(seed, Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var booking = await db.Events.SingleAsync(e => e.Id == slot.Id);
            booking.Claim(seed.Person.PersonId, seed.Service.Id, Now, Now.AddMinutes(30), slot.Id);
            booking.Cancel(Now.AddMinutes(5));
            booking.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task AnAvailableSlot_WithNoBooking_NeverAppearsInTheList()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slot = CalendarSeed.Slot(seed, Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task TheHandler_WithoutCustomerRead_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddHours(1));

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
        var result = await new GetPersonBookingsHandler(
                new PersonBookingReadStore(fixture.DataSource),
                new PermissionChecker(new RoleAssignmentProjectionStore(reader)),
                new ContactVisibilityProjectionStore(reader))
            .HandleAsync(
                new GetPersonBookings(strangerId, seed.Tenant.Id, seed.Person.PersonId),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("person_bookings.forbidden", result.Error!.Value.Code);
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

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        var row = Assert.Single(rows);
        Assert.True(row.Masked);
        Assert.NotEqual(seed.Person.Phone.Value, row.Phone);
    }

    /// <summary>`20-18`'s own multi-slot shape, restated for a person's own read: a three-slot run must
    /// render as one row, not three.</summary>
    [Fact]
    public async Task AThreeSlotBooking_ShowsAsOneRowInTheList()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var slots = new List<Event>(3);
        var start = Now.AddHours(1);
        for (var i = 0; i < 3; i++)
        {
            slots.Add(CalendarSeed.Slot(seed, start, 30));
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
                eventRow.Claim(seed.Person.PersonId, seed.Service.Id, Now, Now.AddMinutes(30), anchorId);
                eventRow.ClearDomainEvents();
            }

            await db.SaveChangesAsync();
        }

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

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        var row = Assert.Single(rows);
        Assert.Equal(anchorId, row.BookingId);
        Assert.Equal(slots[0].StartsAt, row.StartsAt);
        Assert.Equal(slots[^1].EndsAt, row.EndsAt);
    }

    [Fact]
    public async Task TheReadStore_CarriesTheOriginConversationId_ForAChatOriginatedBooking()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var conversationId = CalendarSeed.NewId();
        await ABookedBookingAsync(seed, Now.AddHours(1), originConversationId: conversationId);

        var rows = await ListAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.PersonId);

        Assert.Equal(conversationId, Assert.Single(rows).OriginConversationId);
    }

    [Fact]
    public async Task TheConsoleEndpoint_ReturnsTheList_OverRealHttp()
    {
        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed, Now.AddDays(-1));
        await ABookedBookingAsync(seed, Now.AddDays(1));

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/console/contacts/{seed.Person.PersonId}/bookings");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookings = await response.Content.ReadFromJsonAsync<PersonBookingResponse[]>();
        Assert.Equal(2, bookings!.Length);
        Assert.All(bookings, booking => Assert.Equal(seed.Person.PersonId, booking.PersonId));
        Assert.All(bookings, booking => Assert.Equal("Booked", booking.Status));
    }

    [Fact]
    public async Task TheConsoleEndpoint_ReturnsAnEmptyArray_ForAPersonWithNoBookings_OverRealHttp()
    {
        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();
        var seed = await CalendarSeed.WriteAsync(fixture);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/console/contacts/{seed.Person.PersonId}/bookings");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookings = await response.Content.ReadFromJsonAsync<PersonBookingResponse[]>();
        Assert.Empty(bookings!);
    }

    private async Task<Event> ABookedBookingAsync(
        SeededTenant seed, DateTimeOffset startsAt, int minutes = 45, Guid? personId = null,
        Guid? originConversationId = null)
    {
        var slot = CalendarSeed.Slot(seed, startsAt, minutes);

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Events.SingleAsync(e => e.Id == slot.Id);
            row.Claim(
                personId ?? seed.Person.PersonId, seed.Service.Id, Now.AddDays(-30), Now.AddDays(-30).AddMinutes(30),
                slot.Id, originConversationId: originConversationId);
            row.Confirm(Now.AddDays(-30).AddMinutes(31));
            row.ClearDomainEvents();
            await db.SaveChangesAsync();
        }

        return slot;
    }

    private async Task<IReadOnlyList<PersonBookingRow>> ListAsync(
        OperatorId operatorId, TenantId tenantId, Guid personId)
    {
        await using var db = fixture.CreateDbContext();
        var result = await new GetPersonBookingsHandler(
                new PersonBookingReadStore(fixture.DataSource),
                new PermissionChecker(new RoleAssignmentProjectionStore(db)),
                new ContactVisibilityProjectionStore(db))
            .HandleAsync(new GetPersonBookings(operatorId, tenantId, personId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }
}
