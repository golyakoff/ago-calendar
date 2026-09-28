using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PersonBookings;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`26-269`'s own per-person bookings read, every port faked - the same shape
/// `ConfirmedBookingsHandlerTests` already establishes for its sibling handler: the permission gate and
/// the rung-masking decision are the whole of what this handler adds over the read store, so that is the
/// whole of what these tests are about. The tenant-and-person isolation and the held-status filter live
/// in `PersonBookingsTests` (`Ago.Calendar.Integration.Tests`) against a real Postgres, where a fake
/// read store could not exercise the SQL itself.</summary>
public class PersonBookingsHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly Guid PersonId = new("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithCustomerRead_ReturnsTheStoresRows()
    {
        var row = ARow();
        var store = new FakePersonBookingReadStore(row);
        var handler = new GetPersonBookingsHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonBookings(Caller, TenantId, PersonId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(row.BookingId, Assert.Single(result.Value).BookingId);
        Assert.Equal(row.Status, Assert.Single(result.Value).Status);
        var asked = Assert.Single(store.AskedFor);
        Assert.Equal(TenantId, asked.TenantId);
        Assert.Equal(PersonId, asked.PersonId);
    }

    [Fact]
    public async Task WithNoBookings_ReturnsAnEmptyList_NeverAnError()
    {
        // `26-269`'s own scope: a person the operator is legitimately looking at who simply has not
        // booked yet is a real, honest empty state - never a refusal.
        var store = new FakePersonBookingReadStore();
        var handler = new GetPersonBookingsHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonBookings(Caller, TenantId, PersonId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task WithoutCustomerRead_IsRefused_AndNeverAsksTheStore()
    {
        var store = new FakePersonBookingReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerRead);
        var handler = new GetPersonBookingsHandler(store, permissions, new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonBookings(Caller, TenantId, PersonId), CancellationToken.None);

        Assert.Equal("person_bookings.forbidden", result.Error!.Value.Code);
        Assert.Empty(store.AskedFor);
    }

    [Fact]
    public async Task OnTheMaskedRung_AsksTheStoreToMask()
    {
        var store = new FakePersonBookingReadStore(ARow());
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.MaskedWithReveal);
        var handler = new GetPersonBookingsHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(
            new GetPersonBookings(Caller, TenantId, PersonId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Assert.Single(store.AskedFor).Mask);
    }

    [Fact]
    public async Task OnTheVisibleRung_NeverAsksTheStoreToMask()
    {
        var store = new FakePersonBookingReadStore();
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.Visible);
        var handler = new GetPersonBookingsHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(
            new GetPersonBookings(Caller, TenantId, PersonId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(Assert.Single(store.AskedFor).Mask);
    }

    private static PersonBookingRow ARow() => new(
        new EventId(Guid.CreateVersion7(Now)),
        new CalendarId(Guid.CreateVersion7(Now)),
        new WorkerId(Guid.CreateVersion7(Now)),
        "Alex Doe",
        new ServiceId(Guid.CreateVersion7(Now)),
        "Haircut",
        PersonId,
        Now,
        Now.AddMinutes(45),
        DateOnly.FromDateTime(Now.UtcDateTime),
        (int)Now.UtcDateTime.DayOfWeek,
        "+79990000001",
        false,
        Guid.CreateVersion7(Now),
        EventStatus.Booked);

    private static FakePermissionChecker Permissive() => new();
}
