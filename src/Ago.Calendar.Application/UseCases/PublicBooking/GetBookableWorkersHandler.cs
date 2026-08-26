using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PublicBooking;

/// <summary>Who on this calendar performs this service.</summary>
public readonly record struct GetBookableWorkers(
    string PublicKey, Guid CalendarId, Guid ServiceId, string? Origin);

/// <summary>
/// The second step of the public flow, and the reason the port takes a service id: a worker who does
/// not perform the chosen service is not a choice, and offering them produces a booking attempt
/// <c>BookEventHandler</c> refuses with <c>booking.service_not_offered</c> - a picker whose options
/// are not options.
/// </summary>
public sealed class GetBookableWorkersHandler(EmbedScopeResolver scope, IBookingSurfaceReadStore surface)
{
    public async Task<Result<IReadOnlyList<BookableWorkerRow>>> HandleAsync(
        GetBookableWorkers query, CancellationToken cancellationToken)
    {
        var resolved = await scope.ResolveAsync(
            query.PublicKey, query.CalendarId, query.Origin, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error!.Value;
        }

        var workers = await surface.ListWorkersAsync(
            resolved.Value.Calendar!.Id, new ServiceId(query.ServiceId), cancellationToken);

        return Result<IReadOnlyList<BookableWorkerRow>>.Success(workers);
    }
}
