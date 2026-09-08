using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-60`/`adr/0161`: which of two merge candidates survives - one rule, called from both
/// <see cref="MergeCustomersHandler"/> (which acts on the answer) and
/// <see cref="GetCustomerMergePreviewHandler"/> (which only shows it, so the confirmation screen
/// states the outcome before an operator commits rather than surprising them afterwards). Extracted
/// to its own type specifically so there is exactly one place this decision is made - two independent
/// copies of "which one wins" is exactly the kind of drift that would let the preview and the real
/// merge disagree.
///
/// <para><b>The reasoning belongs to <see cref="MergeCustomersHandler"/>'s own doc comment</b> - this
/// type is only that reasoning's mechanical statement. Two <see cref="CustomerSource.Booking"/>-sourced
/// rows can never reach here sharing a phone (<c>ux_customers_tenant_phone</c>'s own partial-unique
/// guarantee), so the only asymmetric case possible is exactly one of the two being
/// <see cref="CustomerSource.Booking"/>-sourced.</para>
/// </summary>
internal static class CustomerMergeSurvivorRule
{
    public static (Customer Survivor, Customer Absorbed) Choose(Customer first, Customer second)
    {
        if (first.Source == CustomerSource.Booking && second.Source != CustomerSource.Booking)
        {
            return (first, second);
        }

        if (second.Source == CustomerSource.Booking && first.Source != CustomerSource.Booking)
        {
            return (second, first);
        }

        if (first.FirstSeenAt != second.FirstSeenAt)
        {
            return first.FirstSeenAt < second.FirstSeenAt ? (first, second) : (second, first);
        }

        // Deterministic tie-break for two rows first seen at the exact same instant - never left to
        // whichever order the caller happened to name them in. Guid has no `<` operator, so this
        // compares explicitly rather than assuming one exists.
        return first.Id.Value.CompareTo(second.Id.Value) < 0 ? (first, second) : (second, first);
    }
}
