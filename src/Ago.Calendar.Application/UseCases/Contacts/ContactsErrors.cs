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
}
