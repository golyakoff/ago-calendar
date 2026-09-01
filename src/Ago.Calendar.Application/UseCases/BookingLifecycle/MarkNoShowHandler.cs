using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>
/// An operator records that a confirmed visit did not happen. <c>Booked -&gt; NoShow</c>.
///
/// <para><b>Only after the slot has ended</b>, and <see cref="Event.MarkNoShow"/> is what enforces
/// it rather than this handler: a no-show is a statement about something that did not happen, and it
/// cannot be made about a visit that has not had its chance yet. That check is a fact about one row
/// and belongs to the aggregate; the permission is a fact about the caller and belongs here
/// (adr/0016's own division).</para>
///
/// <para><b>The flag, and only the flag.</b> `20-04`'s scope is explicit that the pre-payment rule a
/// no-show history eventually feeds is not built here - so nothing reads
/// <see cref="EventStatus.NoShow"/> yet and nothing enforces anything.</para>
///
/// <para><b>A gap this handler deliberately leaves, stated rather than hidden.</b>
/// <see cref="Customer.NoShowCount"/> and <see cref="Customer.RecordNoShow"/> exist since `20-01`,
/// whose <see cref="EventNoShowRecorded"/> doc comment names the lead card as this event's only
/// consumer - but `20-04`'s own scope says "just the flag and its persistence", and incrementing a
/// second aggregate in the same transaction needs a multi-aggregate port this item was not asked to
/// invent (the shape <see cref="IBookingStore"/> took in `20-03`, for a write that genuinely had to
/// be atomic). So the counter stays zero for now. The two statements disagree and the item's own
/// scope wins; whoever builds the pre-payment rule needs a writer for that column and should read
/// this paragraph first.</para>
/// </summary>
public sealed class MarkNoShowHandler(
    IEventRepository events,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result> HandleAsync(MarkNoShow command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.BookingMarkNoShow, cancellationToken);
        if (!allowed)
        {
            return BookingLifecycleErrors.Forbidden(Permission.BookingMarkNoShow);
        }

        var booking = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (booking is null)
        {
            return BookingLifecycleErrors.NotFound(command.EventId);
        }

        if (booking.TenantId != command.TenantId)
        {
            return BookingLifecycleErrors.WrongTenant(command.EventId);
        }

        // `20-18`: the route names one slot, which may be any member of the run - resolve the whole
        // group before recording anything. Event.MarkNoShow's own "not before EndsAt" check, applied
        // per row, is what makes this correct without any extra group-level logic: a run's last slot
        // ends after every earlier one, so "now >= last slot's EndsAt" implies "now >= every other
        // row's own EndsAt" - an operator who tries to no-show a run before its final slot has ended
        // has that row's own check refuse, which aborts the whole group exactly as it should.
        var group = await events.ListByBookingIdAsync(booking.BookingId ?? booking.Id, cancellationToken);

        try
        {
            foreach (var slot in group)
            {
                slot.MarkNoShow(clock.UtcNow);
            }
        }
        catch (InvalidEventStateException exception)
        {
            // Two reachable messages here, and both are things an operator can act on: the visit is
            // not Booked (never confirmed, or already cancelled), or it has not ended yet.
            return BookingLifecycleErrors.InvalidState(exception.Message);
        }

        foreach (var slot in group)
        {
            slot.ClearDomainEvents();
        }

        try
        {
            await events.SaveRangeAsync(group, cancellationToken);
        }
        catch (EventConcurrencyConflictException)
        {
            return BookingLifecycleErrors.ConcurrencyConflict(command.EventId);
        }

        return Result.Success();
    }
}
