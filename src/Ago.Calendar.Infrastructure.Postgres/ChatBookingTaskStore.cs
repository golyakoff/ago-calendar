using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// The adapter for <see cref="IChatBookingTaskStore"/>. Plain EF load-mutate-save throughout, unlike
/// <see cref="BookingStore"/>'s raw compare-and-set: see the port's own remarks for why this aggregate
/// has no concurrent-write problem to solve.
/// </summary>
public sealed class ChatBookingTaskStore(AgoCalendarDbContext db) : IChatBookingTaskStore
{
    public Task<ChatBookingTask?> GetByIdAsync(ChatBookingTaskId id, CancellationToken cancellationToken) =>
        db.ChatBookingTasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task AddAsync(ChatBookingTask task, CancellationToken cancellationToken)
    {
        db.ChatBookingTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(ChatBookingTask task, CancellationToken cancellationToken)
    {
        if (db.Entry(task).State == EntityState.Detached)
        {
            db.ChatBookingTasks.Update(task);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
