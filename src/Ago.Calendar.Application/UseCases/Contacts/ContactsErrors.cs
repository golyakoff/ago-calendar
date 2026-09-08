using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

public static class ContactsErrors
{
    public static Error Forbidden(Permission permission) => new(
        "contacts.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    /// <summary>`23-12`: the same "wrong tenant reads like no such row" info-hiding shape `ago-chat`'s
    /// own <c>ConversationErrors.ContactDetailNotFound</c> uses - a customer id that exists but
    /// belongs to a different tenant than the caller's own is this, not a different, more informative
    /// error that would confirm the id is real.</summary>
    public static Error CustomerNotFound(CustomerId customerId) => new(
        "contacts.customer_not_found", $"Customer {customerId.Value} does not exist in this tenant.");

    /// <summary>`23-60`: a request naming the same id twice - nonsensical rather than merely
    /// redundant, since <see cref="MergeCustomersHandler"/>'s own survivor choice has nothing to
    /// choose between.</summary>
    public static Error CannotMergeCustomerWithItself(CustomerId customerId) => new(
        "contacts.cannot_merge_customer_with_itself",
        $"Customer {customerId.Value} cannot be merged with itself.");

    /// <summary>`23-60`/`adr/0147`: a merge is irreversible, so a request naming an already-tombstoned
    /// row is refused rather than silently re-applied or, worse, chained into a second merge nobody
    /// asked for - <see cref="Customer.MarkMergedInto"/>'s own remarks state why the domain throws
    /// here rather than treating a second call as an idempotent no-op.</summary>
    public static Error CustomerAlreadyMerged(CustomerId customerId, CustomerId mergedIntoCustomerId) => new(
        "contacts.customer_already_merged",
        $"Customer {customerId.Value} was already merged into {mergedIntoCustomerId.Value}.");
}
