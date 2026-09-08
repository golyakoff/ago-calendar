using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `23-60`/`adr/0147`: the permission gate, the tenant-isolation refusal, the self-merge and
/// already-merged refusals, and the survivor-choice rule <see cref="MergeCustomersHandler"/>'s own doc
/// comment argues for - every port faked, so these tests are about the handler's own decisions, not a
/// real database.
/// </summary>
public class MergeCustomersHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly TenantId OtherTenantId = new(new Guid("99999999-9999-9999-9999-999999999999"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static Customer BookingCustomer(TenantId tenantId, DateTimeOffset firstSeenAt, string phone = "+79990000001") =>
        Customer.Register(new CustomerId(Guid.CreateVersion7(firstSeenAt)), tenantId, new PhoneNumber(phone), firstSeenAt);

    private static Customer ChatCustomer(TenantId tenantId, DateTimeOffset firstSeenAt, string phone = "+79990000001") =>
        Customer.RegisterFromChat(new CustomerId(Guid.CreateVersion7(firstSeenAt)), tenantId, new PhoneNumber(phone), Guid.NewGuid(), firstSeenAt);

    [Fact]
    public async Task WithoutCustomerEdit_IsRefused_AndNeverLoadsEitherCustomer()
    {
        var booking = BookingCustomer(TenantId, Now);
        var chat = ChatCustomer(TenantId, Now);
        var customers = new FakeCustomerRepositoryForMerge(booking, chat);
        var store = new FakeCustomerMergeStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerEdit);
        var handler = new MergeCustomersHandler(customers, store, permissions, new SequentialIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new MergeCustomers(Caller, TenantId, booking.Id, chat.Id), CancellationToken.None);

        Assert.Equal("contacts.forbidden", result.Error!.Value.Code);
        Assert.Empty(customers.Loaded);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task GivenTheSameIdTwice_IsRefused()
    {
        var booking = BookingCustomer(TenantId, Now);
        var customers = new FakeCustomerRepositoryForMerge(booking);
        var store = new FakeCustomerMergeStore();
        var handler = new MergeCustomersHandler(customers, store, Permissive(), new SequentialIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new MergeCustomers(Caller, TenantId, booking.Id, booking.Id), CancellationToken.None);

        Assert.Equal("contacts.cannot_merge_customer_with_itself", result.Error!.Value.Code);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task GivenACustomerFromAnotherTenant_ReadsLikeNotFound_NeverALeakedCrossTenantError()
    {
        var mine = BookingCustomer(TenantId, Now);
        var theirs = BookingCustomer(OtherTenantId, Now, "+79990000002");
        var customers = new FakeCustomerRepositoryForMerge(mine, theirs);
        var store = new FakeCustomerMergeStore();
        var handler = new MergeCustomersHandler(customers, store, Permissive(), new SequentialIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new MergeCustomers(Caller, TenantId, mine.Id, theirs.Id), CancellationToken.None);

        Assert.Equal("contacts.customer_not_found", result.Error!.Value.Code);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task WhenTheFirstCandidateIsAlreadyMerged_IsRefused()
    {
        var survivor = BookingCustomer(TenantId, Now);
        var alreadyAbsorbed = ChatCustomer(TenantId, Now);
        alreadyAbsorbed.MarkMergedInto(survivor.Id, Now);
        var third = ChatCustomer(TenantId, Now, "+79990000003");
        var customers = new FakeCustomerRepositoryForMerge(survivor, alreadyAbsorbed, third);
        var store = new FakeCustomerMergeStore();
        var handler = new MergeCustomersHandler(customers, store, Permissive(), new SequentialIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new MergeCustomers(Caller, TenantId, alreadyAbsorbed.Id, third.Id), CancellationToken.None);

        Assert.Equal("contacts.customer_already_merged", result.Error!.Value.Code);
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABookingSourcedCustomer_AlwaysSurvivesOverAChatSourcedOne_RegardlessOfArgumentOrder(
        bool bookingFirst)
    {
        var booking = BookingCustomer(TenantId, Now);
        var chat = ChatCustomer(TenantId, Now.AddDays(-1));
        var customers = new FakeCustomerRepositoryForMerge(booking, chat);
        var store = new FakeCustomerMergeStore();
        var handler = new MergeCustomersHandler(customers, store, Permissive(), new SequentialIdGenerator(), new FakeClock(Now));

        var command = bookingFirst
            ? new MergeCustomers(Caller, TenantId, booking.Id, chat.Id)
            : new MergeCustomers(Caller, TenantId, chat.Id, booking.Id);
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(booking.Id, result.Value.SurvivorCustomerId);
        Assert.Equal(chat.Id, result.Value.AbsorbedCustomerId);
        var call = Assert.Single(store.Calls);
        Assert.Equal(booking.Id, call.Survivor.Id);
        Assert.Equal(chat.Id, call.Absorbed.Id);
    }

    [Fact]
    public async Task BetweenTwoChatSourcedRows_TheEarlierFirstSeenSurvives()
    {
        var earlier = ChatCustomer(TenantId, Now.AddDays(-5));
        var later = ChatCustomer(TenantId, Now);
        var customers = new FakeCustomerRepositoryForMerge(earlier, later);
        var store = new FakeCustomerMergeStore();
        var handler = new MergeCustomersHandler(customers, store, Permissive(), new SequentialIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new MergeCustomers(Caller, TenantId, later.Id, earlier.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(earlier.Id, result.Value.SurvivorCustomerId);
        Assert.Equal(later.Id, result.Value.AbsorbedCustomerId);
    }

    [Fact]
    public async Task OnSuccess_TheSurvivorAbsorbsNoShowHistory_AndTheAbsorbedIsTombstoned()
    {
        var survivor = BookingCustomer(TenantId, Now.AddDays(-30));
        survivor.RecordNoShow(Now);
        var absorbed = ChatCustomer(TenantId, Now.AddDays(-1));
        absorbed.RecordNoShow(Now);
        absorbed.RecordNoShow(Now);
        var customers = new FakeCustomerRepositoryForMerge(survivor, absorbed);
        var store = new FakeCustomerMergeStore { BookingsMovedToReturn = 4 };
        var handler = new MergeCustomersHandler(customers, store, Permissive(), new SequentialIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new MergeCustomers(Caller, TenantId, survivor.Id, absorbed.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(4, result.Value.BookingsMoved);
        var call = Assert.Single(store.Calls);
        Assert.Equal(3, call.Survivor.NoShowCount);
        Assert.Equal(survivor.Id, call.Absorbed.MergedIntoCustomerId);
        Assert.Equal(Now, call.Absorbed.MergedAt);
        Assert.Equal(Caller, call.OperatorId);
        Assert.Equal(TenantId, call.TenantId);
    }

    private static FakePermissionChecker Permissive() => new();
}
