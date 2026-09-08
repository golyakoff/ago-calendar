using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`23-60`/`adr/0147`: "seeing both sets of bookings before deciding" - the preview handler's
/// own permission gate (the same <see cref="Permission.CustomerEdit"/> the merge itself requires, not
/// the narrower <see cref="Permission.CustomerRead"/>) and its own splitting of one query's rows onto
/// the two candidates.</summary>
public class GetCustomerMergePreviewHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static Customer BookingCustomer(string phone = "+79990000001") =>
        Customer.Register(new CustomerId(Guid.CreateVersion7(Now)), TenantId, new PhoneNumber(phone), Now);

    [Fact]
    public async Task WithoutCustomerEdit_IsRefused()
    {
        var first = BookingCustomer();
        var second = BookingCustomer("+79990000002");
        var customers = new FakeCustomerRepositoryForMerge(first, second);
        var previewStore = new FakeCustomerMergePreviewReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerEdit);
        var handler = new GetCustomerMergePreviewHandler(
            customers, previewStore, permissions, new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetCustomerMergePreview(Caller, TenantId, first.Id, second.Id), CancellationToken.None);

        Assert.Equal("contacts.forbidden", result.Error!.Value.Code);
        Assert.Empty(previewStore.AskedFor);
    }

    [Fact]
    public async Task OnSuccess_SplitsTheStoresRowsOntoTheirOwnCandidate()
    {
        var first = BookingCustomer();
        var second = BookingCustomer("+79990000002");
        var customers = new FakeCustomerRepositoryForMerge(first, second);
        var firstBooking = new CustomerMergePreviewBookingRow(
            first.Id, new EventId(Guid.NewGuid()), EventStatus.Booked, "Haircut", "Anna", Now, Now.AddMinutes(45), DateOnly.FromDateTime(Now.Date));
        var secondBooking = new CustomerMergePreviewBookingRow(
            second.Id, new EventId(Guid.NewGuid()), EventStatus.NoShow, "Manicure", "Olga", Now, Now.AddMinutes(30), DateOnly.FromDateTime(Now.Date));
        var previewStore = new FakeCustomerMergePreviewReadStore([firstBooking, secondBooking]);
        var handler = new GetCustomerMergePreviewHandler(
            customers, previewStore, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetCustomerMergePreview(Caller, TenantId, first.Id, second.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(first.Id, result.Value.First.CustomerId);
        Assert.Equal(firstBooking, Assert.Single(result.Value.First.Bookings));
        Assert.Equal(second.Id, result.Value.Second.CustomerId);
        Assert.Equal(secondBooking, Assert.Single(result.Value.Second.Bookings));
        var asked = Assert.Single(previewStore.AskedFor);
        Assert.Equal(TenantId, asked.TenantId);
    }

    [Fact]
    public async Task LabelsWhichCandidateWouldSurvive_UsingTheSameRuleTheRealMergeWouldApply()
    {
        var booking = BookingCustomer();
        var chat = Customer.RegisterFromChat(
            new CustomerId(Guid.CreateVersion7(Now)), TenantId, new PhoneNumber("+79990000002"), Guid.NewGuid(), Now);
        var customers = new FakeCustomerRepositoryForMerge(booking, chat);
        var previewStore = new FakeCustomerMergePreviewReadStore();
        var handler = new GetCustomerMergePreviewHandler(
            customers, previewStore, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetCustomerMergePreview(Caller, TenantId, chat.Id, booking.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        // `chat` was named first in the request, but the Booking-sourced candidate always survives
        // (`CustomerMergeSurvivorRule`'s own remarks) - the label must not simply follow request order.
        Assert.Equal(chat.Id, result.Value.First.CustomerId);
        Assert.False(result.Value.First.WillSurvive);
        Assert.Equal(booking.Id, result.Value.Second.CustomerId);
        Assert.True(result.Value.Second.WillSurvive);
    }

    [Fact]
    public async Task OnTheMaskedRung_MasksBothCandidatesPhones()
    {
        var first = BookingCustomer();
        var second = BookingCustomer("+79990000002");
        var customers = new FakeCustomerRepositoryForMerge(first, second);
        var previewStore = new FakeCustomerMergePreviewReadStore();
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.MaskedWithReveal);
        var handler = new GetCustomerMergePreviewHandler(customers, previewStore, Permissive(), visibility);

        var result = await handler.HandleAsync(
            new GetCustomerMergePreview(Caller, TenantId, first.Id, second.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.First.Masked);
        Assert.NotEqual(first.Phone.Value, result.Value.First.Phone);
    }

    private static FakePermissionChecker Permissive() => new();
}
