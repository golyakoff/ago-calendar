using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

public static class ContactsErrors
{
    public static Error Forbidden(Permission permission) => new(
        "contacts.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");
}
