using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PublicBooking;

/// <summary>Free times on one calendar for one service, optionally with one worker.</summary>
/// <param name="WorkerId">Null means "anyone" - the choice a customer who does not care makes, and
/// the reason it is a nullable parameter rather than two query types.</param>
/// <param name="Limit">A page bound. Clamped, never rejected, for the reason
/// <c>GetPendingBookingsForTenantHandler</c> gives: a caller asking for more than the bound wants as
/// many as they can have, and an error would make them guess the number.</param>
public readonly record struct GetOpenSlots(
    string PublicKey, Guid CalendarId, Guid ServiceId, Guid? WorkerId, int Limit, string? Origin);

/// <summary>
/// The step a customer actually picks from.
///
/// <para><b>The window starts at <c>now</c> and has no end.</b> Not "the next fourteen days": the
/// horizon is already bounded by `20-02`'s materialiser, which is the thing that decides how far
/// ahead rows exist at all, and a second bound here would be a number this item invented that could
/// disagree with it. What bounds the response is <see cref="MaxLimit"/>, in rows, which is the unit a
/// renderer actually has a problem with.</para>
/// </summary>
public sealed class GetOpenSlotsHandler(
    EmbedScopeResolver scope, IBookingSurfaceReadStore surface, IClock clock)
{
    /// <summary>
    /// Generous rather than tuned, and bounded by the weakest renderer rather than by the database -
    /// the same reasoning adr/0061 applies to its action count. A browser can draw two hundred
    /// buttons; a text channel printing a numbered list cannot, and the whole point of this shape is
    /// that both render the same thing. The widget asks for far fewer.
    /// </summary>
    public const int MaxLimit = 200;

    public async Task<Result<IReadOnlyList<OpenSlotRow>>> HandleAsync(
        GetOpenSlots query, CancellationToken cancellationToken)
    {
        var resolved = await scope.ResolveAsync(
            query.PublicKey, query.CalendarId, query.Origin, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error!.Value;
        }

        var slots = await surface.ListOpenSlotsAsync(
            resolved.Value.Calendar!.Id,
            new ServiceId(query.ServiceId),
            query.WorkerId is { } workerId ? new WorkerId(workerId) : null,
            clock.UtcNow,
            Math.Clamp(query.Limit, 1, MaxLimit),
            cancellationToken);

        return Result<IReadOnlyList<OpenSlotRow>>.Success(slots);
    }
}
