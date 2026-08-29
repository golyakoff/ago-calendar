using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="ChatBookingTask"/>. A plain load/save pair, unlike
/// <see cref="IBookingStore"/>: this aggregate has no concurrent-claim problem of its own to solve -
/// exactly one caller (the visitor on this one chat task) ever advances one row, one reply at a time,
/// so there is no compare-and-set to design here the way there is for <see cref="Event"/>.
/// </summary>
public interface IChatBookingTaskStore
{
    /// <summary>Looked up by its own id, which is also the wire's <c>externalTaskId</c> in string
    /// form - see <see cref="ChatBookingTask"/>'s own remarks on why no separate correlation column
    /// exists.</summary>
    Task<ChatBookingTask?> GetByIdAsync(ChatBookingTaskId id, CancellationToken cancellationToken);

    Task AddAsync(ChatBookingTask task, CancellationToken cancellationToken);

    Task SaveAsync(ChatBookingTask task, CancellationToken cancellationToken);
}
