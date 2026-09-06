using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-12`/`decisions.md` §5: "I called and it is them" - a fact only an operator who could see the
/// number to call it can state, recorded on <see cref="Customer.RecordOperatorConfirmedPhone"/>.
///
/// <para><b>Gated on <see cref="Permission.CustomerRead"/>, and that alone - the decision this item's
/// own brief asked to be made and argued rather than defaulted.</b> §5 says the confirm act "lives
/// only on rungs one and two... somebody who cannot see a number cannot confirm it by calling", which
/// reads as a per-rung refusal. It is not implemented as one, because under today's type system there
/// is no rung where a caller who holds <see cref="Permission.CustomerRead"/> cannot see the number:
/// <see cref="ContactVisibility.Visible"/> shows it plainly and <see cref="ContactVisibility.MaskedWithReveal"/>
/// shows it the moment <see cref="RevealCustomerPhoneHandler"/> is called - both are "can see it",
/// one directly and one on demand. The rung this sentence is actually describing is rung three
/// (<c>Never</c>), and `adr/0123`/this product's own <see cref="ContactVisibility"/> deliberately do
/// not add it to the enum (§5's own instruction, taken literally). So the one gate that already
/// distinguishes "can see this tenant's numbers at all" from "cannot" - <see cref="Permission.CustomerRead"/>
/// - is the whole of what §5 asks for today; a second, rung-keyed check would have nothing to
/// discriminate on until rung three is real, and adding one now would be exactly the kind of
/// ahead-of-its-subscriber plumbing this codebase already avoids elsewhere (<c>ContactVisibility</c>'s
/// own remarks on rung three's absence).</para>
///
/// <para><b>No reveal record is written here.</b> Confirming is not itself a read of the masked value
/// - the operator already saw the number (directly, or through a prior
/// <see cref="RevealCustomerPhoneHandler"/> call, which is what left its own record) - so this write
/// carries nothing an audit view needs beyond what <see cref="Customer.PhoneConfirmedByOperatorAt"/>
/// already states plainly in the read model.</para>
///
/// <para>Tenant-isolated the same way <see cref="RevealCustomerPhoneHandler"/> is: a customer id
/// belonging to another tenant reads as <see cref="ContactsErrors.CustomerNotFound"/>, never a
/// different, more informative error.</para>
/// </summary>
public sealed class ConfirmOperatorVerifiedPhoneHandler(
    ICustomerRepository customers, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result<DateTimeOffset>> HandleAsync(
        ConfirmOperatorVerifiedPhone command, CancellationToken cancellationToken)
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

        customer.RecordOperatorConfirmedPhone(clock.UtcNow);
        await customers.SaveAsync(customer, cancellationToken);

        return customer.PhoneConfirmedByOperatorAt!.Value;
    }
}
