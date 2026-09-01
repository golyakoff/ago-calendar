using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// The expected failures of tenant setup. Precise about why, like `20-04`'s
/// <c>BookingLifecycleErrors</c> and unlike `20-03`'s: every caller here is an operator who has
/// already passed a permission check, so they are entitled to know the difference between "that time
/// zone is not a zone" and "that worker is not on this calendar" - and cannot fix their configuration
/// without it.
///
/// <para>The one exception is <see cref="NotFound"/>, which covers "does not exist" and "belongs to
/// another tenant" with one message, for the reason <c>BookingLifecycleErrors.WrongTenant</c> gives:
/// an operator of tenant A learning that an id exists in tenant B is a cross-tenant leak however
/// politely it is worded.</para>
/// </summary>
public static class ConfigurationErrors
{
    public static Error Forbidden(Permission permission) => new(
        "configuration.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    public static Error NotFound(string what, Guid id) => new(
        "configuration.not_found", $"No {what} {id} in this tenant.");

    public static Error TenantNotFound(TenantId tenantId) => new(
        "configuration.not_found", $"No tenant {tenantId.Value}.");

    /// <summary>
    /// A domain constructor said no. Turned into an ordinary rejection rather than allowed to escape
    /// as an exception: a tenant typing "-5" into a buffer field is a caller mistake, and letting
    /// <c>ArgumentOutOfRangeException</c> reach the endpoint would make a 400 look like a 500 in
    /// every log (<c>BookEventHandler</c> makes the same call for a malformed phone number).
    /// </summary>
    public static Error Invalid(string reason) => new("configuration.invalid", reason);

    /// <summary>`20-13`: the one refusal <c>DELETE /workers/{id}</c> can give that is not
    /// "not found" - a worker with an <see cref="EventStatus.PendingConfirmation"/>,
    /// <see cref="EventStatus.Booked"/> or <see cref="EventStatus.NoShow"/> row cannot be deleted, and
    /// the message says why and what to do instead, the same way every other error here does for an
    /// operator who already passed the permission check.</summary>
    public static Error WorkerHasBookingHistory(WorkerId workerId) => new(
        "configuration.worker_has_booking_history",
        $"Worker {workerId.Value} has a booking that is pending, confirmed, or a recorded no-show, " +
        "and cannot be deleted. Deactivate him instead.");

    /// <summary>`20-14`: <c>GET /workers/{id}/schedule</c> when no schedule has been written yet - a
    /// real, common state distinct from the worker not existing at all (<see cref="NotFound"/> covers
    /// that one).</summary>
    public static Error NoSchedule(WorkerId workerId) => new(
        "configuration.no_schedule",
        $"Worker {workerId.Value} has no schedule yet.");
}
