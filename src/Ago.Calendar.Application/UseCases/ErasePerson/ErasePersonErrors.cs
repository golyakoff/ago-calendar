using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ErasePerson;

public static class ErasePersonErrors
{
    public static Error Forbidden(Permission permission) => new(
        "person_erase.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    /// <summary>The identical "wrong tenant reads like no such row" info-hiding shape
    /// <see cref="Ago.Calendar.Application.UseCases.Contacts.ContactsErrors.CustomerNotFound"/> already
    /// gives its own sibling: a person id that exists but belongs to another tenant is this, never a
    /// different, more informative error that would confirm the id is real.</summary>
    public static Error PersonNotFound(Guid personId) => new(
        "person_erase.not_found", $"Person {personId} does not exist in this tenant.");

    /// <summary>`26-275`/`adr/0189`'s own guard: this person has at least one <see cref="EventStatus.Booked"/>
    /// or <see cref="EventStatus.PendingConfirmation"/> event still ahead of now. 409, not 400 - the
    /// request is well-formed and the person exists; what refuses it is a state the guard itself
    /// protects, the identical "the world says no" shape
    /// <see cref="Ago.Calendar.Application.UseCases.Configuration.ConfigurationErrors.WorkerHasBookingHistory"/>
    /// already gives its own sibling refusal. The remedy this message names - cancel first, then erase -
    /// is exactly what the console UI (`26-275` slice #4) surfaces this code to offer.</summary>
    public static Error FutureBookingsExist(Guid personId) => new(
        "person_erase.future_bookings",
        $"Person {personId} has one or more upcoming bookings. Cancel or reschedule them before erasing this client.");
}
