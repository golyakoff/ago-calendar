using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookingLifecycle;

/// <summary>The queue an operator works. One tenant, every calendar.</summary>
/// <param name="OperatorId">Whose permission is checked.</param>
/// <param name="TenantId">Whose queue. Never inferred from the operator row - see the handler.</param>
/// <param name="Limit">A page bound; the queue is short by construction, since a row leaves it within
/// a sweep tick of its deadline.</param>
public readonly record struct GetPendingBookingsForTenant(OperatorId OperatorId, TenantId TenantId, int Limit);

/// <summary>
/// The shared pending-bookings queue, gated by the one permission that can act on it.
///
/// <para><b>Gated by <see cref="Permission.BookingReject"/>, not by a separate read permission.</b>
/// The queue exists to be acted on: everything in it auto-confirms unless somebody vetoes it, so an
/// operator who can see it and cannot act on it is being shown a countdown they are powerless to
/// stop. Tying the read to the action keeps the two from drifting - the alternative, a
/// <c>booking:read</c> nobody grants separately, would be a permission that exists only to be granted
/// alongside another one.</para>
///
/// <para><b>Every operator sees every calendar's bookings.</b> No filtering by "theirs", because
/// there is no "theirs" - see <see cref="IPendingBookingReadStore"/> for why v1 has no assignment at
/// all.</para>
/// </summary>
public sealed class GetPendingBookingsForTenantHandler(
    IPendingBookingReadStore queue,
    IPermissionChecker permissions,
    IClock clock)
{
    /// <summary>A page bound that is generous rather than tuned: the queue drains continuously, so a
    /// tenant with more than this many pending bookings at once has a sweep problem, not a paging
    /// problem - and the overdue rows at the top of the page are what say so.</summary>
    public const int MaxLimit = 500;

    public async Task<Result<IReadOnlyList<PendingBookingRow>>> HandleAsync(
        GetPendingBookingsForTenant query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.BookingReject, cancellationToken);
        if (!allowed)
        {
            return BookingLifecycleErrors.Forbidden(Permission.BookingReject);
        }

        // `20-12`: a *second*, independent permission check - never a reason to refuse the whole
        // read, only whether the row's phone field is populated. The queue is shared and unassigned
        // (IPendingBookingReadStore's own remarks), so this cannot be a static per-row property the
        // way a chat conversation's own visibility might be; it is re-resolved on every request
        // against this caller's real, current roles.
        var canReadContacts = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerRead, cancellationToken);

        // Clamped rather than rejected: a caller asking for more than the page bound wants "as many
        // as I can have", and an error would make them guess the number.
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);

        var rows = await queue.GetPendingForTenantAsync(
            query.TenantId, clock.UtcNow, limit, canReadContacts, cancellationToken);
        return Result<IReadOnlyList<PendingBookingRow>>.Success(rows);
    }
}
