using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

/// <summary>
/// Creates a worker, puts them on a calendar and records what they perform - in one aggregate and
/// therefore in one commit.
///
/// <para><b>Why this is one use case and not three.</b> A worker who is on no calendar and offers
/// nothing is invisible to every other part of this product: the materialiser skips them
/// (no working hours can even be created for them - <see cref="WorkingHoursRule.For"/> refuses a
/// worker who does not work in the calendar), and the booking surface cannot offer them. Three
/// separate calls would make that empty state reachable by stopping halfway, and the shop would
/// discover it as "the new stylist never appears" rather than as an error.</para>
///
/// <para><b>And why one commit rather than a transaction across aggregates.</b> Both joins live on
/// <see cref="Worker"/> (its own remarks say why: an aggregate that owns a relationship is the one
/// that can enforce a rule about it), so this is a single <c>AddAsync</c> - not a unit of work
/// spanning three repositories, which <c>ITenantRepository</c>'s own remarks explicitly refuse to
/// introduce without a use case that needs one.</para>
/// </summary>
public sealed class CreateWorkerHandler(
    IBookingCalendarRepository calendars,
    IServiceRepository services,
    IWorkerRepository workers,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<WorkerId>> HandleAsync(CreateWorker command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);
        if (calendar is null || calendar.TenantId != command.TenantId)
        {
            return ConfigurationErrors.NotFound("calendar", command.CalendarId.Value);
        }

        Worker worker;
        try
        {
            worker = Worker.Create(
                new WorkerId(idGenerator.NewId(clock.UtcNow)), command.TenantId, command.DisplayName);

            // The whole aggregate, not its id - Worker.JoinCalendar and Worker.Offer take the related
            // aggregate precisely so the cross-tenant check is an invariant rather than a convention
            // every call site must remember. Both are re-checked here by the domain, on top of the
            // tenant checks above, and that redundancy is the design: this handler could be wrong and
            // the aggregate would still refuse.
            worker.JoinCalendar(calendar);

            foreach (var serviceId in command.ServiceIds ?? [])
            {
                var service = await services.GetByIdAsync(new ServiceId(serviceId), cancellationToken);
                if (service is null || service.TenantId != command.TenantId)
                {
                    return ConfigurationErrors.NotFound("service", serviceId);
                }

                worker.Offer(service);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or TenantMismatchException or WorkerCalendarLimitException)
        {
            return ConfigurationErrors.Invalid(exception.Message);
        }

        await workers.AddAsync(worker, cancellationToken);
        return Result<WorkerId>.Success(worker.Id);
    }
}
