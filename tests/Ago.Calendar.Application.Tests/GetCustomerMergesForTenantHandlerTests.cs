using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`23-60`/`adr/0147`'s own Done-when: "the merge is recorded ... and is visible
/// afterwards." Gated on <see cref="Permission.CalendarConfigure"/>, the same wider-than-the-action
/// permission <see cref="GetPhoneRevealsForTenantHandler"/> already uses for its own sibling audit
/// trail - <see cref="GetPhoneRevealsForTenantHandler"/>'s tests are the closest precedent this file
/// follows.</summary>
public class GetCustomerMergesForTenantHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithCalendarConfigure_ReturnsTheStoresRows()
    {
        var record = new CustomerMergeRecord(Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3);
        var store = new FakeCustomerMergeReadStore(record);
        var handler = new GetCustomerMergesForTenantHandler(store, Permissive());

        var result = await handler.HandleAsync(new GetCustomerMergesForTenant(Caller, TenantId, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(record.Id, Assert.Single(result.Value.Items).Id);
        Assert.Equal(TenantId, Assert.Single(store.AskedFor).TenantId);
    }

    [Fact]
    public async Task WithoutCalendarConfigure_IsRefused_AndNeverAsksTheStore()
    {
        var store = new FakeCustomerMergeReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CalendarConfigure);
        var handler = new GetCustomerMergesForTenantHandler(store, permissions);

        var result = await handler.HandleAsync(new GetCustomerMergesForTenant(Caller, TenantId, null, null), CancellationToken.None);

        Assert.Equal("contacts.forbidden", result.Error!.Value.Code);
        Assert.Empty(store.AskedFor);
    }

    [Fact]
    public async Task ALimitOutsideTheAllowedRange_IsClampedRatherThanRejected()
    {
        var store = new FakeCustomerMergeReadStore();
        var handler = new GetCustomerMergesForTenantHandler(store, Permissive());

        await handler.HandleAsync(new GetCustomerMergesForTenant(Caller, TenantId, null, 10_000), CancellationToken.None);

        // 200 - GetCustomerMergesForTenantHandler.MaxLimit's own value, not referenced directly: it
        // is `internal` (no InternalsVisibleTo wires this test assembly to it, the same reason
        // GetPhoneRevealsForTenantHandlerTests - if one existed - could not reference its own twin
        // either), so this asserts the clamp's observable effect rather than the constant itself.
        Assert.Equal(200, Assert.Single(store.AskedFor).Limit);
    }

    private static FakePermissionChecker Permissive() => new();
}
