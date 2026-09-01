using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.WorkerSlots;

/// <summary>
/// The expected failures of `20-15`'s own read, in the <c>&lt;area&gt;.&lt;reason&gt;</c> vocabulary
/// <c>BookingLifecycleErrors</c> and <c>AvailabilityErrors</c> already established.
/// </summary>
public static class WorkerSlotsErrors
{
    public static Error Forbidden(Permission permission) => new(
        "worker_slots.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    /// <summary>Also what another tenant's worker id produces - the same vagueness
    /// <c>BookingLifecycleErrors.WrongTenant</c> chose, and for the identical reason: telling an
    /// operator of tenant A that a worker id exists in tenant B is a cross-tenant leak however it is
    /// worded, so both cases are reported as this one, undistinguishable, "not found".</summary>
    public static Error WorkerNotFound(WorkerId workerId) => new(
        "worker_slots.worker_not_found", $"Worker {workerId.Value} does not exist.");

    public static Error InvalidRange(DateOnly from, DateOnly to) => new(
        "worker_slots.invalid_range",
        $"The range must end on or after it starts; got {from:yyyy-MM-dd} .. {to:yyyy-MM-dd}.");
}
