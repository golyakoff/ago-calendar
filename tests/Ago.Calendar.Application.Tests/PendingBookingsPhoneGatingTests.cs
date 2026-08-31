using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-12`: <c>GetPendingBookingsForTenantHandler</c>'s own second, independent permission check -
/// never a reason to refuse the read (that is still <see cref="Permission.BookingReject"/> alone), only
/// whether the phone field gets fetched. No database here: whether the read store's SQL actually
/// joins to <c>customers</c> is proven in <c>Ago.Calendar.Integration.Tests.SharedPendingQueueTests</c>;
/// this is only about the handler's own decision, which is the part a fake can prove in microseconds.
/// </summary>
public class PendingBookingsPhoneGatingTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACallerHoldingCustomerRead_AsksTheReadStoreToIncludeContactData()
    {
        var world = new World();

        var result = await world.QueueAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Assert.Single(world.Queue.AskedFor).IncludeContactData);
    }

    [Fact]
    public async Task ACallerWithoutCustomerRead_StillSeesTheQueue_ButAsksTheReadStoreToOmitContactData()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CustomerRead);

        var result = await world.QueueAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(Assert.Single(world.Queue.AskedFor).IncludeContactData);
    }

    [Fact]
    public async Task ACallerWithoutBookingReject_IsRefused_RegardlessOfCustomerRead()
    {
        // BookingReject remains the read gate - CustomerRead only ever adds or withholds one field on
        // rows the caller was already allowed to see.
        var world = new World();
        world.Permissions.Deny(Permission.BookingReject);

        var result = await world.QueueAsync();

        Assert.Equal("booking.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Queue.AskedFor);
    }

    private sealed class World
    {
        private readonly GetPendingBookingsForTenantHandler _handler;

        public World()
        {
            _handler = new GetPendingBookingsForTenantHandler(Queue, Permissions, new FakeClock(Now));
        }

        public FakePendingBookingReadStore Queue { get; } = new();

        public FakePermissionChecker Permissions { get; } = new();

        public Task<Ago.Platform.Kernel.Result<IReadOnlyList<PendingBookingRow>>> QueueAsync() =>
            _handler.HandleAsync(new GetPendingBookingsForTenant(Caller, TenantId, 100), CancellationToken.None);
    }
}
