using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.ConfirmedBookings;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`23-34`'s own confirmed-bookings read, every port faked - the same shape
/// `ContactsHandlerTests` already establishes for its sibling handler: the permission gate and the
/// rung-masking decision are the whole of what this handler adds over the read store, so that is the
/// whole of what these tests are about. The tenant-isolation and multi-slot-grouping proofs live in
/// `ConfirmedBookingsTests` (`Ago.Calendar.Integration.Tests`) against a real Postgres, where a fake
/// read store could not exercise the SQL itself.</summary>
public class ConfirmedBookingsHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    [Fact]
    public async Task WithCustomerRead_ReturnsTheStoresRows()
    {
        var row = ARow();
        var store = new FakeConfirmedBookingReadStore(row);
        var handler = new GetConfirmedBookingsForTenantHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetConfirmedBookingsForTenant(Caller, TenantId, Today, Today.AddDays(6)), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(row.BookingId, Assert.Single(result.Value).BookingId);
        Assert.Equal(TenantId, Assert.Single(store.AskedFor).TenantId);
    }

    [Fact]
    public async Task WithoutCustomerRead_IsRefused_AndNeverAsksTheStore()
    {
        var store = new FakeConfirmedBookingReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerRead);
        var handler = new GetConfirmedBookingsForTenantHandler(
            store, permissions, new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetConfirmedBookingsForTenant(Caller, TenantId, Today, Today.AddDays(6)), CancellationToken.None);

        Assert.Equal("confirmed_bookings.forbidden", result.Error!.Value.Code);
        Assert.Empty(store.AskedFor);
    }

    [Fact]
    public async Task ARangeEndingBeforeItStarts_IsRejected_AndNeverAsksTheStore()
    {
        var store = new FakeConfirmedBookingReadStore();
        var handler = new GetConfirmedBookingsForTenantHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetConfirmedBookingsForTenant(Caller, TenantId, Today.AddDays(1), Today), CancellationToken.None);

        Assert.Equal("confirmed_bookings.invalid_range", result.Error!.Value.Code);
        Assert.Empty(store.AskedFor);
    }

    [Fact]
    public async Task ARangeCheck_NeverRunsBeforeThePermissionGate()
    {
        // The actor check comes before the shape check - `GetConfirmedBookingsForTenantHandler`'s own
        // doc comment, the identical order `GetWorkerSlotsHandler` uses. A caller who may not read
        // this screen at all must never learn whether their own malformed range would otherwise have
        // been accepted.
        var store = new FakeConfirmedBookingReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerRead);
        var handler = new GetConfirmedBookingsForTenantHandler(
            store, permissions, new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetConfirmedBookingsForTenant(Caller, TenantId, Today.AddDays(1), Today), CancellationToken.None);

        Assert.Equal("confirmed_bookings.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task OnTheMaskedRung_AsksTheStoreToMask()
    {
        var store = new FakeConfirmedBookingReadStore(ARow());
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.MaskedWithReveal);
        var handler = new GetConfirmedBookingsForTenantHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(
            new GetConfirmedBookingsForTenant(Caller, TenantId, Today, Today.AddDays(6)), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Assert.Single(store.AskedFor).Mask);
    }

    [Fact]
    public async Task OnTheVisibleRung_NeverAsksTheStoreToMask()
    {
        var store = new FakeConfirmedBookingReadStore();
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.Visible);
        var handler = new GetConfirmedBookingsForTenantHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(
            new GetConfirmedBookingsForTenant(Caller, TenantId, Today, Today.AddDays(6)), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(Assert.Single(store.AskedFor).Mask);
    }

    private static ConfirmedBookingRow ARow() => new(
        new EventId(Guid.CreateVersion7(Now)),
        new CalendarId(Guid.CreateVersion7(Now)),
        new WorkerId(Guid.CreateVersion7(Now)),
        "Alex Doe",
        new ServiceId(Guid.CreateVersion7(Now)),
        "Haircut",
        new CustomerId(Guid.CreateVersion7(Now)),
        "Ivan",
        Now,
        Now.AddMinutes(45),
        DateOnly.FromDateTime(Now.UtcDateTime),
        (int)Now.UtcDateTime.DayOfWeek,
        "+79990000001",
        false);

    private static FakePermissionChecker Permissive() => new();
}
