using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>The tenant contacts report, every port faked - the permission gate is the whole of what
/// this handler adds over the read store, so that is the whole of what these tests are about.
///
/// <para>`23-12`: plus the second, independent read of the tenant's own rung, and that it is only
/// asked once the permission gate has already passed.</para>
/// </summary>
public class ContactsHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithCustomerRead_ReturnsTheStoresRows()
    {
        var row = new ContactRow(
            new CustomerId(Guid.CreateVersion7(Now)), "+79990000001", false, "Anna", null, 0, null, null, Now, Now, []);
        var store = new FakeContactsReadStore(row);
        var handler = new GetTenantContactsHandler(store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(new GetTenantContacts(Caller, TenantId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(row.CustomerId, Assert.Single(result.Value).CustomerId);
        Assert.Equal(TenantId, Assert.Single(store.AskedFor).TenantId);
    }

    [Fact]
    public async Task WithoutCustomerRead_IsRefused_AndNeverAsksTheStore()
    {
        var store = new FakeContactsReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerRead);
        var handler = new GetTenantContactsHandler(store, permissions, new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(new GetTenantContacts(Caller, TenantId), CancellationToken.None);

        Assert.Equal("contacts.forbidden", result.Error!.Value.Code);
        Assert.Empty(store.AskedFor);
    }

    [Fact]
    public async Task OnTheMaskedRung_AsksTheStoreToMask()
    {
        var row = new ContactRow(
            new CustomerId(Guid.CreateVersion7(Now)), "+79990000001", false, "Anna", null, 0, null, null, Now, Now, []);
        var store = new FakeContactsReadStore(row);
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.MaskedWithReveal);
        var handler = new GetTenantContactsHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(new GetTenantContacts(Caller, TenantId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Assert.Single(store.AskedFor).Mask);
    }

    [Fact]
    public async Task OnTheVisibleRung_NeverAsksTheStoreToMask()
    {
        var store = new FakeContactsReadStore();
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.Visible);
        var handler = new GetTenantContactsHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(new GetTenantContacts(Caller, TenantId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(Assert.Single(store.AskedFor).Mask);
    }

    private static FakePermissionChecker Permissive() => new();
}
