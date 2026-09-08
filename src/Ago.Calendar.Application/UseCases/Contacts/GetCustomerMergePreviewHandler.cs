using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-60`/`adr/0147`: the read behind the confirmation step - two lead cards and every booking each
/// one has ever had, so an operator decides "are these the same person" from evidence rather than a
/// phone number alone.
///
/// <para><b>Gated on <see cref="Permission.CustomerEdit"/>, the same permission
/// <see cref="MergeCustomersHandler"/> checks - not the narrower <see cref="Permission.CustomerRead"/>
/// every other contacts read uses.</b> An operator who can look at a lead card but not act on it would
/// otherwise reach a confirmation screen whose only button always fails - this handler refuses before
/// that screen ever opens, the same "the client-side gate matches exactly what the write requires"
/// discipline the console's own nav already applies elsewhere (`CalendarContactsPage`'s `23-57` doc
/// comment).</para>
///
/// <para><b>Every status, not only <see cref="EventStatus.Booked"/>.</b>
/// <see cref="ICustomerMergePreviewReadStore"/>'s own remarks state why: a cancellation or a no-show
/// under one identity is exactly the fact this screen exists to surface before an operator commits to
/// treating the two identities as one.</para>
/// </summary>
public sealed class GetCustomerMergePreviewHandler(
    ICustomerRepository customers,
    ICustomerMergePreviewReadStore previewBookings,
    IPermissionChecker permissions,
    IContactVisibilityProjectionStore visibility)
{
    public async Task<Result<CustomerMergePreviewResult>> HandleAsync(
        GetCustomerMergePreview query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerEdit, cancellationToken);
        if (!allowed)
        {
            return ContactsErrors.Forbidden(Permission.CustomerEdit);
        }

        if (query.FirstCustomerId == query.SecondCustomerId)
        {
            return ContactsErrors.CannotMergeCustomerWithItself(query.FirstCustomerId);
        }

        var first = await customers.GetByIdAsync(query.FirstCustomerId, cancellationToken);
        if (first is null || first.TenantId != query.TenantId)
        {
            return ContactsErrors.CustomerNotFound(query.FirstCustomerId);
        }

        var second = await customers.GetByIdAsync(query.SecondCustomerId, cancellationToken);
        if (second is null || second.TenantId != query.TenantId)
        {
            return ContactsErrors.CustomerNotFound(query.SecondCustomerId);
        }

        var rung = await visibility.GetAsync(query.TenantId, cancellationToken);
        var mask = rung == ContactVisibility.MaskedWithReveal;

        var bookings = await previewBookings.ListForCandidatesAsync(
            query.TenantId, first.Id, second.Id, cancellationToken);

        // `adr/0161`: the same rule MergeCustomersHandler would apply if this preview turned into a
        // real merge, computed here only to label the preview - CustomerMergeSurvivorRule's own
        // remarks explain why this is the one place the rule lives, not a second copy of it.
        var (survivor, _) = CustomerMergeSurvivorRule.Choose(first, second);

        return new CustomerMergePreviewResult(
            ToCandidate(first, first.Id == survivor.Id, mask, bookings),
            ToCandidate(second, second.Id == survivor.Id, mask, bookings));
    }

    private static CustomerMergeCandidate ToCandidate(
        Customer customer, bool willSurvive, bool mask, IReadOnlyList<CustomerMergePreviewBookingRow> bookings) =>
        new(
            customer.Id,
            customer.Source,
            willSurvive,
            mask ? customer.Phone.Masked() : customer.Phone.Value,
            mask,
            customer.DisplayName,
            customer.NoShowCount,
            [.. bookings.Where(row => row.CustomerId == customer.Id)]);
}

/// <summary>One of the two candidates, as the confirmation screen shows it - the lead card's own
/// summary fields plus its full booking history.</summary>
/// <param name="WillSurvive">`adr/0161`: whether <see cref="CustomerMergeSurvivorRule"/> would keep
/// this candidate if the operator goes on to confirm - shown so the confirmation screen states the
/// outcome before it happens rather than after. Display-only: the merge itself re-runs the identical
/// rule rather than trusting this flag, so a stale preview can never let a caller choose the wrong
/// side by racing a request.</param>
public readonly record struct CustomerMergeCandidate(
    CustomerId CustomerId,
    CustomerSource Source,
    bool WillSurvive,
    string Phone,
    bool Masked,
    string? DisplayName,
    int NoShowCount,
    IReadOnlyList<CustomerMergePreviewBookingRow> Bookings);

public readonly record struct CustomerMergePreviewResult(CustomerMergeCandidate First, CustomerMergeCandidate Second);
