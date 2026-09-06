using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-12`/`decisions.md` §5: "masked, revealed on demand, and the reveal is recorded." Gated on
/// <see cref="Permission.CustomerRead"/> - the identical permission and the identical tenant-scope
/// check <see cref="GetTenantContactsHandler"/> already applies, because a reveal is not a second,
/// stronger capability layered on top of reading the contacts list; it is the same capability, applied
/// to one field the tenant's own rung chose to mask by default. There is deliberately no check against
/// <see cref="ContactVisibility"/> here - the rung governs only the list reads, never this one (the
/// identical shape `ago-chat`'s own <c>RevealVisitorContactDetailHandler</c> already established for
/// its own reveal).
///
/// <para><b>Writes exactly one reveal record, and only on success.</b> A caller refused by the
/// permission check or a customer id that does not resolve in this tenant never reaches the write -
/// the same "a denied or not-found read has nothing to attest to" principle `adr/0113`'s own remarks
/// state for `access_records`.</para>
///
/// <para><b>Wrong tenant reads like no such customer</b> - <see cref="ContactsErrors.CustomerNotFound"/>,
/// never a different, more informative error. This is also `23-12`'s own tenant-isolation guarantee:
/// an operator who holds <see cref="Permission.CustomerRead"/> in their own tenant and happens to know
/// another tenant's customer id still cannot reveal it, because the lookup below is scoped to the
/// caller's own <see cref="TenantId"/> and a customer belonging to someone else's tenant simply does
/// not match it.</para>
/// </summary>
public sealed class RevealCustomerPhoneHandler(
    ICustomerRepository customers,
    IPermissionChecker permissions,
    IContactPhoneRevealRepository reveals,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<string>> HandleAsync(RevealCustomerPhone command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CustomerRead, cancellationToken);
        if (!allowed)
        {
            return ContactsErrors.Forbidden(Permission.CustomerRead);
        }

        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);
        if (customer is null || customer.TenantId != command.TenantId)
        {
            return ContactsErrors.CustomerNotFound(command.CustomerId);
        }

        var now = clock.UtcNow;
        await reveals.RecordAsync(
            new ContactPhoneRevealToWrite(
                idGenerator.NewId(now), now, command.TenantId, command.CustomerId, command.OperatorId,
                command.Surface),
            cancellationToken);

        return customer.Phone.Value;
    }
}
